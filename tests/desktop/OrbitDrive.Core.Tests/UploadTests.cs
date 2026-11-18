using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

/// <summary>D9: receiving files from a browser. Every rule that keeps a half-received or unwanted file from becoming a real file.</summary>
public sealed class UploadTests : IDisposable
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-upload-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SharedRoot> _roots = [];
    private readonly Clock _clock = new();
    private string _rootPath = "";
    private FileAccessService _files = null!;
    private UploadService _uploads = null!;
    private const string Device = "dev-1";

    public UploadTests() => Setup();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private void Setup(UploadOptions? options = null, bool enabled = true, long quota = 0, long maxFile = 0)
    {
        _roots.Clear();
        _rootPath = Directory.CreateDirectory(Path.Join(_dir, "share")).FullName;
        Directory.CreateDirectory(Path.Join(_rootPath, "Inbox"));
        _roots.Add(new SharedRoot("r1", "Share", _rootPath, UploadsEnabled: enabled, UploadQuotaBytes: quota, UploadMaxFileBytes: maxFile));
        _files = new FileAccessService(() => _roots, _tokens);
        _uploads = Service(options);
    }

    private UploadService Service(UploadOptions? options = null) =>
        new(_files, () => _roots, (id, bytes) => { var i = _roots.FindIndex(r => r.RootId == id); _roots[i] = _roots[i] with { UploadUsedBytes = _roots[i].UploadUsedBytes + bytes }; },
            options ?? new UploadOptions { FreeBytes = _ => 100L << 30 }, _clock);

    private string Folder(params string[] path)
    {
        var parent = new DirectoryQuery { RootId = "r1", PageSize = 200 };
        FileEntry? found = null;
        foreach (var name in path)
        {
            found = _files.ListDirectory(parent).Entries.Single(e => e.Name == name);
            parent = new DirectoryQuery { RootId = "r1", EntryId = found.EntryId, PageSize = 200 };
        }
        return found!.EntryId;
    }

    private UploadStarted Start(string name, long size, string collision = "reject", string? folder = null, string device = Device) =>
        _uploads.Start(new StartUploadRequest { RootId = "r1", FolderEntryId = folder ?? Folder("Inbox"), Name = name, Size = size, Collision = collision, DeviceId = device });

    private static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private Task<WriteChunkResult> Send(string id, long offset, byte[] data, string? sha = null, string device = Device, int? declared = null) =>
        _uploads.WriteChunkAsync(new WriteChunkRequest { UploadId = id, DeviceId = device, Offset = offset, Length = declared ?? data.Length, Sha256 = sha ?? Hex(data), StreamId = "s", UplinkToken = "t" },
            _ => Task.FromResult<Stream>(new MemoryStream(data)), CancellationToken.None);

    private async Task<UploadStatus> Upload(string name, byte[] content, string collision = "reject", int chunk = 1000, string? folder = null)
    {
        var started = Start(name, content.Length, collision, folder);
        for (long offset = 0; offset < content.Length; offset += chunk)
        {
            var part = content.AsSpan((int)offset, (int)Math.Min(chunk, content.Length - offset)).ToArray();
            Assert.True((await Send(started.UploadId, offset, part)).Accepted);
        }
        return _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
    }

    private static byte[] Bytes(int length, int seed = 1) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }
    private string InboxFile(string name) => Path.Join(_rootPath, "Inbox", name);
    private string[] Names(string folder) => [.. Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)!).Order()];
    private static ErrorCode CodeOf(Exception e) => ((ProtocolException)e).Code;

    // ----- the good path -----

    [Fact]
    public async Task A_file_arrives_in_pieces_and_is_identical_with_nothing_left_behind()
    {
        var content = Bytes(3500);
        var started = Start("photo.jpg", content.Length);
        Assert.Equal(new[] { "Share", "Inbox" }, started.Destination);
        Assert.False(File.Exists(InboxFile("photo.jpg")));
        Assert.True(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        for (long offset = 0; offset < content.Length; offset += 1000)
            Assert.True((await Send(started.UploadId, offset, content.AsSpan((int)offset, (int)Math.Min(1000, content.Length - offset)).ToArray())).Accepted);
        Assert.False(File.Exists(InboxFile("photo.jpg")));                         // nothing exists under the real name before the commit
        var done = _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.Equal("committed", done.State);
        Assert.Equal("photo.jpg", done.FinalName);
        Assert.False(done.Renamed);
        Assert.Equal(Hex(content).ToLowerInvariant(), done.Sha256);
        Assert.Equal(content.Length.ToString(), done.CommittedBytes.ToString());
        Assert.True(File.ReadAllBytes(InboxFile("photo.jpg")).AsSpan().SequenceEqual(content));
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));  // the private folder is gone when it holds nothing
        Assert.Equal(3500L, _roots[0].UploadUsedBytes);
        Assert.Equal(OperatingSystem.IsWindows(), done.OriginMarked);
        if (OperatingSystem.IsWindows()) Assert.True(File.Exists(InboxFile("photo.jpg") + ":Zone.Identifier"));
    }

    [Fact]
    public async Task An_empty_file_can_be_uploaded()
    {
        var done = await Upload("empty.txt", []);
        Assert.Equal("committed", done.State);
        Assert.Equal(0L, new FileInfo(InboxFile("empty.txt")).Length);
        Assert.Equal(Hex([]).ToLowerInvariant(), done.Sha256);
    }

    [Fact]
    public async Task Finish_is_idempotent_and_counts_the_file_once()
    {
        var started = Start("a.bin", 10);
        await Send(started.UploadId, 0, Bytes(10));
        var reference = new UploadRef { UploadId = started.UploadId, DeviceId = Device };
        var first = _uploads.Finish(reference);
        var second = _uploads.Finish(reference);
        Assert.Equal(first.FinalName, second.FinalName);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(new[] { "a.bin" }, Names(Path.Join(_rootPath, "Inbox")));
        Assert.Equal(10L, _roots[0].UploadUsedBytes);
    }

    [Fact]
    public async Task The_partial_folder_is_invisible_to_browsing_and_cannot_be_named()
    {
        var started = Start("big.bin", 100);
        await Send(started.UploadId, 0, Bytes(50));
        var page = _files.ListDirectory(new DirectoryQuery { RootId = "r1", PageSize = 200 });
        Assert.Equal(new[] { "Inbox" }, page.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(0, page.SkippedLinks);
        Assert.False(PathPolicy.IsSafeSegment(PathPolicy.PartialDirectoryName));
        Assert.False(PathPolicy.IsSafeSegment(PathPolicy.PartialDirectoryName.ToUpperInvariant()));
        var error = Assert.Throws<ProtocolException>(() => Start(PathPolicy.PartialDirectoryName, 1));
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
    }

    // ----- sequencing, integrity, interruption -----

    [Fact]
    public async Task Duplicate_and_skipped_pieces_are_not_written_and_say_where_the_PC_is()
    {
        var content = Bytes(3000);
        var started = Start("c.bin", content.Length);
        Assert.True((await Send(started.UploadId, 0, content[..1000])).Accepted);
        var duplicate = await Send(started.UploadId, 0, content[..1000]);
        Assert.False(duplicate.Accepted);
        Assert.Equal(1000L, duplicate.Status.CommittedBytes);
        var skipped = await Send(started.UploadId, 2000, content[2000..]);
        Assert.False(skipped.Accepted);
        Assert.Equal(1000L, skipped.Status.CommittedBytes);
        Assert.True((await Send(started.UploadId, 1000, content[1000..2000])).Accepted);
        Assert.True((await Send(started.UploadId, 2000, content[2000..])).Accepted);
        var done = _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.True(File.ReadAllBytes(InboxFile("c.bin")).AsSpan().SequenceEqual(content));
        Assert.Equal(Hex(content).ToLowerInvariant(), done.Sha256);
    }

    [Fact]
    public async Task A_damaged_piece_is_refused_and_leaves_what_was_stored_unchanged()
    {
        var content = Bytes(2000);
        var started = Start("d.bin", content.Length);
        await Send(started.UploadId, 0, content[..1000]);
        var bad = content[1000..].ToArray(); bad[5] ^= 0xFF;
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 1000, bad, Hex(content[1000..])));
        Assert.Equal(ErrorCode.ChecksumMismatch, e.Code);
        Assert.Equal(1000L, _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).CommittedBytes);
        Assert.Equal(1000L, new FileInfo(Directory.GetFiles(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)).Single()).Length);
        Assert.True((await Send(started.UploadId, 1000, content[1000..])).Accepted);          // resending the right bytes works
        var done = _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.True(File.ReadAllBytes(InboxFile("d.bin")).AsSpan().SequenceEqual(content));
        Assert.Equal("committed", done.State);
    }

    [Fact]
    public async Task A_connection_that_ends_mid_piece_stores_nothing_of_it()
    {
        var started = Start("e.bin", 2000);
        var data = Bytes(1000);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => _uploads.WriteChunkAsync(
            new WriteChunkRequest { UploadId = started.UploadId, DeviceId = Device, Offset = 0, Length = 1000, Sha256 = Hex(data), StreamId = "s", UplinkToken = "t" },
            _ => Task.FromResult<Stream>(new MemoryStream(data[..400])), CancellationToken.None));
        Assert.Equal(ErrorCode.Unavailable, e.Code);
        Assert.Equal(0L, _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).CommittedBytes);
        Assert.Equal(0L, new FileInfo(Directory.GetFiles(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)).Single()).Length);
        Assert.True((await Send(started.UploadId, 0, data)).Accepted);
    }

    [Fact]
    public async Task An_incomplete_upload_cannot_be_finished_and_leaves_no_file()
    {
        var started = Start("f.bin", 2000);
        await Send(started.UploadId, 0, Bytes(1000));
        var e = Assert.Throws<ProtocolException>(() => _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device }));
        Assert.Equal(ErrorCode.Conflict, e.Code);
        Assert.False(File.Exists(InboxFile("f.bin")));
        Assert.Equal("receiving", _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).State);   // still resumable
    }

    [Fact]
    public async Task Piece_sizes_are_bounded_by_what_was_declared_and_by_the_PC()
    {
        var started = Start("g.bin", 3000);
        var e1 = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 0, Bytes(3500)));              // more than the file
        Assert.Equal(ErrorCode.InvalidRequest, e1.Code);
        var e2 = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 0, Bytes(10), declared: 0));    // empty piece
        Assert.Equal(ErrorCode.InvalidRequest, e2.Code);
        var e3 = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 0, Bytes(10), sha: "xyz"));     // no checksum
        Assert.Equal(ErrorCode.InvalidRequest, e3.Code);
        Setup(new UploadOptions { FreeBytes = _ => 100L << 30, MaxChunkBytes = 500 });
        var s2 = Start("h.bin", 3000);
        var e4 = await Assert.ThrowsAsync<ProtocolException>(() => Send(s2.UploadId, 0, Bytes(600)));
        Assert.Equal(ErrorCode.InvalidRequest, e4.Code);
        Assert.Equal(0L, _uploads.Status(new UploadRef { UploadId = s2.UploadId, DeviceId = Device }).CommittedBytes);
    }

    [Fact]
    public async Task Two_pieces_at_the_same_time_cannot_both_be_written()
    {
        var gate = new TaskCompletionSource();
        Setup(new UploadOptions { FreeBytes = _ => 100L << 30, BeforeWrite = () => gate.Task });
        var started = Start("i.bin", 2000);
        var first = Send(started.UploadId, 0, Bytes(1000));
        await Task.Delay(100);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 0, Bytes(1000)));
        Assert.Equal(ErrorCode.Conflict, e.Code);
        gate.SetResult();
        Assert.True((await first).Accepted);
    }

    // ----- limits -----

    [Fact]
    public void Too_big_files_are_refused_before_any_data()
    {
        Setup(maxFile: 1000);
        var e = Assert.Throws<ProtocolException>(() => Start("big.bin", 1001));
        Assert.Equal(ErrorCode.TooLarge, e.Code);
        Assert.Equal(1000L, _files.ListRoots().Roots.Single().MaxUploadBytes);
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => Start("neg.bin", -1)).Code);
    }

    [Fact]
    public void The_owners_space_allowance_counts_what_was_already_taken_and_what_is_in_flight()
    {
        Setup(quota: 1000);
        _roots[0] = _roots[0] with { UploadUsedBytes = 600 };
        Assert.Equal(400L, _files.ListRoots().Roots.Single().UploadQuotaLeftBytes);
        Assert.Equal(ErrorCode.TooLarge, Assert.Throws<ProtocolException>(() => Start("x.bin", 500)).Code);
        Start("y.bin", 250);
        Assert.Equal(ErrorCode.TooLarge, Assert.Throws<ProtocolException>(() => Start("z.bin", 250)).Code);   // 250 + 250 > 400
        Start("w.bin", 150);
    }

    [Fact]
    public async Task Running_out_of_disk_is_reported_at_the_start_and_during_the_upload_and_saves_nothing()
    {
        long free = 100L << 30;
        Setup(new UploadOptions { FreeBytes = _ => free, ReserveFreeBytes = 1000 });
        free = 1500;
        Assert.Equal(ErrorCode.DiskFull, Assert.Throws<ProtocolException>(() => Start("huge.bin", 600)).Code);   // 600 + reserve 1000 > 1500
        free = 100L << 30;
        var started = Start("half.bin", 4000);
        await Send(started.UploadId, 0, Bytes(1000));
        free = 500;                                                                                              // the disk fills while the upload runs
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 1000, Bytes(1000)));
        Assert.Equal(ErrorCode.DiskFull, e.Code);
        var status = _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.Equal("failed", status.State);
        Assert.False(File.Exists(InboxFile("half.bin")));
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.Equal(0L, _roots[0].UploadUsedBytes);
    }

    [Fact]
    public void Only_a_few_uploads_run_at_once()
    {
        Setup(new UploadOptions { FreeBytes = _ => 100L << 30, MaxActive = 2 });
        Start("1.bin", 10); Start("2.bin", 10);
        Assert.Equal(ErrorCode.LimitExceeded, Assert.Throws<ProtocolException>(() => Start("3.bin", 10)).Code);
    }

    // ----- names and collisions -----

    [Fact]
    public async Task By_default_an_existing_name_is_never_replaced()
    {
        File.WriteAllText(InboxFile("Report.txt"), "original");
        Assert.Equal(ErrorCode.NameExists, Assert.Throws<ProtocolException>(() => Start("Report.txt", 3)).Code);
        Assert.Equal(ErrorCode.NameExists, Assert.Throws<ProtocolException>(() => Start("REPORT.TXT", 3)).Code);      // case differences count as the same name
        Assert.Equal("original", File.ReadAllText(InboxFile("Report.txt")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_name_taken_between_start_and_finish_fails_the_upload_and_the_new_file_is_untouched()
    {
        var started = Start("race.txt", 4);
        await Send(started.UploadId, 0, [1, 2, 3, 4]);
        File.WriteAllText(InboxFile("race.txt"), "someone else's");
        var e = Assert.Throws<ProtocolException>(() => _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device }));
        Assert.Equal(ErrorCode.NameExists, e.Code);
        Assert.Equal("someone else's", File.ReadAllText(InboxFile("race.txt")));
        Assert.Equal("failed", _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).State);
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
    }

    [Fact]
    public async Task Keep_both_adds_the_lowest_free_number_and_says_so()
    {
        File.WriteAllText(InboxFile("a.txt"), "one");
        var second = await Upload("a.txt", [2], "rename");
        Assert.Equal("a (2).txt", second.FinalName);
        Assert.True(second.Renamed);
        var third = await Upload("a.txt", [3], "rename");
        Assert.Equal("a (3).txt", third.FinalName);
        File.Delete(InboxFile("a (2).txt"));
        var fourth = await Upload("a.txt", [4], "rename");
        Assert.Equal("a (2).txt", fourth.FinalName);                                  // deterministic: the lowest free number
        Assert.Equal("one", File.ReadAllText(InboxFile("a.txt")));
        Assert.Equal(new[] { "a (2).txt", "a (3).txt", "a.txt" }, Names(Path.Join(_rootPath, "Inbox")));
        var plain = await Upload("README", [1], "rename");
        Assert.Equal("README", plain.FinalName);
        Assert.Equal("README (2)", (await Upload("README", [1], "rename")).FinalName);
        Assert.Equal(".gitignore", (await Upload(".gitignore", [1], "rename")).FinalName);
        Assert.Equal(".gitignore (2)", (await Upload(".gitignore", [1], "rename")).FinalName);   // a leading dot is not an extension
    }

    [Fact]
    public void Unsafe_names_are_refused_before_anything_is_created()
    {
        var bad = new[]
        {
            "", "..", "../x.txt", "a/b.txt", "a\\b.txt", "CON", "con.txt", "NUL.log", "COM1", "trailing.", "trailing ", "colon:stream", "star*.txt", "q?.txt", "pipe|.txt",
            "quote\".txt", "lt<.txt", "ctrl\u0001.txt", "evil.lnk", "EVIL.URL", "x.scf", "y.library-ms", "desktop.ini", "Thumbs.db", "autorun.inf",
            "fdp‮txt.exe", new string('a', 256),
        };
        foreach (var name in bad)
        {
            var e = Assert.Throws<ProtocolException>(() => Start(name, 1));
            Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        }
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Start("fine name (1).tar.gz", 1);                                                  // ordinary names, spaces and dots in the middle are fine
        Start("اردو فائل 😀.txt", 1);
    }

    [Fact]
    public void A_path_too_long_for_Windows_is_refused()
    {
        var deep = _rootPath;
        var segments = new List<string>();
        while (deep.Length < 200) { var name = new string('d', 40); deep = Directory.CreateDirectory(Path.Join(deep, name)).FullName; segments.Add(name); }
        var folder = Folder([.. segments]);
        var e = Assert.Throws<ProtocolException>(() => Start(new string('n', 70) + ".txt", 1, folder: folder));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
    }

    // ----- who may write where -----

    [Fact]
    public void A_folder_that_is_not_enabled_for_uploads_rejects_every_write()
    {
        Setup(enabled: false);
        var summary = _files.ListRoots().Roots.Single();
        Assert.True(summary.ReadOnly);
        Assert.False(summary.CanUpload);
        Assert.Equal(ErrorCode.Forbidden, Assert.Throws<ProtocolException>(() => Start("x.txt", 1)).Code);
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
    }

    [Fact]
    public void The_destination_must_be_a_folder_the_PC_issued()
    {
        var file = Path.Join(_rootPath, "Inbox", "existing.txt");
        File.WriteAllText(file, "x");
        var fileEntry = _files.ListDirectory(new DirectoryQuery { RootId = "r1", EntryId = Folder("Inbox"), PageSize = 50 }).Entries.Single().EntryId;
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: fileEntry)).Code);
        Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: "not-a-token"));
        Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: ""));
        var other = new SealedTokens(RandomNumberGenerator.GetBytes(32));
        var foreign = other.Seal(new TokenBody { Type = "e", RootId = "r1", Segments = ["Inbox"], IsDirectory = true });
        Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: foreign));                                 // sealed by another PC's key
        var unknownRoot = _tokens.Seal(new TokenBody { Type = "e", RootId = "gone", Segments = [], IsDirectory = true });
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: unknownRoot)).Code);
    }

    [Fact]
    public async Task An_upload_belongs_to_the_browser_that_made_it()
    {
        var started = Start("mine.bin", 10);
        var reference = new UploadRef { UploadId = started.UploadId, DeviceId = "someone-else" };
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => _uploads.Status(reference)).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 0, Bytes(10), device: "someone-else"))).Code);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => _uploads.Finish(reference)).Code);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => _uploads.Cancel(reference)).Code);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => _uploads.Status(new UploadRef { UploadId = "0123456789abcdef0123456789abcdef", DeviceId = Device })).Code);
    }

    [Fact]
    public async Task Turning_uploads_off_or_revoking_the_browser_discards_what_was_in_flight()
    {
        var a = Start("a.bin", 100);
        await Send(a.UploadId, 0, Bytes(50));
        _uploads.CancelForDevice(Device);
        Assert.Equal("cancelled", _uploads.Status(new UploadRef { UploadId = a.UploadId, DeviceId = Device }).State);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<ProtocolException>(() => Send(a.UploadId, 50, Bytes(50)))).Code);
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));

        var b = Start("b.bin", 100);
        await Send(b.UploadId, 0, Bytes(50));
        _roots[0] = _roots[0] with { UploadsEnabled = false };                       // the owner turns uploads off mid-upload
        Assert.Equal(ErrorCode.Forbidden, (await Assert.ThrowsAsync<ProtocolException>(() => Send(b.UploadId, 50, Bytes(50)))).Code);
        Assert.Equal(ErrorCode.Forbidden, Assert.Throws<ProtocolException>(() => _uploads.Finish(new UploadRef { UploadId = b.UploadId, DeviceId = Device })).Code);
        _uploads.DropForRoot("r1");
        Assert.False(File.Exists(InboxFile("b.bin")));
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
    }

    [Fact]
    public async Task Cancelling_removes_the_partial_file_but_a_finished_file_stays()
    {
        var a = Start("c.bin", 10);
        await Send(a.UploadId, 0, Bytes(10));
        _uploads.Cancel(new UploadRef { UploadId = a.UploadId, DeviceId = Device });
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.False(File.Exists(InboxFile("c.bin")));
        var done = await Upload("kept.bin", Bytes(10));
        _uploads.Cancel(new UploadRef { UploadId = done.UploadId, DeviceId = Device });
        Assert.True(File.Exists(InboxFile("kept.bin")));
    }

    // ----- time and crashes -----

    [Fact]
    public async Task A_quiet_upload_expires_and_its_partial_file_is_removed()
    {
        var started = Start("idle.bin", 100);
        await Send(started.UploadId, 0, Bytes(50));
        _clock.Now += TimeSpan.FromMinutes(44);
        Assert.Equal("receiving", _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).State);   // looking counts as activity
        _clock.Now += TimeSpan.FromMinutes(46);
        _uploads.Sweep();
        var status = _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.Equal("expired", status.State);
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.Equal(ErrorCode.Expired, (await Assert.ThrowsAsync<ProtocolException>(() => Send(started.UploadId, 50, Bytes(50)))).Code);
        Assert.Equal(ErrorCode.Expired, Assert.Throws<ProtocolException>(() => _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device })).Code);
        Assert.False(File.Exists(InboxFile("idle.bin")));
    }

    [Fact]
    public async Task An_upload_cannot_be_kept_alive_forever()
    {
        var started = Start("long.bin", 100);
        for (var round = 0; round < 19; round++) { _clock.Now += TimeSpan.FromMinutes(40); _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }); }
        Assert.Equal("expired", _uploads.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device }).State);   // 12 h after the start, however often it was looked at
        await Task.CompletedTask;
    }

    [Fact]
    public async Task After_a_crash_nothing_half_received_survives_and_no_final_file_exists()
    {
        var started = Start("crash.bin", 3000);
        await Send(started.UploadId, 0, Bytes(1000));
        Assert.Single(Directory.GetFiles(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        var restarted = Service();                                                       // a new process knows nothing of the old session
        restarted.CleanupAfterRestart();
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.False(File.Exists(InboxFile("crash.bin")));
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => restarted.Status(new UploadRef { UploadId = started.UploadId, DeviceId = Device })).Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => restarted.WriteChunkAsync(
            new WriteChunkRequest { UploadId = started.UploadId, DeviceId = Device, Offset = 1000, Length = 10, Sha256 = Hex(Bytes(10)), StreamId = "s", UplinkToken = "t" },
            _ => Task.FromResult<Stream>(new MemoryStream(Bytes(10))), CancellationToken.None))).Code);
    }

    // ----- links -----

    private bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }   // no privilege to make links on this host
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_never_receives_the_file()
    {
        var outside = Directory.CreateDirectory(Path.Join(_dir, "outside")).FullName;
        Directory.CreateDirectory(Path.Join(_rootPath, "Swap"));
        var swap = Folder("Swap");
        Directory.Delete(Path.Join(_rootPath, "Swap"));
        if (!TryLink(Path.Join(_rootPath, "Swap"), outside)) return;
        Assert.Equal(ErrorCode.LinkNotAllowed, Assert.Throws<ProtocolException>(() => Start("x.txt", 1, folder: swap)).Code);

        // Swapped after the upload started, before the commit.
        Directory.Delete(Path.Join(_rootPath, "Swap"));
        Directory.CreateDirectory(Path.Join(_rootPath, "Swap"));
        var started = Start("y.txt", 3, folder: swap);
        await Send(started.UploadId, 0, [1, 2, 3]);
        Directory.Delete(Path.Join(_rootPath, "Swap"));
        Assert.True(TryLink(Path.Join(_rootPath, "Swap"), outside));
        Assert.Equal(ErrorCode.LinkNotAllowed, Assert.Throws<ProtocolException>(() => _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device })).Code);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void A_partial_folder_that_is_really_a_link_is_not_used()
    {
        var outside = Directory.CreateDirectory(Path.Join(_dir, "elsewhere")).FullName;
        if (!TryLink(Path.Join(_rootPath, PathPolicy.PartialDirectoryName), outside)) return;
        Assert.Equal(ErrorCode.LinkNotAllowed, Assert.Throws<ProtocolException>(() => Start("x.txt", 1)).Code);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Service().CleanupAfterRestart();                                                  // the restart cleanup removes the link itself, never what it points to
        Assert.False(Directory.Exists(Path.Join(_rootPath, PathPolicy.PartialDirectoryName)));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void A_file_cannot_be_moved_over_an_existing_one()
    {
        var a = Path.Join(_dir, "src.txt"); var b = Path.Join(_dir, "dst.txt");
        File.WriteAllText(a, "new"); File.WriteAllText(b, "old");
        Assert.False(NativeFile.MoveNoReplace(a, b));
        Assert.Equal("old", File.ReadAllText(b));
        Assert.True(File.Exists(a));
        File.Delete(b);
        Assert.True(NativeFile.MoveNoReplace(a, b));
        Assert.Equal("new", File.ReadAllText(b));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public async Task The_top_of_a_shared_folder_is_a_destination_and_a_folder_of_another_shared_folder_is_not()
    {
        var started = _uploads.Start(new StartUploadRequest { RootId = "r1", FolderEntryId = null, Name = "top.txt", Size = 1, Collision = "reject", DeviceId = Device });
        Assert.Equal(new[] { "Share" }, started.Destination);
        await Send(started.UploadId, 0, [7]);
        _uploads.Finish(new UploadRef { UploadId = started.UploadId, DeviceId = Device });
        Assert.True(File.Exists(Path.Join(_rootPath, "top.txt")));
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => _uploads.Start(new StartUploadRequest { RootId = "nope", FolderEntryId = null, Name = "x.txt", Size = 1, Collision = "reject", DeviceId = Device })).Code);
        var other = Directory.CreateDirectory(Path.Join(_dir, "other")).FullName;
        _roots.Add(new SharedRoot("r2", "Other", other, UploadsEnabled: true));
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => _uploads.Start(new StartUploadRequest { RootId = "r2", FolderEntryId = Folder("Inbox"), Name = "x.txt", Size = 1, Collision = "reject", DeviceId = Device })).Code);
        Assert.Empty(Directory.GetFileSystemEntries(other));
    }

    [Fact]
    public async Task Uploads_into_a_subfolder_land_there_and_nowhere_else()
    {
        Directory.CreateDirectory(Path.Join(_rootPath, "Inbox", "Trips"));
        await Upload("t.txt", [1, 2, 3], folder: Folder("Inbox", "Trips"));
        Assert.Equal(new[] { "t.txt" }, Names(Path.Join(_rootPath, "Inbox", "Trips")));
        Assert.Empty(Names(Path.Join(_rootPath, "Inbox")).Where(n => n == "t.txt"));
        Assert.False(File.Exists(Path.Join(_rootPath, "t.txt")));
    }
}
