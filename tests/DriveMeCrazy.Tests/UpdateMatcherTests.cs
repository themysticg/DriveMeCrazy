using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.Tests;

/// <summary>Fixtures modelled on real Windows Update offers seen on an AMD Ryzen laptop.</summary>
public class UpdateMatcherTests
{
    private const string DrtmHwId = @"ACPI\AMDI0090";
    private const string ApoHwId = @"SWC\VID_1022&APO";
    private const string GpioHwId = @"ACPI\AMDI0030";
    private const string GpuHwId = @"PCI\VEN_1002&DEV_15BF";
    private const string SmbusHwId = @"pci\ven_1022&dev_790b";

    private static DriverInfo Driver(string name, string deviceId, string version, DateTime date, string provider = "Advanced Micro Devices, Inc") =>
        new(name, "SYSTEM", "AMD", provider, version, date, "oem1.inf", null, deviceId);

    private static UpdateItem Offer(string title, string hwId, DateTime date, bool eula = false) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Kind = UpdateKind.Driver,
        Title = title,
        HardwareId = hwId,
        NewDate = date,
        NewVersion = VersionText.FromTitle(title),
        RequiresLicenseAcceptance = eula,
    };

    private static DeviceIndex Index(params (string hwId, string deviceId, string name)[] entries)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in entries.GroupBy(e => e.hwId, StringComparer.OrdinalIgnoreCase))
            map[g.Key] = g.Select(e => e.deviceId).ToList();
        foreach (var e in entries) names[e.deviceId] = e.name;
        return new DeviceIndex(map, names);
    }

    [Fact]
    public void Newest_offer_per_hardware_is_recommended_and_older_duplicate_is_superseded()
    {
        var drivers = new[] { Driver("AMD DRTM Boot Driver", "ACPI\\AMDI0090\\0", "1.0.18.1", new(2023, 11, 15)) };
        var index = Index((DrtmHwId, "ACPI\\AMDI0090\\0", "AMD DRTM Boot Driver"));
        var older = Offer("Advanced Micro Devices, Inc System Driver Update (1.0.20.0)", DrtmHwId, new(2026, 8, 26));
        var newer = Offer("Advanced Micro Devices, Inc System Driver Update (1.0.20.1)", DrtmHwId, new(2026, 10, 16));

        UpdateMatcher.Apply([older, newer], drivers, index);

        Assert.Equal(UpdateAdvice.Superseded, older.Advice);
        Assert.Equal(UpdateAdvice.Recommended, newer.Advice);
        Assert.Equal(["AMD DRTM Boot Driver"], newer.MatchedDevices);
        Assert.Equal("1.0.18.1", newer.InstalledVersion);
        Assert.Equal(new DateTime(2023, 11, 15), newer.InstalledDate);
    }

    [Fact]
    public void Offer_older_than_installed_driver_is_not_recommended()
    {
        var drivers = new[] { Driver("AMD GPIO Controller", "ACPI\\AMDI0030\\0", "2.2.0.130", new(2021, 1, 1)) };
        var index = Index((GpioHwId, "ACPI\\AMDI0030\\0", "AMD GPIO Controller"));
        var offer = Offer("Advanced Micro Devices, Inc - System - 2.2.0.121", GpioHwId, new(2019, 9, 29));

        UpdateMatcher.Apply([offer], drivers, index);

        Assert.Equal(UpdateAdvice.OlderThanInstalled, offer.Advice);
        Assert.False(offer.IsRecommended);
    }

    [Fact]
    public void Same_date_falls_back_to_version_compare()
    {
        var drivers = new[] { Driver("Device", "DEV\\1", "5.0.0.10", new(2024, 5, 1)) };
        var index = Index(("HW\\1", "DEV\\1", "Device"));
        var offer = Offer("Vendor - System - 5.0.0.9", "HW\\1", new(2024, 5, 1));

        UpdateMatcher.Apply([offer], drivers, index);

        Assert.Equal(UpdateAdvice.OlderThanInstalled, offer.Advice);
    }

    [Fact]
    public void Device_without_driver_uses_its_PnP_name_and_is_recommended()
    {
        // The real GPU was on "Microsoft Basic Display Adapter" with no vendor driver entry.
        var index = Index((GpuHwId, "PCI\\VEN_1002&DEV_15BF\\4", "Microsoft Basic Display Adapter"));
        var offer = Offer("Advanced Micro Devices, Inc. Driver Update (32.0.21030.2001)", GpuHwId, new(2025, 9, 25));

        UpdateMatcher.Apply([offer], [], index);

        Assert.Equal(UpdateAdvice.Recommended, offer.Advice);
        Assert.Equal(["Microsoft Basic Display Adapter"], offer.MatchedDevices);
        Assert.Null(offer.InstalledDate);
    }

    [Fact]
    public void Hardware_id_match_is_case_insensitive()
    {
        var index = Index((SmbusHwId.ToUpperInvariant(), "PCI\\SMBUS\\0", "AMD SMBus"));
        var offer = Offer("Advanced Micro Devices, Inc - System - 8/30/2017 12:00:00 AM - 5.12.0.38", SmbusHwId, new(2017, 8, 30));

        UpdateMatcher.Apply([offer], [], index);

        Assert.Equal(["AMD SMBus"], offer.MatchedDevices);
    }

    [Fact]
    public void Offer_with_licence_terms_is_never_preselected()
    {
        var offer = Offer("Microsoft Corporation AudioProcessingObject Driver Update (1.0.4.55794)", ApoHwId, new(2026, 10, 16), eula: true);

        UpdateMatcher.Apply([offer], [], DeviceIndex.Empty);

        Assert.Equal(UpdateAdvice.NeedsLicense, offer.Advice);
    }

    [Fact]
    public void Real_world_list_preselects_six_of_nine()
    {
        var index = Index(
            (SmbusHwId, "PCI\\SMBUS\\0", "AMD SMBus"),
            (GpioHwId, "ACPI\\AMDI0030\\0", "AMD GPIO Controller"),
            (GpuHwId, "PCI\\GPU\\0", "Microsoft Basic Display Adapter"),
            (ApoHwId, "SWC\\APO\\0", "Generic software component"),
            (DrtmHwId, "ACPI\\AMDI0090\\0", "AMD DRTM Boot Driver"),
            (@"PCI\VEN_1022&DEV_1502", "PCI\\IPU\\0", "AMD IPU Device"));
        var drivers = new[]
        {
            Driver("AMD GPIO Controller", "ACPI\\AMDI0030\\0", "2.2.0.71", new(2017, 5, 17)),
            Driver("AMD IPU Device", "PCI\\IPU\\0", "10.1.0.1", new(2023, 12, 14)),
            Driver("AMD DRTM Boot Driver", "ACPI\\AMDI0090\\0", "1.0.18.1", new(2023, 11, 15)),
        };
        UpdateItem[] offers =
        [
            Offer("Advanced Micro Devices, Inc - System - 8/30/2017 12:00:00 AM - 5.12.0.38", SmbusHwId, new(2017, 8, 30)),
            Offer("Advanced Micro Devices, Inc driver update for AMD SMBus", SmbusHwId, new(2015, 1, 18)),
            Offer("Advanced Micro Devices, Inc - System - 2.2.0.121", GpioHwId, new(2019, 9, 29)),
            Offer("AMD - ComputeAccelerator - 32.0.203.240", @"PCI\VEN_1022&DEV_1502", new(2024, 12, 3)),
            Offer("Advanced Micro Devices, Inc. Driver Update (32.0.21030.2001)", GpuHwId, new(2025, 9, 25)),
            Offer("Microsoft Corporation AudioProcessingObject Driver Update (1.0.4.7057)", ApoHwId, new(2026, 6, 16)),
            Offer("Advanced Micro Devices, Inc System Driver Update (1.0.20.0)", DrtmHwId, new(2026, 8, 26)),
            Offer("Microsoft Corporation AudioProcessingObject Driver Update (1.0.4.55794)", ApoHwId, new(2026, 10, 16)),
            Offer("Advanced Micro Devices, Inc System Driver Update (1.0.20.1)", DrtmHwId, new(2026, 10, 16)),
        ];

        UpdateMatcher.Apply(offers, drivers, index);

        Assert.Equal(6, offers.Count(o => o.IsRecommended));
        Assert.Equal(3, offers.Count(o => o.Advice == UpdateAdvice.Superseded));
    }

    [Fact]
    public void Windows_updates_respect_optional_and_licence_flags()
    {
        var important = new UpdateItem { Id = "1", Kind = UpdateKind.Windows, Title = "Cumulative Update" };
        var optional = new UpdateItem { Id = "2", Kind = UpdateKind.Windows, Title = "Preview", IsOptional = true };
        var feature = new UpdateItem { Id = "3", Kind = UpdateKind.Windows, Title = "Feature update", RequiresLicenseAcceptance = true };

        UpdateMatcher.ApplyWindows([important, optional, feature]);

        Assert.Equal(UpdateAdvice.Recommended, important.Advice);
        Assert.Equal(UpdateAdvice.Optional, optional.Advice);
        Assert.Equal(UpdateAdvice.NeedsLicense, feature.Advice);
    }
}

