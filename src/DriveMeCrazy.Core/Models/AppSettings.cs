namespace DriveMeCrazy.Core.Models;

public sealed class AppSettings
{
    /// <summary>Drivers at least this many years old are flagged as old.</summary>
    public int OldDriverYears { get; set; } = 3;

    /// <summary>Create a System Restore point before installing anything.</summary>
    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>After each scan, look up newer drivers for old devices in the Microsoft Update Catalog.</summary>
    public bool SearchCatalog { get; set; } = true;

    /// <summary>"DeviceId|Version" pairs Windows refused to switch to; not offered again.</summary>
    public List<string> DeclinedCatalogDrivers { get; set; } = [];

    /// <summary>"DeviceId|Version" packages that turned out not to list the device; not offered again.</summary>
    public List<string> NotForDeviceCatalogDrivers { get; set; } = [];

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
}
