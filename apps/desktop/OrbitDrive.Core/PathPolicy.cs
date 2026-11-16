using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

/// <summary>A folder the owner approved locally. Remote callers can never create or alter one.</summary>
public sealed record SharedRoot(string RootId, string Name, string Path, bool ShowHidden = false);

public static class PathPolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
    };

    /// <summary>True when a single path component is safe to join beneath a root on Windows and Unix.</summary>
    public static bool IsSafeSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > 255 || segment is "." or "..") return false;
        foreach (var c in segment)
            if (c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\u007f') return false;
        if (segment[^1] is '.' or ' ') return false; // Windows silently strips these, aliasing another name
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }

    /// <summary>
    /// Walks each component beneath the root, refusing links/junctions and non-directories on the way.
    /// The caller must still validate the opened handle; this walk alone is not the access boundary.
    /// </summary>
    public static string Resolve(SharedRoot root, IReadOnlyList<string> segments, bool expectDirectory)
    {
        if (segments.Count > 128) throw new ProtocolException(ErrorCode.InvalidRequest, "Path is too deep.");
        var current = System.IO.Path.GetFullPath(root.Path);
        if (!Directory.Exists(current)) throw new ProtocolException(ErrorCode.Unavailable, "The shared folder is currently unavailable.");
        for (var i = 0; i < segments.Count; i++)
        {
            if (!IsSafeSegment(segments[i])) throw new ProtocolException(ErrorCode.InvalidRequest, "Invalid path component.");
            current = System.IO.Path.Join(current, segments[i]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists."); }
            catch (UnauthorizedAccessException)
            { throw new ProtocolException(ErrorCode.AccessDeniedLocal, "Windows denied access to this item."); }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ProtocolException(ErrorCode.LinkNotAllowed, "Links and junctions are not followed.");
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var last = i == segments.Count - 1;
            if (!last && !isDirectory) throw new ProtocolException(ErrorCode.NotFound, "The item no longer exists.");
            if (last && isDirectory != expectDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Item type mismatch.");
        }
        if (segments.Count == 0 && !expectDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Item type mismatch.");
        return current;
    }
}
