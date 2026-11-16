using DriveMeCrazy.Core.Models;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.Tests;

public class CatalogTests
{
    private static string Row(string id, string title, string products, string cls, string date, string version, string size) => $"""
        <tr id="{id}_R0">
          <td class="resultsbottomBorder resultspadding" id="{id}_C1_R0">
            <a id='{id}_link' href= "javascript:void(0);" onclick='goToDetails("{id}");' class="contentTextItemSpacerNoBreakLink">
                {title}
            </a>
          </td><td class="resultsbottomBorder resultspadding" id="{id}_C2_R0">
                {products}
          </td><td class="resultsbottomBorder resultspadding" id="{id}_C3_R0">
                {cls}
          </td><td class="resultsbottomBorder resultspadding " id="{id}_C4_R0">
                {date}
          </td><td class="resultsbottomBorder resultspadding" id="{id}_C5_R0">
                {version}
          </td><td class="resultsbottomBorder resultspadding resultsSizeWidth" id="{id}_C6_R0">
               <span id="{id}_size">{size}</span>
               <span class="noDisplay" id="{id}_originalSize">328000</span>
          </td>
        </tr>
        """;

    private const string Id1 = "a6317cda-a850-45f2-a302-a2ded3a980bc";
    private const string Id2 = "b6317cda-a850-45f2-a302-a2ded3a980bc";
    private const string Id3 = "c6317cda-a850-45f2-a302-a2ded3a980bc";
    private const string Id4 = "d6317cda-a850-45f2-a302-a2ded3a980bc";

    private static readonly string Page =
        "<table>" +
        Row(Id1, "Realtek Net Driver Update (1125.30.50.508)",
            "Windows 11 Client,  version 24H2 and later,  Servicing Drivers, Windows 11 Client,  version 24H2 and later,  Upgrade &amp; Servicing Drivers",
            "Drivers (Networking)", "5/7/2026", "1125.30.50.508", "320 KB") +
        Row(Id2, "Realtek - Net - 1125.9.614.2022", "Windows 10 and later drivers", "Drivers (Networking)", "6/14/2022", "1125.9.614.2022", "300 KB") +
        Row(Id3, "Realtek Net Driver Update (1125.40.0.1)", "Windows 11 Client, version 27H1 and later, Servicing Drivers",
            "Drivers (Networking)", "10/1/2026", "1125.40.0.1", "330 KB") +
        Row(Id4, "Realtek Firmware Update (2.0.0.1)", "Windows 11 Client, version 24H2 and later, Servicing Drivers",
            "Firmware", "9/1/2026", "2.0.0.1", "1.5 MB") +
        "</table>";