public class DriverInfoTests
{
    private static readonly DateTime Now = new(2026, 11, 16);

    [Fact]
    public void Microsoft_inbox_drivers_are_never_old()
    {
        var d = new DriverInfo("AMD Processor", "PROCESSOR", "AMD", "Microsoft", "10.0.26100.1", new(2009, 4, 21), null, null, "X");
        Assert.True(d.IsInbox);
        Assert.False(d.IsOld(Now, 3));
    }

    [Theory]
    [InlineData(2017, 5, 17, 3, true)]
    [InlineData(2023, 11, 8, 3, true)]
    [InlineData(2024, 6, 1, 3, false)]
    [InlineData(2024, 6, 1, 1, true)]
    public void Age_threshold(int y, int m, int d, int years, bool expectedOld)
    {
        var drv = new DriverInfo("Dev", "NET", "Realtek", "Realtek", "1.0", new(y, m, d), null, null, "X");
        Assert.Equal(expectedOld, drv.IsOld(Now, years));
    }

    [Fact]
    public void Unknown_date_is_not_old()
    {
        var drv = new DriverInfo("Dev", "NET", "V", "V", "1.0", null, null, null, "X");
        Assert.False(drv.IsOld(Now, 1));
    }
}

public class VersionTextTests
{
    [Theory]
    [InlineData("Advanced Micro Devices, Inc. Driver Update (32.0.21030.2001)", "32.0.21030.2001")]
    [InlineData("AMD - ComputeAccelerator - 32.0.203.240", "32.0.203.240")]
    [InlineData("Advanced Micro Devices, Inc - System - 8/30/2017 12:00:00 AM - 5.12.0.38", "5.12.0.38")]
    [InlineData("Advanced Micro Devices, Inc driver update for AMD SMBus", null)]
    public void Extracts_version_from_title(string title, string? expected) =>
        Assert.Equal(expected, VersionText.FromTitle(title));

    [Fact]
    public void Parses_and_compares_versions() =>
        Assert.True(VersionText.Parse("1.0.20.1") > VersionText.Parse("1.0.20.0"));

    [Theory]
    [InlineData("20230709000000.******+***", 2023, 7, 9)]
    [InlineData("20060621000000.000000-000", 2006, 6, 21)]
    public void Parses_wmi_dates(string wmi, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), DeviceInventoryService.ParseWmiDate(wmi));

    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "500 B")]
    [InlineData(759_800_000, "724.6 MB")]
    public void Formats_sizes(long bytes, string expected) =>
        Assert.Equal(expected, VersionText.FormatBytes(bytes));
}
