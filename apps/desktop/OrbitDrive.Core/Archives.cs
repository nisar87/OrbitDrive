using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

public sealed record ArchiveOptions
{
    /// <summary>Private staging folder. Anything found in it when the service starts is a leftover of a crash and is removed.</summary>
    public required string Directory { get; init; }
    public long MaxSourceBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public long MaxStagedBytes { get; init; } = 24L * 1024 * 1024 * 1024;
    public int MaxFiles { get; init; } = 100_000;
    public int MaxSelection { get; init; } = 200;
    public int MaxDepth { get; init; } = 64;
    public int MaxActive { get; init; } = 3;
    public int MaxConcurrentBuilds { get; init; } = 1;
    public long ReserveFreeBytes { get; init; } = 512L * 1024 * 1024;
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(6);
    public int MaxReportedOmissions { get; init; } = 50;
    /// <summary>Tests only: write ZIP64 structures for every entry so the large-archive format can be exercised with small files.</summary>
    public bool ForceZip64 { get; init; }
    /// <summary>Tests only: free space the staging disk reports (null asks the operating system).</summary>
    public Func<long>? FreeBytes { get; init; }
    /// <summary>Tests only: runs after a source file was opened and measured, before its bytes are copied (to change or hold it).</summary>
    public Func<string, Task>? AfterOpen { get; init; }
    public const string OmissionReportName = "_OrbitDrive_omitted.txt";
}

/// <summary>
/// Folder / multi-selection ZIPs. An archive is STAGED: built into a private file on this PC (free-space, size and count limits, a
/// time-to-live and crash/cancel cleanup), after which it is an ordinary immutable file with a known size and hash that the existing
/// range/resume download path serves. (A streamed ZIP of unknown length could not resume; see ADR-16.) Originals are only read.
/// Anything that cannot be included (locked, unreadable, changed while being read, a link, an unsafe name) is listed, never hidden:
/// the archive says it is incomplete, carries a report file, and strict mode refuses to finish instead.
/// </summary>
public sealed class ArchiveService : IDisposable
{
    private enum State { Queued, Building, Ready, Failed, Cancelled }

