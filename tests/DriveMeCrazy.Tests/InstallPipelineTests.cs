using System.Management;
using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.Tests;

public class InstallPipelineTests
{
    // Real pnputil output captured when the Realtek 2.5GbE driver was installed.
    private const string RealOutput =
        "Microsoft PnP Utility\r\n\r\nAdding driver package:  rt25cx21x64.inf\r\nDriver package added successfully.\r\n" +
        "Published Name:         oem74.inf\r\nDriver package installed on device: PCI\\VEN_10EC&DEV_8125&SUBSYS_B0161F4C&REV_05\\4&2cd835bc&0&01000000684CE00000\r\n" +
        "\r\nTotal driver packages:  1\r\nAdded driver packages:  1";

    [Fact]
    public void Pnputil_install_on_device_is_recognised()
    {
        var r = PnpUtilResult.Interpret(0, RealOutput);
        Assert.True(r.InstalledOnDevice);
        Assert.False(r.RebootRequired);
    }

    [Fact]
    public void Pnputil_reboot_exit_code_is_recognised() =>
        Assert.True(PnpUtilResult.Interpret(3010, "Driver package added successfully.").RebootRequired);

    [Fact]
    public void Pnputil_added_but_not_installed_is_not_success() =>
        Assert.False(PnpUtilResult.Interpret(0, "Adding driver package: x.inf\r\nDriver package added successfully.\r\nPublished Name: oem9.inf").InstalledOnDevice);

    [Theory]
    [InlineData("1125.30.508.2026", "1125.30.508.2026", true)]
    [InlineData("3.6.0.1434", "3.6.0.01434", true)]
    [InlineData("1125.9.614.2022", "1125.30.508.2026", false)]
    public void Version_equality(string a, string b, bool equal) =>
        Assert.Equal(equal, DriverCatalogService.VersionsEqual(a, b));

    [Fact]
    public void Pnputil_259_means_windows_kept_the_current_driver()
    {
        // Real output for the AMD BT Audio package.
        const string output = "Adding driver package:  amdacpbt.inf\r\nDriver package added successfully.\r\nPublished Name: oem80.inf\r\n" +
                              "Driver package is up-to-date on device: ACP\\DEVTYPE_0004&DEVREV_0000&VEN_1022&DEV_15E2\\5&259fe658&0&05";
        var r = PnpUtilResult.Interpret(259, output);
        Assert.True(r.WindowsKeptCurrent);
        Assert.False(r.InstalledOnDevice);
    }

    [Fact]
    public void Declined_versions_and_older_are_not_offered_again()
    {
        var driver = new DriverInfo("AMD BT Audio Device", "MEDIA", "AMD", "AMD", "6.0.0.44", null, null, null, @"ACP\BT");
        CatalogEntry E(string v) => new(v, "t", "p", "Drivers", null, v, 0);
        var lookup = new CatalogLookup(driver, [], [E("7.2610.1.0"), E("7.2509.0.0"), E("8.0.0.1")]);

        var filtered = lookup.WithoutDeclined([CatalogLookup.DeclinedKey(@"ACP\BT", "7.2610.1.0"), CatalogLookup.DeclinedKey(@"OTHER\DEV", "9.0")]);
        Assert.Equal(["8.0.0.1"], filtered.Candidates.Select(c => c.Version));
        Assert.False(filtered.WindowsPrefersCurrent);

        var none = new CatalogLookup(driver, [], [E("7.2610.1.0")]).WithoutDeclined([CatalogLookup.DeclinedKey(@"ACP\BT", "7.2610.1.0")]);
        Assert.Null(none.Best);
        Assert.True(none.WindowsPrefersCurrent);
    }

    [Fact]
    public void Packages_not_made_for_the_device_are_dropped_exactly()
    {
        var driver = new DriverInfo("Realtek High Definition Audio", "MEDIA", "Realtek", "Realtek", "6.0.9614.1", null, null, null, @"HDAUDIO\RT");
        CatalogEntry E(string v) => new(v, "t", "p", "Drivers", null, v, 0);
        var lookup = new CatalogLookup(driver, [], [E("6.0.10029.1"), E("6.0.9700.1")]);

        var filtered = lookup.WithoutDeclined([], [CatalogLookup.DeclinedKey(@"HDAUDIO\RT", "6.0.10029.1")]);

        Assert.Equal(["6.0.9700.1"], filtered.Candidates.Select(c => c.Version));
        Assert.False(filtered.WindowsPrefersCurrent);
    }

    [Fact]
    public void Live_driver_date_is_read()
    {
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPSignedDriver WHERE DriverVersion IS NOT NULL AND DriverDate IS NOT NULL");
        var id = searcher.Get().Cast<ManagementObject>().Select(m => m["DeviceID"] as string).First(x => x is not null);
        Assert.NotNull(DeviceProperties.GetDriverDate(id!));
    }

    [Fact]
    public void Network_adapters_are_installed_last()
    {
        PreparedDriver Make(string cls) => new(
            new CatalogLookup(new DriverInfo(cls, cls, null, null, "1.0", null, null, null, cls), [], []),
            new CatalogEntry("x", "t", "p", "Drivers", null, "2.0", 0), "a.inf", "dir", null);

        var order = new[] { Make("NET"), Make("MEDIA"), Make("BLUETOOTH") }.OrderBy(p => p.IsNetworkAdapter).Select(p => p.Lookup.Driver.DeviceClass);

        Assert.Equal(["MEDIA", "BLUETOOTH", "NET"], order);
    }

    [Fact]
    public void Live_driver_version_matches_windows_for_a_real_device()
    {
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, DriverVersion FROM Win32_PnPSignedDriver WHERE DriverVersion IS NOT NULL");
        var first = searcher.Get().Cast<ManagementObject>()
            .Select(m => (Id: m["DeviceID"] as string, Version: m["DriverVersion"] as string))
            .First(x => x.Id is not null && x.Version is not null);

        Assert.Equal(first.Version, DeviceProperties.GetDriverVersion(first.Id!));
        Assert.Null(DeviceProperties.GetDriverVersion(@"PCI\VEN_FFFF&DEV_FFFF\DOES_NOT_EXIST"));
    }
}
