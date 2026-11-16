using System.Globalization;
using System.Management;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>Reads the devices and drivers installed on this PC through WMI.</summary>
public sealed class DeviceInventoryService
{
    public IReadOnlyList<DriverInfo> GetDrivers()
    {
        var result = new List<DriverInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, DeviceID FROM Win32_PnPSignedDriver");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var name = mo["DeviceName"] as string;
                var version = mo["DriverVersion"] as string;
                var deviceId = mo["DeviceID"] as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || deviceId is null)
                    continue;

                // WMI's driver list can lag until a restart after a driver is replaced, so prefer
                // the live values from the PnP manager when they're available.
                var date = ParseWmiDate(mo["DriverDate"] as string);
                var liveVersion = DeviceProperties.GetDriverVersion(deviceId);
                if (liveVersion is not null && !string.Equals(liveVersion, version, StringComparison.OrdinalIgnoreCase))
                {
                    version = liveVersion;
                    date = DeviceProperties.GetDriverDate(deviceId) ?? date;
                }

                result.Add(new DriverInfo(
                    name.Trim(),
                    mo["DeviceClass"] as string,
                    mo["Manufacturer"] as string,
                    mo["DriverProviderName"] as string,
                    version,
                    date,
                    mo["InfName"] as string,
                    mo["HardWareID"] as string,
                    deviceId));
            }
        }
        return result;
    }

    public DeviceIndex GetDeviceIndex()
    {
        var byHwId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, HardwareID, CompatibleID FROM Win32_PnPEntity");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                if (mo["DeviceID"] is not string deviceId) continue;
                if (mo["Name"] is string name && !string.IsNullOrWhiteSpace(name)) names[deviceId] = name.Trim();

                foreach (var prop in new[] { "HardwareID", "CompatibleID" })
                {
                    if (mo[prop] is not string[] ids) continue;
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        if (!byHwId.TryGetValue(id, out var list)) byHwId[id] = list = new List<string>();
                        if (!list.Contains(deviceId, StringComparer.OrdinalIgnoreCase)) list.Add(deviceId);
                    }
                }
            }
        }

        return new DeviceIndex(
            byHwId.ToDictionary(k => k.Key, k => (IReadOnlyList<string>)k.Value, StringComparer.OrdinalIgnoreCase),
            names);
    }

    /// <summary>
    /// IDs a driver package may use to target this device, most specific first: all hardware IDs plus
    /// compatible IDs that still name the exact vendor and device (never generic class codes).
    /// </summary>
    public IReadOnlyList<string> GetHardwareIds(string deviceId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT HardwareID, CompatibleID FROM Win32_PnPEntity WHERE DeviceID = '{EscapeWql(deviceId)}'");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var ids = (mo["HardwareID"] as string[] ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
                ids.AddRange(SpecificCompatibleIds(mo["CompatibleID"] as string[] ?? []));
                return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        return [];
    }

    /// <summary>Keeps compatible IDs like PCI\VEN_10EC&amp;DEV_8125 but drops class-code / generic ones.</summary>
    internal static IEnumerable<string> SpecificCompatibleIds(IEnumerable<string> compatibleIds) =>
        compatibleIds.Where(c => !string.IsNullOrWhiteSpace(c)
                                 && c.Contains("DEV_", StringComparison.OrdinalIgnoreCase)
                                 && !c.Contains("CC_", StringComparison.OrdinalIgnoreCase)
                                 && !c.Contains("CLASS_", StringComparison.OrdinalIgnoreCase));

    /// <summary>The driver version currently bound to a device.</summary>
    public string? GetDriverVersion(string deviceId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID = '{EscapeWql(deviceId)}'");
        foreach (ManagementObject mo in searcher.Get())
            using (mo)
                return mo["DriverVersion"] as string;
        return null;
    }

    private static string EscapeWql(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>WMI CIM_DATETIME, e.g. "20230709000000.******+***". Only the date part matters.</summary>
    public static DateTime? ParseWmiDate(string? value) =>
        value is { Length: >= 8 } &&
        DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
