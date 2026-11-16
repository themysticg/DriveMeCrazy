using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>
/// Matches Windows Update driver offers to the devices in this PC and decides which ones are
/// worth installing. Pure logic, no I/O, so it is fully unit-tested.
/// </summary>
public static class UpdateMatcher
{
    public static void Apply(IEnumerable<UpdateItem> driverUpdates, IReadOnlyList<DriverInfo> drivers, DeviceIndex index)
    {
        var updates = driverUpdates.ToList();
        var driversByDeviceId = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drivers) driversByDeviceId.TryAdd(d.DeviceId, d);

        // 1. Which devices does each offer target, and what's installed on them now?
        foreach (var u in updates)
        {
            u.MatchedDevices.Clear();
            if (string.IsNullOrEmpty(u.HardwareId) ||
                !index.DevicesByHardwareId.TryGetValue(u.HardwareId, out var deviceIds))
                continue;

            DriverInfo? newestInstalled = null;
            foreach (var id in deviceIds)
            {
                string? name = null;
                if (driversByDeviceId.TryGetValue(id, out var drv))
                {
                    name = drv.DeviceName;
                    if (drv.Date is not null && (newestInstalled?.Date is null || drv.Date > newestInstalled.Date))
                        newestInstalled = drv;
                }
                else if (index.NamesByDeviceId.TryGetValue(id, out var entityName))
                {
                    name = entityName;
                }

                if (!string.IsNullOrWhiteSpace(name) &&
                    !u.MatchedDevices.Contains(name, StringComparer.OrdinalIgnoreCase))
                    u.MatchedDevices.Add(name);
            }

            if (newestInstalled is not null)
            {
                u.InstalledDate = newestInstalled.Date;
                u.InstalledVersion = newestInstalled.Version;
            }
        }

        // 2. When several versions are offered for the same hardware, only the newest is recommended.
        var newestPerHardwareId = updates
            .Where(u => !string.IsNullOrEmpty(u.HardwareId))
            .GroupBy(u => u.HardwareId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x, OfferComparer.Instance).First(),
                StringComparer.OrdinalIgnoreCase);

        // 3. Advice.
        foreach (var u in updates)
        {
            if (u.RequiresLicenseAcceptance)
                u.Advice = UpdateAdvice.NeedsLicense;
            else if (IsOlderThanInstalled(u))
                u.Advice = UpdateAdvice.OlderThanInstalled;
            else if (!string.IsNullOrEmpty(u.HardwareId) &&
                     !ReferenceEquals(newestPerHardwareId[u.HardwareId], u))
                u.Advice = UpdateAdvice.Superseded;
            else
                u.Advice = UpdateAdvice.Recommended;
        }
    }

    /// <summary>Advice for Windows (non-driver) updates.</summary>
    public static void ApplyWindows(IEnumerable<UpdateItem> windowsUpdates)
    {
        foreach (var u in windowsUpdates)
        {
            u.Advice = u.RequiresLicenseAcceptance ? UpdateAdvice.NeedsLicense
                : u.IsOptional ? UpdateAdvice.Optional
                : UpdateAdvice.Recommended;
        }
    }

    internal static bool IsOlderThanInstalled(UpdateItem u)
    {
        if (u.InstalledDate is null || u.NewDate is null) return false;
        if (u.NewDate.Value.Date < u.InstalledDate.Value.Date) return true;
        if (u.NewDate.Value.Date > u.InstalledDate.Value.Date) return false;
        // Same date: fall back to the version number.
        var newV = VersionText.Parse(u.NewVersion);
        var oldV = VersionText.Parse(u.InstalledVersion);
        return newV is not null && oldV is not null && newV < oldV;
    }

    private sealed class OfferComparer : IComparer<UpdateItem>
    {
        public static readonly OfferComparer Instance = new();

        public int Compare(UpdateItem? x, UpdateItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int c = Nullable.Compare(x.NewDate, y.NewDate);
            if (c != 0) return c;
            return Comparer<Version?>.Default.Compare(VersionText.Parse(x.NewVersion), VersionText.Parse(y.NewVersion));
        }
    }
}
