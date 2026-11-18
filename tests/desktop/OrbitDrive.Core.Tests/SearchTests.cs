using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

/// <summary>D8: filename search stays inside what is shared, is bounded and cancellable, and reports incompleteness; entry checks for favorites.</summary>
public sealed class SearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-search-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SharedRoot> _roots = [];
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string MakeRoot(string name, string id, bool showHidden = false)
    {
        var path = Directory.CreateDirectory(Path.Join(_dir, "root-" + id)).FullName;
        _roots.Add(new SharedRoot(id, name, path, showHidden));
        return path;
    }
    private (FileAccessService Files, SearchService Search) Make(SearchOptions? options = null, Func<IReadOnlyList<SharedRoot>>? roots = null)
    {
        var files = new FileAccessService(() => _roots, _tokens);
        return (files, new SearchService(files, roots ?? (() => _roots), options));
    }
    private static SearchRequest Ask(string query, string? rootId = null, string? entryId = null, int limit = 0) =>
        new() { SearchId = Guid.NewGuid().ToString("N"), Query = query, RootId = rootId, EntryId = entryId, Limit = limit };

    [Fact]
    public async Task Finds_by_any_case_and_every_word_and_gives_the_folder_each_hit_is_in()
    {
        var root = MakeRoot("Docs", "r1");
        Directory.CreateDirectory(Path.Join(root, "Taxes", "2023"));
        File.WriteAllText(Path.Join(root, "Taxes", "2023", "Return FINAL.pdf"), "x");
        File.WriteAllText(Path.Join(root, "Taxes", "notes.txt"), "x");
        File.WriteAllText(Path.Join(root, "Ünïcode 文書.txt"), "x");
        var (files, search) = Make();
        var one = await search.SearchAsync(Ask("return final"), CancellationToken.None);
        var hit = Assert.Single(one.Hits);
        Assert.Equal("Return FINAL.pdf", hit.Entry.Name);
        Assert.Equal(["Taxes", "2023"], hit.Path);
        Assert.Equal("Docs", hit.RootName);
        Assert.True(one.Complete); Assert.Null(one.Reason);
        Assert.Single((await search.SearchAsync(Ask("文書 ünï"), CancellationToken.None)).Hits);
        Assert.Empty((await search.SearchAsync(Ask("return missing"), CancellationToken.None)).Hits);
        // The identifier works exactly like one from browsing.
        var listed = files.ListDirectory(new DirectoryQuery { RootId = "r1", EntryId = (await search.SearchAsync(Ask("2023"), CancellationToken.None)).Hits.Single().Entry.EntryId, PageSize = 10 });
        Assert.Equal("Return FINAL.pdf", listed.Entries.Single().Name);
    }

    [Fact]
    public async Task Never_returns_hidden_items_links_or_anything_outside_the_shared_folder()
    {
        var root = MakeRoot("Docs", "r1");
        var outside = Directory.CreateDirectory(Path.Join(_dir, "outside")).FullName;
        File.WriteAllText(Path.Join(outside, "secret-plan.txt"), "x");
        File.WriteAllText(Path.Join(root, "secret-visible.txt"), "x");
        var hidden = Path.Join(root, OperatingSystem.IsWindows() ? "secret-hidden.txt" : ".secret-hidden.txt");     // a dot-name is hidden on Unix, the attribute on Windows
        File.WriteAllText(hidden, "x");
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Join(root, ".secret-folder")); File.WriteAllText(Path.Join(root, ".secret-folder", "secret-inside.txt"), "x");
        File.SetAttributes(Path.Join(root, ".secret-folder"), FileAttributes.Hidden | FileAttributes.Directory);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Join(root, "escape"), outside);
            File.CreateSymbolicLink(Path.Join(root, "secret-link.txt"), Path.Join(outside, "secret-plan.txt"));
        }
        var (_, search) = Make();
        var found = (await search.SearchAsync(Ask("secret"), CancellationToken.None)).Hits.Select(h => h.Entry.Name).Order().ToArray();
        Assert.Equal(["secret-visible.txt"], found);
        Assert.Empty((await search.SearchAsync(Ask("secret-plan"), CancellationToken.None)).Hits);
    }

    [Fact]
    public async Task A_hidden_item_appears_only_when_the_owner_allowed_hidden_items_for_that_folder()
    {
        var root = MakeRoot("Docs", "r1", showHidden: true);
        var hidden = Path.Join(root, OperatingSystem.IsWindows() ? "needle-hidden.txt" : ".needle-hidden.txt");
        File.WriteAllText(hidden, "x");
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);
        var (_, search) = Make();
        Assert.Single((await search.SearchAsync(Ask("needle"), CancellationToken.None)).Hits);
    }

    [Fact]
    public async Task Scope_is_one_folder_one_shared_folder_or_all_and_foreign_scopes_are_refused()
    {
        var a = MakeRoot("Alpha", "ra"); var b = MakeRoot("Beta", "rb");
        Directory.CreateDirectory(Path.Join(a, "Sub")); File.WriteAllText(Path.Join(a, "Sub", "report.txt"), "x"); File.WriteAllText(Path.Join(a, "report-top.txt"), "x");
        File.WriteAllText(Path.Join(b, "report.txt"), "x");
        var (files, search) = Make();
        var all = await search.SearchAsync(Ask("report"), CancellationToken.None);
        Assert.Equal(3, all.Hits.Count);
        Assert.Equal(2, all.RootsSearched);
        Assert.Equal(["Alpha", "Beta"], all.Hits.Where(h => h.Entry.Name == "report.txt").Select(h => h.RootName).Order().ToArray());          // same name twice stays distinguishable
        Assert.Equal(2, all.Hits.Where(h => h.Entry.Name == "report.txt").Select(h => h.Entry.EntryId).Distinct().Count());
        Assert.Equal(2, (await search.SearchAsync(Ask("report", rootId: "ra"), CancellationToken.None)).Hits.Count);
        var sub = files.ListDirectory(new DirectoryQuery { RootId = "ra", PageSize = 10 }).Entries.Single(e => e.Name == "Sub").EntryId;
        Assert.Single((await search.SearchAsync(Ask("report", entryId: sub), CancellationToken.None)).Hits);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask("report", rootId: "nope"), CancellationToken.None))).Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask("report", rootId: "rb", entryId: sub), CancellationToken.None))).Code);
        var file = files.ListDirectory(new DirectoryQuery { RootId = "ra", PageSize = 10 }).Entries.Single(e => e.Name == "report-top.txt").EntryId;
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask("x", entryId: file), CancellationToken.None))).Code);
        var otherPc = new FileAccessService(() => _roots, new SealedTokens(RandomNumberGenerator.GetBytes(32)));
        var foreign = otherPc.ListDirectory(new DirectoryQuery { RootId = "ra", PageSize = 10 }).Entries.First(e => e.Kind == EntryKind.Directory).EntryId;
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask("x", entryId: foreign), CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("a\u0000b")] [InlineData("a\nb")]
    public async Task Empty_or_control_character_queries_are_refused(string query)
    {
        MakeRoot("Docs", "r1");
        var (_, search) = Make();
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask(query), CancellationToken.None))).Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(Ask(new string('a', 101)), CancellationToken.None))).Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<ProtocolException>(() => search.SearchAsync(new SearchRequest { SearchId = "x", Query = "ok", Limit = 5 }, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Limits_stop_the_search_and_say_so_instead_of_pretending_to_be_complete()
    {
        var root = MakeRoot("Docs", "r1");
        for (var i = 0; i < 30; i++) File.WriteAllText(Path.Join(root, $"match{i:00}.txt"), "x");
        var (_, search) = Make();
        var limited = await search.SearchAsync(Ask("match", limit: 5), CancellationToken.None);
        Assert.Equal(5, limited.Hits.Count); Assert.False(limited.Complete); Assert.Equal("limit", limited.Reason);

        var (_, scanLimited) = Make(new SearchOptions { MaxEntriesScanned = 10 });
        var scan = await scanLimited.SearchAsync(Ask("match"), CancellationToken.None);
        Assert.False(scan.Complete); Assert.Equal("scan_limit", scan.Reason);

        var (_, timed) = Make(new SearchOptions { TimeBudget = TimeSpan.Zero });
        var time = await timed.SearchAsync(Ask("match"), CancellationToken.None);
        Assert.False(time.Complete); Assert.Equal("time", time.Reason);

        var deep = root; for (var i = 0; i < 5; i++) { deep = Path.Join(deep, "d" + i); Directory.CreateDirectory(deep); }
        File.WriteAllText(Path.Join(deep, "bottom.txt"), "x");
        var (_, shallow) = Make(new SearchOptions { MaxDepth = 2 });
        var depth = await shallow.SearchAsync(Ask("bottom"), CancellationToken.None);
        Assert.Empty(depth.Hits); Assert.False(depth.Complete);
        Assert.Single((await search.SearchAsync(Ask("bottom"), CancellationToken.None)).Hits);
    }

    [Fact]
    public async Task A_cancelled_search_returns_what_it_had_marked_cancelled()
    {
        var root = MakeRoot("Docs", "r1");
        File.WriteAllText(Path.Join(root, "a.txt"), "x");
        var (_, search) = Make();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await search.SearchAsync(Ask("a"), cancelled.Token);
        Assert.False(result.Complete); Assert.Equal("cancelled", result.Reason);
        Assert.True(search.Cancel(new CancelSearchRequest { SearchId = "does-not-exist" }).Ok);     // cancelling something unknown reveals nothing
        Assert.True(search.Cancel(new CancelSearchRequest { SearchId = "bad id!" }).Ok);
    }

    [Fact]
    public async Task An_unavailable_shared_folder_is_named_not_counted_as_having_no_matches()
    {
        var a = MakeRoot("Alpha", "ra"); MakeRoot("Gone", "rb");
        File.WriteAllText(Path.Join(a, "report.txt"), "x");
        Directory.Delete(Path.Join(_dir, "root-rb"));
        var (_, search) = Make();
        var result = await search.SearchAsync(Ask("report"), CancellationToken.None);
        Assert.Single(result.Hits);
        Assert.Equal(["Gone"], result.RootsUnavailable);
        Assert.False(result.Complete); Assert.Equal("unavailable", result.Reason);
        Assert.Equal(1, result.RootsSearched);
    }

    [Fact]
    public async Task A_folder_unshared_during_the_search_is_not_read_any_further()
    {
        var root = MakeRoot("Docs", "r1");
        for (var i = 0; i < 20; i++) { Directory.CreateDirectory(Path.Join(root, $"d{i:00}")); File.WriteAllText(Path.Join(root, $"d{i:00}", "match.txt"), "x"); }
        var calls = 0;
        IReadOnlyList<SharedRoot> Roots() { calls++; return calls > 4 ? [] : _roots; }      // the owner stops sharing partway through
        var files = new FileAccessService(() => _roots, _tokens);
        var search = new SearchService(files, Roots);
        var result = await search.SearchAsync(Ask("match", rootId: null), CancellationToken.None);
        Assert.True(result.Hits.Count < 20);
        Assert.Contains("Docs", result.RootsUnavailable);
        Assert.False(result.Complete);
    }

    [Fact]
    public void Entry_checks_report_gone_moved_foreign_and_forged_items_as_unavailable_never_as_data()
    {
        var root = MakeRoot("Docs", "r1", showHidden: true);
        var hideName = OperatingSystem.IsWindows() ? "hide.txt" : ".hide.txt";
        File.WriteAllText(Path.Join(root, "keep.txt"), "abc"); File.WriteAllText(Path.Join(root, "gone.txt"), "abc"); File.WriteAllText(Path.Join(root, hideName), "abc");
        if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Join(root, hideName), FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Join(root, "Folder"));
        var (files, _) = Make();
        var page = files.ListDirectory(new DirectoryQuery { RootId = "r1", PageSize = 50 }).Entries.ToDictionary(e => e.Name, e => e.EntryId);
        File.Delete(Path.Join(root, "gone.txt"));
        _roots[0] = _roots[0] with { ShowHidden = false };                                       // the owner stops showing hidden items
        var otherPc = new FileAccessService(() => _roots, new SealedTokens(RandomNumberGenerator.GetBytes(32)));
        var foreign = otherPc.ListDirectory(new DirectoryQuery { RootId = "r1", PageSize = 50 }).Entries[0].EntryId;
        var checks = files.CheckEntries(new CheckEntriesRequest { EntryIds = [page["keep.txt"], page["gone.txt"], page[hideName], page["Folder"], foreign, "forged"] }).Entries;
        Assert.True(checks[0].Available); Assert.Equal(3, checks[0].Entry!.Size);
        Assert.False(checks[1].Available); Assert.Null(checks[1].Entry);
        Assert.False(checks[2].Available);
        Assert.True(checks[3].Available); Assert.Equal(EntryKind.Directory, checks[3].Entry!.Kind);
        Assert.False(checks[4].Available); Assert.Contains("does not belong to this PC", checks[4].Reason!);
        Assert.False(checks[5].Available);
        _roots.Clear();                                                                          // folder no longer shared at all
        Assert.False(files.CheckEntries(new CheckEntriesRequest { EntryIds = [page["keep.txt"]] }).Entries[0].Available);
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => files.CheckEntries(new CheckEntriesRequest { EntryIds = [.. Enumerable.Repeat("x", 101)] })).Code);
    }
}
