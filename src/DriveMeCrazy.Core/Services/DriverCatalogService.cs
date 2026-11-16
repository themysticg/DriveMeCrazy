using System.Diagnostics;
using System.Net.Http;
using System.Text;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>
/// Finds and installs newer drivers from the Microsoft Update Catalog (catalog.update.microsoft.com).
/// Safety rules: downloads only from Microsoft hosts, requires a valid Microsoft signature on the package,
/// requires the package's INF to list this device's hardware ID, and installs through Windows' pnputil,
/// which only switches drivers when Windows ranks the new one as a match.
/// </summary>
public sealed class DriverCatalogService : IDisposable
{
    private const string CatalogBase = "https://www.catalog.update.microsoft.com/";
    private static readonly string[] AllowedDownloadHosts = ["windowsupdate.com", "microsoft.com"];

    private readonly HttpClient _http;
    private readonly DeviceInventoryService _inventory;
    private readonly OsInfo _os;

    public DriverCatalogService(DeviceInventoryService inventory, OsInfo? os = null)
    {
        _inventory = inventory;
        _os = os ?? OsInfo.Current();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveMeCrazy/1.1 (+https://github.com/themysticg/DriveMeCrazy)");
    }

    public static string DownloadsDir => Path.Combine(AppPaths.DataDir, "downloads");

    /// <summary>Looks up newer catalog drivers for one installed driver.</summary>
    public async Task<CatalogLookup> FindAsync(DriverInfo driver, CancellationToken ct = default)
    {
        var hwIds = await Task.Run(() => _inventory.GetHardwareIds(driver.DeviceId), ct);
        var all = new List<CatalogEntry>();
        foreach (var term in CatalogPicker.SearchTerms(hwIds))
        {
            ct.ThrowIfCancellationRequested();
            var html = await _http.GetStringAsync(CatalogBase + "Search.aspx?q=" + Uri.EscapeDataString(term), ct);
            // Search every term (specific and general) so the newest package isn't missed; the INF check
            // during install still makes sure a package really targets this device.
            all.AddRange(CatalogParser.ParseSearch(html));
        }
        var picked = CatalogPicker.Pick(all, driver, _os);
        Log.Info($"Catalog: {driver.DeviceName} ({driver.Version}) -> {picked.Count} newer package(s)" +
                 (picked.Count > 0 ? $", best {picked[0].Summary}" : ""));
        return new CatalogLookup(driver, hwIds, picked);
    }

    /// <summary>
    /// Step 1 (needs internet): downloads the best package, checks its Microsoft signature, unpacks it and finds
    /// the INF that lists this device. Tries up to four candidates. Returns null plus a reason when none fits.
    /// </summary>
    public async Task<(PreparedDriver? Prepared, string? Problem)> PrepareAsync(
        CatalogLookup lookup, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        await PrepareAsync(lookup, progress, ct, notForDevice: null);

    /// <param name="notForDevice">Receives versions whose INF turned out not to list this device.</param>
    public async Task<(PreparedDriver? Prepared, string? Problem)> PrepareAsync(
        CatalogLookup lookup, IProgress<InstallProgress>? progress, CancellationToken ct, ICollection<string>? notForDevice)
    {
        string id = lookup.Driver.DeviceId;
        void Report(InstallState s, string m, double? pct = null) => progress?.Report(new InstallProgress(id, s, m, pct));

        string? lastProblem = null;
        foreach (var candidate in lookup.Candidates.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            string work = Path.Combine(DownloadsDir, $"{candidate.UpdateId}-{Guid.NewGuid():N}");
            bool keep = false;
            try
            {
                Report(InstallState.Downloading, $"Downloading {candidate.Version}…");
                var cab = await DownloadAsync(candidate, work, p => Report(InstallState.Downloading, $"Downloading {candidate.Version}… {p:0}%", p), ct);

                Report(InstallState.Downloading, "Checking Microsoft signature…");
                var (valid, signer) = Authenticode.Verify(cab);
                if (!valid || !Authenticode.IsMicrosoft(signer))
                {
                    lastProblem = $"Package {candidate.Version} failed the signature check";
                    Log.Error($"Catalog: signature check failed for {cab} (valid={valid}, signer={signer})");
                    continue;
                }

                string extracted = Path.Combine(work, "files");
                await ExtractAsync(cab, extracted, ct);

                var inf = FindMatchingInf(extracted, lookup.HardwareIds);
                if (inf is null)
                {
                    lastProblem = $"Package {candidate.Version} isn't made for this exact device";
                    Log.Info($"Catalog: {candidate.UpdateId} has no INF for {lookup.Driver.DeviceName}");
                    if (candidate.Version is not null) notForDevice?.Add(candidate.Version);
                    continue;
                }

                keep = true;
                Report(InstallState.Queued, $"Ready to install {candidate.Version}");
                return (new PreparedDriver(lookup, candidate, inf, work, signer), null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastProblem = ex is HttpRequestException ? $"Couldn't download: {ex.Message}" : ex.Message;
                Log.Error($"Catalog prepare of {candidate.UpdateId} failed", ex);
            }
            finally
            {
                if (!keep) TryDelete(work);
            }
        }
        return (null, lastProblem ?? "No suitable package found");
    }

    /// <summary>
    /// Step 2 (no internet needed): installs a prepared package with pnputil and confirms the result
    /// by reading the device's live driver version. Requires admin.
    /// </summary>
    public async Task<InstallProgress> InstallPreparedAsync(PreparedDriver prepared, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var lookup = prepared.Lookup;
        string id = lookup.Driver.DeviceId;
        var candidate = prepared.Candidate;
        try
        {
            // Another row for the same hardware may already have installed this package.
            var before = DeviceProperties.GetDriverVersion(id) ?? lookup.Driver.Version;
            if (VersionsEqual(before, candidate.Version))
                return new InstallProgress(id, InstallState.Installed, $"Updated to {before}", 100);

            progress?.Report(new InstallProgress(id, InstallState.Installing, $"Installing {candidate.Version}…"));
            var (exitCode, output) = await RunAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                $"/add-driver \"{prepared.InfPath}\" /install", ct);
            Log.Info($"pnputil exit {exitCode}: {output.Replace(Environment.NewLine, " | ")}");

            var result = PnpUtilResult.Interpret(exitCode, output);
            var after = DeviceProperties.GetDriverVersion(id);
            bool changed = after is not null && !VersionsEqual(after, lookup.Driver.Version);

            if (changed)
                return new InstallProgress(id, InstallState.Installed,
                    $"Updated to {after}" + (result.RebootRequired ? " – restart required" : ""), 100, result.RebootRequired);
            if (result.InstalledOnDevice || result.RebootRequired)
                return new InstallProgress(id, InstallState.Installed,
                    $"Installed {candidate.Version} – restart to finish", 100, true);
            if (result.WindowsKeptCurrent)
                return new InstallProgress(id, InstallState.Skipped,
                    $"Windows kept the current driver – it ranks it as a better match than {candidate.Version}");
            return new InstallProgress(id, InstallState.Failed, exitCode == 0
                ? $"Windows kept the current driver (it ranks it higher than {candidate.Version})"
                : $"pnputil couldn't install {candidate.Version} (exit code {exitCode})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"Catalog install of {candidate.UpdateId} failed", ex);
            return new InstallProgress(id, InstallState.Failed, ex.Message);
        }
        finally
        {
            TryDelete(prepared.WorkDir);
        }
    }

    /// <summary>Prepares and (unless <paramref name="dryRun"/>) installs one driver.</summary>
    public async Task<InstallProgress> InstallAsync(CatalogLookup lookup, IProgress<InstallProgress>? progress, CancellationToken ct, bool dryRun = false)
    {
        var (prepared, problem) = await PrepareAsync(lookup, progress, ct);
        if (prepared is null)
            return new InstallProgress(lookup.Driver.DeviceId, InstallState.Failed, problem ?? "No suitable package found");
        if (dryRun)
        {
            TryDelete(prepared.WorkDir);
            return new InstallProgress(lookup.Driver.DeviceId, InstallState.Skipped,
                $"Dry run OK: {prepared.Candidate.Version} signed by Microsoft ({prepared.Signer}), {Path.GetFileName(prepared.InfPath)} lists this device");
        }
        return await InstallPreparedAsync(prepared, progress, ct);
    }

    internal static bool VersionsEqual(string? a, string? b)
    {
        var va = VersionText.Parse(a);
        var vb = VersionText.Parse(b);
        return va is not null && vb is not null ? va == vb : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads with a few retries, so a brief network blip doesn't fail the whole update.</summary>
    private async Task<string> DownloadAsync(CatalogEntry entry, string work, Action<double> onPercent, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await DownloadOnceAsync(entry, work, onPercent, ct); }
            catch (HttpRequestException ex) when (attempt < 4)
            {
                Log.Info($"Catalog: download attempt {attempt} failed ({ex.Message}), retrying");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    private async Task<string> DownloadOnceAsync(CatalogEntry entry, string work, Action<double> onPercent, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["updateIDs"] = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{entry.UpdateId}\",\"updateID\":\"{entry.UpdateId}\"}}]",
        });
        var dialog = await (await _http.PostAsync(CatalogBase + "DownloadDialog.aspx", form, ct)).Content.ReadAsStringAsync(ct);
        var url = CatalogParser.ParseDownloadUrls(dialog).FirstOrDefault(u => u.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException("The catalog didn't return a download link");

        var uri = new Uri(url);
        if (uri.Scheme == Uri.UriSchemeHttp) uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (!IsAllowedHost(uri.Host))
            throw new InvalidOperationException($"Refusing to download from {uri.Host}");

        Directory.CreateDirectory(work);
        string path = Path.Combine(work, "package.cab");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        double last = -10;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
            {
                double pct = 100.0 * read / total.Value;
                if (pct - last >= 5) { last = pct; onPercent(pct); }
            }
        }
        return path;
    }

    internal static bool IsAllowedHost(string host) =>
        AllowedDownloadHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                      host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private static async Task ExtractAsync(string cab, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        var (code, output) = await RunAsync(Path.Combine(Environment.SystemDirectory, "expand.exe"), $"-F:* \"{cab}\" \"{dest}\"", ct);
        if (code != 0 || !Directory.EnumerateFiles(dest, "*.inf", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException($"Couldn't unpack the driver package (expand exit {code})");
    }

    /// <summary>Returns the INF that lists one of the device's hardware IDs for this CPU architecture.</summary>
    internal static string? FindMatchingInf(string folder, IReadOnlyList<string> hardwareIds)
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                      System.Runtime.InteropServices.Architecture.Arm64 ? "NTarm64" : "NTamd64";
        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(inf); } catch { continue; }
            if (InfMatches(text, hardwareIds, arch)) return inf;
        }
        return null;
    }

    /// <summary>
    /// True if the INF targets this architecture and lists one of the IDs as a whole ID
    /// ("PCI\VEN_10EC&amp;DEV_8125" must not match inside "PCI\VEN_10EC&amp;DEV_8125&amp;SUBSYS_1234").
    /// </summary>
    internal static bool InfMatches(string infText, IReadOnlyList<string> hardwareIds, string arch) =>
        infText.Contains(arch, StringComparison.OrdinalIgnoreCase) &&
        hardwareIds.Any(h => h.Length > 6 && !h.StartsWith('*') &&
                             System.Text.RegularExpressions.Regex.IsMatch(infText,
                                 System.Text.RegularExpressions.Regex.Escape(h) + @"(?![&\w])",
                                 System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await p.WaitForExitAsync(timeout.Token);
        return (p.ExitCode, (await stdout + await stderr).Trim());
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    public void Dispose() => _http.Dispose();
}
