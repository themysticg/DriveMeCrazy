using System.Text.RegularExpressions;

namespace DriveMeCrazy.Core.Services;

public static partial class VersionText
{
    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionRegex();

    /// <summary>Parses "32.0.21030.2001" (or the first version found in a string) into a Version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = VersionRegex().Match(text);
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>
    /// Windows Update driver titles carry the version at the end, e.g.
    /// "AMD - ComputeAccelerator - 32.0.203.240" or "... Driver Update (32.0.21030.2001)".
    /// Returns the last dotted number with at least three parts.
    /// </summary>
    public static string? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string? last = null;
        foreach (Match m in VersionRegex().Matches(title))
            if (m.Value.Count(c => c == '.') >= 2) last = m.Value;
        return last;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }
}
