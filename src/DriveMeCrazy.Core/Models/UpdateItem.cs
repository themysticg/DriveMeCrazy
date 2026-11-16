namespace DriveMeCrazy.Core.Models;

public enum UpdateKind { Driver, Firmware, Windows, StoreApp }

public enum UpdateAdvice
{
    /// <summary>Safe and useful: pre-selected.</summary>
    Recommended,
    /// <summary>Offered but not flagged as important by Microsoft.</summary>
    Optional,
    /// <summary>The offered driver is older than the one already installed.</summary>
    OlderThanInstalled,
    /// <summary>A newer offer for the same hardware is also in the list.</summary>
    Superseded,
    /// <summary>Has licence terms; we never accept terms on the user's behalf.</summary>
    NeedsLicense,
}

/// <summary>One installable update from Windows Update or the Microsoft Store.</summary>
public sealed class UpdateItem
{
    public required string Id { get; init; }
    public required UpdateKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }

    // Driver / firmware details
    public string? HardwareId { get; init; }
    public string? Manufacturer { get; init; }
    public string? DeviceClass { get; init; }

    public string? NewVersion { get; init; }
    public DateTime? NewDate { get; init; }
    public long SizeBytes { get; init; }
    public bool RequiresLicenseAcceptance { get; init; }
    public bool IsOptional { get; init; }
    public string? KbArticle { get; init; }
    public string? SupportUrl { get; init; }
    public string? PackageFamilyName { get; init; }

    // Filled in by UpdateMatcher / StoreUpdateService
    public List<string> MatchedDevices { get; } = new();
    public string? InstalledVersion { get; set; }
    public DateTime? InstalledDate { get; set; }
    public UpdateAdvice Advice { get; set; } = UpdateAdvice.Recommended;

    public bool IsRecommended => Advice == UpdateAdvice.Recommended;

    public override string ToString() => $"{Kind}: {Title} ({Advice})";
}
