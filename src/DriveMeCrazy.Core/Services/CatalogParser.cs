using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>Parses Microsoft Update Catalog HTML. Pure functions, unit-tested against saved pages.</summary>
public static partial class CatalogParser
{
    [GeneratedRegex(@"id=['""](?<id>[0-9a-fA-F\-]{36})_link['""][^>]*>\s*(?<title>[^<]+?)\s*</a>", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*'(?<url>[^']+)'")]
    private static partial Regex DownloadUrlRegex();

    public static IReadOnlyList<CatalogEntry> ParseSearch(string html)
    {
        var list = new List<CatalogEntry>();
        foreach (Match m in RowRegex().Matches(html))
        {
            string id = m.Groups["id"].Value;
            string title = Clean(m.Groups["title"].Value);
            string products = Cell(html, id, 2);
            string classification = Cell(html, id, 3);
            DateTime? date = DateTime.TryParse(Cell(html, id, 4), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ? d.Date : null;
            string version = Cell(html, id, 5);
            long size = ParseSize(SizeSpan(html, id));
            list.Add(new CatalogEntry(id, title, products, classification, date,
                string.IsNullOrWhiteSpace(version) || version == "n/a" ? VersionText.FromTitle(title) : version, size));
        }
        return list;
    }

    public static IReadOnlyList<string> ParseDownloadUrls(string html) =>
        DownloadUrlRegex().Matches(html).Select(m => m.Groups["url"].Value).Distinct().ToList();

    private static string Cell(string html, string id, int column)
    {
        var m = Regex.Match(html, $@"id=['""]{Regex.Escape(id)}_C{column}_R\d+['""][^>]*>(?<v>.*?)</td>", RegexOptions.Singleline);
        return m.Success ? Clean(Regex.Replace(m.Groups["v"].Value, "<[^>]+>", " ")) : "";
    }

    private static string SizeSpan(string html, string id)
    {
        var m = Regex.Match(html, $@"id=['""]{Regex.Escape(id)}_size['""][^>]*>(?<v>[^<]*)<");
        return m.Success ? m.Groups["v"].Value : "";
    }

    private static string Clean(string s) => Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

    internal static long ParseSize(string text)
    {
        var m = Regex.Match(text, @"(?<n>[\d.,]+)\s*(?<u>KB|MB|GB|B)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        return m.Groups["u"].Value.ToUpperInvariant() switch
        {
            "GB" => (long)(n * 1024 * 1024 * 1024),
            "MB" => (long)(n * 1024 * 1024),
            "KB" => (long)(n * 1024),
            _ => (long)n,
        };
    }
}

/// <summary>Chooses the best catalog packages for an installed driver.</summary>
public static partial class CatalogPicker
{
    [GeneratedRegex(@"Windows 11[^,]*,\s*version (?<v>\d\d[Hh][12]) and later", RegexOptions.IgnoreCase)]
    private static partial Regex Win11MinVersionRegex();

    /// <summary>
    /// Keeps driver packages (not firmware) for this Windows that are strictly newer than the installed
    /// driver, newest first. The INF inside is checked against the device again before installing.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Pick(IEnumerable<CatalogEntry> entries, DriverInfo installed, OsInfo os)
    {
        var installedVersion = VersionText.Parse(installed.Version);
        return entries
            .Where(e => e.Classification.StartsWith("Drivers", StringComparison.OrdinalIgnoreCase))
            .Where(e => SupportsOs(e, os))
            .Where(e => IsNewer(e, installed, installedVersion))
            .GroupBy(e => e.UpdateId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(e => VersionText.Parse(e.Version))
            .ThenByDescending(e => e.Date)
            .ToList();
    }

    internal static bool IsNewer(CatalogEntry e, DriverInfo installed, Version? installedVersion)
    {
        var v = VersionText.Parse(e.Version);
        if (v is not null && installedVersion is not null)
        {
            if (v <= installedVersion) return false;
            // Same driver family should share the major version; a wildly different major usually means another product line.
            return v.Major == installedVersion.Major || (e.Date is { } d1 && installed.Date is { } d0 && d1 > d0);
        }
        return e.Date is { } d && installed.Date is { } i && d > i;
    }

    internal static bool SupportsOs(CatalogEntry e, OsInfo os)
    {
        string p = e.Products + " " + e.Title;
        bool arm = p.Contains("ARM64", StringComparison.OrdinalIgnoreCase);
        if (arm != os.IsArm64 && arm) return false;

        bool mentions10 = p.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);
        bool mentions11 = p.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
        if (!os.IsWindows11)
            return mentions10;                       // Windows 11-only packages won't run on Windows 10
        if (!mentions11) return mentions10;          // Windows 10 drivers generally work on 11

        // "Windows 11 Client, version 24H2 and later": skip if this PC is older than that.
        if (os.DisplayVersionCode is { } current)
        {
            var mins = Win11MinVersionRegex().Matches(p)
                .Select(m => OsInfo.ParseDisplayVersion(m.Groups["v"].Value.ToUpperInvariant()))
                .Where(v => v is not null).Select(v => v!.Value).ToList();
            if (mins.Count > 0 && mins.Min() > current) return mentions10;
        }
        return true;
    }

    /// <summary>
    /// Search terms to try for a device, most specific first. The catalog matches IDs without the bus prefix
    /// (e.g. "VEN_10EC&amp;DEV_8125&amp;SUBSYS_B0161F4C" or "AMDI0030").
    /// </summary>
    public static IReadOnlyList<string> SearchTerms(IEnumerable<string> hardwareIds)
    {
        var terms = new List<string>();
        foreach (var id in hardwareIds)
        {
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith('*')) continue;
            var term = id.Contains('\\') ? id[(id.IndexOf('\\') + 1)..] : id;
            term = Regex.Replace(term, @"&REV_[0-9A-F]+$", "", RegexOptions.IgnoreCase);
            if (term.Length < 6 || term.Contains("CC_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        }
        // Also try the plain vendor+device ID (catalog packages often list many subsystems).
        foreach (var t in terms.ToList())
        {
            var m = Regex.Match(t, @"^(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})", RegexOptions.IgnoreCase);
            if (m.Success && !terms.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) terms.Add(m.Value);
        }
        return terms.Take(4).ToList();
    }
}
