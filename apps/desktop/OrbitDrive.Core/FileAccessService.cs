using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

/// <summary>An opened, validated source file. Bytes are served from this handle, never from a re-opened path.</summary>
public sealed class ValidatedRead(SafeFileHandle handle, long offset, long length, string name = "") : IDisposable
{
    public string Name { get; } = name;
    /// <summary>Bytes of this range written to the relay so far (not what a browser saved).</summary>
    public long BytesSent;
    public SafeFileHandle Handle { get; } = handle;
    public long Offset { get; } = offset;
    public long Length { get; } = length;
    public void Dispose() => Handle.Dispose();
}

/// <summary>Local, read-only filesystem policy. Applied to every instruction regardless of gateway authorisation.</summary>
public sealed class FileAccessService(Func<IReadOnlyList<SharedRoot>> roots, SealedTokens tokens, Func<string, string?>? volumeIdentity = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    /// <summary>Marks a read token whose file is one the owner picked in Explorer rather than one under a shared folder.</summary>
    public const string OneShotRoot = "~oneshot";
    private readonly Func<string, string?> _volumeIdentity = volumeIdentity ?? NativeFile.GetVolumeIdentity;
    /// <summary>Thumbnail generation (D7); null leaves previews of pictures unavailable and everything else unchanged.</summary>
    public ThumbnailService? Thumbnails { get; set; }
    /// <summary>Staged folder/multi-file ZIPs (D8); null means archives are not offered.</summary>
    public ArchiveService? Archives { get; set; }
    private const string DifferentDisk = "A different disk is at this location now. Share the folder again on the PC to allow access.";

    /// <summary>True when the folder exists AND is still on the disk it was approved on.</summary>
    public bool IsAvailable(SharedRoot root) => Directory.Exists(root.Path) && (root.VolumeId is null || _volumeIdentity(root.Path) == root.VolumeId);

    /// <summary>Refuses a root whose path now leads to another volume than the one the owner approved.</summary>
    private void EnsureApprovedVolume(SharedRoot root)
    {
        if (root.VolumeId is null) return;
        var current = _volumeIdentity(root.Path);
        if (current is null) throw new ProtocolException(ErrorCode.Unavailable, "The shared folder is currently unavailable.");
        if (current != root.VolumeId) throw new ProtocolException(ErrorCode.Unavailable, DifferentDisk);
    }

    public const int MaxPageSize = 200;
    public const int HashBufferBytes = 1 << 20;

    public RootListResponse ListRoots() => new()
    {
        Roots = [.. roots().Select(r => new RootSummary
        {
            RootId = r.RootId, Name = r.Name, ReadOnly = !r.UploadsEnabled, Available = IsAvailable(r),
            CanUpload = r.UploadsEnabled, MaxUploadBytes = r.UploadsEnabled ? UploadLimits.MaxFileBytes(r) : 0,
            UploadQuotaLeftBytes = r.UploadsEnabled ? UploadLimits.QuotaLeft(r) : null,
        })],
    };

    internal SharedRoot FindRoot(string rootId) =>
        roots().FirstOrDefault(r => r.RootId == rootId) ?? throw new ProtocolException(ErrorCode.NotFound, "That shared folder is not available.");

    private static string RootFinalPath(SharedRoot root)
    {
        try { return NativeFile.GetFinalDirectoryPath(Path.GetFullPath(root.Path)); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { throw new ProtocolException(ErrorCode.Unavailable, "The shared folder is currently unavailable."); }
    }

    /// <summary>
    /// Opens a folder inside a root and proves the OPENED folder is inside the root (not a link or junction, not another disk).
    /// The caller works through the returned handle's path and disposes it.
    /// </summary>
    internal NativeFile.PinnedDirectory PinFolder(SharedRoot root, string[] segments)
    {
        EnsureApprovedVolume(root);
        var path = PathPolicy.Resolve(root, segments, expectDirectory: true);
        NativeFile.PinnedDirectory pinned;
        try { pinned = NativeFile.OpenDirectory(path, followFinalLink: segments.Length == 0); }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this folder."); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        { throw new ProtocolException(segments.Length == 0 ? ErrorCode.Unavailable : ErrorCode.NotFound, segments.Length == 0 ? "The shared folder is currently unavailable." : "The item no longer exists."); }
        try
        {
            var rootFinal = RootFinalPath(root);
            var inside = segments.Length == 0
                ? string.Equals(pinned.FinalPath.TrimEnd(Path.DirectorySeparatorChar), rootFinal.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                : !pinned.IsLink && NativeFile.IsUnder(pinned.FinalPath, rootFinal);
            if (!inside) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    /// <summary>
    /// The one place a folder's children are read. The folder is opened, the OPENED folder is proven to be inside the shared root, and
    /// the listing goes through that same handle. Links, unsafe names, system items and (unless the owner allowed them) hidden items
    /// are left out. Browsing, searching and archiving all use this, so they share one access boundary.
    /// </summary>
    internal List<FileSystemInfo> EnumerateChildren(SharedRoot root, string[] segments, out int skipped)
    {
        using var pinned = PinFolder(root, segments);
        var skippedHere = 0;
        var items = new List<FileSystemInfo>();
        try
        {
            foreach (var info in new DirectoryInfo(pinned.EnumerationPath).EnumerateFileSystemInfos())
            {
                if (PathPolicy.IsInternalName(info.Name)) continue;                  // the private folder of files still being received
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !PathPolicy.IsSafeSegment(info.Name)) { skippedHere++; continue; }
                if (info.Attributes.HasFlag(FileAttributes.System)) continue;
                if (info.Attributes.HasFlag(FileAttributes.Hidden) && !root.ShowHidden) continue;
                items.Add(info);
            }
        }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this folder."); }
        catch (DirectoryNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists."); }
        catch (IOException) { throw new ProtocolException(ErrorCode.Unavailable, "The folder could not be read right now."); }
        skipped = skippedHere;
        return items;
    }

    internal TokenBody UnsealEntry(string? entryId, out SharedRoot root)
    {
        var body = tokens.Unseal(entryId, "e");
        root = FindRoot(body.RootId);
        return body;
    }

    internal string SealEntry(SharedRoot root, string[] segments, bool isDirectory) =>
        tokens.Seal(new TokenBody { Type = "e", RootId = root.RootId, Segments = segments, IsDirectory = isDirectory });

    public DirectoryPage ListDirectory(DirectoryQuery query)
    {
        var root = FindRoot(query.RootId);
        string[] segments = [];
        if (query.EntryId is not null)
        {
            var body = tokens.Unseal(query.EntryId, "e");
            if (body.RootId != root.RootId || !body.IsDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Identifier does not belong to this folder.");
            segments = body.Segments;
        }
        var items = EnumerateChildren(root, segments, out var skipped);

        var offset = 0;
        if (query.Cursor is not null)
        {
            var cursor = tokens.Unseal(query.Cursor, "c");
            if (cursor.RootId != root.RootId || !cursor.Segments.SequenceEqual(segments) || cursor.Offset < 0)
                throw new ProtocolException(ErrorCode.InvalidRequest, "Cursor does not match this folder.");
            offset = cursor.Offset;
        }

        items.Sort((a, b) =>
        {
            var ad = a is DirectoryInfo; var bd = b is DirectoryInfo;
            if (ad != bd) return ad ? -1 : 1;
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = items.Skip(offset).Take(pageSize).Select(info =>
        {
            var isDirectory = info is DirectoryInfo;
            return new FileEntry
            {
                EntryId = tokens.Seal(new TokenBody { Type = "e", RootId = root.RootId, Segments = [.. segments, info.Name], IsDirectory = isDirectory }),
                Name = info.Name,
                Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
                Size = info is FileInfo file ? file.Length : 0,
                ModifiedUnixMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                Hidden = info.Attributes.HasFlag(FileAttributes.Hidden),
            };
        }).ToList();

        var breadcrumbs = new List<Breadcrumb> { new() { EntryId = null, Name = root.Name } };
        for (var i = 0; i < segments.Length; i++)
            breadcrumbs.Add(new Breadcrumb
            {
                EntryId = tokens.Seal(new TokenBody { Type = "e", RootId = root.RootId, Segments = segments[..(i + 1)], IsDirectory = true }),
                Name = segments[i],
            });

        var next = offset + page.Count;
        return new DirectoryPage
        {
            Entries = page,
            Breadcrumbs = breadcrumbs,
            SkippedLinks = skipped,
            NextCursor = next < items.Count ? tokens.Seal(new TokenBody { Type = "c", RootId = root.RootId, Segments = segments, Offset = next }) : null,
        };
    }

    /// <summary>
    /// Favorites and recents are remembered as ordinary entry IDs. Before one is shown or used it is asked about here: it must still be
    /// genuine for THIS PC, still inside a folder that is shared now, and still exist, otherwise it is reported as unavailable (never as data).
    /// </summary>
    public CheckEntriesResult CheckEntries(CheckEntriesRequest request)
    {
        if (request.EntryIds is null || request.EntryIds.Count > 100) throw new ProtocolException(ErrorCode.InvalidRequest, "Check at most 100 items at a time.");
        var result = new List<EntryCheck>();
        foreach (var id in request.EntryIds)
        {
            try
            {
                var body = tokens.Unseal(id, "e");
                var root = FindRoot(body.RootId);
                if (!IsAvailable(root)) throw new ProtocolException(ErrorCode.Unavailable, "The shared folder is not available right now.");
                var path = PathPolicy.Resolve(root, body.Segments, expectDirectory: body.IsDirectory);
                FileSystemInfo info = body.IsDirectory ? new DirectoryInfo(path) : new FileInfo(path);
                if (info.Attributes.HasFlag(FileAttributes.System) || (info.Attributes.HasFlag(FileAttributes.Hidden) && !root.ShowHidden))
                    throw new ProtocolException(ErrorCode.NotFound, "The item is no longer shared.");
                result.Add(new EntryCheck
                {
                    EntryId = id, Available = true, Reason = null,
                    Entry = new FileEntry
                    {
                        EntryId = id, Name = body.Segments[^1], Kind = body.IsDirectory ? EntryKind.Directory : EntryKind.File,
                        Size = info is FileInfo f ? f.Length : 0, ModifiedUnixMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), Hidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                    },
                });
            }
            catch (ProtocolException e) { result.Add(new EntryCheck { EntryId = id, Available = false, Entry = null, Reason = e.Code == ErrorCode.InvalidRequest ? "This item does not belong to this PC." : e.Message }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Add(new EntryCheck { EntryId = id, Available = false, Entry = null, Reason = "The item could not be read." }); }
        }
        return new CheckEntriesResult { Entries = result };
    }

    /// <summary>Opens the file, then proves the opened handle is a single-link regular file that really lives under the root.</summary>
    internal SafeFileHandle OpenValidated(SharedRoot root, string[] segments)
    {
        EnsureApprovedVolume(root);
        var path = PathPolicy.Resolve(root, segments, expectDirectory: false);
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists."); }
        catch (DirectoryNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists."); }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this file."); }
        catch (IOException) { throw new ProtocolException(ErrorCode.Unavailable, "The file is in use by another program and cannot be read right now."); }
        try
        {
            var (links, isReparse) = NativeFile.GetLinkInfo(handle);
            if (isReparse || !NativeFile.IsUnder(NativeFile.GetFinalPath(handle), RootFinalPath(root)))
                throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");
            if (links > 1) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Files with multiple hard links are not shared.");
            // The opened file itself must be on the approved volume (a mount point inside the folder is another disk).
            if (root.VolumeId is not null && volumeIdentity is null && NativeFile.GetVolumeIdentity(handle) is { } volume && volume != root.VolumeId)
                throw new ProtocolException(ErrorCode.Unavailable, DifferentDisk);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    public sealed record OneShotOffer(string Name, long Size, long ModifiedUnixMs, string Grant);

    /// <summary>
    /// The owner picked this exact file (Explorer "Send to my phone"). It is sealed into a grant bound to the file's real location, size,
    /// modified time and filesystem change stamp, and to an expiry. The grant reveals nothing about the parent folder to the relay or the
    /// phone, creates no shared folder, and works only for the receiver the PC names in the invitation.
    /// </summary>
    public OneShotOffer IssueOneShot(string path, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7)) throw new ProtocolException(ErrorCode.InvalidRequest, "Invalid lifetime.");
        using var handle = OpenOneShotHandle(path, out var finalPath);
        var name = Path.GetFileName(finalPath);
        if (!PathPolicy.IsSafeSegment(name)) throw new ProtocolException(ErrorCode.InvalidRequest, "This file name cannot be sent.");
        var modified = File.GetLastWriteTimeUtc(handle);
        var size = RandomAccess.GetLength(handle);
        var grant = tokens.Seal(new TokenBody
        {
            Type = "o", RootId = OneShotRoot, Segments = [finalPath], Size = size, ModifiedTicks = modified.Ticks,
            ChangeStamp = NativeFile.GetChangeStamp(handle), ExpiresUnixMs = _clock.GetUtcNow().Add(lifetime).ToUnixTimeMilliseconds(),
        });
        return new OneShotOffer(name, size, new DateTimeOffset(modified).ToUnixTimeMilliseconds(), grant);
    }

    /// <summary>
    /// Windows reports final paths in the \\?\ form. A grant stores (and later re-opens) the ordinary drive form; a network location
    /// (\\?\UNC\...) is not a file on this PC's own drives and is refused.
    /// </summary>
    private static string PlainFinalPath(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new ProtocolException(ErrorCode.InvalidRequest, "Only files on this PC's own drives can be sent.");
        return finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) && finalPath.Length > 6 && finalPath[5] == ':' ? finalPath[4..] : finalPath;
    }

    /// <summary>Opens one explicitly chosen file read-only and proves the opened object is a plain single-link regular file.</summary>
    private static SafeFileHandle OpenOneShotHandle(string path, out string finalPath)
    {
        if (path.Length is 0 or > 32000 || path.Contains('\0') || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && path.IndexOf(':', 2) >= 0))
            throw new ProtocolException(ErrorCode.InvalidRequest, "Only files on this PC's own drives can be sent.");
        if (Directory.Exists(path)) throw new ProtocolException(ErrorCode.InvalidRequest, "Folders cannot be sent yet.");
        try { var info = new FileInfo(path); if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and shortcuts are not followed. Send the file they point to."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* reported precisely by the open below */ }
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "The file no longer exists."); }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this file."); }
        catch (IOException) { throw new ProtocolException(ErrorCode.Unavailable, "The file is in use by another program and cannot be read right now."); }
        try
        {
            var (links, isReparse) = NativeFile.GetLinkInfo(handle);
            if (isReparse) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");
            if (links > 1) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Files with multiple hard links are not sent.");
            finalPath = PlainFinalPath(NativeFile.GetFinalPath(handle));
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Re-opens a granted file and proves it is still the very file, at the very revision, the owner offered.</summary>
    private SafeFileHandle OpenGranted(TokenBody grant)
    {
        if (grant.ExpiresUnixMs <= _clock.GetUtcNow().ToUnixTimeMilliseconds()) throw new ProtocolException(ErrorCode.Expired, "The permission to send this file ran out.");
        var handle = OpenOneShotHandle(grant.Segments[0], out var finalPath);
        try
        {
            var sameFile = string.Equals(finalPath, grant.Segments[0], OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!sameFile) throw new ProtocolException(ErrorCode.NotFound, "The file was moved or replaced.");
            if (RandomAccess.GetLength(handle) != grant.Size || File.GetLastWriteTimeUtc(handle).Ticks != grant.ModifiedTicks ||
                (grant.ChangeStamp != 0 && NativeFile.GetChangeStamp(handle) != grant.ChangeStamp))
                throw new ProtocolException(ErrorCode.RevisionChanged, "The file changed after it was offered.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Hashes the file under a share-read handle (writers are blocked on Windows) to produce a content-verified revision.</summary>
    public async Task<PrepareReadResult> PrepareReadAsync(PrepareReadRequest request, CancellationToken cancellation)
    {
        if (request.Variant == "archive")
            return (Archives ?? throw new ProtocolException(ErrorCode.Unavailable, "Archives are not available on this PC.")).PrepareRead(request.EntryId);
        var oneShot = tokens.TryUnseal(request.EntryId, "o", out var grant);
        var body = oneShot ? grant : tokens.Unseal(request.EntryId, "e");
        if (body.IsDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Folders cannot be downloaded as a single file.");
        var root = oneShot ? null : FindRoot(body.RootId);
        using var handle = oneShot ? OpenGranted(body) : OpenValidated(root!, body.Segments);
        var size = RandomAccess.GetLength(handle);
        var modified = File.GetLastWriteTimeUtc(handle);
        var changeStamp = NativeFile.GetChangeStamp(handle);
        var variant = request.Variant ?? "original";
        if (variant != "original") return await PrepareDerivedAsync(variant, body, oneShot, root, handle, size, modified, changeStamp, cancellation).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(HashBufferBytes);
        try
        {
            long position = 0;
            while (position < size)
            {
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, HashBufferBytes), position, cancellation).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                position += read;
            }
            if (position != size || RandomAccess.GetLength(handle) != size || File.GetLastWriteTimeUtc(handle) != modified || NativeFile.GetChangeStamp(handle) != changeStamp)
                throw new ProtocolException(ErrorCode.RevisionChanged, "The file changed while it was being prepared.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new PrepareReadResult
        {
            Name = oneShot ? Path.GetFileName(body.Segments[0]) : body.Segments[^1],
            Size = size,
            ModifiedUnixMs = new DateTimeOffset(modified).ToUnixTimeMilliseconds(),
            Revision = "sha256-" + sha,
            ReadToken = tokens.Seal(new TokenBody { Type = "r", RootId = oneShot ? OneShotRoot : root!.RootId, Segments = body.Segments, Size = size, ModifiedTicks = modified.Ticks, ChangeStamp = changeStamp, Sha256 = sha, ExpiresUnixMs = body.ExpiresUnixMs }),
        };
    }

    /// <summary>
    /// Preview variants. "preview" answers quickly for any size (a media file can be gigabytes): its revision is derived from size,
    /// modified time and the filesystem change stamp instead of reading the content, and the type is judged from the first bytes.
    /// "thumb:N" returns a read token for a generated thumbnail; every later read re-checks the source file first.
    /// </summary>
    private async Task<PrepareReadResult> PrepareDerivedAsync(string variant, TokenBody body, bool oneShot, SharedRoot? root, SafeFileHandle handle, long size, DateTime modified, long changeStamp, CancellationToken cancellation)
    {
        var name = oneShot ? Path.GetFileName(body.Segments[0]) : body.Segments[^1];
        var head = new byte[(int)Math.Min(size, ContentSniffer.HeadBytes)];
        var got = 0;
        while (got < head.Length) { var n = RandomAccess.Read(handle, head.AsSpan(got), got); if (n == 0) break; got += n; }
        var sniffed = ContentSniffer.Classify(head.AsSpan(0, got), name);
        var rootId = oneShot ? OneShotRoot : root!.RootId;
        var unix = new DateTimeOffset(modified).ToUnixTimeMilliseconds();
        if (variant == "preview")
        {
            if (!sniffed.Inlineable) throw new ProtocolException(ErrorCode.InvalidRequest, "This kind of file has no preview. Download it instead.");
            var revision = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"meta-v1|{size}|{modified.Ticks}|{changeStamp}")));
            return new PrepareReadResult
            {
                Name = name, Size = size, ModifiedUnixMs = unix, Revision = "sha256-" + revision, ContentType = sniffed.ContentType,
                ReadToken = tokens.Seal(new TokenBody { Type = "r", RootId = rootId, Segments = body.Segments, Size = size, ModifiedTicks = modified.Ticks, ChangeStamp = changeStamp, Sha256 = revision, ExpiresUnixMs = body.ExpiresUnixMs }),
            };
        }
        if (!variant.StartsWith("thumb:", StringComparison.Ordinal) || !int.TryParse(variant.AsSpan(6), out var edge) || !ThumbnailOptions.Edges.Contains(edge))
            throw new ProtocolException(ErrorCode.InvalidRequest, "That kind of preview is not offered.");
        if (Thumbnails is null) throw new ProtocolException(ErrorCode.Unavailable, "Thumbnails are not available on this PC.");
        var key = ThumbnailCache.KeyFor(edge, rootId, body.Segments, size, modified.Ticks, changeStamp);
        var (derived, derivedSize) = await Thumbnails.GetAsync(handle, size, key, edge, name, cancellation).ConfigureAwait(false);
        using (derived)
        {
            var bytes = new byte[derivedSize];
            RandomAccess.Read(derived, bytes, 0);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new PrepareReadResult
            {
                Name = Path.GetFileNameWithoutExtension(name) + ".thumb.webp", Size = derivedSize, ModifiedUnixMs = unix, Revision = "sha256-" + sha, ContentType = "image/webp",
                ReadToken = tokens.Seal(new TokenBody { Type = "p", RootId = rootId, Segments = body.Segments, Size = size, ModifiedTicks = modified.Ticks, ChangeStamp = changeStamp, Sha256 = sha, CacheKey = key, DerivedSize = derivedSize, ExpiresUnixMs = body.ExpiresUnixMs }),
            };
        }
    }

    /// <summary>Re-validates root, path, handle and revision for one byte range. The root must still be approved now.</summary>
    public ValidatedRead OpenRead(string readToken, long offset, long length)
    {
        if (tokens.TryUnseal(readToken, "z", out var archiveBody))
            return (Archives ?? throw new ProtocolException(ErrorCode.Unavailable, "Archives are not available on this PC.")).OpenRead(archiveBody, offset, length);
        var derivedRead = tokens.TryUnseal(readToken, "p", out var derivedBody);
        var body = derivedRead ? derivedBody : tokens.Unseal(readToken, "r");
        var oneShot = body.RootId == OneShotRoot;
        var servedSize = derivedRead ? body.DerivedSize : body.Size;
        var root = oneShot ? null : FindRoot(body.RootId);
        if (offset < 0 || length < 0 || offset > servedSize || length > servedSize - offset)
            throw new ProtocolException(ErrorCode.RangeNotSatisfiable, "Requested range is outside the file.");
        var handle = oneShot ? OpenGranted(body) : OpenValidated(root!, body.Segments);
        // Size and modified time can be put back by whoever rewrites a file; the filesystem change stamp cannot.
        if (RandomAccess.GetLength(handle) != body.Size || File.GetLastWriteTimeUtc(handle).Ticks != body.ModifiedTicks ||
            (body.ChangeStamp != 0 && NativeFile.GetChangeStamp(handle) != body.ChangeStamp))
        {
            handle.Dispose();
            throw new ProtocolException(ErrorCode.RevisionChanged, "The file changed after this download was prepared.");
        }
        if (derivedRead)
        {
            // The source was just proven unchanged and still shared; only then is its derived thumbnail handed out.
            handle.Dispose();
            var cached = Thumbnails?.Cache.TryOpen(body.CacheKey ?? "") ?? throw new ProtocolException(ErrorCode.NotFound, "The preview is no longer stored. Ask for it again.");
            if (RandomAccess.GetLength(cached) != body.DerivedSize) { cached.Dispose(); throw new ProtocolException(ErrorCode.RevisionChanged, "The preview changed."); }
            return new ValidatedRead(cached, offset, length, "thumbnail.webp");
        }
        return new ValidatedRead(handle, offset, length, oneShot ? Path.GetFileName(body.Segments[0]) : body.Segments[^1]);
    }
}
