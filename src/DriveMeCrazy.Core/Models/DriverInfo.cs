namespace DriveMeCrazy.Core.Models;

/// <summary>An installed device driver (from Win32_PnPSignedDriver).</summary>
public sealed record DriverInfo(
    string DeviceName,
    string? DeviceClass,
    string? Manufacturer,
    string? Provider,
    string Version,
    DateTime? Date,
    string? InfName,
    string? HardwareId,
    string DeviceId)
{
    /// <summary>
    /// Drivers that ship with Windows carry placeholder dates (e.g. 21 Jun 2006) and are serviced
    /// by normal Windows updates, so their age says nothing about being outdated.
    /// </summary>
    public bool IsInbox => string.Equals(Provider, "Microsoft", StringComparison.OrdinalIgnoreCase);

    public double? AgeYears(DateTime now) =>
        Date is { } d ? Math.Round((now - d).TotalDays / 365.25, 1) : null;

    public bool IsOld(DateTime now, int years) => !IsInbox && AgeYears(now) >= years;
}

/// <summary>Lookup from hardware/compatible IDs to device instance IDs, plus device names.</summary>
public sealed class DeviceIndex
{
    public DeviceIndex(
        IReadOnlyDictionary<string, IReadOnlyList<string>> devicesByHardwareId,
        IReadOnlyDictionary<string, string> namesByDeviceId)
    {
        DevicesByHardwareId = devicesByHardwareId;
        NamesByDeviceId = namesByDeviceId;
    }

    /// <summary>Case-insensitive.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DevicesByHardwareId { get; }

    /// <summary>Case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> NamesByDeviceId { get; }

    public static DeviceIndex Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