    private sealed class Job
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string[] EntryIds { get; init; }
        public required bool Strict { get; init; }
        public State State = State.Queued;
        public int FilesTotal, FilesDone;
        public long BytesTotal, BytesDone;
        public readonly List<ArchiveOmission> Omitted = [];
        public int OmittedCount;
        public string? Error;
        public long Size;
        public string? Sha256;
        public DateTimeOffset BuiltAt, ExpiresAt;
        public HashSet<string> RootIds = [];
        public readonly CancellationTokenSource Cancel = new();
        public Task? Run;
        public string PartPath = "", FinalPath = "";
    }

    private readonly FileAccessService _files;
    private readonly SealedTokens _tokens;
    private readonly Func<IReadOnlyList<SharedRoot>> _roots;
    private readonly ArchiveOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _builds;
    private readonly object _gate = new();

    public ArchiveService(FileAccessService files, SealedTokens tokens, Func<IReadOnlyList<SharedRoot>> roots, ArchiveOptions options, TimeProvider? clock = null)
    {
        _files = files; _tokens = tokens; _roots = roots; _options = options; _clock = clock ?? TimeProvider.System;
        _builds = new SemaphoreSlim(options.MaxConcurrentBuilds);
        System.IO.Directory.CreateDirectory(options.Directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(options.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var leftover in System.IO.Directory.EnumerateFiles(options.Directory))
            try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".xz", ".bz2", ".zst", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp3", ".m4a", ".aac", ".ogg", ".flac",
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".docx", ".xlsx", ".pptx", ".jar", ".apk", ".pdf",
    };

    public int ActiveCount { get { Sweep(); return _jobs.Count; } }
    public (int Files, long Bytes) Stats()
    {
        var bytes = 0L; var files = 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(_options.Directory)) { try { bytes += new FileInfo(f).Length; files++; } catch (IOException) { } }
        return (files, bytes);
    }

    private string Seal(string id) => _tokens.Seal(new TokenBody { Type = "a", RootId = "~archive", Segments = [], CacheKey = id });

    private Job Find(string? archiveId)
    {
        var body = _tokens.Unseal(archiveId, "a");
        Sweep();
        if (body.CacheKey is null || !_jobs.TryGetValue(body.CacheKey, out var job)) throw new ProtocolException(ErrorCode.NotFound, "That archive is no longer available. Prepare it again.");
        return job;
    }

    /// <summary>Starts preparing an archive. Every selected item is re-validated here and again as it is read.</summary>
    public ArchiveRef Start(ArchiveRequest request)
    {
        Sweep();
        if (request.EntryIds is null || request.EntryIds.Count == 0 || request.EntryIds.Count > _options.MaxSelection)
            throw new ProtocolException(ErrorCode.InvalidRequest, $"Choose between 1 and {_options.MaxSelection} items.");
        var distinct = request.EntryIds.Distinct(StringComparer.Ordinal).ToArray();
        string name;
        var rootIds = new HashSet<string>(StringComparer.Ordinal);
        var firstNames = new List<string>();
        foreach (var id in distinct)
        {
            var body = _files.UnsealEntry(id, out var root);
            if (body.Segments.Length == 0) throw new ProtocolException(ErrorCode.InvalidRequest, "Choose items inside the shared folder.");
            rootIds.Add(root.RootId);
            firstNames.Add(body.Segments[^1]);
        }
        name = distinct.Length == 1 ? firstNames[0] : "MyOrbitDrive selection";
        lock (_gate)
        {
            if (_jobs.Count >= _options.MaxActive) throw new ProtocolException(ErrorCode.LimitExceeded, "Too many archives are waiting on this PC. Finish or cancel one first.");
            var job = new Job { Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Name = name, EntryIds = distinct, Strict = request.Strict, RootIds = rootIds };
            job.PartPath = Path.Combine(_options.Directory, job.Id + ".part");
            job.FinalPath = Path.Combine(_options.Directory, job.Id + ".zip");
            _jobs[job.Id] = job;
            job.Run = Task.Run(() => RunAsync(job));
            return new ArchiveRef { ArchiveId = Seal(job.Id) };
        }
    }

    public ArchiveStatus Status(ArchiveRef request)
    {
        var job = Find(request.ArchiveId);
        lock (job)
            return new ArchiveStatus
            {
                State = job.State switch { State.Queued => "queued", State.Building => "building", State.Ready => "ready", State.Failed => "failed", _ => "cancelled" },
                Name = job.Name + ".zip", FilesDone = job.FilesDone, FilesTotal = job.FilesTotal, BytesDone = job.BytesDone, BytesTotal = job.BytesTotal,
                OmittedCount = job.OmittedCount, Omitted = [.. job.Omitted], Size = job.State == State.Ready ? job.Size : null, Error = job.Error,
                ExpiresUnixMs = job.State == State.Ready ? job.ExpiresAt.ToUnixTimeMilliseconds() : null, Resumable = true,
            };
    }

    /// <summary>Cancels a running build, or discards a finished one. Its staged file is removed either way.</summary>
    public Ack Cancel(ArchiveRef request)
    {
        var body = _tokens.Unseal(request.ArchiveId, "a");
        if (body.CacheKey is not null && _jobs.TryGetValue(body.CacheKey, out var job)) Discard(job, State.Cancelled);
        return new Ack { Ok = true };
    }

    /// <summary>A shared folder was removed: archives that contain anything from it stop existing now.</summary>
    public void DropForRoot(string rootId)
    {
        foreach (var job in _jobs.Values.Where(j => j.RootIds.Contains(rootId)).ToList()) Discard(job, State.Cancelled);
    }

    public void Clear() { foreach (var job in _jobs.Values.ToList()) Discard(job, State.Cancelled); }

    private void Discard(Job job, State state)
    {
        lock (job) { if (job.State is State.Queued or State.Building or State.Ready) job.State = state; }
        try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        _jobs.TryRemove(job.Id, out _);
        var run = job.Run;
        _ = Task.Run(async () =>
        {
            if (run is not null) { try { await run.ConfigureAwait(false); } catch { } }
            foreach (var path in new[] { job.PartPath, job.FinalPath }) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        });
    }

    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var job in _jobs.Values) { bool expired; lock (job) expired = job.State is State.Ready or State.Failed && job.ExpiresAt <= now; if (expired) Discard(job, State.Cancelled); }
    }

    // ----- reading a finished archive ---------------------------------------------------------------------------------------------

    public PrepareReadResult PrepareRead(string archiveId)
    {
        var job = Find(archiveId);
        lock (job)
        {
            if (job.State != State.Ready) throw new ProtocolException(ErrorCode.Conflict, "The archive is not ready yet.");
            return new PrepareReadResult
            {
                Name = job.Name + ".zip", Size = job.Size, ModifiedUnixMs = job.BuiltAt.ToUnixTimeMilliseconds(), Revision = "sha256-" + job.Sha256, ContentType = "application/zip",
                ReadToken = _tokens.Seal(new TokenBody { Type = "z", RootId = "~archive", Segments = [], CacheKey = job.Id, DerivedSize = job.Size, Size = job.Size, Sha256 = job.Sha256 }),
            };
        }
    }

    internal ValidatedRead OpenRead(TokenBody token, long offset, long length)
    {
        Sweep();
        if (token.CacheKey is null || !_jobs.TryGetValue(token.CacheKey, out var job)) throw new ProtocolException(ErrorCode.NotFound, "That archive is no longer available. Prepare it again.");
        lock (job) if (job.State != State.Ready || job.Size != token.DerivedSize || job.Sha256 != token.Sha256) throw new ProtocolException(ErrorCode.NotFound, "That archive is no longer available. Prepare it again.");
        // Every folder it was built from must still be shared right now.
        var current = _roots().Select(r => r.RootId).ToHashSet(StringComparer.Ordinal);
        if (!job.RootIds.All(current.Contains)) { Discard(job, State.Cancelled); throw new ProtocolException(ErrorCode.NotFound, "A shared folder in this archive was removed."); }
        if (offset < 0 || length < 0 || offset > job.Size || length > job.Size - offset) throw new ProtocolException(ErrorCode.RangeNotSatisfiable, "Requested range is outside the file.");
        try
        {
            var handle = File.OpenHandle(job.FinalPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (RandomAccess.GetLength(handle) != job.Size) { handle.Dispose(); throw new ProtocolException(ErrorCode.RevisionChanged, "The archive changed."); }
            return new ValidatedRead(handle, offset, length, job.Name + ".zip");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "That archive is no longer available. Prepare it again."); }
    }

    // ----- building ---------------------------------------------------------------------------------------------------------------

    private sealed record Item(string ZipName, SharedRoot Root, string[] Segments, bool IsDirectory, long Size, DateTime ModifiedUtc);

    private async Task RunAsync(Job job)
    {
        var token = job.Cancel.Token;
        try
        {
            await _builds.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (job) job.State = State.Building;
                var plan = Plan(job, token);
                CheckSpace(job);
                await BuildAsync(job, plan, token).ConfigureAwait(false);
            }
            finally { _builds.Release(); }
        }
        catch (OperationCanceledException) { lock (job) if (job.State is State.Queued or State.Building) job.State = State.Cancelled; Delete(job.PartPath); }
        catch (ProtocolException e) { Fail(job, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ZipSourceChangedException) { Fail(job, "The archive could not be prepared on this PC."); }
    }

    private void Fail(Job job, string message)
    {
        lock (job) { job.State = State.Failed; job.Error = message; job.ExpiresAt = _clock.GetUtcNow().AddMinutes(15); }
        Delete(job.PartPath);
    }

    private static void Delete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private void Omit(Job job, string path, string reason)
    {
        lock (job)
        {
            job.OmittedCount++;
            if (job.Omitted.Count < _options.MaxReportedOmissions) job.Omitted.Add(new ArchiveOmission { Path = path, Reason = reason });
        }
        if (job.Strict) throw new ProtocolException(ErrorCode.Conflict, $"{path}: {reason}. Nothing was prepared because partial archives are not allowed for this request.");
    }

    private List<Item> Plan(Job job, CancellationToken token)
    {
        var plan = new List<Item>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0; var files = 0;
        foreach (var id in job.EntryIds)
        {
            token.ThrowIfCancellationRequested();
            var body = _files.UnsealEntry(id, out var root);
            var top = body.Segments[^1];
            var unique = top; var n = 2;
            while (!topNames.Add(unique)) unique = $"{Path.GetFileNameWithoutExtension(top)} ({n++}){Path.GetExtension(top)}";
            if (!body.IsDirectory)
            {
                using var handle = _files.OpenValidated(root, body.Segments);   // proves it is a plain file under the root now
                files++; bytes += RandomAccess.GetLength(handle);
                Limits(files, bytes);
                plan.Add(new Item(unique, root, body.Segments, false, RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle)));
                continue;
            }
            var stack = new Stack<(string[] Segments, string Zip, int Depth)>();
            stack.Push((body.Segments, unique, 1));
            plan.Add(new Item(unique + "/", root, body.Segments, true, 0, Directory.GetLastWriteTimeUtc(PathPolicy.Resolve(root, body.Segments, expectDirectory: true))));
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (segments, zip, depth) = stack.Pop();
                if (depth > _options.MaxDepth) { Omit(job, zip, "folder is nested too deeply"); continue; }
                List<FileSystemInfo> children;
                try { children = _files.EnumerateChildren(root, segments, out var skipped); if (skipped > 0) Omit(job, zip, $"{skipped} link(s) or items with unsupported names were skipped"); }
                catch (ProtocolException e) when (e.Code is ErrorCode.AccessDeniedLocal or ErrorCode.NotFound or ErrorCode.Unavailable or ErrorCode.LinkNotAllowed)
                { Omit(job, zip, e.Message); continue; }
                children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var child in children)
                {
                    var childSegments = new string[segments.Length + 1];
                    segments.CopyTo(childSegments, 0); childSegments[^1] = child.Name;
                    var childZip = zip + "/" + child.Name;
                    if (child is DirectoryInfo dir)
                    {
                        plan.Add(new Item(childZip + "/", root, childSegments, true, 0, dir.LastWriteTimeUtc));
                        stack.Push((childSegments, childZip, depth + 1));
                    }
                    else if (child is FileInfo file)
                    {
                        files++; bytes += file.Length;
                        Limits(files, bytes);
                        plan.Add(new Item(childZip, root, childSegments, false, file.Length, file.LastWriteTimeUtc));
                    }
                }
            }
        }
        lock (job) { job.FilesTotal = files; job.BytesTotal = bytes; }
        return plan;
    }

    private void Limits(int files, long bytes)
    {
        if (files > _options.MaxFiles) throw new ProtocolException(ErrorCode.LimitExceeded, $"This selection has more than {_options.MaxFiles:N0} files. Choose fewer, or download folders separately.");
        if (bytes > _options.MaxSourceBytes) throw new ProtocolException(ErrorCode.LimitExceeded, $"This selection is larger than {_options.MaxSourceBytes / (1024 * 1024 * 1024)} GiB, the most one archive may hold.");
    }

    private void CheckSpace(Job job)
    {
        var free = _options.FreeBytes?.Invoke() ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.Directory))!).AvailableFreeSpace;
        var (_, staged) = Stats();
        // Worst case the archive is as large as its sources (already-compressed data does not shrink).
        if (job.BytesTotal + _options.ReserveFreeBytes > free)
            throw new ProtocolException(ErrorCode.LimitExceeded, "There is not enough free space on this PC to prepare this archive. Free some space or choose less.");
        if (staged + job.BytesTotal > _options.MaxStagedBytes)
            throw new ProtocolException(ErrorCode.LimitExceeded, "Too much archive data is already waiting on this PC. Discard an archive first.");
    }

    private async Task BuildAsync(Job job, List<Item> plan, CancellationToken token)
    {
        var started = _clock.GetUtcNow();
        await using (var output = new FileStream(job.PartPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous, BufferSize = 1 << 16 }))
        {
            using var zip = new ZipWriter(output, _options.ForceZip64);
            foreach (var item in plan)
            {
                token.ThrowIfCancellationRequested();
                var display = item.ZipName.TrimEnd('/');
                if (item.IsDirectory)
                {
                    zip.AddDirectory(item.ZipName, item.ModifiedUtc);
                    continue;
                }
                var added = false;
                try
                {
                    using var handle = _files.OpenValidated(item.Root, item.Segments);
                    var length = RandomAccess.GetLength(handle);
                    var stamp = NativeFile.GetChangeStamp(handle);
                    var modified = File.GetLastWriteTimeUtc(handle);
                    if (_options.AfterOpen is { } hook) await hook(item.ZipName).ConfigureAwait(false);
                    var compress = !AlreadyCompressed.Contains(Path.GetExtension(item.ZipName));
                    await zip.AddFileAsync(item.ZipName, modified, handle, length, compress, n => { lock (job) job.BytesDone += n; }, token).ConfigureAwait(false);
                    added = true;
                    if (File.GetLastWriteTimeUtc(handle) != modified || (stamp != 0 && NativeFile.GetChangeStamp(handle) != stamp))
                        throw new ZipSourceChangedException("changed");
                    lock (job) job.FilesDone++;
                }
                catch (ZipSourceChangedException) { if (added) zip.RemoveLast(); Omit(job, display, "the file changed while it was being archived"); }
                catch (ProtocolException e) when (e.Code is ErrorCode.AccessDeniedLocal or ErrorCode.NotFound or ErrorCode.Unavailable or ErrorCode.LinkNotAllowed or ErrorCode.RevisionChanged)
                { Omit(job, display, e.Message.TrimEnd('.').ToLowerInvariant()); }
                catch (IOException) { Omit(job, display, "the file could not be read"); }
            }
            if (job.OmittedCount > 0)
            {
                var report = new StringBuilder("These items could NOT be included in this archive, so it is incomplete:\r\n\r\n");
                lock (job) foreach (var o in job.Omitted) report.Append(o.Path).Append(" - ").Append(o.Reason).Append("\r\n");
                if (job.OmittedCount > job.Omitted.Count) report.Append($"\r\n...and {job.OmittedCount - job.Omitted.Count} more.\r\n");
                await zip.AddBytesAsync(ArchiveOptions.OmissionReportName, started.UtcDateTime, Encoding.UTF8.GetBytes(report.ToString()), token).ConfigureAwait(false);
            }
            // A shared folder removed while this ran: nothing that was read may be handed out.
            var current = _roots().Select(r => r.RootId).ToHashSet(StringComparer.Ordinal);
            if (!job.RootIds.All(current.Contains)) throw new OperationCanceledException();
            zip.Finish();
        }
        string sha;
        long size;
        await using (var finished = new FileStream(job.PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            size = finished.Length;
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(finished, token).ConfigureAwait(false));
        }
        File.Move(job.PartPath, job.FinalPath);
        lock (job)
        {
            if (job.State == State.Cancelled) { Delete(job.FinalPath); return; }
            job.Size = size; job.Sha256 = sha; job.BuiltAt = _clock.GetUtcNow(); job.ExpiresAt = job.BuiltAt + _options.Ttl; job.State = State.Ready;
        }
    }

    public void Dispose() => Clear();
}
