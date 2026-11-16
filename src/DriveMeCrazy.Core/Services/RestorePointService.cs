using System.Management;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>Creates System Restore points so driver installs can be rolled back.</summary>
public sealed class RestorePointService
{
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    public RestorePointResult Create(string description)
    {
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
            uint rv = Convert.ToUInt32(output["ReturnValue"]);
            Log.Info($"Restore point: return value 0x{rv:X8}");
            return rv switch
            {
                0 => new(true, "Restore point created"),
                1058 or 0x80070422 => new(false, "System Protection is turned off on this PC", ProtectionDisabled: true),
                _ => new(false, $"Restore point could not be created (0x{rv:X8})"),
            };
        }
        catch (Exception ex)
        {
            Log.Error("Restore point failed", ex);
            bool disabled = ex is ManagementException { ErrorCode: ManagementStatus.NotSupported }
                            || ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase);
            return new(false, disabled ? "System Protection is turned off on this PC" : ex.Message, disabled);
        }
    }

    /// <summary>Turns System Protection on for the system drive (user-initiated only).</summary>
    public RestorePointResult EnableProtection()
    {
        try
        {
            string drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = cls.GetMethodParameters("Enable");
            input["Drive"] = drive;
            using var output = cls.InvokeMethod("Enable", input, null);
            uint rv = Convert.ToUInt32(output["ReturnValue"]);
            Log.Info($"Enable System Protection on {drive}: 0x{rv:X8}");
            return rv == 0 ? new(true, $"System Protection turned on for {drive}") : new(false, $"Could not turn on System Protection (0x{rv:X8})");
        }
        catch (Exception ex)
        {
            Log.Error("Enable System Protection failed", ex);
            return new(false, ex.Message);
        }
    }
}
