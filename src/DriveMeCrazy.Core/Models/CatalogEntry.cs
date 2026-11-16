namespace DriveMeCrazy.Core.Models;

/// <summary>One row from a Microsoft Update Catalog search.</summary>
public sealed record CatalogEntry(
    string UpdateId,
    string Title,
    string Products,
    string Classification,
    DateTime? Date,
    string? Version,
    long SizeBytes)
{
    public string Summary => $"{Version ?? "?"} ({Date:yyyy-MM-dd})";
}

/// <summary>The Windows this app is running on, used to filter catalog results.</summary>
public sealed record OsInfo(bool IsWindows11, int? DisplayVersionCode, bool IsArm64)
{
    /// <summary>"24H2" -> 242, "25H2" -> 252, "21H1" -> 211.</summary>
    public static int? ParseDisplayVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length != 4 || char.ToUpperInvariant(text[2]) != 'H') return null;
        return int.TryParse(text[..2], out var yy) && (text[3] is '1' or '2') ? yy * 10 + (text[3] - '0') : null;
    }

    public static OsInfo Current()
    {
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        string? display = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string;
        }
        catch { }
        bool arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        return new OsInfo(win11, ParseDisplayVersion(display), arm);
    }
}

/// <summary>Result of looking up one installed driver in the catalog.</summary>
public sealed record CatalogLookup(DriverInfo Driver, IReadOnlyList<string> HardwareIds, IReadOnlyList<CatalogEntry> Candidates,
    bool WindowsPrefersCurrent = false)
{
    public CatalogEntry? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>
    /// Drops versions Windows already refused for this device (it ranked the installed driver higher), and any
    /// older versions too, since Windows would refuse those as well.
    /// </summary>
    public CatalogLookup WithoutDeclined(IEnumerable<string> declinedKeys, IEnumerable<string>? notForDeviceKeys = null)
    {
        string prefix = Driver.DeviceId + "|";
        var refused = declinedKeys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => Services.VersionText.Parse(k[prefix.Length..]))
            .Where(v => v is not null)
            .Max();
        // Versions whose package was downloaded and found not to list this device: drop exactly those.
        var mismatched = new HashSet<string>((notForDeviceKeys ?? [])
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..]), StringComparer.OrdinalIgnoreCase);
        if (refused is null && mismatched.Count == 0) return this;

        var kept = Candidates
            .Where(c => refused is null || (Services.VersionText.Parse(c.Version) is { } v && v > refused))
            .Where(c => c.Version is null || !mismatched.Contains(c.Version))
            .ToList();
        return kept.Count == Candidates.Count ? this
            : this with { Candidates = kept, WindowsPrefersCurrent = kept.Count == 0 && refused is not null };
    }

    public static string DeclinedKey(string deviceId, string? version) => $"{deviceId}|{version}";
}
