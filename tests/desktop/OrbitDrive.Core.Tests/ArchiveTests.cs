using System.IO.Compression;
using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

/// <summary>D8: the ZIP writer and the staged archive service. Archives are extracted with the framework's own reader and compared.</summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-archive-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    private readonly List<SharedRoot> _roots = [];
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string MakeRoot(string name = "Share", string id = "r1")
    {
        var path = Directory.CreateDirectory(Path.Join(_dir, "root-" + id)).FullName;
        _roots.Add(new SharedRoot(id, name, path));
        return path;
    }

    private (FileAccessService Files, ArchiveService Archives, string Staging) Make(ArchiveOptions? template = null, TimeProvider? clock = null)
    {
        var staging = Path.Join(_dir, "staging");
        var files = new FileAccessService(() => _roots, _tokens);
        var options = (template ?? new ArchiveOptions { Directory = staging }) with { Directory = staging };
        var archives = new ArchiveService(files, _tokens, () => _roots, options, clock);
        files.Archives = archives;
        return (files, archives, staging);
    }

    private string Entry(FileAccessService files, string rootId, params string[] path)
    {
        var parent = new DirectoryQuery { RootId = rootId, PageSize = 200 };
        FileEntry? found = null;
        for (var i = 0; i < path.Length; i++)
        {
            var page = files.ListDirectory(parent);
            found = page.Entries.Single(e => e.Name == path[i]);
            parent = new DirectoryQuery { RootId = rootId, EntryId = found.EntryId, PageSize = 200 };
        }
        return found!.EntryId;
    }

    private static async Task<ArchiveStatus> Finish(ArchiveService archives, ArchiveRef reference, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var status = archives.Status(reference);
            if (status.State is "ready" or "failed" or "cancelled") return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("archive did not finish");
            await Task.Delay(20);
        }
    }

    private static async Task<byte[]> ReadAll(FileAccessService files, PrepareReadResult prepared)
    {
        using var read = files.OpenRead(prepared.ReadToken, 0, prepared.Size);
        var bytes = new byte[prepared.Size];
        RandomAccess.Read(read.Handle, bytes, 0);
        await Task.CompletedTask;
        return bytes;
    }

    private async Task<string> Extract(FileAccessService files, ArchiveService archives, ArchiveStatus status, ArchiveRef reference, string name = "out")
    {
        Assert.Equal("ready", status.State);
        var prepared = files.PrepareReadAsync(new PrepareReadRequest { EntryId = reference.ArchiveId, Variant = "archive" }, CancellationToken.None).GetAwaiter().GetResult();
        var bytes = await ReadAll(files, prepared);
        Assert.Equal(prepared.Revision, "sha256-" + Convert.ToHexStringLower(SHA256.HashData(bytes)));      // the hash promised is the hash of what is served
        Assert.Equal("application/zip", prepared.ContentType);
        var target = Path.Join(_dir, name + Guid.NewGuid().ToString("N")[..6]);
        var zipPath = target + ".zip";
        await File.WriteAllBytesAsync(zipPath, bytes);
        ZipFile.ExtractToDirectory(zipPath, target);
        return target;
    }

    private static void AssertSame(string source, string extracted)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.Hidden)) { Assert.False(File.Exists(Path.Join(extracted, Path.GetRelativePath(source, file)))); continue; }   // hidden items stay out of archives
            var copy = Path.Join(extracted, Path.GetRelativePath(source, file));
            Assert.True(File.Exists(copy), "missing " + copy);
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(copy)), "content differs " + copy);
        }
    }

    // ----- ZipWriter -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_writer_makes_archives_that_standard_readers_open_including_ZIP64_structures(bool zip64)
    {
        var random = RandomNumberGenerator.GetBytes(3_000_000);
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible line é 文\n", 50_000)));
        await using var output = new FileStream(Path.Join(_dir, "w.zip"), FileMode.Create, FileAccess.ReadWrite);
        using (var zip = new ZipWriter(output, forceZip64: zip64))
        {
            zip.AddDirectory("Folder/", DateTime.UtcNow);
            zip.AddDirectory("Folder/Empty/", DateTime.UtcNow);
            foreach (var (name, bytes, compress) in new[] { ("Folder/random.bin", random, false), ("Folder/ünï 文.txt", text, true), ("Folder/zero.txt", Array.Empty<byte>(), true), ("Folder/stored.txt", text, false) })
            {
                var path = Path.Join(_dir, "src-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(path, bytes);
                using var handle = File.OpenHandle(path);
                await zip.AddFileAsync(name, new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc), handle, bytes.Length, compress, null, CancellationToken.None);
            }
            zip.Finish();
        }
        output.Close();
        using var archive = ZipFile.OpenRead(Path.Join(_dir, "w.zip"));
        Assert.Equal(6, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.FullName == "Folder/Empty/");
        var unicode = archive.GetEntry("Folder/ünï 文.txt")!;
        Assert.True(unicode.CompressedLength < unicode.Length / 4);                                  // really deflated
        Assert.Equal(random.Length, archive.GetEntry("Folder/random.bin")!.CompressedLength);         // stored as is
        await using var read = unicode.Open();
        using var memory = new MemoryStream(); await read.CopyToAsync(memory);
        Assert.True(memory.ToArray().AsSpan().SequenceEqual(text));
        await using var r2 = archive.GetEntry("Folder/random.bin")!.Open();
        using var m2 = new MemoryStream(); await r2.CopyToAsync(m2);
        Assert.True(m2.ToArray().AsSpan().SequenceEqual(random));                                    // CRC is verified by the reader as it goes
    }

    [Fact]
    public async Task More_than_65534_entries_use_the_ZIP64_directory()
    {
        await using var output = new FileStream(Path.Join(_dir, "many.zip"), FileMode.Create, FileAccess.ReadWrite);
        using (var zip = new ZipWriter(output)) { for (var i = 0; i < 66_000; i++) zip.AddDirectory($"d{i}/", DateTime.UtcNow); zip.Finish(); }
        output.Close();
        using var archive = ZipFile.OpenRead(Path.Join(_dir, "many.zip"));
        Assert.Equal(66_000, archive.Entries.Count);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("../x.txt")] [InlineData("/abs.txt")] [InlineData("a//b.txt")] [InlineData("a\\b.txt")] [InlineData("a/./b.txt")] [InlineData("a/../b.txt")] [InlineData("")] [InlineData("dir/")]
    public async Task Unsafe_names_are_refused(string name)
    {
        await using var output = new FileStream(Path.Join(_dir, "n.zip"), FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipWriter(output);
        var path = Path.Join(_dir, "n.src"); await File.WriteAllBytesAsync(path, [1]);
        using var handle = File.OpenHandle(path);
        await Assert.ThrowsAsync<ArgumentException>(() => zip.AddFileAsync(name, DateTime.UtcNow, handle, 1, true, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_source_that_is_shorter_than_announced_leaves_no_half_entry_and_the_archive_stays_valid()
    {
        await using var output = new FileStream(Path.Join(_dir, "short.zip"), FileMode.Create, FileAccess.ReadWrite);
        using (var zip = new ZipWriter(output))
        {
            var good = Path.Join(_dir, "good.src"); await File.WriteAllBytesAsync(good, "good"u8.ToArray());
            var bad = Path.Join(_dir, "bad.src"); await File.WriteAllBytesAsync(bad, new byte[10]);
            using (var h = File.OpenHandle(good)) await zip.AddFileAsync("good.txt", DateTime.UtcNow, h, 4, true, null, CancellationToken.None);
            using (var h = File.OpenHandle(bad)) await Assert.ThrowsAsync<ZipSourceChangedException>(() => zip.AddFileAsync("bad.txt", DateTime.UtcNow, h, 100, true, null, CancellationToken.None));
            using (var h = File.OpenHandle(bad)) await zip.AddFileAsync("bad.txt", DateTime.UtcNow, h, 10, false, null, CancellationToken.None);   // the name is free again
            zip.Finish();
        }
        output.Close();
        using var archive = ZipFile.OpenRead(Path.Join(_dir, "short.zip"));
        Assert.Equal(["bad.txt", "good.txt"], archive.Entries.Select(e => e.FullName).Order().ToArray());
    }

    // ----- ArchiveService --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_folder_and_a_loose_file_become_one_archive_whose_extracted_contents_equal_the_originals()
    {
        var root = MakeRoot();
        var deep = Path.Join(root, "Album", "ünï", "文書", "deep", "deeper"); Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Join(root, "Album", "cover.jpg"), RandomNumberGenerator.GetBytes(120_000));
        File.WriteAllText(Path.Join(root, "Album", "notes é.txt"), string.Concat(Enumerable.Repeat("hello\n", 5000)));
        File.WriteAllBytes(Path.Join(deep, "leaf.bin"), RandomNumberGenerator.GetBytes(70_000));
        Directory.CreateDirectory(Path.Join(root, "Album", "Empty"));
        File.WriteAllText(Path.Join(root, "Album", ".secret"), "hidden");
        File.SetAttributes(Path.Join(root, "Album", ".secret"), File.GetAttributes(Path.Join(root, "Album", ".secret")) | FileAttributes.Hidden);   // a dot-name is hidden on Unix, the attribute on Windows
        File.WriteAllText(Path.Join(root, "loose.txt"), "a loose file");
        var (files, archives, staging) = Make();
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "Album"), Entry(files, "r1", "loose.txt")], Strict = false });
        var status = await Finish(archives, reference);
        Assert.Equal(0, status.OmittedCount);
        Assert.Equal("MyOrbitDrive selection.zip", status.Name);
        Assert.True(status.Resumable);
        var extracted = await Extract(files, archives, status, reference);
        AssertSame(root, extracted);
        Assert.True(Directory.Exists(Path.Join(extracted, "Album", "Empty")));
        Assert.Equal(["Album", "loose.txt"], Directory.EnumerateFileSystemEntries(extracted).Select(p => Path.GetFileName(p)!).Order().ToArray());     // nothing from outside the selection, no absolute or parent paths
        Assert.Equal(status.FilesTotal, status.FilesDone);
        Assert.Equal(status.BytesTotal, status.BytesDone);
        Assert.Single(Directory.GetFiles(staging));                                                                                       // one staged file, no leftovers
    }

    [Fact]
    public async Task A_staged_archive_serves_exact_ranges_and_rejects_ones_outside_it()
    {
        var root = MakeRoot();
        File.WriteAllBytes(Path.Join(root, "a.bin"), RandomNumberGenerator.GetBytes(50_000));
        var (files, archives, _) = Make();
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "a.bin")], Strict = false });
        var status = await Finish(archives, reference);
        var prepared = await files.PrepareReadAsync(new PrepareReadRequest { EntryId = reference.ArchiveId, Variant = "archive" }, CancellationToken.None);
        Assert.Equal("a.bin.zip", prepared.Name);
        var all = await ReadAll(files, prepared);
        using var tail = files.OpenRead(prepared.ReadToken, 100, prepared.Size - 100);
        var bytes = new byte[prepared.Size - 100]; RandomAccess.Read(tail.Handle, bytes, 100);
        Assert.True(bytes.AsSpan().SequenceEqual(all.AsSpan(100)));
        Assert.Equal(ErrorCode.RangeNotSatisfiable, Assert.Throws<ProtocolException>(() => files.OpenRead(prepared.ReadToken, 0, prepared.Size + 1)).Code);
        Assert.Equal("ready", status.State);
    }

    [Fact]
    public async Task A_file_that_changes_while_it_is_read_is_left_out_and_reported_not_silently_included()
    {
        var root = MakeRoot();
        Directory.CreateDirectory(Path.Join(root, "Docs"));
        File.WriteAllText(Path.Join(root, "Docs", "stable.txt"), "stable");
        File.WriteAllText(Path.Join(root, "Docs", "moving.txt"), "0123456789");
        var (files, archives, _) = Make(new ArchiveOptions
        {
            Directory = "x",
            AfterOpen = name => { if (name.EndsWith("moving.txt")) File.AppendAllText(Path.Join(root, "Docs", "moving.txt"), "MORE"); return Task.CompletedTask; },
        });
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "Docs")], Strict = false });
        var status = await Finish(archives, reference);
        Assert.Equal("ready", status.State);
        Assert.Equal(1, status.OmittedCount);
        Assert.Contains("Docs/moving.txt", status.Omitted[0].Path);
        var extracted = await Extract(files, archives, status, reference);
        Assert.False(File.Exists(Path.Join(extracted, "Docs", "moving.txt")));
        Assert.Equal("stable", File.ReadAllText(Path.Join(extracted, "Docs", "stable.txt")));
        var report = File.ReadAllText(Path.Join(extracted, ArchiveOptions.OmissionReportName));      // anyone who unzips it sees that it is incomplete
        Assert.Contains("incomplete", report); Assert.Contains("moving.txt", report);
    }

    [Fact]
    public async Task Strict_mode_refuses_to_finish_a_partial_archive_and_leaves_nothing_staged()
    {
        var root = MakeRoot();
        Directory.CreateDirectory(Path.Join(root, "Docs"));
        File.WriteAllText(Path.Join(root, "Docs", "moving.txt"), "0123456789");
        var (files, archives, staging) = Make(new ArchiveOptions { Directory = "x", AfterOpen = _ => { File.AppendAllText(Path.Join(root, "Docs", "moving.txt"), "MORE"); return Task.CompletedTask; } });
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "Docs")], Strict = true });
        var status = await Finish(archives, reference);
        Assert.Equal("failed", status.State);
        Assert.Contains("partial archives are not allowed", status.Error!);
        await Assert.ThrowsAsync<ProtocolException>(() => files.PrepareReadAsync(new PrepareReadRequest { EntryId = reference.ArchiveId, Variant = "archive" }, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(staging));
    }

    [Fact]
    public async Task Cancelling_during_the_build_removes_the_staged_file_and_the_archive()
    {
        var root = MakeRoot();
        File.WriteAllBytes(Path.Join(root, "big.bin"), RandomNumberGenerator.GetBytes(10_000));
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var (files, archives, staging) = Make(new ArchiveOptions { Directory = "x", AfterOpen = async _ => { entered.TrySetResult(); await release.Task; } });
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "big.bin")], Strict = false });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("building", archives.Status(reference).State);
        Assert.True(archives.Cancel(reference).Ok);
        release.SetResult();
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => archives.Status(reference)).Code);
        for (var i = 0; i < 100 && Directory.GetFiles(staging).Length > 0; i++) await Task.Delay(30);
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Equal(0, archives.ActiveCount);
    }

    [Fact]
    public async Task Removing_a_shared_folder_ends_archives_made_from_it_and_stops_serving_them()
    {
        var root = MakeRoot();
        File.WriteAllText(Path.Join(root, "a.txt"), "data");
        var (files, archives, staging) = Make();
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "a.txt")], Strict = false });
        await Finish(archives, reference);
        var prepared = await files.PrepareReadAsync(new PrepareReadRequest { EntryId = reference.ArchiveId, Variant = "archive" }, CancellationToken.None);
        _roots.Clear();                                       // the owner removed the folder; even before the hook runs, the read is refused
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => files.OpenRead(prepared.ReadToken, 0, 10)).Code);
        archives.DropForRoot("r1");
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => archives.Status(reference)).Code);
        for (var i = 0; i < 100 && Directory.GetFiles(staging).Length > 0; i++) await Task.Delay(30);
        Assert.Empty(Directory.GetFiles(staging));
    }

    [Fact]
    public async Task Archives_expire_and_their_read_tokens_stop_working()
    {
        var root = MakeRoot();
        File.WriteAllText(Path.Join(root, "a.txt"), "data");
        var clock = new Clock();
        var (files, archives, _) = Make(clock: clock);
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "a.txt")], Strict = false });
        await Finish(archives, reference);
        var prepared = await files.PrepareReadAsync(new PrepareReadRequest { EntryId = reference.ArchiveId, Variant = "archive" }, CancellationToken.None);
        clock.Now += TimeSpan.FromHours(7);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => files.OpenRead(prepared.ReadToken, 0, 4)).Code);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<ProtocolException>(() => archives.Status(reference)).Code);
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task Disk_count_size_and_concurrency_limits_are_enforced_with_plain_messages()
    {
        var root = MakeRoot();
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Join(root, $"f{i}.txt"), "x");
        Directory.CreateDirectory(Path.Join(root, "Many"));
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Join(root, "Many", $"f{i}.txt"), "x");
        var (files, archives, _) = Make(new ArchiveOptions { Directory = "x", MaxFiles = 3 });
        var status = await Finish(archives, archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "Many")], Strict = false }));
        Assert.Equal("failed", status.State); Assert.Contains("more than 3 files", status.Error!);

        var (files2, archives2, _) = Make(new ArchiveOptions { Directory = "x", FreeBytes = () => 100 });
        var status2 = await Finish(archives2, archives2.Start(new ArchiveRequest { EntryIds = [Entry(files2, "r1", "f0.txt")], Strict = false }));
        Assert.Equal("failed", status2.State); Assert.Contains("not enough free space", status2.Error!);

        var (files3, archives3, _) = Make(new ArchiveOptions { Directory = "x", MaxSourceBytes = 0 });
        Assert.Equal("failed", (await Finish(archives3, archives3.Start(new ArchiveRequest { EntryIds = [Entry(files3, "r1", "f0.txt")], Strict = false }))).State);

        var (files4, archives4, _) = Make(new ArchiveOptions { Directory = "x", MaxSelection = 2 });
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives4.Start(new ArchiveRequest { EntryIds = [Entry(files4, "r1", "f0.txt"), Entry(files4, "r1", "f1.txt"), Entry(files4, "r1", "f2.txt")], Strict = false })).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives4.Start(new ArchiveRequest { EntryIds = [], Strict = false })).Code);

        var (files5, archives5, _) = Make(new ArchiveOptions { Directory = "x", MaxActive = 1 });
        archives5.Start(new ArchiveRequest { EntryIds = [Entry(files5, "r1", "f0.txt")], Strict = false });
        Assert.Equal(ErrorCode.LimitExceeded, Assert.Throws<ProtocolException>(() => archives5.Start(new ArchiveRequest { EntryIds = [Entry(files5, "r1", "f1.txt")], Strict = false })).Code);
    }

    [Fact]
    public void Forged_foreign_or_wrong_type_identifiers_never_start_or_read_an_archive()
    {
        var root = MakeRoot();
        File.WriteAllText(Path.Join(root, "a.txt"), "data");
        var (files, archives, _) = Make();
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives.Start(new ArchiveRequest { EntryIds = ["AAAA"], Strict = false })).Code);
        var other = new SealedTokens(RandomNumberGenerator.GetBytes(32));                     // another PC's key
        var foreign = new FileAccessService(() => _roots, other);
        var foreignEntry = foreign.ListDirectory(new DirectoryQuery { RootId = "r1", PageSize = 5 }).Entries[0].EntryId;
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives.Start(new ArchiveRequest { EntryIds = [foreignEntry], Strict = false })).Code);
        // A directory-listing cursor or a read token is not an archive reference.
        var ownEntry = Entry(files, "r1", "a.txt");
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives.Status(new ArchiveRef { ArchiveId = ownEntry })).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => archives.Cancel(new ArchiveRef { ArchiveId = "nonsense" })).Code);
        // The shared folder itself (no path) is not a selectable item.
        var unused = files;
    }

    [Fact]
    public async Task Same_named_items_from_different_folders_are_kept_apart_and_a_removed_item_is_reported()
    {
        var root = MakeRoot();
        Directory.CreateDirectory(Path.Join(root, "A")); Directory.CreateDirectory(Path.Join(root, "B"));
        File.WriteAllText(Path.Join(root, "A", "same.txt"), "from A");
        File.WriteAllText(Path.Join(root, "B", "same.txt"), "from B");
        var (files, archives, _) = Make();
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "A", "same.txt"), Entry(files, "r1", "B", "same.txt")], Strict = false });
        var status = await Finish(archives, reference);
        var extracted = await Extract(files, archives, status, reference);
        Assert.Equal(["same (2).txt", "same.txt"], Directory.GetFiles(extracted).Select(p => Path.GetFileName(p)!).Order().ToArray());
        Assert.Equal("from A", File.ReadAllText(Path.Join(extracted, "same.txt")));
        Assert.Equal("from B", File.ReadAllText(Path.Join(extracted, "same (2).txt")));
    }

    [Fact]
    public async Task Links_are_not_followed_into_an_archive_and_are_reported()
    {
        if (OperatingSystem.IsWindows()) return;                       // creating symlinks needs a privilege on Windows runners; junction behaviour is covered by FileAccessTests
        var root = MakeRoot();
        var outside = Directory.CreateDirectory(Path.Join(_dir, "outside")).FullName;
        File.WriteAllText(Path.Join(outside, "secret.txt"), "outside the share");
        Directory.CreateDirectory(Path.Join(root, "Docs"));
        File.WriteAllText(Path.Join(root, "Docs", "ok.txt"), "ok");
        Directory.CreateSymbolicLink(Path.Join(root, "Docs", "escape"), outside);
        File.CreateSymbolicLink(Path.Join(root, "Docs", "link.txt"), Path.Join(outside, "secret.txt"));
        var (files, archives, _) = Make();
        var reference = archives.Start(new ArchiveRequest { EntryIds = [Entry(files, "r1", "Docs")], Strict = false });
        var status = await Finish(archives, reference);
        Assert.Equal(1, status.OmittedCount);                           // "2 link(s) ... were skipped" is one report line for the folder
        Assert.Contains("link", status.Omitted[0].Reason);
        var extracted = await Extract(files, archives, status, reference);
        Assert.False(Directory.Exists(Path.Join(extracted, "Docs", "escape")));
        Assert.DoesNotContain("outside the share", string.Concat(Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Leftovers_of_a_crashed_run_are_removed_when_the_service_starts()
    {
        var staging = Path.Join(_dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Join(staging, "dead.part"), "partial"); File.WriteAllText(Path.Join(staging, "dead.zip"), "old");
        var (_, archives, _) = Make();
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Equal((0, 0), archives.Stats());
        await Task.CompletedTask;
    }
}
