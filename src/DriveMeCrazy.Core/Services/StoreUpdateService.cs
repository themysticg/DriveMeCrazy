using DriveMeCrazy.Core.Models;
using Windows.ApplicationModel.Store.Preview.InstallControl;
using Windows.Management.Deployment;

namespace DriveMeCrazy.Core.Services;

/// <summary>Updates for Microsoft Store apps, via the Store's own install manager.</summary>
public sealed class StoreUpdateService
{
    /// <summary>Lists available Store app updates without starting them.</summary>
    public async Task<IReadOnlyList<UpdateItem>> SearchAsync(CancellationToken ct = default)
    {
        Log.Info("Store: searching for app updates");
        var manager = new AppInstallManager();
        var options = new AppUpdateOptions
        {
            AutomaticallyDownloadAndInstallUpdateIfFound = false,
            AllowForcedAppRestart = false,
        };
        var found = await manager.SearchForAllUpdatesAsync(string.Empty, string.Empty, options).AsTask(ct);

        var packages = new PackageManager();
        var list = new List<UpdateItem>();
        foreach (var item in found)
        {
            string name = item.PackageFamilyName;
            string? installedVersion = null;
            try
            {
                var pkg = packages.FindPackagesForUser(string.Empty, item.PackageFamilyName).FirstOrDefault();
                if (pkg is not null)
                {
                    if (!string.IsNullOrWhiteSpace(pkg.DisplayName)) name = pkg.DisplayName;
                    var v = pkg.Id.Version;
                    installedVersion = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
            }
            catch (Exception ex) { Log.Error($"Store: could not read package {item.PackageFamilyName}", ex); }

            list.Add(new UpdateItem
            {
                Id = item.ProductId,
                Kind = UpdateKind.StoreApp,
                Title = name,
                PackageFamilyName = item.PackageFamilyName,
                InstalledVersion = installedVersion,
                Advice = UpdateAdvice.Recommended,
            });
        }
        Log.Info($"Store: {list.Count} app update(s) found");
        return list;
    }

    /// <summary>Updates the given Store apps one by one and waits for each to finish.</summary>
    public async Task<IReadOnlyList<InstallProgress>> InstallAsync(
        IReadOnlyList<UpdateItem> items, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var results = new List<InstallProgress>();
        void Final(InstallProgress p) { results.Add(p); progress?.Report(p); Log.Info($"Store install {p.Id}: {p.State} {p.Message}"); }

        var manager = new AppInstallManager();
        foreach (var it in items) progress?.Report(new InstallProgress(it.Id, InstallState.Queued, "Waiting..."));

        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) { Final(new(it.Id, InstallState.Cancelled, "Cancelled")); continue; }
            try
            {
                progress?.Report(new InstallProgress(it.Id, InstallState.Downloading, "Starting..."));
                var options = new AppUpdateOptions
                {
                    AutomaticallyDownloadAndInstallUpdateIfFound = true,
                    AllowForcedAppRestart = false,
                };
                var job = await manager.SearchForUpdatesAsync(it.Id, string.Empty, string.Empty, string.Empty, options).AsTask(ct);
                if (job is null)
                {
                    Final(new(it.Id, InstallState.UpToDate, "Already up to date"));
                    continue;
                }
                Final(await WaitAsync(job, it.Id, progress, ct));
            }
            catch (OperationCanceledException)
            {
                Final(new(it.Id, InstallState.Cancelled, "Cancelled"));
            }
            catch (Exception ex)
            {
                Final(new(it.Id, InstallState.Failed, ex.Message));
            }
        }
        return results;
    }

    private static async Task<InstallProgress> WaitAsync(AppInstallItem job, string id, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var stalledSince = DateTime.UtcNow;
        double lastPercent = -1;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                try { job.Cancel(); } catch { }
                return new(id, InstallState.Cancelled, "Cancelled");
            }

            var status = job.GetCurrentStatus();
            switch (status.InstallState)
            {
                case AppInstallState.Completed:
                    return new(id, InstallState.Installed, "Updated", 100);
                case AppInstallState.Error:
                    return new(id, InstallState.Failed, $"Store error 0x{status.ErrorCode.HResult:X8}");
                case AppInstallState.Canceled:
                    return new(id, InstallState.Cancelled, "Cancelled by the Store");
                case AppInstallState.Paused:
                case AppInstallState.PausedLowBattery:
                case AppInstallState.PausedWiFiRecommended:
                case AppInstallState.PausedWiFiRequired:
                case AppInstallState.ReadyToDownload:
                    if (DateTime.UtcNow - stalledSince > TimeSpan.FromSeconds(45))
                        return new(id, InstallState.Skipped, $"Queued in Microsoft Store ({status.InstallState})");
                    break;
                default:
                    stalledSince = DateTime.UtcNow;
                    break;
            }

            double pct = Math.Clamp(status.PercentComplete, 0, 100);
            if (Math.Abs(pct - lastPercent) >= 1)
            {
                lastPercent = pct;
                var state = status.InstallState == AppInstallState.Installing ? InstallState.Installing : InstallState.Downloading;
                progress?.Report(new InstallProgress(id, state, $"{status.InstallState} {pct:0}%", pct));
            }
            await Task.Delay(500, ct).ContinueWith(_ => { });
        }
    }
}
