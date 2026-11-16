using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.App.ViewModels;

public enum SourceStatus { Idle, Searching, Done, Error }

/// <summary>Scan state of one update source (drivers, Windows, Store).</summary>
public partial class SourceState : ObservableObject
{
    public SourceState(string key, string title, string glyph) { Key = key; Title = title; Glyph = glyph; }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(CountText), nameof(Tone))]
    private SourceStatus status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(Tone))]
    private int count;

    [ObservableProperty] private string message = "Not checked yet";

    public bool IsSearching => Status == SourceStatus.Searching;

    public string CountText => Status switch
    {
        SourceStatus.Done => Count.ToString(),
        SourceStatus.Searching => "…",
        SourceStatus.Error => "!",
        _ => "–",
    };

    public string Tone => Status switch
    {
        SourceStatus.Done when Count == 0 => "Good",
        SourceStatus.Done => "Info",
        SourceStatus.Error => "Bad",
        _ => "Neutral",
    };

    public void Begin() { Status = SourceStatus.Searching; Message = "Searching…"; }

    public void Done(int n)
    {
        Count = n;
        Status = SourceStatus.Done;
        Message = n switch { 0 => "Up to date", 1 => "1 update available", _ => $"{n} updates available" };
    }

    public void Fail(string message) { Status = SourceStatus.Error; Message = message; }

    /// <summary>Short text for the overview tile badge.</summary>
    public string ShortMessage => Status == SourceStatus.Error ? "Couldn't check" : Message;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(ShortMessage));
}

public partial class UpdateItemViewModel : ObservableObject
{
    private readonly Action? _selectionChanged;

    public UpdateItemViewModel(UpdateItem item, Action? selectionChanged)
    {
        Item = item;
        _selectionChanged = selectionChanged;
        isSelected = item.IsRecommended;
    }

    public UpdateItem Item { get; }

    [ObservableProperty] private bool isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(HasState), nameof(CanSelect), nameof(StateTone))]
    private InstallState? state;

    [ObservableProperty] private string? stateText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate), nameof(PercentValue))]
    private double? percent;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    partial void OnStateChanged(InstallState? value)
    {
        if (value is InstallState.Installed) IsSelected = false;
    }

    public bool IsWorking => State is InstallState.Downloading or InstallState.Installing or InstallState.Queued;
    public bool HasState => State is not null;
    public bool IsIndeterminate => Percent is null;
    public double PercentValue => Percent ?? 0;
    public bool CanSelect => Item.Advice != UpdateAdvice.NeedsLicense && State is not InstallState.Installed;

    public string StateTone => State switch
    {
        InstallState.Installed => "Good",
        InstallState.Failed => "Bad",
        InstallState.Skipped or InstallState.Cancelled => "Warn",
        _ => "Info",
    };

    public string Title => Item.Title;

    public string Glyph => Item.Kind switch
    {
        UpdateKind.Driver => "",
        UpdateKind.Firmware => "",
        UpdateKind.Windows => "",
        _ => "",
    };

    public string Subtitle => Item.Kind switch
    {
        UpdateKind.Driver or UpdateKind.Firmware when Item.MatchedDevices.Count > 0
            => "For " + string.Join(", ", Item.MatchedDevices),
        UpdateKind.Driver or UpdateKind.Firmware
            => string.Join(" · ", new[] { Item.Manufacturer, Item.DeviceClass }.Where(s => !string.IsNullOrWhiteSpace(s))),
        UpdateKind.Windows
            => string.Join(" · ", new[] { Item.KbArticle, Item.IsOptional ? "Optional" : "Important" }.Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => "Microsoft Store app",
    };

    public string VersionText
    {
        get
        {
            string Part(string? version, DateTime? date) =>
                string.Join(" ", new[] { version, date is { } d ? $"({d:yyyy-MM-dd})" : null }.Where(s => !string.IsNullOrEmpty(s)));

            var installed = Part(Item.InstalledVersion, Item.InstalledDate);
            var offered = Part(Item.NewVersion, Item.NewDate);
            return (installed, offered) switch
            {
                ("", "") => "",
                ("", _) => Item.Kind == UpdateKind.Windows ? $"Released {Item.NewDate:yyyy-MM-dd}" : $"New: {offered}",
                (_, "") => $"Installed: {installed}",
                _ => $"Installed: {installed}   →   New: {offered}",
            };
        }
    }

    public string SizeText => global::DriveMeCrazy.Core.Services.VersionText.FormatBytes(Item.SizeBytes);

    public string BadgeText => Item.Advice switch
    {
        UpdateAdvice.Recommended => "Recommended",
        UpdateAdvice.Optional => "Optional",
        UpdateAdvice.OlderThanInstalled => "Older than installed",
        UpdateAdvice.Superseded => "Newer version listed",
        UpdateAdvice.NeedsLicense => "Needs licence – use Settings",
        _ => "",
    };

    public string BadgeTone => Item.Advice switch
    {
        UpdateAdvice.Recommended => "Good",
        UpdateAdvice.Optional => "Info",
        UpdateAdvice.OlderThanInstalled => "Bad",
        _ => "Warn",
    };

    public void Apply(InstallProgress p)
    {
        State = p.State;
        StateText = p.Message;
        Percent = p.Percent is { } pct && p.State is InstallState.Downloading or InstallState.Installing ? pct : null;
    }
}

/// <summary>A row in the "Old drivers" table.</summary>
public partial class DriverRowViewModel : ObservableObject
{
    public DriverRowViewModel(DriverInfo driver, int oldYears)
    {
        Driver = driver;
        Age = driver.AgeYears(DateTime.Now);
        IsOld = driver.IsOld(DateTime.Now, oldYears);
    }

    public DriverInfo Driver { get; }
    public string Device => Driver.DeviceName;
    public string? Class => Driver.DeviceClass;
    public string? Provider => Driver.Provider;
    public string Version => Driver.Version;
    public string DateText => Driver.Date?.ToString("yyyy-MM-dd") ?? "";
    public double? Age { get; }
    public string AgeText => Age is { } a ? $"{a:0.0} yrs" : "";
    public bool IsOld { get; }

    [ObservableProperty] private string? updateNote;

    public string Status => Driver.IsInbox ? "Windows built-in" : IsOld ? "Old" : "OK";
    public string StatusTone => Driver.IsInbox ? "Neutral" : IsOld ? "Warn" : "Good";

    [RelayCommand]
    private void SearchOnline()
    {
        var maker = Driver.Manufacturer;
        if (string.IsNullOrWhiteSpace(maker) || maker.StartsWith('(') ||
            maker.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            maker = Driver.Provider;
        var query = $"{maker} {Driver.DeviceName} driver download official site".Trim();
        AppInfo.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(query));
    }
}

public partial class NavItem : ObservableObject
{
    public NavItem(string key, string title, string glyph, object page)
    {
        Key = key; Title = title; Glyph = glyph; Page = page;
    }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public object Page { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private int badge;

    public bool HasBadge => Badge > 0;
}
