using System.Runtime.InteropServices;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>
/// Drivers, firmware and Windows updates through the Windows Update Agent API
/// (the same engine and Microsoft-signed catalog that Settings &gt; Windows Update uses).
/// </summary>
public sealed class WindowsUpdateService
{
    private const string Criteria = "IsInstalled=0 and IsHidden=0";
    private const int ServerSelectionWindowsUpdate = 2;
    private const string ClientId = "DriveMeCrazy";

    public Task<IReadOnlyList<UpdateItem>> SearchAsync() => Sta.Run<IReadOnlyList<UpdateItem>>(() =>
    {
        Log.Info("Windows Update: searching online");
        dynamic session = CreateSession();
        dynamic result = Search(session);
        dynamic updates = result.Updates;
        int count = updates.Count;
        var list = new List<UpdateItem>(count);
        for (int i = 0; i < count; i++)
        {
            try { list.Add(Map(updates[i])); }
            catch (Exception ex) { Log.Error("Windows Update: could not read an update", ex); }
        }
        Log.Info($"Windows Update: {list.Count} update(s) found");
        return list;
    }, "DriveMeCrazy WU search");

    /// <summary>
    /// Downloads and installs the given updates one at a time. Returns one final result per id.
    /// Requires administrator rights.
    /// </summary>
    public Task<IReadOnlyList<InstallProgress>> InstallAsync(
        IReadOnlyCollection<string> updateIds, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        Sta.Run<IReadOnlyList<InstallProgress>>(() =>
        {
            var results = new List<InstallProgress>();
            void Final(InstallProgress p) { results.Add(p); progress?.Report(p); Log.Info($"Install {p.Id}: {p.State} {p.Message}"); }

            dynamic session = CreateSession();
            foreach (var id in updateIds) progress?.Report(new InstallProgress(id, InstallState.Queued, "Waiting..."));

            dynamic searchResult = Search(session);
            dynamic all = searchResult.Updates;
            var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
            var found = new List<dynamic>();
            int count = all.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = all[i];
                if (wanted.Contains((string)u.Identity.UpdateID)) found.Add(u);
            }
            var foundIds = new HashSet<string>(found.Select(u => (string)u.Identity.UpdateID), StringComparer.OrdinalIgnoreCase);
            foreach (var missing in updateIds.Where(id => !foundIds.Contains(id)))
                Final(new InstallProgress(missing, InstallState.UpToDate, "No longer offered by Windows Update (already installed)"));

            foreach (dynamic u in found)
            {
                string id = u.Identity.UpdateID;
                if (ct.IsCancellationRequested)
                {
                    Final(new InstallProgress(id, InstallState.Cancelled, "Cancelled"));
                    continue;
                }
                if (!(bool)u.EulaAccepted)
                {
                    Final(new InstallProgress(id, InstallState.Skipped,
                        "Has licence terms - install it from Settings > Windows Update"));
                    continue;
                }

                try
                {
                    dynamic coll = CreateCom("Microsoft.Update.UpdateColl");
                    coll.Add(u);

                    if (!(bool)u.IsDownloaded)
                    {
                        progress?.Report(new InstallProgress(id, InstallState.Downloading, "Downloading..."));
                        dynamic downloader = session.CreateUpdateDownloader();
                        downloader.Updates = coll;
                        dynamic dr = downloader.Download();
                        int dcode = (int)dr.ResultCode;
                        if (dcode is not (2 or 3))
                        {
                            Final(new InstallProgress(id, InstallState.Failed, "Download failed: " + Describe((int)dr.HResult)));
                            continue;
                        }
                    }

                    progress?.Report(new InstallProgress(id, InstallState.Installing, "Installing..."));
                    dynamic installer = session.CreateUpdateInstaller();
                    installer.Updates = coll;
                    try { installer.AllowSourcePrompts = false; } catch { /* not supported everywhere */ }
                    dynamic ir = installer.Install();
                    dynamic ur = ir.GetUpdateResult(0);
                    int code = (int)ur.ResultCode;
                    bool reboot = (bool)ur.RebootRequired;
                    int hr = (int)ur.HResult;

                    if (code is 2 or 3)
                        Final(new InstallProgress(id, InstallState.Installed,
                            reboot ? "Installed - restart required" : "Installed", 100, reboot));
                    else
                        Final(new InstallProgress(id, InstallState.Failed, "Install failed: " + Describe(hr)));
                }
                catch (COMException ex)
                {
                    Final(new InstallProgress(id, InstallState.Failed, Describe(ex.HResult)));
                }
                catch (Exception ex)
                {
                    Final(new InstallProgress(id, InstallState.Failed, ex.Message));
                }
            }
            return results;
        }, "DriveMeCrazy WU install");

