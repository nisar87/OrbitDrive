using System.Buffers;
using System.Security.Cryptography;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

public static class UploadLimits
{
    public const long DefaultMaxFileBytes = 4L << 30;
    public static long MaxFileBytes(SharedRoot root) => root.UploadMaxFileBytes > 0 ? root.UploadMaxFileBytes : DefaultMaxFileBytes;
    public static long? QuotaLeft(SharedRoot root) => root.UploadQuotaBytes > 0 ? Math.Max(0, root.UploadQuotaBytes - root.UploadUsedBytes) : null;
}

public sealed class UploadOptions
{
    /// <summary>Size of one chunk the browser is asked to send. Each chunk is held in memory once, verified, then written.</summary>
    public int ChunkBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>Largest chunk the PC will accept, whatever a caller declares.</summary>
    public int MaxChunkBytes { get; init; } = 8 * 1024 * 1024;
    public long ReserveFreeBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxActive { get; init; } = 3;
    /// <summary>A session with no data for this long is removed together with its partial file.</summary>
    public TimeSpan IdleTtl { get; init; } = TimeSpan.FromMinutes(45);
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(12);
    /// <summary>Total length (folder + name) allowed; older Windows programs cannot open longer paths.</summary>
    public int MaxPathChars { get; init; } = 259;
    /// <summary>Free bytes on the disk that holds a folder. Tests replace it; the default asks the OS.</summary>
    public Func<string, long>? FreeBytes { get; init; }
    /// <summary>Test hook: runs after a verified chunk was accepted into memory and before it is written.</summary>
    public Func<Task>? BeforeWrite { get; init; }
}

