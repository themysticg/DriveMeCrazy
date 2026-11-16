using System.Runtime.InteropServices;
using DriveMeCrazy.Core.Models;

namespace DriveMeCrazy.Core.Services;

/// <summary>
/// Reads live device properties from the PnP manager (cfgmgr32). Unlike WMI's Win32_PnPSignedDriver,
/// this reflects a driver change immediately after pnputil installs it.
/// </summary>
public static class DeviceProperties
{
    // DEVPKEY_Device_DriverVersion
    private static readonly DEVPROPKEY DriverVersionKey = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 3);
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;

    // DEVPKEY_Device_DriverDate
    private static readonly DEVPROPKEY DriverDateKey = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 2);
    private const uint DEVPROP_TYPE_FILETIME = 0x10;

    public static string? GetDriverVersion(string deviceInstanceId)
    {
        var bytes = Get(deviceInstanceId, DriverVersionKey, DEVPROP_TYPE_STRING);
        return bytes is null ? null : System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    public static DateTime? GetDriverDate(string deviceInstanceId)
    {
        var bytes = Get(deviceInstanceId, DriverDateKey, DEVPROP_TYPE_FILETIME);
        if (bytes is not { Length: 8 }) return null;
        try { return DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 0)).Date; }
        catch { return null; }
    }

    private static byte[]? Get(string deviceInstanceId, DEVPROPKEY propertyKey, uint expectedType)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint devInst, deviceInstanceId, 0) != CR_SUCCESS) return null;
            var key = propertyKey;
            uint size = 0;
            int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, null, ref size, 0);
            if (cr != CR_BUFFER_SMALL || size == 0) return null;
            var buffer = new byte[size];
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0) != CR_SUCCESS || type != expectedType)
                return null;
            return buffer;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read a property of {deviceInstanceId}", ex);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint propertyType,
        byte[]? buffer, ref uint bufferSize, uint flags);
}

/// <summary>A downloaded, signature-checked, unpacked driver package that matches its device.</summary>
public sealed record PreparedDriver(CatalogLookup Lookup, CatalogEntry Candidate, string InfPath, string WorkDir, string? Signer)
{
    /// <summary>Network adapters drop the connection while their driver is replaced, so they go last.</summary>
    public bool IsNetworkAdapter => string.Equals(Lookup.Driver.DeviceClass, "NET", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads what pnputil reported.</summary>
public sealed record PnpUtilResult(bool InstalledOnDevice, bool RebootRequired, bool WindowsKeptCurrent = false)
{
    public static PnpUtilResult Interpret(int exitCode, string output) => new(
        InstalledOnDevice: output.Contains("installed on device", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("Driver package installed on matching devices", StringComparison.OrdinalIgnoreCase),
        // Exit 259 / "up-to-date on device": the package was added, but Windows ranks the current driver as the better match.
        WindowsKeptCurrent: exitCode == 259 || output.Contains("up-to-date on device", StringComparison.OrdinalIgnoreCase),
        RebootRequired: exitCode == 3010
                        || output.Contains("reboot", StringComparison.OrdinalIgnoreCase)
                        || output.Contains("restart", StringComparison.OrdinalIgnoreCase));
}
