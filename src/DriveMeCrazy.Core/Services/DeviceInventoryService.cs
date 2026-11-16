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

                result.Add(new DriverInfo(
                    name.Trim(),
                    mo["DeviceClass"] as string,
                    mo["Manufacturer"] as string,
                    mo["DriverProviderName"] as string,
                    version,
                    ParseWmiDate(mo["DriverDate"] as string),
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

    /// <summary>WMI CIM_DATETIME, e.g. "20230709000000.******+***". Only the date part matters.</summary>
    public static DateTime? ParseWmiDate(string? value) =>
        value is { Length: >= 8 } &&
        DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
