using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OrbitDrive.Core;

/// <summary>Handle-based identity checks. The path of an opened handle, not a path string, is authoritative.</summary>
internal static class NativeFile
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // BY_HANDLE_FILE_INFORMATION: FILETIME is two DWORDs (4-byte aligned). Declaring them as 64-bit fields
    // would insert padding and shift NumberOfLinks - caught by the Windows CI run of the first slice.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr ptr);

    private const uint FileShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000, FileAttributeReparsePoint = 0x400;

    /// <summary>Final, link-resolved path for an opened file handle.</summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length > buffer.Length)
            {
                buffer = new char[length + 1];
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new string(buffer, 0, (int)length);
        }
        var target = new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget;
        return target ?? throw new IOException("Unable to resolve the opened handle.");
    }

    /// <summary>Final, link-resolved path of a directory (opened by handle on Windows).</summary>
    public static string GetFinalDirectoryPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return GetFinalPath(handle);
        }
        var ptr = RealPath(path, IntPtr.Zero);
        if (ptr == IntPtr.Zero) throw new DirectoryNotFoundException("Directory is unavailable.");
        try { return Marshal.PtrToStringUTF8(ptr)!; }
        finally { Free(ptr); }
    }

    /// <summary>Number of hard links to the opened file; 1 when the platform check is unavailable (non-Windows dev hosts).</summary>
    public static (uint Links, bool IsReparse) GetLinkInfo(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return (1, false);
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.NumberOfLinks, (info.FileAttributes & FileAttributeReparsePoint) != 0);
    }

    public static bool IsUnder(string finalPath, string rootFinalPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = rootFinalPath.TrimEnd(Path.DirectorySeparatorChar);
        return finalPath.Length > root.Length && finalPath.StartsWith(root, comparison) && finalPath[root.Length] == Path.DirectorySeparatorChar;
    }
}
