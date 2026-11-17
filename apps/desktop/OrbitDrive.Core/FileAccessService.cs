using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

/// <summary>An opened, validated source file. Bytes are served from this handle, never from a re-opened path.</summary>
public sealed class ValidatedRead(SafeFileHandle handle, long offset, long length) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;
    public long Offset { get; } = offset;
    public long Length { get; } = length;
    public void Dispose() => Handle.Dispose();
}

/// <summary>Local, read-only filesystem policy. Applied to every instruction regardless of gateway authorisation.</summary>
public sealed class FileAccessService(Func<IReadOnlyList<SharedRoot>> roots, SealedTokens tokens, Func<string, string?>? volumeIdentity = null)
{
    private readonly Func<string, string?> _volumeIdentity = volumeIdentity ?? NativeFile.GetVolumeIdentity;
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
        Roots = [.. roots().Select(r => new RootSummary { RootId = r.RootId, Name = r.Name, ReadOnly = true, Available = IsAvailable(r) })],
    };

    private SharedRoot FindRoot(string rootId) =>
        roots().FirstOrDefault(r => r.RootId == rootId) ?? throw new ProtocolException(ErrorCode.NotFound, "That shared folder is not available.");

    private static string RootFinalPath(SharedRoot root)
    {
        try { return NativeFile.GetFinalDirectoryPath(Path.GetFullPath(root.Path)); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { throw new ProtocolException(ErrorCode.Unavailable, "The shared folder is currently unavailable."); }
    }

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
        EnsureApprovedVolume(root);
        var path = PathPolicy.Resolve(root, segments, expectDirectory: true);
        // Open the folder, prove the OPENED folder is inside the shared root, and list through that same handle.
        // (Validating the path and then listing it by name again would let a folder be swapped for a link in between.)
        NativeFile.PinnedDirectory pinned;
        try { pinned = NativeFile.OpenDirectory(path, followFinalLink: segments.Length == 0); }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this folder."); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        { throw new ProtocolException(segments.Length == 0 ? ErrorCode.Unavailable : ErrorCode.NotFound, segments.Length == 0 ? "The shared folder is currently unavailable." : "The item no longer exists."); }
        using var held = pinned;
        var rootFinal = RootFinalPath(root);
        var inside = segments.Length == 0
            ? string.Equals(pinned.FinalPath.TrimEnd(Path.DirectorySeparatorChar), rootFinal.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            : !pinned.IsLink && NativeFile.IsUnder(pinned.FinalPath, rootFinal);
        if (!inside) throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");

        var offset = 0;
        if (query.Cursor is not null)
        {
            var cursor = tokens.Unseal(query.Cursor, "c");
            if (cursor.RootId != root.RootId || !cursor.Segments.SequenceEqual(segments) || cursor.Offset < 0)
                throw new ProtocolException(ErrorCode.InvalidRequest, "Cursor does not match this folder.");
            offset = cursor.Offset;
        }

        var skipped = 0;
        var items = new List<FileSystemInfo>();
        try
        {
            foreach (var info in new DirectoryInfo(pinned.EnumerationPath).EnumerateFileSystemInfos())
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !PathPolicy.IsSafeSegment(info.Name)) { skipped++; continue; }
                if (info.Attributes.HasFlag(FileAttributes.System)) continue;
                if (info.Attributes.HasFlag(FileAttributes.Hidden) && !root.ShowHidden) continue;
                items.Add(info);
            }
        }
        catch (UnauthorizedAccessException) { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this folder."); }
        catch (DirectoryNotFoundException) { throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists."); }
        catch (IOException) { throw new ProtocolException(ErrorCode.Unavailable, "The folder could not be read right now."); }

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

    /// <summary>Opens the file, then proves the opened handle is a single-link regular file that really lives under the root.</summary>
    private SafeFileHandle OpenValidated(SharedRoot root, string[] segments)
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

    /// <summary>Hashes the file under a share-read handle (writers are blocked on Windows) to produce a content-verified revision.</summary>
    public async Task<PrepareReadResult> PrepareReadAsync(PrepareReadRequest request, CancellationToken cancellation)
    {
        var body = tokens.Unseal(request.EntryId, "e");
        if (body.IsDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Folders cannot be downloaded as a single file.");
        var root = FindRoot(body.RootId);
        using var handle = OpenValidated(root, body.Segments);
        var size = RandomAccess.GetLength(handle);
        var modified = File.GetLastWriteTimeUtc(handle);
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
            if (position != size || RandomAccess.GetLength(handle) != size || File.GetLastWriteTimeUtc(handle) != modified)
                throw new ProtocolException(ErrorCode.RevisionChanged, "The file changed while it was being prepared.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new PrepareReadResult
        {
            Name = body.Segments[^1],
            Size = size,
            ModifiedUnixMs = new DateTimeOffset(modified).ToUnixTimeMilliseconds(),
            Revision = "sha256-" + sha,
            ReadToken = tokens.Seal(new TokenBody { Type = "r", RootId = root.RootId, Segments = body.Segments, Size = size, ModifiedTicks = modified.Ticks, Sha256 = sha }),
        };
    }

    /// <summary>Re-validates root, path, handle and revision for one byte range. The root must still be approved now.</summary>
    public ValidatedRead OpenRead(string readToken, long offset, long length)
    {
        var body = tokens.Unseal(readToken, "r");
        var root = FindRoot(body.RootId);
        if (offset < 0 || length < 0 || offset > body.Size || length > body.Size - offset)
            throw new ProtocolException(ErrorCode.RangeNotSatisfiable, "Requested range is outside the file.");
        var handle = OpenValidated(root, body.Segments);
        if (RandomAccess.GetLength(handle) != body.Size || File.GetLastWriteTimeUtc(handle).Ticks != body.ModifiedTicks)
        {
            handle.Dispose();
            throw new ProtocolException(ErrorCode.RevisionChanged, "The file changed after this download was prepared.");
        }
        return new ValidatedRead(handle, offset, length);
    }
}
