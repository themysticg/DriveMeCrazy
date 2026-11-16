using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DeviceInventoryService _inventory = new();
    private readonly WindowsUpdateService _windowsUpdate = new();
    private readonly StoreUpdateService _store = new();
    private readonly RestorePointService _restore = new();
    private readonly SettingsService _settingsService = new();
    private readonly HistoryService _historyService = new();
    private IReadOnlyList<DriverInfo> _drivers = [];
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        Settings = _settingsService.Load();
        foreach (var h in _historyService.Load().OrderByDescending(h => h.Timestamp)) History.Add(h);
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsHistoryEmpty));

        DriverSource = new SourceState("drivers", "Drivers & firmware", "");
        WindowsSource = new SourceState("windows", "Windows Update", "");
        StoreSource = new SourceState("store", "Microsoft Store apps", "");
        Sources = [DriverSource, WindowsSource, StoreSource];

        DriverList = new UpdateListViewModel(this, DriverSource, "Driver & firmware updates",
            "Signed drivers and firmware from Microsoft's Windows Update catalog, matched to the exact hardware in this PC.",
            "No driver updates found. Run a check, or look at Old drivers for devices to update from the manufacturer.")
        { ShowHeader = false };
        WindowsList = new UpdateListViewModel(this, WindowsSource, "Windows Update",
            "Security, quality and optional updates for Windows itself.",
            "Windows is up to date.")
        { ExternalActionText = "Open Windows Update", ExternalActionTarget = "ms-settings:windowsupdate" };
        StoreList = new UpdateListViewModel(this, StoreSource, "Microsoft Store apps",
            "Updates for apps installed from the Microsoft Store.",
            "All Store apps are up to date.")
        { ExternalActionText = "Open Microsoft Store", ExternalActionTarget = "ms-windows-store://downloadsandupdates" };

        DriversPage = new DriversPageViewModel(this, DriverList);
        NavItems =
        [
            new NavItem("overview", "Overview", "", new OverviewPageViewModel(this)),
            new NavItem("drivers", "Drivers", "", DriversPage),
            new NavItem("windows", "Windows Update", "", WindowsList),
            new NavItem("store", "Store apps", "", StoreList),
            new NavItem("history", "History", "", new HistoryPageViewModel(this)),
            new NavItem("settings", "Settings", "", new SettingsPageViewModel(this)),
        ];
        selectedNav = NavItems[0];
    }

    public AppSettings Settings { get; }
    public bool IsAdmin => AppInfo.IsAdmin;
    public string Version => AppInfo.Version;

    public SourceState DriverSource { get; }
    public SourceState WindowsSource { get; }
    public SourceState StoreSource { get; }
    public IReadOnlyList<SourceState> Sources { get; }

    public UpdateListViewModel DriverList { get; }
    public UpdateListViewModel WindowsList { get; }
    public UpdateListViewModel StoreList { get; }
    public DriversPageViewModel DriversPage { get; }

    public IReadOnlyList<NavItem> NavItems { get; }
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public List<DriverRowViewModel> AllDrivers { get; private set; } = [];
    public int OldDriverCount => AllDrivers.Count(d => d.IsOld);

    [ObservableProperty] private NavItem selectedNav;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(InstallRecommendedCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isBusy;

    [ObservableProperty] private bool isInstalling;
    [ObservableProperty] private string statusText = "Ready. Click \"Check for updates\" to scan your PC.";
    [ObservableProperty] private double? progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroTitle), nameof(HeroSubtitle))]
    private DateTime? lastScan;

    [ObservableProperty] private bool restartRequired;
    [ObservableProperty] private string? restorePointStatus;

    public bool IsIdle => !IsBusy;
    public bool IsHistoryEmpty => History.Count == 0;
    public bool ShowAdminWarning => !IsAdmin;

    public int RecommendedCount =>
        DriverList.Items.Count(i => i.CanSelect && i.Item.IsRecommended) +
        WindowsList.Items.Count(i => i.CanSelect && i.Item.IsRecommended) +
        StoreList.Items.Count(i => i.CanSelect && i.Item.IsRecommended);

    public bool HasRecommended => RecommendedCount > 0;
    public string InstallAllText => $"Update all recommended ({RecommendedCount})";

    public string HeroTitle => LastScan is null
        ? "Let's freshen up your PC"
        : RecommendedCount switch
        {
            0 => "Everything is up to date",
            1 => "1 recommended update",
            var n => $"{n} recommended updates",
        };

    public string HeroSubtitle => LastScan is null
        ? "Checks drivers, firmware, Windows and Microsoft Store apps against Microsoft's official update servers. Nothing is installed until you say so."
        : $"Last checked {LastScan:dddd HH:mm}. {OldDriverCount} driver(s) older than {Settings.OldDriverYears} years.";

    public string RestoreSetting => Settings.CreateRestorePoint ? "On – a restore point is created before installing" : "Off";

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var l in new[] { DriverList, WindowsList, StoreList })
            l.InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(RecommendedCount));
        OnPropertyChanged(nameof(HasRecommended));
        OnPropertyChanged(nameof(InstallAllText));
        OnPropertyChanged(nameof(HeroTitle));
        InstallRecommendedCommand.NotifyCanExecuteChanged();
    }

    private void UpdateBadges()
    {
        int Pending(UpdateListViewModel l) => l.Items.Count(i => i.CanSelect && i.State is not InstallState.Installed);
        NavItems.First(n => n.Key == "drivers").Badge = Pending(DriverList);
        NavItems.First(n => n.Key == "windows").Badge = Pending(WindowsList);
        NavItems.First(n => n.Key == "store").Badge = Pending(StoreList);
        NotifySelectionChanged();
        OnPropertyChanged(nameof(HeroSubtitle));
    }

    [RelayCommand]
    private void Navigate(string key) => SelectedNav = NavItems.FirstOrDefault(n => n.Key == key) ?? SelectedNav;

    // ---------------------------------------------------------------- scanning

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task ScanAsync()
    {
        IsBusy = true;
        ProgressValue = null;
        StatusText = "Checking for updates… Windows Update can take a minute or two.";
        foreach (var s in Sources) s.Begin();
        var sw = Stopwatch.StartNew();

        var inventoryTask = Task.Run(() => (Drivers: _inventory.GetDrivers(), Index: _inventory.GetDeviceIndex()));
        var windowsUpdateTask = _windowsUpdate.SearchAsync();
        var storeTask = _store.SearchAsync();

        IReadOnlyList<DriverInfo> drivers = [];
        DeviceIndex index = DeviceIndex.Empty;
        try
        {
            (drivers, index) = await inventoryTask;
        }
        catch (Exception ex)
        {
            Log.Error("Driver inventory failed", ex);
        }
        _drivers = drivers;
        RebuildDriverRows();

        try
        {
            var all = await windowsUpdateTask;
            var driverUpdates = all.Where(u => u.Kind is UpdateKind.Driver or UpdateKind.Firmware).ToList();
            var windowsUpdates = all.Where(u => u.Kind == UpdateKind.Windows).ToList();
            UpdateMatcher.Apply(driverUpdates, drivers, index);
            UpdateMatcher.ApplyWindows(windowsUpdates);
            DriverList.SetItems(driverUpdates);
            WindowsList.SetItems(windowsUpdates);
            DriverSource.Done(driverUpdates.Count(u => u.IsRecommended));
            WindowsSource.Done(windowsUpdates.Count(u => u.Advice is UpdateAdvice.Recommended or UpdateAdvice.Optional));
            MarkDriverRowsWithOffers(driverUpdates);
        }
        catch (Exception ex)
        {
            Log.Error("Windows Update search failed", ex);
            DriverSource.Fail(ex.Message);
            WindowsSource.Fail(ex.Message);
        }

        try
        {
            var storeUpdates = await storeTask;
            StoreList.SetItems(storeUpdates);
            StoreSource.Done(storeUpdates.Count);
        }
        catch (Exception ex)
        {
            Log.Error("Store search failed", ex);
            StoreSource.Fail(ex is UnauthorizedAccessException
                ? "Windows didn't let DriveMeCrazy check the Store. Use \"Open Microsoft Store\" to update Store apps there."
                : "Couldn't check the Store: " + ex.Message);
        }

        if (_windowsUpdate.IsRebootPending()) RestartRequired = true;
        LastScan = DateTime.Now;
        UpdateBadges();
        StatusText = $"Scan finished in {sw.Elapsed.TotalSeconds:0}s · {RecommendedCount} recommended update(s).";
        IsBusy = false;
    }

    public void RebuildDriverRows()
    {
        var notes = AllDrivers.ToDictionary(d => d.Driver.DeviceId, d => d.UpdateNote, StringComparer.OrdinalIgnoreCase);
        AllDrivers = _drivers
            .Select(d => new DriverRowViewModel(d, Settings.OldDriverYears)
            {
                UpdateNote = notes.GetValueOrDefault(d.DeviceId)
            })
            .OrderByDescending(d => d.IsOld)
            .ThenByDescending(d => d.Age ?? -1)
            .ToList();
        OnPropertyChanged(nameof(AllDrivers));
        OnPropertyChanged(nameof(OldDriverCount));
        OnPropertyChanged(nameof(HeroSubtitle));
        DriversPage.Refresh();
    }

    private void MarkDriverRowsWithOffers(IEnumerable<UpdateItem> driverUpdates)
    {
        foreach (var row in AllDrivers) row.UpdateNote = null;
        foreach (var u in driverUpdates.Where(u => u.IsRecommended))
            foreach (var row in AllDrivers.Where(r => u.MatchedDevices.Contains(r.Device, StringComparer.OrdinalIgnoreCase)))
                row.UpdateNote = $"Update available ({u.NewDate:yyyy-MM-dd})";
    }

    // ---------------------------------------------------------------- installing

    private bool CanInstallRecommended() => IsIdle && HasRecommended;

    [RelayCommand(CanExecute = nameof(CanInstallRecommended))]
    private Task InstallRecommendedAsync()
    {
        var items = new[] { DriverList, WindowsList, StoreList }
            .SelectMany(l => l.Items)
            .Where(i => i.CanSelect && i.Item.IsRecommended)
            .ToList();
        foreach (var i in items) i.IsSelected = true;
        return InstallAsync(items);
    }

    public async Task InstallAsync(IReadOnlyList<UpdateItemViewModel> items)
    {
        if (items.Count == 0 || IsBusy) return;
        if (!IsAdmin)
        {
            MessageBox.Show("Installing updates needs administrator rights.\n\nClose DriveMeCrazy and start it again with \"Run as administrator\".",
                "DriveMeCrazy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", items.Take(12).Select(i => "•  " + i.Title));
        if (items.Count > 12) preview += $"\n…and {items.Count - 12} more";
        if (MessageBox.Show($"Install {items.Count} update(s)?\n\n{preview}", "Confirm install",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        IsInstalling = true;
        _cts = new CancellationTokenSource();
        foreach (var i in items) i.Apply(new InstallProgress(i.Item.Id, InstallState.Queued, "Waiting…"));

        try
        {
            if (Settings.CreateRestorePoint && !await CreateRestorePointAsync())
            {
                foreach (var i in items) { i.State = null; i.StateText = null; }
                StatusText = "Install cancelled.";
                return;
            }

            var byId = items.ToDictionary(i => i.Item.Id, StringComparer.OrdinalIgnoreCase);
            int done = 0;
            var progress = new Progress<InstallProgress>(p =>
            {
                if (!byId.TryGetValue(p.Id, out var vm)) return;
                if (vm.State is { } s && (s is InstallState.Installed or InstallState.Failed or InstallState.Skipped or InstallState.Cancelled)) return;
                vm.Apply(p);
                if (p.IsFinal) done++;
                ProgressValue = 100.0 * done / items.Count;
                StatusText = $"{done} of {items.Count} done · {vm.Title}: {p.Message}";
            });

            var results = new List<InstallProgress>();
            var wuIds = items.Where(i => i.Item.Kind != UpdateKind.StoreApp).Select(i => i.Item.Id).ToList();
            if (wuIds.Count > 0)
                results.AddRange(await _windowsUpdate.InstallAsync(wuIds, progress, _cts.Token));

            var storeItems = items.Where(i => i.Item.Kind == UpdateKind.StoreApp).Select(i => i.Item).ToList();
            if (storeItems.Count > 0)
                results.AddRange(await _store.InstallAsync(storeItems, progress, _cts.Token));

            // Apply final results directly (Progress<T> callbacks may still be queued).
            foreach (var r in results)
                if (byId.TryGetValue(r.Id, out var vm)) vm.Apply(r);

            foreach (var r in results)
            {
                if (!byId.TryGetValue(r.Id, out var vm)) continue;
                AddHistory(new HistoryEntry(DateTime.Now, KindLabel(vm.Item.Kind), vm.Title, r.Message,
                    r.State == InstallState.Installed, r.RebootRequired));
            }

            if (results.Any(r => r.RebootRequired) || _windowsUpdate.IsRebootPending()) RestartRequired = true;

            int ok = results.Count(r => r.State == InstallState.Installed);
            int failed = results.Count(r => r.State == InstallState.Failed);
            int skipped = results.Count - ok - failed;
            StatusText = $"Finished: {ok} installed, {failed} failed, {skipped} skipped." + (RestartRequired ? " Restart required." : "");
            ProgressValue = 100;

            var summary = string.Join("\n", results.Select(r =>
                $"{(r.State == InstallState.Installed ? "✔" : r.State == InstallState.Failed ? "✖" : "•")}  {byId.GetValueOrDefault(r.Id)?.Title ?? r.Id}\n      {r.Message}"));
            MessageBox.Show(summary + (RestartRequired ? "\n\nRestart your PC to finish installing." : ""),
                $"{ok} of {results.Count} installed", MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            StatusText = "Install failed: " + ex.Message;
            MessageBox.Show(ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsInstalling = false;
            IsBusy = false;
            UpdateBadges();
            foreach (var l in new[] { DriverList, WindowsList, StoreList }) l.RefreshSelection();
        }
    }

    private async Task<bool> CreateRestorePointAsync()
    {
        StatusText = "Creating a System Restore point…";
        var result = await Task.Run(() => _restore.Create("DriveMeCrazy: before installing updates"));

        if (!result.Success && result.ProtectionDisabled)
        {
            var answer = MessageBox.Show(
                "System Protection (System Restore) is turned off on this PC, so a restore point can't be created.\n\n" +
                "Yes – turn System Protection on for the system drive and create a restore point\n" +
                "No – continue without a restore point\n" +
                "Cancel – don't install anything",
                "Restore point", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes)
            {
                var enabled = await Task.Run(_restore.EnableProtection);
                result = enabled.Success
                    ? await Task.Run(() => _restore.Create("DriveMeCrazy: before installing updates"))
                    : enabled;
            }
            else
            {
                RestorePointStatus = "Skipped by you";
                return true;
            }
        }

        if (!result.Success)
        {
            var answer = MessageBox.Show($"{result.Message}\n\nContinue installing without a restore point?",
                "Restore point", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            RestorePointStatus = result.Message;
            AddHistory(new HistoryEntry(DateTime.Now, "Safety", "System Restore point", result.Message, false));
            return answer == MessageBoxResult.Yes;
        }

        RestorePointStatus = $"Created {DateTime.Now:HH:mm}";
        AddHistory(new HistoryEntry(DateTime.Now, "Safety", "System Restore point", result.Message, true));
        return true;
    }

    private static string KindLabel(UpdateKind kind) => kind switch
    {
        UpdateKind.Driver => "Driver",
        UpdateKind.Firmware => "Firmware",
        UpdateKind.Windows => "Windows",
        _ => "Store app",
    };

    private void AddHistory(HistoryEntry entry)
    {
        History.Insert(0, entry);
        _historyService.Save(History);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (MessageBox.Show("Clear the update history?", "DriveMeCrazy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        History.Clear();
        _historyService.Save(History);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        if (_cts is null) return;
        _cts.Cancel();
        StatusText = "Cancelling after the current update finishes…";
    }

    [RelayCommand]
    private void RestartNow()
    {
        if (MessageBox.Show("Restart your PC now? Save your work in other apps first.", "Restart",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"Restarting to finish installing updates (DriveMeCrazy)\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    [RelayCommand]
    private void DismissRestart() => RestartRequired = false;

    public void SaveSettings()
    {
        _settingsService.Save(Settings);
        OnPropertyChanged(nameof(RestoreSetting));
        OnPropertyChanged(nameof(HeroSubtitle));
    }
}
