namespace DriveMeCrazy.Core.Models;

public sealed class AppSettings
{
    /// <summary>Drivers at least this many years old are flagged as old.</summary>
    public int OldDriverYears { get; set; } = 3;

    /// <summary>Create a System Restore point before installing anything.</summary>
    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>After each scan, look up newer drivers for old devices in the Microsoft Update Catalog.</summary>
    public bool SearchCatalog { get; set; } = true;

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
}