    /// <summary>True if Windows says a restart is pending to finish earlier installs.</summary>
    public bool IsRebootPending()
    {
        try
        {
            dynamic info = CreateCom("Microsoft.Update.SystemInfo");
            return (bool)info.RebootRequired;
        }
        catch { return false; }
    }

    private static dynamic Search(dynamic session)
    {
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerSelectionWindowsUpdate; // ask Microsoft online, not a cache
        searcher.Online = true;
        try { return searcher.Search(Criteria); }
        catch (COMException ex) { throw new InvalidOperationException(Describe(ex.HResult), ex); }
    }

    private static dynamic CreateSession()
    {
        dynamic session = CreateCom("Microsoft.Update.Session");
        try { session.ClientApplicationID = ClientId; } catch { }
        return session;
    }

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    private static UpdateItem Map(dynamic u)
    {
        int type = (int)u.Type; // 1 = software, 2 = driver
        string title = u.Title;
        string id = u.Identity.UpdateID;
        long size = SafeLong(() => u.MaxDownloadSize);
        bool eula = !(bool)u.EulaAccepted;
        string? description = Safe(() => (string?)u.Description);
        string? supportUrl = Safe(() => (string?)u.SupportUrl);

        if (type == 2)
        {
            string? cls = Safe(() => (string?)u.DriverClass);
            DateTime? date = Safe<DateTime?>(() => (DateTime)u.DriverVerDate);
            return new UpdateItem
            {
                Id = id,
                Kind = string.Equals(cls, "Firmware", StringComparison.OrdinalIgnoreCase) ? UpdateKind.Firmware : UpdateKind.Driver,
                Title = title,
                Description = description,
                SupportUrl = supportUrl,
                HardwareId = Safe(() => (string?)u.DriverHardwareID),
                Manufacturer = Safe(() => (string?)u.DriverManufacturer),
                DeviceClass = cls,
                NewVersion = VersionText.FromTitle(title),
                NewDate = date?.Date,
                SizeBytes = size,
                RequiresLicenseAcceptance = eula,
            };
        }

        string? kb = Safe(() =>
        {
            dynamic kbs = u.KBArticleIDs;
            return (int)kbs.Count > 0 ? "KB" + (string)kbs[0] : null;
        });
        bool autoSelect = Safe(() => (bool)u.AutoSelectOnWebSites);
        DateTime? changed = Safe<DateTime?>(() => (DateTime)u.LastDeploymentChangeTime);
        return new UpdateItem
        {
            Id = id,
            Kind = UpdateKind.Windows,
            Title = title,
            Description = description,
            SupportUrl = supportUrl,
            KbArticle = kb,
            NewDate = changed?.Date,
            SizeBytes = size,
            RequiresLicenseAcceptance = eula,
            IsOptional = !autoSelect,
        };
    }

    private static T? Safe<T>(Func<T?> f)
    {
        try { return f(); } catch { return default; }
    }

    private static long SafeLong(Func<object?> f)
    {
        try { return Convert.ToInt64(f() ?? 0); } catch { return 0; }
    }

    /// <summary>Human-readable text for common Windows Update error codes.</summary>
    public static string Describe(int hresult)
    {
        uint hr = unchecked((uint)hresult);
        string text = hr switch
        {
            0 => "OK",
            0x8024402C or 0x80072EE7 or 0x80072EFD or 0x8024401C or 0x80244022
                => "Can't reach Windows Update. Check your internet connection.",
            0x80240044 => "Administrator rights are required.",
            0x8024001E => "The operation was cancelled because Windows is shutting down.",
            0x80240016 => "Another install is already in progress. Try again in a few minutes.",
            0x80070422 => "The Windows Update service is disabled.",
            0x80240020 => "Installation needs a signed-in user.",
            0x80070005 => "Access denied.",
            0x8024200B or 0x80242006 => "The update could not be installed by Windows (installer error).",
            0x80246007 => "The update hasn't been downloaded yet.",
            0x8024000E or 0x80240022 => "The update metadata or package is invalid.",
            _ => "Windows Update error",
        };
        return $"{text} (0x{hr:X8})";
    }
}
