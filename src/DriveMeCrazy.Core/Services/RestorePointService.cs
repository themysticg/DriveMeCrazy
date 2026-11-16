using System.Management;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>Creates System Restore points so driver installs can be rolled back.</summary>
public sealed class RestorePointService
{
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    /// <summary>
    /// False when System Protection is turned off. Windows stores this as RPSessionInterval = 0;
    /// in that state CreateRestorePoint can fail with an empty COM error instead of a clear code.
    /// </summary>
    public static bool? IsProtectionEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            using var policy = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
            if (policy?.GetValue("DisableSR") is int disabled && disabled == 1) return false;
            return key?.GetValue("RPSessionInterval") is int interval ? interval != 0 : null;
        }
        catch { return null; }
    }

    public RestorePointResult Create(string description, bool skipProtectionCheck = false)
    {
        if (!skipProtectionCheck && IsProtectionEnabled() == false)
            return new(false, "System Protection is turned off on this PC", ProtectionDisabled: true);
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
            Log.Error($"Restore point failed (0x{ex.HResult:X8})", ex);
            bool disabled = ex is ManagementException { ErrorCode: ManagementStatus.NotSupported }
                            || ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                            || IsProtectionEnabled() == false;
            string message = disabled ? "System Protection is turned off on this PC"
                : string.IsNullOrWhiteSpace(ex.Message) ? $"Windows couldn't create a restore point (0x{ex.HResult:X8})"
                : ex.Message;
            return new(false, message, disabled);
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
