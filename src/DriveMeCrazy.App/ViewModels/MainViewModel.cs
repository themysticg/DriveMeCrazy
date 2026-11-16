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
    private readonly DriverCatalogService _catalog;
    private readonly Dictionary<string, CatalogLookup> _catalogResults = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DriverInfo> _drivers = [];
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        Settings = _settingsService.Load();
        _catalog = new DriverCatalogService(_inventory);
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
    public int CatalogFoundCount => AllDrivers.Count(d => d.HasCandidate);
    public int CatalogSelectedCount => AllDrivers.Count(d => d.IsSelected && d.CanSelect);
    public string CatalogInstallText => CatalogSelectedCount == 0 ? "Update selected drivers" : $"Update selected drivers ({CatalogSelectedCount})";

    public string OldDriversSummary => CatalogFoundCount > 0
        ? $"{CatalogFoundCount} of them have a newer Microsoft-signed driver in the Microsoft Update Catalog, ready to install."
        : "Drivers older than your threshold. DriveMeCrazy looks for newer Microsoft-signed versions in the Microsoft Update Catalog.";

    public void NotifyCatalogSelection()
    {
        OnPropertyChanged(nameof(CatalogFoundCount));
        OnPropertyChanged(nameof(CatalogSelectedCount));
        OnPropertyChanged(nameof(CatalogInstallText));
        OnPropertyChanged(nameof(OldDriversSummary));
        OnPropertyChanged(nameof(HasCatalogFound));
        OnPropertyChanged(nameof(ReviewDriversText));
        OnPropertyChanged(nameof(HeroTitle));
        InstallCatalogDriversCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty] private NavItem selectedNav;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(InstallRecommendedCommand), nameof(CancelCommand),
        nameof(FindCatalogDriversCommand), nameof(InstallCatalogDriversCommand))]
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
        : (RecommendedCount + CatalogFoundCount) switch
        {
            0 => "Everything is up to date",
            1 => "1 update available",
            var n => $"{n} updates available",
        };

    public bool HasCatalogFound => CatalogFoundCount > 0;
    public string ReviewDriversText => $"Update old drivers ({CatalogFoundCount})";

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
        int Pending(UpdateListViewModel l) => l.Items.Count(i => i.IsPending);
        int drivers = Pending(DriverList), windows = Pending(WindowsList), store = Pending(StoreList);
        NavItems.First(n => n.Key == "drivers").Badge = drivers + CatalogFoundCount;
        NavItems.First(n => n.Key == "windows").Badge = windows;
        NavItems.First(n => n.Key == "store").Badge = store;
        DriverSource.UpdateCount(drivers + CatalogFoundCount);
        WindowsSource.UpdateCount(windows);
        StoreSource.UpdateCount(store);
        NotifySelectionChanged();
        OnPropertyChanged(nameof(HeroSubtitle));
    }

    [RelayCommand]
    private void Navigate(string key) => SelectedNav = NavItems.FirstOrDefault(n => n.Key == key) ?? SelectedNav;

    [RelayCommand]
    private void ReviewOldDrivers()
    {
        DriversPage.SelectedTab = 1;
        Navigate("drivers");
    }

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

        if (Settings.SearchCatalog)
            await SearchCatalogAsync();

        StatusText = $"Scan finished in {sw.Elapsed.TotalSeconds:0}s · {RecommendedCount} recommended update(s)" +
                     (CatalogFoundCount > 0 ? $" · {CatalogFoundCount} newer driver(s) in the Microsoft Update Catalog." : ".");
        IsBusy = false;
    }

    // ---------------------------------------------------------------- Microsoft Update Catalog

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task FindCatalogDriversAsync()
    {
        IsBusy = true;
        try { await SearchCatalogAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>Searches the catalog for every old (non-built-in) driver, four at a time.</summary>
    private async Task SearchCatalogAsync()
    {
        var rows = AllDrivers.Where(r => r.IsOld && !r.Driver.IsInbox && r.UpdateNote is null).ToList();
        if (rows.Count == 0) return;

        StatusText = $"Searching the Microsoft Update Catalog for {rows.Count} old driver(s)…";
        ProgressValue = 0;
        int done = 0;
        using var gate = new SemaphoreSlim(4);
        foreach (var r in rows) r.IsSearching = true;

        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var lookup = await _catalog.FindAsync(row.Driver);
                _catalogResults[row.Driver.DeviceId] = lookup;
                row.Lookup = lookup;
                row.IsSelected = lookup.Best is not null;
            }
            catch (Exception ex)
            {
                Log.Error($"Catalog search failed for {row.Device}", ex);
                row.StateText = "Couldn't reach the Microsoft Update Catalog";
            }
            finally
            {
                row.IsSearching = false;
                gate.Release();
                done++;
                ProgressValue = 100.0 * done / rows.Count;
            }
        });
        await Task.WhenAll(tasks);

        ProgressValue = null;
        UpdateBadges();
        NotifyCatalogSelection();
        StatusText = CatalogFoundCount > 0
            ? $"Found {CatalogFoundCount} newer driver(s) in the Microsoft Update Catalog. See Drivers → Old drivers."
            : "No newer drivers in the Microsoft Update Catalog for your old drivers.";
    }

    private bool CanInstallCatalog() => IsIdle && CatalogSelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanInstallCatalog))]
    private async Task InstallCatalogDriversAsync()
    {
        var rows = AllDrivers.Where(r => r.IsSelected && r.CanSelect).ToList();
        if (rows.Count == 0) return;
        if (!IsAdmin)
        {
            MessageBox.Show("Installing drivers needs administrator rights.\n\nClose DriveMeCrazy and start it again with \"Run as administrator\".",
                "DriveMeCrazy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join("\n", rows.Select(r => $"•  {r.Device}\n      {r.Driver.Version}  →  {r.Lookup!.Best!.Version}"));
        if (MessageBox.Show(
                $"Install {rows.Count} driver update(s) from the Microsoft Update Catalog?\n\n{preview}\n\n" +
                "Each package is checked for a valid Microsoft signature and must list your exact device before Windows installs it.",
                "Confirm driver update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        IsInstalling = true;
        _cts = new CancellationTokenSource();
        var results = new List<(DriverRowViewModel Row, InstallProgress Result)>();
        try
        {
            if (Settings.CreateRestorePoint && !await CreateRestorePointAsync())
            {
                StatusText = "Install cancelled.";
                return;
            }

            foreach (var r in rows) { r.State = InstallState.Queued; r.StateText = "Waiting…"; }
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int position = i + 1;
                if (_cts.IsCancellationRequested)
                {
                    row.State = InstallState.Cancelled; row.StateText = "Cancelled";
                    continue;
                }
                var progress = new Progress<InstallProgress>(p =>
                {
                    row.State = p.State;
                    row.StateText = p.Message;
                    StatusText = $"{position} of {rows.Count} · {row.Device}: {p.Message}";
                });
                InstallProgress result;
                try { result = await _catalog.InstallAsync(row.Lookup!, progress, _cts.Token); }
                catch (OperationCanceledException) { result = new InstallProgress(row.Driver.DeviceId, InstallState.Cancelled, "Cancelled"); }
                row.State = result.State;
                row.StateText = result.Message;
                results.Add((row, result));
                ProgressValue = 100.0 * (i + 1) / rows.Count;
                AddHistory(new HistoryEntry(DateTime.Now, "Driver (Catalog)", row.Device, result.Message, result.IsDone, result.RebootRequired));
                if (result.IsDone) _catalogResults.Remove(row.Driver.DeviceId);
                if (result.RebootRequired) RestartRequired = true;
            }

            int ok = results.Count(r => r.Result.IsDone);
            int failed = results.Count(r => r.Result.State == InstallState.Failed);
            StatusText = $"Driver updates finished: {ok} updated, {failed} not updated." + (RestartRequired ? " Restart required." : "");
            var summary = string.Join("\n", results.Select(r =>
                $"{(r.Result.IsDone ? "✔" : r.Result.State == InstallState.Failed ? "✖" : "•")}  {r.Row.Device}\n      {r.Result.Message}"));
            MessageBox.Show(summary + (RestartRequired ? "\n\nRestart your PC to finish installing." : ""),
                $"{ok} of {results.Count} drivers updated", MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // Re-read installed drivers so versions and ages are current.
            _drivers = await Task.Run(_inventory.GetDrivers);
            RebuildDriverRows();
        }
        catch (Exception ex)
        {
            Log.Error("Catalog install failed", ex);
            StatusText = "Driver update failed: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsInstalling = false;
            IsBusy = false;
            UpdateBadges();
            NotifyCatalogSelection();
        }
    }

    /// <summary>Runs every catalog install step except the install itself, for each found driver (diagnostics).</summary>
    public async Task<IReadOnlyList<string>> VerifyCatalogAsync()
    {
        var lines = new List<string>();
        foreach (var row in AllDrivers.Where(r => r.HasCandidate))
        {
            var r = await _catalog.InstallAsync(row.Lookup!, null, CancellationToken.None, dryRun: true);
            lines.Add($"{row.Device} | {row.Driver.Version} -> {row.Lookup!.Best!.Version} | {r.State}: {r.Message}");
        }
        return lines;
    }

    public void RebuildDriverRows()
    {
        var notes = AllDrivers.ToDictionary(d => d.Driver.DeviceId, d => d.UpdateNote, StringComparer.OrdinalIgnoreCase);
        AllDrivers = _drivers
            .Select(d => new DriverRowViewModel(d, Settings.OldDriverYears,
                _catalogResults.TryGetValue(d.DeviceId, out var l) && l.Driver.Version == d.Version ? l : null,
                NotifyCatalogSelection)
            {
                UpdateNote = notes.GetValueOrDefault(d.DeviceId)
            })
            .OrderByDescending(d => d.IsOld)
            .ThenByDescending(d => d.Age ?? -1)
            .ToList();
        OnPropertyChanged(nameof(AllDrivers));
        OnPropertyChanged(nameof(OldDriverCount));
        OnPropertyChanged(nameof(HeroSubtitle));
        NotifyCatalogSelection();
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
                if (vm.State is { } s && new InstallProgress(p.Id, s, "").IsFinal) return;
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
                    r.IsDone, r.RebootRequired));
            }

            if (results.Any(r => r.RebootRequired) || _windowsUpdate.IsRebootPending()) RestartRequired = true;

            int ok = results.Count(r => r.State == InstallState.Installed);
            int upToDate = results.Count(r => r.State == InstallState.UpToDate);
            int failed = results.Count(r => r.State == InstallState.Failed);
            int skipped = results.Count - ok - upToDate - failed;
            StatusText = $"Finished: {ok} installed, {upToDate} already up to date, {failed} failed, {skipped} skipped."
                         + (RestartRequired ? " Restart required." : "");
            ProgressValue = 100;

            var summary = string.Join("\n", results.Select(r =>
                $"{(r.IsDone ? "✔" : r.State == InstallState.Failed ? "✖" : "•")}  {byId.GetValueOrDefault(r.Id)?.Title ?? r.Id}\n      {r.Message}"));
            MessageBox.Show(summary + (RestartRequired ? "\n\nRestart your PC to finish installing." : ""),
                $"{ok + upToDate} of {results.Count} up to date", MessageBoxButton.OK,
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
