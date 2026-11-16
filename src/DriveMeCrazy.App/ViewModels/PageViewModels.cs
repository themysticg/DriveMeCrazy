using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.App.ViewModels;

public sealed class OverviewPageViewModel(MainViewModel main)
{
    public MainViewModel Main { get; } = main;
}

/// <summary>A list of updates from one source with selection and install commands.</summary>
public partial class UpdateListViewModel : ObservableObject
{
    public UpdateListViewModel(MainViewModel main, SourceState source, string title, string description, string emptyText)
    {
        Main = main;
        Source = source;
        Title = title;
        Description = description;
        EmptyText = emptyText;
        Items.CollectionChanged += (_, _) => { RefreshSelection(); OnPropertyChanged(nameof(IsEmpty)); };
        source.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(EmptyGlyph));
            OnPropertyChanged(nameof(EmptyTone));
        };
    }

    /// <summary>Optional shortcut to Windows' own page for this source.</summary>
    public string? ExternalActionText { get; init; }
    public string? ExternalActionTarget { get; init; }
    public bool HasExternalAction => ExternalActionText is not null;

    public string EmptyMessage => Source.Status switch
    {
        SourceStatus.Idle => "Not checked yet. Click \"Check for updates\" on the Overview page.",
        SourceStatus.Searching => "Searching…",
        SourceStatus.Error => Source.Message,
        _ => EmptyText,
    };

    public string EmptyGlyph => Source.Status switch
    {
        SourceStatus.Error => "",
        SourceStatus.Done => "",
        _ => "",
    };

    public string EmptyTone => Source.Status switch
    {
        SourceStatus.Error => "Warn",
        SourceStatus.Done => "Good",
        _ => "Neutral",
    };

    [RelayCommand]
    private void OpenExternal()
    {
        if (ExternalActionTarget is not null) AppInfo.Open(ExternalActionTarget);
    }

    public MainViewModel Main { get; }
    public SourceState Source { get; }
    public string Title { get; }
    public string Description { get; }
    public string EmptyText { get; }
    public bool ShowHeader { get; init; } = true;
    public ObservableCollection<UpdateItemViewModel> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;
    public int SelectedCount => Items.Count(i => i.IsSelected && i.CanSelect);
    public string InstallText => SelectedCount == 0 ? "Install selected" : $"Install selected ({SelectedCount})";

    public void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallText));
        InstallSelectedCommand.NotifyCanExecuteChanged();
        Main.NotifySelectionChanged();
    }

    public void SetItems(IEnumerable<UpdateItem> items)
    {
        Items.Clear();
        foreach (var it in items
                     .OrderBy(i => i.Advice)
                     .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new UpdateItemViewModel(it, RefreshSelection));
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var i in Items) i.IsSelected = i.CanSelect && i.Item.IsRecommended;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallSelectedAsync() =>
        Main.InstallAsync(Items.Where(i => i.IsSelected && i.CanSelect).ToList());

    private bool CanInstall() => SelectedCount > 0 && !Main.IsBusy;
}

public partial class DriversPageViewModel : ObservableObject
{
    public DriversPageViewModel(MainViewModel main, UpdateListViewModel updates)
    {
        Main = main;
        Updates = updates;
    }

    public MainViewModel Main { get; }
    public UpdateListViewModel Updates { get; }

    [ObservableProperty] private bool showAllDrivers;
    [ObservableProperty] private int selectedTab;

    partial void OnShowAllDriversChanged(bool value) => OnPropertyChanged(nameof(DriverRows));

    public IEnumerable<DriverRowViewModel> DriverRows =>
        ShowAllDrivers ? Main.AllDrivers : Main.AllDrivers.Where(d => d.IsOld);

    public string OldDriversHeader => $"Old drivers ({Main.OldDriverCount})";

    public void Refresh()
    {
        OnPropertyChanged(nameof(DriverRows));
        OnPropertyChanged(nameof(OldDriversHeader));
    }
}

public sealed partial class HistoryPageViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;

    [RelayCommand]
    private static void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        AppInfo.Open(AppPaths.LogsDir);
    }
}

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsPageViewModel(MainViewModel main)
    {
        _main = main;
        oldDriverYears = main.Settings.OldDriverYears;
        createRestorePoint = main.Settings.CreateRestorePoint;
        searchCatalog = main.Settings.SearchCatalog;
        theme = main.Settings.Theme;
    }

    public IReadOnlyList<int> YearChoices { get; } = Enumerable.Range(1, 10).ToList();
    public IReadOnlyList<string> ThemeChoices { get; } = ["System", "Light", "Dark"];
    public string Version => AppInfo.Version;
    public string RepoUrl => AppInfo.RepoUrl;

    [ObservableProperty] private int oldDriverYears;
    [ObservableProperty] private bool createRestorePoint;
    [ObservableProperty] private string theme;
    [ObservableProperty] private bool searchCatalog;

    partial void OnSearchCatalogChanged(bool value)
    {
        _main.Settings.SearchCatalog = value;
        _main.SaveSettings();
    }

    partial void OnOldDriverYearsChanged(int value)
    {
        _main.Settings.OldDriverYears = value;
        _main.SaveSettings();
        _main.RebuildDriverRows();
    }

    partial void OnCreateRestorePointChanged(bool value)
    {
        _main.Settings.CreateRestorePoint = value;
        _main.SaveSettings();
    }

    partial void OnThemeChanged(string value)
    {
        _main.Settings.Theme = value;
        _main.SaveSettings();
        AppInfo.ApplyTheme(value);
    }

    [RelayCommand] private static void OpenLogs() { Directory.CreateDirectory(AppPaths.LogsDir); AppInfo.Open(AppPaths.LogsDir); }
    [RelayCommand] private static void OpenWindowsUpdate() => AppInfo.Open("ms-settings:windowsupdate");
    [RelayCommand] private static void OpenStoreUpdates() => AppInfo.Open("ms-windows-store://downloadsandupdates");
    [RelayCommand] private static void OpenSystemProtection() => AppInfo.Open("SystemPropertiesProtection.exe");
    [RelayCommand] private static void OpenRepo() => AppInfo.Open(AppInfo.RepoUrl);
}
