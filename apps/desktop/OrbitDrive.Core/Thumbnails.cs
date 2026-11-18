using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

public sealed record ThumbnailOptions
{
    public bool Enabled { get; init; } = true;
    public long CacheQuotaBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan CacheMaxAge { get; init; } = TimeSpan.FromDays(30);
    public long MaxSourceBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 512 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxConcurrent { get; init; } = 2;
    public long HelperMemoryBytes { get; init; } = 768L * 1024 * 1024;
    public static readonly int[] Edges = [128, 256, 512];
}

/// <summary>
/// Private, quota-bounded store of generated thumbnails. File names are hashes of the source identity (never a name or path),
/// so the folder reveals nothing by itself; every use is still re-authorised against the live source by the caller.
/// </summary>
public sealed class ThumbnailCache
{
    private readonly string _directory;
    private readonly ThumbnailOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public ThumbnailCache(string directory, ThumbnailOptions options, TimeProvider? clock = null)
    {
        _directory = directory; _options = options; _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string PathFor(string key) => Path.Combine(_directory, key + ".thumb");

    public static string KeyFor(int edge, string rootId, IReadOnlyList<string> segments, long size, long modifiedTicks, long changeStamp)
    {
        var material = string.Join('\u0001', "thumb-v1", edge, rootId, string.Join('\u0000', segments), size, modifiedTicks, changeStamp);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    /// <summary>Opens a cached thumbnail, or null. A hit is marked as recently used.</summary>
    public SafeFileHandle? TryOpen(string key)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;
            if (_clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) > _options.CacheMaxAge) { TryDelete(path); return null; }
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            try { File.SetLastAccessTimeUtc(path, _clock.GetUtcNow().UtcDateTime); } catch (IOException) { }
            return handle;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Put(string key, ReadOnlySpan<byte> bytes)
    {
        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, bytes.ToArray());
        try { File.Move(temp, path, overwrite: true); }
        catch (IOException) { TryDelete(temp); }
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime);
        Trim();
    }

    /// <summary>Drops expired files, then the least recently written ones until the folder is within its quota.</summary>
    public void Trim()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var files = new DirectoryInfo(_directory).EnumerateFiles().Where(f => f.Extension is ".thumb" or ".tmp").ToList();
            foreach (var f in files.Where(f => now - f.LastWriteTimeUtc > (f.Extension == ".tmp" ? TimeSpan.FromMinutes(10) : _options.CacheMaxAge)).ToList()) { TryDelete(f.FullName); files.Remove(f); }
            var total = files.Sum(f => f.Length);
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc)) { if (total <= _options.CacheQuotaBytes) break; if (TryDelete(f.FullName)) total -= f.Length; }
        }
    }

    public (int Files, long Bytes) Stats()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("*.thumb").ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>Owner control: removes every derived file.</summary>
    public int Clear()
    {
        lock (_gate)
        {
            var removed = 0;
            foreach (var f in new DirectoryInfo(_directory).EnumerateFiles()) if (TryDelete(f.FullName)) removed++;
            return removed;
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Turns image bytes into a thumbnail inside a separate, restricted process.</summary>
public interface IThumbnailRunner
{
    Task<byte[]> RunAsync(Func<Stream, CancellationToken, Task> feedSource, int edge, CancellationToken cancellation);
}

/// <summary>
/// Runs the decoder helper as a child process that receives the image on standard input and answers on standard output: it is given
/// no file path, no network and (on Windows) a job object with a memory ceiling and no right to start further processes. The
/// whole process tree is killed on timeout, on oversized output and when the owner's agent goes away.
/// </summary>
public sealed class ProcessThumbnailRunner(string helperPath, ThumbnailOptions options) : IThumbnailRunner
{
    public static string? Locate(string baseDirectory)
    {
        var overridePath = Environment.GetEnvironmentVariable("ORBITDRIVE_THUMBNAIL_HELPER");
        if (!string.IsNullOrEmpty(overridePath)) return File.Exists(overridePath) ? overridePath : null;
        var name = OperatingSystem.IsWindows() ? "OrbitDrive.ThumbnailHelper.exe" : "OrbitDrive.ThumbnailHelper";
        var path = Path.Combine(baseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public async Task<byte[]> RunAsync(Func<Stream, CancellationToken, Task> feedSource, int edge, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(helperPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        info.ArgumentList.Add("--edge"); info.ArgumentList.Add(edge.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--max-bytes"); info.ArgumentList.Add(options.MaxOutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo = info };
        using var limits = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(limits.Token, cancellation);
        if (!process.Start()) throw new ProtocolException(ErrorCode.Unavailable, "The preview helper could not be started.");
        using var job = OperatingSystem.IsWindows() ? JobLimits.Confine(process, options.HelperMemoryBytes) : null;
        void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } }
        using var registration = linked.Token.Register(Kill);
        try
        {
            var output = ReadLimitedAsync(process.StandardOutput.BaseStream, options.MaxOutputBytes, linked.Token);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, linked.Token);   // drained so a chatty helper cannot block
            try
            {
                await feedSource(process.StandardInput.BaseStream, linked.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (IOException) { /* the helper may stop reading early; its answer or exit code decides */ }
            finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            var bytes = await output.ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || bytes.Length == 0) throw new ProtocolException(ErrorCode.InvalidRequest, "This file could not be turned into a preview.");
            return bytes;
        }
        catch (OperationCanceledException) when (limits.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            Kill();
            throw new ProtocolException(ErrorCode.Timeout, "Making a preview of this file took too long.");
        }
        catch { Kill(); throw; }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellation)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > limit) throw new ProtocolException(ErrorCode.InvalidRequest, "The preview helper produced too much data.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}

/// <summary>Windows job object: memory ceiling, one process only, and the helper dies with the job.</summary>
internal static class JobLimits
{
    public sealed class Job(IntPtr handle) : IDisposable
    {
        public void Dispose() => CloseHandle(handle);
    }

    public static Job Confine(Process process, long memoryBytes)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new ExtendedLimit
            {
                Basic = { LimitFlags = LimitKillOnClose | LimitProcessMemory | LimitActiveProcess, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = (UIntPtr)(ulong)memoryBytes,
            };
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new Job(job);
        }
        catch
        {
            CloseHandle(job);
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new ProtocolException(ErrorCode.Unavailable, "The preview helper could not be restricted, so it was not used.");
        }
    }

    private const uint LimitActiveProcess = 0x8, LimitProcessMemory = 0x100, LimitKillOnClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Generates and caches thumbnails; limits concurrency; remembers recent failures so a hostile file cannot be retried in a loop.</summary>
public sealed class ThumbnailService(ThumbnailCache cache, IThumbnailRunner? runner, ThumbnailOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrent));
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, ProtocolException Error)> _recentFailures = new();
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(5);

    /// <summary>The owner's switch; changing it takes effect for the next request.</summary>
    public bool Enabled { get; set; } = options.Enabled;
    public bool HasHelper => runner is not null;
    public bool Available => Enabled && runner is not null;
    public ThumbnailCache Cache => cache;

    /// <summary>Returns an open handle on the (cached or freshly made) thumbnail and its size.</summary>
    public async Task<(SafeFileHandle Handle, long Size)> GetAsync(SafeFileHandle source, long sourceSize, string key, int edge, string name, CancellationToken cancellation)
    {
        if (!Enabled) throw new ProtocolException(ErrorCode.Unavailable, "Previews are turned off on this PC.");
        if (runner is null) throw new ProtocolException(ErrorCode.Unavailable, "Thumbnails are not available on this PC.");
        if (!ThumbnailOptions.Edges.Contains(edge)) throw new ProtocolException(ErrorCode.InvalidRequest, "That thumbnail size is not offered.");
        if (cache.TryOpen(key) is { } cached) return (cached, RandomAccess.GetLength(cached));
        if (_recentFailures.TryGetValue(key, out var failed) && failed.Until > _clock.GetUtcNow()) throw failed.Error;
        if (sourceSize > options.MaxSourceBytes) throw Remember(key, new ProtocolException(ErrorCode.LimitExceeded, "This file is too large to make a preview of. Download it instead."));

        var head = new byte[(int)Math.Min(sourceSize, ImageProbe.ProbeBytes)];
        var got = 0;
        while (got < head.Length) { var n = RandomAccess.Read(source, head.AsSpan(got), got); if (n == 0) break; got += n; }
        var sniffed = ContentSniffer.Classify(head.AsSpan(0, got), name);
        if (sniffed.Kind != PreviewKind.Image) throw Remember(key, new ProtocolException(ErrorCode.InvalidRequest, "Only pictures get thumbnails."));
        try { ImageProbe.Require(head.AsSpan(0, got), sniffed.ContentType); }
        catch (ProtocolException e) { throw Remember(key, e); }

        await _slots.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (cache.TryOpen(key) is { } raced) return (raced, RandomAccess.GetLength(raced));   // another request finished it meanwhile
            byte[] thumb;
            try
            {
                thumb = await runner.RunAsync(async (stdin, ct) =>
                {
                    var buffer = new byte[64 * 1024];
                    long position = 0;
                    while (position < sourceSize)
                    {
                        var read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, sourceSize - position)), position, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        await stdin.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        position += read;
                    }
                }, edge, cancellation).ConfigureAwait(false);
            }
            catch (ProtocolException e) when (e.Code is not ErrorCode.Unavailable) { throw Remember(key, e); }
            if (!ContentSniffer.Classify(thumb, "t.webp").ContentType.Equals("image/webp", StringComparison.Ordinal))
                throw Remember(key, new ProtocolException(ErrorCode.InvalidRequest, "The preview helper returned something that is not a picture."));
            cache.Put(key, thumb);
            return cache.TryOpen(key) is { } made ? (made, RandomAccess.GetLength(made)) : throw new ProtocolException(ErrorCode.Unavailable, "The preview could not be stored.");
        }
        finally { _slots.Release(); }
    }

    private ProtocolException Remember(string key, ProtocolException error)
    {
        _recentFailures[key] = (_clock.GetUtcNow() + FailureMemory, error);
        if (_recentFailures.Count > 2000) foreach (var k in _recentFailures.Where(p => p.Value.Until <= _clock.GetUtcNow()).Select(p => p.Key).ToList()) _recentFailures.TryRemove(k, out _);
        return error;
    }
}
