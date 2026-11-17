using System.Diagnostics;
using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

public sealed class FileAccessTests : IDisposable
{
    private readonly string _base = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-core-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _root;
    private readonly string _outside;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SharedRoot> _roots = [];
    private readonly FileAccessService _service;

    public FileAccessTests()
    {
        _root = Directory.CreateDirectory(Path.Join(_base, "shared")).FullName;
        _outside = Directory.CreateDirectory(Path.Join(_base, "private")).FullName;
        File.WriteAllText(Path.Join(_outside, "secret.txt"), "secret");
        File.WriteAllText(Path.Join(_root, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Join(_root, "sub"));
        File.WriteAllText(Path.Join(_root, "sub", "b.txt"), "bravo!");
        _roots.Add(new SharedRoot("root1", "Shared", _root));
        _service = new FileAccessService(() => _roots, _tokens);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ErrorCode CodeOf(Action action) => Assert.Throws<ProtocolException>(action).Code;
    private string Entry(bool directory, params string[] segments) => _tokens.Seal(new TokenBody { Type = "e", RootId = "root1", Segments = segments, IsDirectory = directory });
    private DirectoryPage List(string? entryId = null, int pageSize = 200, string? cursor = null) =>
        _service.ListDirectory(new DirectoryQuery { RootId = "root1", EntryId = entryId, PageSize = pageSize, Cursor = cursor });

    [Theory]
    [InlineData("report.txt", true)]
    [InlineData("اردو فائل 😀.txt", true)]
    [InlineData(".hidden", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:", false)]
    [InlineData("file.txt:stream", false)]       // NTFS alternate data stream
    [InlineData("file.txt::$DATA", false)]
    [InlineData("name.", false)]                 // Windows strips trailing dots/spaces
    [InlineData("name ", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("LPT9.log", false)]
    [InlineData("a\0b", false)]
    [InlineData("tab\tname", false)]
    [InlineData("what?", false)]
    [InlineData("star*", false)]
    public void SegmentValidation(string segment, bool expected) => Assert.Equal(expected, PathPolicy.IsSafeSegment(segment));

    [Fact]
    public void OverlongSegmentIsRejected() => Assert.False(PathPolicy.IsSafeSegment(new string('a', 256)));

    [Fact]
    public void ListsRealEntriesWithDirectoriesFirstAndNoAbsolutePaths()
    {
        var page = List();
        Assert.Equal(["sub", "a.txt"], page.Entries.Select(e => e.Name));
        Assert.Equal(EntryKind.Directory, page.Entries[0].Kind);
        Assert.Equal(5, page.Entries[1].Size);
        Assert.DoesNotContain(page.Entries, e => e.EntryId.Contains("shared") || e.EntryId.Contains(_root));
        var nested = List(page.Entries[0].EntryId);
        Assert.Equal(["b.txt"], nested.Entries.Select(e => e.Name));
        Assert.Equal(["Shared", "sub"], nested.Breadcrumbs.Select(b => b.Name));
    }

    // These tokens are sealed with the genuine key: they prove the resolver itself refuses escapes,
    // independently of the fact that clients cannot forge tokens.
    [Theory]
    [InlineData("..")]
    [InlineData("..", "private")]
    [InlineData("sub", "..", "..", "private")]
    [InlineData("sub/../../private")]
    [InlineData("..\\private")]
    [InlineData("C:\\Windows")]
    [InlineData("/etc")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\")]
    [InlineData("a.txt:hidden")]
    [InlineData("sub.")]
    [InlineData("NUL")]
    public void GenuinelySealedEscapingPathsAreStillRefused(params string[] segments)
    {
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(Entry(true, segments))));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, [.. segments, "secret.txt"]) }, default).GetAwaiter().GetResult()));
    }

    [Fact]
    public void TokensAreOpaqueTamperEvidentAndTypeBound()
    {
        var entry = List().Entries[1].EntryId;
        var chars = entry.ToCharArray();
        chars[^3] = chars[^3] == 'A' ? 'B' : 'A';
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(new string(chars))));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List("not-a-token")));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(new string('A', 9000))));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(entry)));                               // a file is not a folder
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.OpenRead(entry, 0, 1)));            // an entry id is not a read token
        var foreign = new SealedTokens(RandomNumberGenerator.GetBytes(32)).Seal(new TokenBody { Type = "e", RootId = "root1", Segments = ["sub"], IsDirectory = true });
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(foreign)));                             // another PC's token
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.ListDirectory(new DirectoryQuery { RootId = "other", PageSize = 10 })));
        var otherRoot = _tokens.Seal(new TokenBody { Type = "e", RootId = "root2", Segments = ["sub"], IsDirectory = true });
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(otherRoot)));
    }

    [Fact]
    public void SymbolicLinksAreSkippedInListingsAndRefusedWhenAddressedDirectly()
    {
        File.CreateSymbolicLink(Path.Join(_root, "link.txt"), Path.Join(_outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Join(_root, "linkdir"), _outside);
        var page = List();
        Assert.Equal(["sub", "a.txt"], page.Entries.Select(e => e.Name));
        Assert.Equal(2, page.SkippedLinks);
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => List(Entry(true, "linkdir"))));
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "link.txt") }, default).GetAwaiter().GetResult()));
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "linkdir", "secret.txt") }, default).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task PrepareHashesContentAndOpenReadEnforcesRangeAndRevision()
    {
        var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "sub", "b.txt") }, default);
        Assert.Equal(6, prepared.Size);
        Assert.Equal("sha256-" + Convert.ToHexStringLower(SHA256.HashData("bravo!"u8)), prepared.Revision);
        using (var read = _service.OpenRead(prepared.ReadToken, 2, 4))
        {
            var buffer = new byte[4];
            Assert.Equal(4, RandomAccess.Read(read.Handle, buffer, read.Offset));
            Assert.Equal("avo!"u8.ToArray(), buffer);
        }
        _service.OpenRead(prepared.ReadToken, 6, 0).Dispose();
        foreach (var (offset, length) in new[] { (-1L, 1L), (0L, -1L), (0L, 7L), (7L, 0L), (long.MaxValue, 1L), (1L, long.MaxValue), (long.MaxValue, long.MaxValue) })
            Assert.Equal(ErrorCode.RangeNotSatisfiable, CodeOf(() => _service.OpenRead(prepared.ReadToken, offset, length)));

        File.WriteAllText(Path.Join(_root, "sub", "b.txt"), "BRAVO?");                     // same size, different content
        File.SetLastWriteTimeUtc(Path.Join(_root, "sub", "b.txt"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(ErrorCode.RevisionChanged, CodeOf(() => _service.OpenRead(prepared.ReadToken, 0, 6)));
        File.Delete(Path.Join(_root, "sub", "b.txt"));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.OpenRead(prepared.ReadToken, 0, 6)));
    }

    [Fact]
    public async Task RemovingTheRootRevokesListingsAndPreparedReads()
    {
        var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "a.txt") }, default);
        _roots.Clear();
        Assert.Empty(_service.ListRoots().Roots);
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => List()));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.OpenRead(prepared.ReadToken, 0, 1)));
    }

    [Theory]
    [InlineData("..%2fprivate")]
    [InlineData("..%5cprivate")]
    [InlineData("%2e%2e")]
    [InlineData("%2e%2e%2f%2e%2e%2fprivate")]
    [InlineData("..%252fprivate")]
    [InlineData("..%c0%afprivate")]              // overlong UTF-8 slash
    [InlineData("．．")]                          // full-width dots
    [InlineData("..／private")]                   // full-width slash
    [InlineData("%00")]
    public void EncodedOrLookAlikeSeparatorsAreNeverDecoded(string segment)
    {
        // Nothing decodes names: these are literal file names that simply do not exist under the shared folder.
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => List(Entry(true, segment))));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, segment, "secret.txt") }, default).GetAwaiter().GetResult()));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, segment) }, default).GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task ADifferentDiskAtTheSamePathDoesNotInheritTheApproval()
    {
        // The owner approved the folder while disk A was mounted there. Later disk B appears at the same path.
        var mounted = "vol-AAAA0001";
        var roots = new List<SharedRoot> { new("usb", "USB stick", _root, VolumeId: "vol-AAAA0001") };
        var service = new FileAccessService(() => roots, _tokens, _ => mounted);
        string Usb(bool directory, params string[] segments) => _tokens.Seal(new TokenBody { Type = "e", RootId = "usb", Segments = segments, IsDirectory = directory });

        Assert.True(service.ListRoots().Roots.Single().Available);
        Assert.Equal(["sub", "a.txt"], service.ListDirectory(new DirectoryQuery { RootId = "usb", PageSize = 50 }).Entries.Select(e => e.Name));
        var prepared = await service.PrepareReadAsync(new PrepareReadRequest { EntryId = Usb(false, "a.txt") }, default);

        mounted = "vol-BBBB0002";                                       // another disk, same drive letter and folder names
        Assert.False(service.ListRoots().Roots.Single().Available);
        var refused = Assert.Throws<ProtocolException>(() => service.ListDirectory(new DirectoryQuery { RootId = "usb", PageSize = 50 }));
        Assert.Equal(ErrorCode.Unavailable, refused.Code);
        Assert.Contains("different disk", refused.Message);
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => service.ListDirectory(new DirectoryQuery { RootId = "usb", EntryId = Usb(true, "sub"), PageSize = 50 })));
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => service.PrepareReadAsync(new PrepareReadRequest { EntryId = Usb(false, "a.txt") }, default).GetAwaiter().GetResult()));
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => service.OpenRead(prepared.ReadToken, 0, prepared.Size)));   // an already prepared download too

        mounted = null!;                                                // the disk is unplugged: unavailable, never "empty"
        Assert.False(service.ListRoots().Roots.Single().Available);
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => service.ListDirectory(new DirectoryQuery { RootId = "usb", PageSize = 50 })));

        mounted = "vol-AAAA0001";                                       // the original disk is back: access resumes
        Assert.True(service.ListRoots().Roots.Single().Available);
        using var read = service.OpenRead(prepared.ReadToken, 0, prepared.Size);
        Assert.Equal(5, read.Length);
    }

    [Fact]
    public void RealVolumeIdentityIsStableForAFolderAndDiffersBetweenRealVolumes()
    {
        var here = NativeFile.GetVolumeIdentity(_root);
        Assert.NotNull(here);
        Assert.Equal(here, NativeFile.GetVolumeIdentity(Path.Join(_root, "sub")));
        Assert.Null(NativeFile.GetVolumeIdentity(Path.Join(_base, "not-there")));
        var bound = new List<SharedRoot> { new("r", "Bound", _root, VolumeId: here) };
        Assert.True(new FileAccessService(() => bound, _tokens).ListRoots().Roots.Single().Available);

        // Two different real volumes on this machine must not share an identity.
        var other = OperatingSystem.IsWindows()
            ? DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).FirstOrDefault(d => NativeFile.GetVolumeIdentity(d) != here)
            : new[] { "/proc", "/dev", "/sys" }.FirstOrDefault(d => Directory.Exists(d) && NativeFile.GetVolumeIdentity(d) != here);
        Assert.NotNull(other);                                          // CI runners have a second volume (D: / procfs)
        var wrong = new List<SharedRoot> { new("r", "Bound to another disk", _root, VolumeId: NativeFile.GetVolumeIdentity(other!)) };
        var service = new FileAccessService(() => wrong, _tokens);
        Assert.False(service.ListRoots().Roots.Single().Available);
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => service.ListDirectory(new DirectoryQuery { RootId = "r", PageSize = 10 })));
    }

    [Fact]
    public async Task SwappingAnAncestorFolderForALinkDuringAccessNeverLeaksOutsideContent()
    {
        // An attacker with write access inside the share keeps replacing "swap" with a link to a private folder while
        // downloads of swap/data.txt are in flight. Every outcome must be the inside content or a refusal.
        var inside = Path.Join(_root, "swap");
        var parked = Path.Join(_root, "swap-real");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Join(inside, "data.txt"), "INSIDE");
        File.WriteAllText(Path.Join(_outside, "data.txt"), "SECRET");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var flips = 0;
        var flipper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Directory.Move(inside, parked);
                    Directory.CreateSymbolicLink(inside, _outside);
                    Thread.Yield();
                    Directory.Delete(inside);
                    Directory.Move(parked, inside);
                    flips++;
                }
                catch (IOException) { if (!Directory.Exists(inside) && Directory.Exists(parked)) { try { Directory.Move(parked, inside); } catch (IOException) { } } }
                catch (UnauthorizedAccessException) { }
            }
        });
        int served = 0, refused = 0;
        var buffer = new byte[64];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "swap", "data.txt") }, default);
                using var read = _service.OpenRead(prepared.ReadToken, 0, prepared.Size);
                var count = RandomAccess.Read(read.Handle, buffer, 0);
                Assert.Equal("INSIDE", System.Text.Encoding.UTF8.GetString(buffer, 0, count));
                served++;
            }
            catch (ProtocolException e)
            {
                Assert.Contains(e.Code, new[] { ErrorCode.LinkNotAllowed, ErrorCode.NotFound, ErrorCode.Unavailable, ErrorCode.RevisionChanged, ErrorCode.AccessDeniedLocal });
                refused++;
            }
            try { foreach (var entry in List(Entry(true, "swap")).Entries) Assert.Equal("data.txt", entry.Name); }
            catch (ProtocolException e) { Assert.Contains(e.Code, new[] { ErrorCode.LinkNotAllowed, ErrorCode.NotFound, ErrorCode.Unavailable, ErrorCode.AccessDeniedLocal }); }
        }
        await flipper;
        if (Directory.Exists(parked) && !Directory.Exists(inside)) Directory.Move(parked, inside);
        Assert.True(flips > 5, $"the swap really happened repeatedly ({flips} flips)");
        Assert.True(served + refused > 5, $"access was attempted repeatedly ({served} served, {refused} refused)");
    }

    [Fact]
    public void ASharedFolderChosenThroughALinkStillWorksButLinksInsideItAreNotFollowed()
    {
        var alias = Path.Join(_base, "alias-to-shared");
        Directory.CreateSymbolicLink(alias, _root);
        Directory.CreateSymbolicLink(Path.Join(_root, "escape"), _outside);
        var roots = new List<SharedRoot> { new("viaLink", "Via link", alias) };
        var service = new FileAccessService(() => roots, _tokens);
        var page = service.ListDirectory(new DirectoryQuery { RootId = "viaLink", PageSize = 50 });
        Assert.Equal(["sub", "a.txt"], page.Entries.Select(e => e.Name));
        Assert.Equal(1, page.SkippedLinks);
        var escape = _tokens.Seal(new TokenBody { Type = "e", RootId = "viaLink", Segments = ["escape"], IsDirectory = true });
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => service.ListDirectory(new DirectoryQuery { RootId = "viaLink", EntryId = escape, PageSize = 50 })));
    }

    [Fact]
    public void FilesWithASecondHardLinkAreRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // link counts are read from the Windows file handle; other hosts are development only
        var original = Path.Join(_outside, "ledger.txt");
        File.WriteAllText(original, "private ledger");
        var alias = Path.Join(_root, "innocent.txt");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{alias}\" \"{original}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        Assert.Equal("private ledger", File.ReadAllText(alias));       // the alias really exposes the outside file's bytes
        Assert.Contains("innocent.txt", List().Entries.Select(e => e.Name));
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "innocent.txt") }, default).GetAwaiter().GetResult()));
    }

    [Fact]
    public void HiddenItemsAreSharedOnlyWhenTheOwnerOptsInAndSystemItemsNever()
    {
        var hidden = Path.Join(_root, ".private-notes");
        File.WriteAllText(hidden, "x");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.WriteAllText(Path.Join(_root, "pagefile.sys"), "x");
            File.SetAttributes(Path.Join(_root, "pagefile.sys"), FileAttributes.Hidden | FileAttributes.System);
        }
        Assert.DoesNotContain(List().Entries, e => e.Name == ".private-notes");
        _roots[0] = _roots[0] with { ShowHidden = true };
        var entry = List().Entries.Single(e => e.Name == ".private-notes");
        Assert.True(entry.Hidden);
        Assert.DoesNotContain(List().Entries, e => e.Name == "pagefile.sys");
        Assert.DoesNotContain(List().Entries, e => e.Name != ".private-notes" && e.Hidden);
    }

    [Fact]
    public void MissingRootFolderIsReportedUnavailableNotEmpty()
    {
        _roots.Add(new SharedRoot("gone", "Unplugged drive", Path.Join(_base, "not-there")));
        Assert.False(_service.ListRoots().Roots.Single(r => r.RootId == "gone").Available);
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => _service.ListDirectory(new DirectoryQuery { RootId = "gone", PageSize = 10 })));
    }

    [Fact]
    public void TenThousandEntriesPageCompletelyAndPromptly()
    {
        var big = Directory.CreateDirectory(Path.Join(_root, "big")).FullName;
        for (var i = 0; i < 10_000; i++) File.WriteAllBytes(Path.Join(big, $"f{i:D5}.dat"), []);
        var watch = Stopwatch.StartNew();
        var seen = new HashSet<string>();
        string? cursor = null;
        do
        {
            var page = List(Entry(true, "big"), 200, cursor);
            Assert.InRange(page.Entries.Count, 1, FileAccessService.MaxPageSize);
            foreach (var entry in page.Entries) Assert.True(seen.Add(entry.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(10_000, seen.Count);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 60);
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => List(null, 200, _tokens.Seal(new TokenBody { Type = "c", RootId = "root1", Segments = ["sub"], Offset = 5 }))));
    }

    [Fact]
    public void FileLockedByAnotherProgramFailsTruthfullyOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return; // exclusive share modes are a Windows behaviour
        using var exclusive = new FileStream(Path.Join(_root, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(ErrorCode.Unavailable, CodeOf(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = Entry(false, "a.txt") }, default).GetAwaiter().GetResult()));
    }
}
