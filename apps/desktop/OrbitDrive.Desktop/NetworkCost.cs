using System.Runtime.InteropServices;

namespace OrbitDrive.Desktop;

/// <summary>
/// Asks Windows what the current internet connection costs (Network List Manager). Returns the raw
/// NLM_CONNECTION_COST flags, or null when Windows does not answer; interpretation is in OrbitDrive.Core.ConnectionCost.
/// </summary>
internal static class NetworkCost
{
    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager { }

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        [PreserveSig] int GetCost(out uint cost, IntPtr destinationAddress);
    }

    public static uint? Read()
    {
        object? manager = null;
        try
        {
            manager = new NetworkListManager();
            return ((INetworkCostManager)manager).GetCost(out var cost, IntPtr.Zero) == 0 ? cost : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or NotSupportedException) { return null; }
        finally { if (manager is not null) Marshal.FinalReleaseComObject(manager); }
    }
}