/// <summary>
/// Receives files from a browser into a folder the owner enabled for uploads. A browser never names a path: it names a folder it
/// was shown (a sealed entry id inside an upload-enabled root) and a file name, and the PC decides everything else.
/// Data goes to a private temporary file on the same disk and becomes a real file only by one atomic, never-replacing rename
/// after every byte was checked. Nothing here opens, runs, installs or unpacks what it receives.
/// </summary>
public sealed class UploadService
{
    private static readonly HashSet<string> RefusedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".scf", ".library-ms", ".searchconnector-ms", ".appref-ms",
    };
    private static readonly HashSet<string> RefusedNames = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", "autorun.inf" };

    private sealed class Session
    {
        public required string Id { get; init; }
        public required string RootId { get; init; }
        public required string DeviceId { get; init; }
        public required string Name { get; init; }
        public required string Collision { get; init; }
        public required string[] FolderSegments { get; init; }
        public required long Size { get; init; }
        public required string TempPath { get; init; }
        public required DateTimeOffset Created { get; init; }
        public DateTimeOffset LastActive { get; set; }
        public long Committed;
        public IncrementalHash? Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string State = "receiving";   // receiving | committed | failed | cancelled | expired
        public string? FinalName, Sha256, Error;
        public bool Renamed, OriginMarked;
        public DateTimeOffset Ended;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly FileAccessService _files;
    private readonly Func<IReadOnlyList<SharedRoot>> _roots;
    private readonly Action<string, long> _addUsed;
    private readonly UploadOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public UploadService(FileAccessService files, Func<IReadOnlyList<SharedRoot>> roots, Action<string, long> addUsed, UploadOptions? options = null, TimeProvider? clock = null)
    {
        _files = files;
        _roots = roots;
        _addUsed = addUsed;
        _options = options ?? new UploadOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public int ActiveCount { get { lock (_lock) return _sessions.Values.Count(s => s.State == "receiving"); } }
    public UploadOptions Options => _options;

    // ----- names -----

    /// <summary>Throws a plain-language InvalidRequest unless the name is safe to create on Windows and cannot make Windows act on it.</summary>
    public static void ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name)) throw new ProtocolException(ErrorCode.InvalidRequest, "The file has no name.");
        if (!PathPolicy.IsSafeSegment(name))
            throw new ProtocolException(ErrorCode.InvalidRequest, "This file name cannot be used on Windows (reserved name, forbidden character, or it ends with a dot or space).");
        foreach (var c in name)
            if (c is (>= '\u0080' and <= '\u009f') or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '﻿')
                throw new ProtocolException(ErrorCode.InvalidRequest, "This file name contains hidden direction or control characters.");
        if (RefusedNames.Contains(name) || RefusedExtensions.Contains(Path.GetExtension(name)))
            throw new ProtocolException(ErrorCode.InvalidRequest, "Windows reads this kind of file automatically when a folder is opened, so OrbitDrive does not accept it.");
    }

    /// <summary>"name.ext" → "name (2).ext", "name (3).ext" … The first number that is free, so the result is predictable.</summary>
    public static string NumberedName(string name, int number)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
    }

    // ----- sessions -----

    private SharedRoot UploadRoot(string rootId)
    {
        var root = _roots().FirstOrDefault(r => r.RootId == rootId) ?? throw new ProtocolException(ErrorCode.NotFound, "That shared folder is not available.");
        if (!root.UploadsEnabled) throw new ProtocolException(ErrorCode.Forbidden, "The owner has not turned on uploads for this shared folder on the PC.");
        return root;
    }

    private long FreeBytes(string path) => _options.FreeBytes?.Invoke(path) ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    private static string PartialDirectory(SharedRoot root) => Path.Join(Path.GetFullPath(root.Path), PathPolicy.PartialDirectoryName);

    private static bool NameTaken(string directory, string name)
    {
        // Compared without regard to case on every system, because the files will end up on Windows.
        foreach (var existing in Directory.EnumerateFileSystemEntries(directory))
            if (string.Equals(Path.GetFileName(existing), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public UploadStarted Start(StartUploadRequest request)
    {
        Sweep();
        if (string.IsNullOrEmpty(request.DeviceId)) throw new ProtocolException(ErrorCode.InvalidRequest, "Missing device.");
        if (request.Collision is not ("reject" or "rename")) throw new ProtocolException(ErrorCode.InvalidRequest, "Unknown choice for files that already exist.");
        ValidateName(request.Name);
        if (request.Size < 0) throw new ProtocolException(ErrorCode.InvalidRequest, "Invalid file size.");

        // No folder id means the shared folder itself; otherwise the id must be one this PC issued for a folder of that same shared folder.
        string[] folderSegments;
        SharedRoot found;
        if (request.FolderEntryId is null) { found = _files.FindRoot(request.RootId ?? ""); folderSegments = []; }
        else
        {
            var folder = _files.UnsealEntry(request.FolderEntryId, out found);
            if (!folder.IsDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Choose a folder to upload into.");
            if (found.RootId != request.RootId) throw new ProtocolException(ErrorCode.InvalidRequest, "That folder belongs to a different shared folder.");
            folderSegments = folder.Segments;
        }
        var root = UploadRoot(found.RootId);
        var max = UploadLimits.MaxFileBytes(root);
        if (request.Size > max) throw new ProtocolException(ErrorCode.TooLarge, $"This file is larger than the {Format(max)} the PC owner allows for one upload here.");

        string destinationDisplay;
        string finalFolder;
        using (var pinned = _files.PinFolder(root, folderSegments))
        {
            finalFolder = pinned.FinalPath;
            if (finalFolder.Length + 1 + request.Name.Length > _options.MaxPathChars)
                throw new ProtocolException(ErrorCode.InvalidRequest, "The path of this file on the PC would be too long for Windows.");
            RequireSameDisk(root, pinned.FinalPath);
            if (request.Collision == "reject" && NameTaken(pinned.EnumerationPath, request.Name))
                throw new ProtocolException(ErrorCode.NameExists, "A file with this name is already in that folder.");
            destinationDisplay = root.Name;
        }

        Session session;
        lock (_lock)
        {
            var active = _sessions.Values.Where(s => s.State == "receiving").ToList();
            if (active.Count >= _options.MaxActive || active.Count(s => s.DeviceId == request.DeviceId) >= _options.MaxActive)
                throw new ProtocolException(ErrorCode.LimitExceeded, "Too many uploads are in progress on this PC. Wait for one to finish.");
            var reserved = active.Where(s => s.RootId == root.RootId).Sum(s => s.Size);
            if (UploadLimits.QuotaLeft(root) is { } left && request.Size + reserved > left)
                throw new ProtocolException(ErrorCode.TooLarge, "That would go over the space the PC owner allowed for uploads into this folder.");
            var free = FreeBytes(root.Path);
            if (request.Size + active.Sum(s => s.Size - s.Committed) + _options.ReserveFreeBytes > free)
                throw new ProtocolException(ErrorCode.DiskFull, "There is not enough free space on the PC for this file.");

            var partial = PartialDirectory(root);
            PreparePartialDirectory(partial);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var temp = Path.Join(partial, id + ".part");
            try { using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new ProtocolException(ErrorCode.Unavailable, "The PC could not prepare space for this file."); }
            var now = _clock.GetUtcNow();
            session = new Session
            {
                Id = id, RootId = root.RootId, DeviceId = request.DeviceId, Name = request.Name, Collision = request.Collision,
                FolderSegments = folderSegments, Size = request.Size, TempPath = temp, Created = now, LastActive = now,
            };
            _sessions[id] = session;
        }
        return new UploadStarted
        {
            UploadId = session.Id, Name = session.Name, Size = session.Size, ChunkBytes = _options.ChunkBytes, ExpiresUnixMs = Expires(session).ToUnixTimeMilliseconds(),
            Collision = session.Collision, Destination = [destinationDisplay, .. folderSegments], QuotaLeftBytes = UploadLimits.QuotaLeft(root),
        };
    }

    private void RequireSameDisk(SharedRoot root, string folderFinalPath)
    {
        var rootDisk = NativeFile.GetVolumeIdentity(Path.GetFullPath(root.Path));
        var folderDisk = NativeFile.GetVolumeIdentity(folderFinalPath);
        if (rootDisk is null || folderDisk is null || rootDisk != folderDisk)
            throw new ProtocolException(ErrorCode.Unavailable, "That folder is on a different disk than the shared folder.");
    }

    private static void PreparePartialDirectory(string partial)
    {
        if (Directory.Exists(partial))
        {
            if (File.GetAttributes(partial).HasFlag(FileAttributes.ReparsePoint)) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");
            return;
        }
        try
        {
            Directory.CreateDirectory(partial);
            File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ProtocolException(ErrorCode.Unavailable, "The PC could not prepare space for this file."); }
    }

    private DateTimeOffset Expires(Session s)
    {
        var idle = s.LastActive + _options.IdleTtl;
        var hard = s.Created + _options.MaxLifetime;
        return idle < hard ? idle : hard;
    }

    private Session Find(UploadRef reference) => Find(reference.UploadId, reference.DeviceId);

    /// <summary>A session is visible only to the browser that created it. Anything else looks exactly like "no such upload".</summary>
    private Session Find(string uploadId, string? deviceId)
    {
        Sweep();
        lock (_lock)
        {
            if (!_sessions.TryGetValue(uploadId ?? "", out var session) || !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
                throw new ProtocolException(ErrorCode.NotFound, "This upload is no longer available. Start it again.");
            return session;
        }
    }

    private UploadStatus StatusOf(Session s) => new()
    {
        UploadId = s.Id, State = s.State, Name = s.Name, Size = s.Size, CommittedBytes = s.State == "committed" ? s.Size : s.Committed,
        FinalName = s.FinalName, Renamed = s.Renamed, Sha256 = s.Sha256, OriginMarked = s.OriginMarked, Error = s.Error, ExpiresUnixMs = Expires(s).ToUnixTimeMilliseconds(),
    };

    public UploadStatus Status(UploadRef reference)
    {
        var session = Find(reference);
        if (session.State == "receiving") session.LastActive = _clock.GetUtcNow();
        return StatusOf(session);
    }

    // ----- data -----

    /// <summary>
    /// Takes one chunk. Chunks must arrive in order, exactly at the number of bytes already stored: a repeated or skipped chunk is
    /// not written and the answer says where the PC really is, so the browser can continue from there. A chunk is stored only after
    /// its SHA-256 matched, and a failed chunk leaves the stored bytes exactly as they were.
    /// </summary>
    public async Task<WriteChunkResult> WriteChunkAsync(WriteChunkRequest request, Func<CancellationToken, Task<Stream>> openBody, CancellationToken cancellation)
    {
        var session = Find(request.UploadId, request.DeviceId);
        if (!session.Gate.Wait(0)) throw new ProtocolException(ErrorCode.Conflict, "Another piece of this upload is still being received.");
        try
        {
            if (session.State != "receiving") throw new ProtocolException(session.State == "expired" ? ErrorCode.Expired : ErrorCode.Conflict, session.Error ?? "This upload is no longer receiving data.");
            UploadRoot(session.RootId);                                                   // the owner may have turned uploads off meanwhile
            if (request.Offset != session.Committed)
                return new WriteChunkResult { Accepted = false, Status = StatusOf(session) };
            if (request.Length <= 0 || request.Length > _options.MaxChunkBytes || request.Length > session.Size - session.Committed)
                throw new ProtocolException(ErrorCode.InvalidRequest, "That piece is larger than what is left of this upload.");
            if (request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit))
                throw new ProtocolException(ErrorCode.InvalidRequest, "Missing checksum for this piece.");

            var buffer = ArrayPool<byte>.Shared.Rent(request.Length);
            try
            {
                using (var body = await openBody(cancellation))
                {
                    try { await body.ReadExactlyAsync(buffer.AsMemory(0, request.Length), cancellation); }
                    catch (EndOfStreamException) { throw new ProtocolException(ErrorCode.Unavailable, "The connection ended before this piece arrived. Nothing from it was stored."); }
                }
                var data = buffer.AsMemory(0, request.Length);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(data.Span)), request.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ProtocolException(ErrorCode.ChecksumMismatch, "This piece arrived damaged. Nothing from it was stored; send it again.");
                if (_options.BeforeWrite is { } hook) await hook();
                WriteVerified(session, data, request.Offset, cancellation);
                session.Hash!.AppendData(data.Span);
                session.Committed += request.Length;
                session.LastActive = _clock.GetUtcNow();
                return new WriteChunkResult { Accepted = true, Status = StatusOf(session) };
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { session.Gate.Release(); }
    }

    private void WriteVerified(Session session, ReadOnlyMemory<byte> data, long offset, CancellationToken cancellation)
    {
        var root = UploadRoot(session.RootId);
        if (FreeBytes(root.Path) < data.Length + _options.ReserveFreeBytes) throw DiskFullAndFail(session);
        try
        {
            using var handle = File.OpenHandle(session.TempPath, FileMode.Open, FileAccess.Write, FileShare.None, FileOptions.None);
            RandomAccess.Write(handle, data.Span, offset);
            RandomAccess.FlushToDisk(handle);
        }
        catch (IOException e) when (IsDiskFull(e)) { throw DiskFullAndFail(session); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ProtocolException(ErrorCode.Unavailable, "The PC could not write this piece to disk."); }
        cancellation.ThrowIfCancellationRequested();
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 112 or 39 or 28;     // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL, ENOSPC

    private ProtocolException DiskFullAndFail(Session session)
    {
        Fail(session, "There is not enough free space on the PC. Nothing was saved.");
        return new ProtocolException(ErrorCode.DiskFull, "There is not enough free space on the PC. Nothing was saved.");
    }

    // ----- commit -----

    /// <summary>
    /// Turns the verified temporary file into the real file. Safe to ask twice: the second answer is the first result and nothing is created
    /// again. Names are never overwritten.
    /// </summary>
    public UploadStatus Finish(UploadRef reference)
    {
        var session = Find(reference);
        session.Gate.Wait();
        try
        {
            if (session.State == "committed") return StatusOf(session);
            if (session.State != "receiving") throw new ProtocolException(session.State == "expired" ? ErrorCode.Expired : ErrorCode.Conflict, session.Error ?? "This upload did not complete.");
            UploadRoot(session.RootId);                                                   // a folder the owner switched back to read-only takes no more writes
            if (session.Committed != session.Size)
                throw new ProtocolException(ErrorCode.Conflict, $"Only {session.Committed} of {session.Size} bytes arrived. The upload is not complete.");
            try
            {
                var root = UploadRoot(session.RootId);
                if (new FileInfo(session.TempPath).Length != session.Size) { Fail(session, "The received file is not the expected size. Nothing was saved."); throw new ProtocolException(ErrorCode.Conflict, session.Error!); }
                var digest = Convert.ToHexString(session.Hash!.GetHashAndReset()).ToLowerInvariant();
                session.OriginMarked = NativeFile.MarkAsFromInternet(session.TempPath);
                using var pinned = _files.PinFolder(root, session.FolderSegments);
                RequireSameDisk(root, pinned.FinalPath);
                var finalName = MoveIntoPlace(session, pinned.EnumerationPath);
                session.Sha256 = digest;
                session.FinalName = finalName;
                session.Renamed = finalName != session.Name;
                session.State = "committed";
                session.Ended = _clock.GetUtcNow();
                session.Hash.Dispose();
                session.Hash = null;
                _addUsed(root.RootId, session.Size);
                RemovePartialDirectoryIfEmpty(root);
                return StatusOf(session);
            }
            catch (ProtocolException e) when (session.State == "receiving") { Fail(session, e.Message); throw; }
            catch (Exception e) when (session.State == "receiving" && e is IOException or UnauthorizedAccessException)
            {
                Fail(session, "The PC could not finish saving the file. Nothing was saved.");
                throw new ProtocolException(ErrorCode.Unavailable, session.Error!);
            }
        }
        finally { session.Gate.Release(); }
    }

    private string MoveIntoPlace(Session session, string folderPath)
    {
        if (session.Collision == "reject")
        {
            if (NameTaken(folderPath, session.Name) || !NativeFile.MoveNoReplace(session.TempPath, Path.Join(folderPath, session.Name)))
                throw new ProtocolException(ErrorCode.NameExists, "A file with this name appeared in that folder meanwhile. Nothing was saved.");
            return session.Name;
        }
        for (var number = 1; number <= 999; number++)
        {
            var candidate = number == 1 ? session.Name : NumberedName(session.Name, number);
            if (candidate.Length > 255 || NameTaken(folderPath, candidate)) continue;
            if (NativeFile.MoveNoReplace(session.TempPath, Path.Join(folderPath, candidate))) return candidate;
        }
        throw new ProtocolException(ErrorCode.NameExists, "Too many files with this name already exist in that folder.");
    }

    // ----- ending a session without a file -----

    private void Fail(Session session, string message)
    {
        if (session.State != "receiving") return;
        session.State = "failed";
        session.Error = message;
        End(session);
    }

    private void End(Session session)
    {
        session.Ended = _clock.GetUtcNow();
        session.Hash?.Dispose();
        session.Hash = null;
        TryDelete(session.TempPath);
        if (_roots().FirstOrDefault(r => r.RootId == session.RootId) is { } root) RemovePartialDirectoryIfEmpty(root);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void RemovePartialDirectoryIfEmpty(SharedRoot root)
    {
        try
        {
            var directory = PartialDirectory(root);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public Ack Cancel(UploadRef reference)
    {
        var session = Find(reference);
        session.Gate.Wait();
        try
        {
            if (session.State == "receiving") { session.State = "cancelled"; session.Error = "Cancelled."; End(session); }
            return new Ack { Ok = true };   // a file that was already committed is not taken back
        }
        finally { session.Gate.Release(); }
    }

    private void CancelWhere(Func<Session, bool> predicate, string reason)
    {
        List<Session> hit;
        lock (_lock) hit = [.. _sessions.Values.Where(s => s.State == "receiving" && predicate(s))];
        foreach (var session in hit)
        {
            session.Gate.Wait();
            try { if (session.State == "receiving") { session.State = "cancelled"; session.Error = reason; End(session); } }
            finally { session.Gate.Release(); }
        }
    }

    /// <summary>A browser was revoked or unpaired: everything it had in flight is discarded.</summary>
    public Ack CancelForDevice(string deviceId)
    {
        CancelWhere(s => s.DeviceId == deviceId, "The browser's access was removed.");
        return new Ack { Ok = true };
    }

    /// <summary>The owner removed a folder or turned its uploads off.</summary>
    public void DropForRoot(string rootId) => CancelWhere(s => s.RootId == rootId, "Uploads into this folder were turned off.");

    /// <summary>Removes sessions that went quiet or passed their lifetime, and forgets old finished ones. Cheap enough to call often.</summary>
    public void Sweep()
    {
        var now = _clock.GetUtcNow();
        List<Session> expired, forgotten;
        lock (_lock)
        {
            expired = [.. _sessions.Values.Where(s => s.State == "receiving" && now >= Expires(s))];
            forgotten = [.. _sessions.Values.Where(s => s.State != "receiving" && now - s.Ended > _options.IdleTtl)];
            foreach (var s in forgotten) _sessions.Remove(s.Id);
        }
        foreach (var session in expired)
        {
            if (!session.Gate.Wait(0)) continue;                      // being written right now: it is not idle
            try { if (session.State == "receiving") { session.State = "expired"; session.Error = "This upload expired."; End(session); } }
            finally { session.Gate.Release(); }
        }
    }

    /// <summary>
    /// Run once when the agent starts, before any session exists: whatever is in a private partial folder belongs to a run that ended
    /// without finishing, and a half-received file is never kept.
    /// </summary>
    public void CleanupAfterRestart()
    {
        foreach (var root in _roots())
        {
            try
            {
                var directory = PartialDirectory(root);
                if (!Directory.Exists(directory)) continue;
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(directory, false); continue; }
                foreach (var file in Directory.EnumerateFiles(directory)) TryDelete(file);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GiB" : $"{bytes / (double)(1L << 20):0.#} MiB";
}
