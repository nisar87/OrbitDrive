using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OrbitDrive.Core;

/// <summary>Handle-based identity checks. The path of an opened handle, not a path string, is authoritative.</summary>
public static class NativeFile
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

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int OpenPosix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>
    /// Opens a directory and keeps it open so that what is enumerated is exactly what was validated.
    /// Checking a path and then listing it by name again leaves a gap in which a folder can be swapped for a link;
    /// here the listing goes through the opened handle. Windows: the open handle also stops the folder and its
    /// parents from being renamed or replaced while it is held, and enumeration uses the handle's own final path.
    /// Linux (development hosts): enumeration goes through the descriptor itself (/proc/self/fd/N).
    /// </summary>
    public sealed class PinnedDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public string FinalPath { get; }
        public string EnumerationPath { get; }
        public bool IsLink { get; }

        internal PinnedDirectory(SafeFileHandle handle, string finalPath, string enumerationPath, bool isLink)
        { _handle = handle; FinalPath = finalPath; EnumerationPath = enumerationPath; IsLink = isLink; }

        public void Dispose() => _handle.Dispose();
    }

    /// <param name="followFinalLink">True only for the shared root itself, which the owner may have chosen through a link.</param>
    public static PinnedDirectory OpenDirectory(string path, bool followFinalLink = false)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint listDirectory = 0x1, shareReadWrite = 0x3, openReparsePoint = 0x00200000;
            var handle = CreateFileW(path, listDirectory, shareReadWrite, IntPtr.Zero, OpenExisting, BackupSemantics | (followFinalLink ? 0 : openReparsePoint), IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try
            {
                if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var final = GetFinalPath(handle);
                return new PinnedDirectory(handle, final, final, (info.FileAttributes & FileAttributeReparsePoint) != 0);
            }
            catch { handle.Dispose(); throw; }
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Directory pinning is implemented for Windows and Linux.");
        // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC (flag values differ between x64 and arm64)
        var arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
        var flags = (arm ? 0x4000 : 0x10000) | (followFinalLink ? 0 : arm ? 0x8000 : 0x20000) | 0x80000;
        var fd = OpenPosix(path, flags);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is 40 or 20) throw new IOException("The folder was replaced by a link.");      // ELOOP / ENOTDIR
            if (errno == 13) throw new UnauthorizedAccessException();
            throw new DirectoryNotFoundException("Directory is unavailable.");
        }
        var descriptor = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        try
        {
            var via = $"/proc/self/fd/{fd}";
            var target = new FileInfo(via).LinkTarget ?? throw new IOException("Unable to resolve the opened folder.");
            return new PinnedDirectory(descriptor, target, via, false);
        }
        catch { descriptor.Dispose(); throw; }
    }

    /// <summary>
    /// Identity of the volume that currently holds a directory, or null when it cannot be read (missing folder, no
    /// access). Windows: the volume serial number of the opened directory handle. Other systems (development hosts):
    /// the device numbers of the mount that contains the resolved path.
    /// </summary>
    public static string? GetVolumeIdentity(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var handle = CreateFileW(directory, 0, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
                return $"vol-{info.VolumeSerialNumber:X8}";
            }
            var real = GetFinalDirectoryPath(directory);
            string? best = null, device = null;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                if (fields.Length < 5) continue;
                var mount = fields[4].Replace("\\040", " ");
                if ((real == mount || mount == "/" || real.StartsWith(mount + "/", StringComparison.Ordinal)) && (best is null || mount.Length >= best.Length)) { best = mount; device = fields[2]; }
            }
            return device is null ? null : "dev-" + device;
        }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Volume serial of an opened file (Windows); null where the check is unavailable.</summary>
    public static string? GetVolumeIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetFileInformationByHandle(handle, out var info) ? $"vol-{info.VolumeSerialNumber:X8}" : null;
    }

    public static bool IsUnder(string finalPath, string rootFinalPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = rootFinalPath.TrimEnd(Path.DirectorySeparatorChar);
        return finalPath.Length > root.Length && finalPath.StartsWith(root, comparison) && finalPath[root.Length] == Path.DirectorySeparatorChar;
    }
}
