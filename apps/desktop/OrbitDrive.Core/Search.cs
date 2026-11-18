using System.Collections.Concurrent;
using System.Diagnostics;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

public sealed record SearchOptions
{
    public int DefaultLimit { get; init; } = 50;
    public int MaxLimit { get; init; } = 100;
    public int MaxQueryChars { get; init; } = 100;
    public int MaxFoldersVisited { get; init; } = 20_000;
    public int MaxEntriesScanned { get; init; } = 300_000;
    public int MaxDepth { get; init; } = 48;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(8);
    public int MaxConcurrent { get; init; } = 2;
}

/// <summary>
/// Filename search over the folders the owner shared. It walks the live folders (no index is kept, so results are never stale and
/// nothing about unshared data is stored) breadth-first through the same access boundary as browsing: links, unsafe names, system
/// items and hidden items (unless allowed) are not seen, and a removed or unavailable folder is not searched. It is bounded in time,
/// folders, entries and results, can be cancelled, and says plainly when the answer is not complete.
/// </summary>
public sealed class SearchService(FileAccessService files, Func<IReadOnlyList<SharedRoot>> roots, SearchOptions? options = null)
{
    private readonly SearchOptions _options = options ?? new SearchOptions();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _slots = new((options ?? new SearchOptions()).MaxConcurrent);

    private static bool ValidSearchId(string? id) => id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public Ack Cancel(CancelSearchRequest request)
    {
        if (ValidSearchId(request.SearchId) && _running.TryGetValue(request.SearchId, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        return new Ack { Ok = true };
    }

    /// <summary>Cancels everything for a folder that was just removed from sharing (the searches end with what they already had).</summary>
    public void CancelAll() { foreach (var cts in _running.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } } }

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellation)
    {
        var query = (request.Query ?? "").Trim();
        if (query.Length == 0 || query.Length > _options.MaxQueryChars || query.Any(char.IsControl))
            throw new ProtocolException(ErrorCode.InvalidRequest, "Type between 1 and " + _options.MaxQueryChars + " characters to search for.");
        if (!ValidSearchId(request.SearchId)) throw new ProtocolException(ErrorCode.InvalidRequest, "Invalid search identifier.");
        var limit = request.Limit <= 0 ? _options.DefaultLimit : Math.Min(request.Limit, _options.MaxLimit);
        var terms = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        // Decide the scope BEFORE taking a slot, so a bad request never occupies one.
        var scope = new List<(SharedRoot Root, string[] Segments)>();
        var unavailable = new List<string>();
        if (request.EntryId is not null)
        {
            var body = files.UnsealEntry(request.EntryId, out var root);
            if (!body.IsDirectory) throw new ProtocolException(ErrorCode.InvalidRequest, "Search inside a folder.");
            if (request.RootId is not null && request.RootId != root.RootId) throw new ProtocolException(ErrorCode.InvalidRequest, "That folder belongs to another shared folder.");
            scope.Add((root, body.Segments));
        }
        else
        {
            var all = roots();
            var chosen = request.RootId is null ? all : [.. all.Where(r => r.RootId == request.RootId)];
            if (request.RootId is not null && chosen.Count == 0) throw new ProtocolException(ErrorCode.NotFound, "That shared folder is not available.");
            foreach (var root in chosen)
                if (files.IsAvailable(root)) scope.Add((root, [])); else unavailable.Add(root.Name);
        }

        if (!await _slots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) throw new ProtocolException(ErrorCode.LimitExceeded, "The PC is already searching. Try again in a moment.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (!_running.TryAdd(request.SearchId, cts)) { _slots.Release(); throw new ProtocolException(ErrorCode.Conflict, "That search is already running."); }
        try { return await Task.Run(() => Run(scope, unavailable, terms, limit, cts.Token), CancellationToken.None).ConfigureAwait(false); }
        finally { _running.TryRemove(request.SearchId, out _); _slots.Release(); }
    }

    private SearchResult Run(List<(SharedRoot Root, string[] Segments)> scope, List<string> unavailable, string[] terms, int limit, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var hits = new List<SearchHit>();
        string? reason = null;
        var folders = 0; var scanned = 0; var unreadable = false;
        var searched = 0;
        foreach (var (root, start) in scope)
        {
            if (reason is not null) break;
            searched++;
            var queue = new Queue<(string[] Segments, int Depth)>();
            queue.Enqueue((start, 0));
            while (queue.Count > 0 && reason is null)
            {
                if (token.IsCancellationRequested) { reason = "cancelled"; break; }
                if (watch.Elapsed >= _options.TimeBudget) { reason = "time"; break; }
                if (folders >= _options.MaxFoldersVisited || scanned >= _options.MaxEntriesScanned) { reason = "scan_limit"; break; }
                var (segments, depth) = queue.Dequeue();
                // The owner may stop sharing this folder while the search runs; from that moment nothing more is read from it.
                if (!roots().Any(r => r.RootId == root.RootId)) { unavailable.Add(root.Name); searched--; queue.Clear(); break; }
                folders++;
                List<FileSystemInfo> children;
                try { children = files.EnumerateChildren(root, segments, out _); }
                catch (ProtocolException e) when (e.Code is ErrorCode.AccessDeniedLocal or ErrorCode.NotFound or ErrorCode.Unavailable or ErrorCode.LinkNotAllowed)
                { if (segments.Length == 0) { unavailable.Add(root.Name); searched--; } else unreadable = true; continue; }
                foreach (var child in children)
                {
                    if (scanned >= _options.MaxEntriesScanned) { reason = "scan_limit"; break; }
                    scanned++;
                    var isDirectory = child is DirectoryInfo;
                    if (Matches(child.Name, terms))
                    {
                        string[] childSegments = [.. segments, child.Name];
                        hits.Add(new SearchHit
                        {
                            Entry = new FileEntry
                            {
                                EntryId = files.SealEntry(root, childSegments, isDirectory), Name = child.Name, Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
                                Size = child is FileInfo f ? f.Length : 0, ModifiedUnixMs = new DateTimeOffset(child.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                                Hidden = child.Attributes.HasFlag(FileAttributes.Hidden),
                            },
                            RootId = root.RootId, RootName = root.Name, Path = segments,
                            ParentEntryId = segments.Length == 0 ? null : files.SealEntry(root, segments, true),
                        });
                        if (hits.Count >= limit) { reason = "limit"; break; }
                    }
                    if (isDirectory)
                    {
                        if (depth + 1 > _options.MaxDepth) unreadable = true; else queue.Enqueue(([.. segments, child.Name], depth + 1));
                    }
                }
            }
            if (queue.Count > 0 && reason is null) reason = "scan_limit";
            queue.Clear();
        }
        reason ??= unreadable ? "unreadable" : null;
        return new SearchResult
        {
            Hits = hits, Complete = reason is null && unavailable.Count == 0, Reason = reason ?? (unavailable.Count > 0 ? "unavailable" : null),
            Scanned = scanned, ElapsedMs = (int)watch.ElapsedMilliseconds, RootsSearched = searched, RootsUnavailable = unavailable,
        };
    }

    private static bool Matches(string name, string[] terms)
    {
        foreach (var term in terms)
            if (name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        return true;
    }
}