    private static readonly DriverInfo Realtek = new("Realtek Gaming 2.5GbE Family Controller", "NET", "Realtek", "Realtek",
        "1125.9.614.2022", new(2022, 6, 14), "oem5.inf", @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C&REV_05", "PCI\\X");

    private static readonly OsInfo Win11_25H2 = new(true, 252, false);

    [Fact]
    public void Parses_catalog_search_rows()
    {
        var rows = CatalogParser.ParseSearch(Page);

        Assert.Equal(4, rows.Count);
        var r = rows[0];
        Assert.Equal(Id1, r.UpdateId);
        Assert.Equal("Realtek Net Driver Update (1125.30.50.508)", r.Title);
        Assert.Equal("Drivers (Networking)", r.Classification);
        Assert.Equal(new DateTime(2026, 5, 7), r.Date);
        Assert.Equal("1125.30.50.508", r.Version);
        Assert.Equal(320 * 1024, r.SizeBytes);
        Assert.Contains("Upgrade & Servicing Drivers", r.Products);
    }

    [Fact]
    public void Picks_newer_drivers_for_this_windows_and_skips_firmware_and_older()
    {
        var picked = CatalogPicker.Pick(CatalogParser.ParseSearch(Page), Realtek, Win11_25H2);

        // Id2 = same as installed, Id3 = needs Windows 11 27H1, Id4 = firmware.
        Assert.Equal([Id1], picked.Select(p => p.UpdateId));
    }

    [Fact]
    public void Future_windows_package_is_allowed_once_windows_is_new_enough()
    {
        var picked = CatalogPicker.Pick(CatalogParser.ParseSearch(Page), Realtek, new OsInfo(true, 271, false));

        Assert.Equal([Id3, Id1], picked.Select(p => p.UpdateId));
    }

    [Fact]
    public void Windows_10_pc_ignores_windows_11_only_packages()
    {
        var picked = CatalogPicker.Pick(CatalogParser.ParseSearch(Page), Realtek, new OsInfo(false, 222, false));

        Assert.Empty(picked);
    }

    [Fact]
    public void Different_major_version_with_older_date_is_not_offered()
    {
        var entries = new[] { new CatalogEntry("x", "Other line", "Windows 10 and later drivers", "Drivers (Networking)", new(2021, 1, 1), "2000.1.0.0", 0) };

        Assert.Empty(CatalogPicker.Pick(entries, Realtek, Win11_25H2));
    }

    [Fact]
    public void Parses_download_dialog_urls()
    {
        const string html = "downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/10/abc.cab';";
        Assert.Equal(["https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/10/abc.cab"],
            CatalogParser.ParseDownloadUrls(html));
    }

    [Fact]
    public void Search_terms_go_from_specific_to_general_and_drop_rev_and_class_codes()
    {
        var terms = CatalogPicker.SearchTerms([
            @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C&REV_05",
            @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C",
            @"PCI\VEN_10EC&DEV_8125&CC_020000",
        ]);

        Assert.Equal(["VEN_10EC&DEV_8125&SUBSYS_B0161F4C", "VEN_10EC&DEV_8125"], terms);
    }

    [Fact]
    public void Acpi_ids_search_by_short_id()
    {
        var terms = CatalogPicker.SearchTerms([@"ACPI\VEN_AMDI&DEV_0030", @"ACPI\AMDI0030", "*AMDI0030"]);

        Assert.Equal(["VEN_AMDI&DEV_0030", "AMDI0030"], terms);
    }

    [Theory]
    [InlineData("catalog.s.download.windowsupdate.com", true)]
    [InlineData("download.windowsupdate.com", true)]
    [InlineData("download.microsoft.com", true)]
    [InlineData("windowsupdate.com.evil.example", false)]
    [InlineData("evilwindowsupdate.com", false)]
    public void Only_microsoft_download_hosts_are_allowed(string host, bool allowed) =>
        Assert.Equal(allowed, DriverCatalogService.IsAllowedHost(host));

    [Fact]
    public void Inf_must_list_the_device_and_architecture()
    {
        const string inf = """
            [Manufacturer]
            %Realtek% = Realtek, NTamd64.10.0...22000
            [Realtek.NTamd64.10.0...22000]
            %RTL8125.DeviceDesc% = RTL8125, PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C
            """;
        string[] ids = [@"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C&REV_05", @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C"];

        Assert.True(DriverCatalogService.InfMatches(inf, ids, "NTamd64"));
        Assert.False(DriverCatalogService.InfMatches(inf, ids, "NTarm64"));
        Assert.False(DriverCatalogService.InfMatches(inf, [@"PCI\VEN_14C3&DEV_0616"], "NTamd64"));
    }

    [Fact]
    public void Inf_matching_uses_whole_ids_and_accepts_device_level_compatible_id()
    {
        const string generic = "[Realtek.NTamd64]\n%RTL8125% = RTL8125, PCI\\VEN_10EC&DEV_8125\n";
        const string otherModel = "[Realtek.NTamd64]\n%RTL8125% = RTL8125, PCI\\VEN_10EC&DEV_8125&SUBSYS_12345678\n";
        string[] ids =
        [
            @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C&REV_05",
            @"PCI\VEN_10EC&DEV_8125&SUBSYS_B0161F4C",
            .. DeviceInventoryService.SpecificCompatibleIds([@"PCI\VEN_10EC&DEV_8125&REV_05", @"PCI\VEN_10EC&DEV_8125", @"PCI\VEN_10EC&CC_020000", @"PCI\CC_0200"]),
        ];

        Assert.Equal(4, ids.Length); // class-code compatible IDs dropped
        Assert.True(DriverCatalogService.InfMatches(generic, ids, "NTamd64"));
        Assert.False(DriverCatalogService.InfMatches(otherModel, ids, "NTamd64"));
    }

    [Theory]
    [InlineData("24H2", 242)]
    [InlineData("25H2", 252)]
    [InlineData("21H1", 211)]
    [InlineData("2004", null)]
    [InlineData(null, null)]
    public void Parses_windows_display_version(string? text, int? expected) =>
        Assert.Equal(expected, OsInfo.ParseDisplayVersion(text));
}
