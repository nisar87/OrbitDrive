using System.IO.Compression;
using System.Security.Cryptography;
using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

/// <summary>R47: the archive writer against hostile names, random contents and sources that change under it. Whatever happens, the archive that comes out must be readable by the framework's own reader.</summary>
public sealed class ZipWriterFuzzTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-zipfuzz-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Source(byte[] content) { var path = Path.Join(_dir, Guid.NewGuid().ToString("N")); File.WriteAllBytes(path, content); return path; }

    private static async Task Add(ZipWriter zip, string name, string path, long declared, bool compress)
    {
        using var handle = File.OpenHandle(path);
        await zip.AddFileAsync(name, DateTime.UtcNow, handle, declared, compress, null, CancellationToken.None);
    }

    public static TheoryData<string> UnsafeNames => new()
    {
        "", "/", "/abs", "a//b", "a\\b", "..", "../x", "a/../b", "a/./b", "./a", "a/..", "a\0b", "dir/", "x/y/", new string('n', 65536),
    };

    [Theory, MemberData(nameof(UnsafeNames))]
    public async Task Names_that_could_escape_or_confuse_an_extractor_are_refused_and_leave_the_archive_valid(string name)
    {
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        var source = Source([1, 2, 3]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Add(zip, name, source, 3, compress: false));
        await Add(zip, "good.bin", source, 3, compress: false);
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(["good.bin"], archive.Entries.Select(e => e.FullName).ToArray());
    }

    [Fact]
    public async Task Odd_but_legal_names_survive_a_round_trip_unchanged()
    {
        var names = new[] { "naïve café.txt", "日本語/ファイル.txt", "emoji 😀.bin", "אבג/דהו.txt", "a b/ c .txt", "CON.txt", "name.with.dots...", "UPPER", "upper", "dir/sub dir/file (1).tar.gz", new string('é', 120) + ".txt" };
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        var source = Source("x"u8.ToArray());
        foreach (var name in names) await Add(zip, name, source, 1, compress: false);
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var entry in archive.Entries) { using var s = entry.Open(); Assert.Equal((byte)'x', (byte)s.ReadByte()); }
    }

    [Fact]
    public async Task A_duplicate_name_is_refused_without_damaging_the_archive()
    {
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        var source = Source([9]);
        await Add(zip, "same.txt", source, 1, false);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Add(zip, "same.txt", source, 1, false));
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Single(archive.Entries);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Random_contents_and_sizes_round_trip_byte_for_byte_with_and_without_compression_and_zip64(bool forceZip64)
    {
        for (var seed = 0; seed < 15; seed++)
        {
            var random = new Random(seed * 7919 + (forceZip64 ? 1 : 0));
            using var output = new MemoryStream();
            var zip = new ZipWriter(output, forceZip64);
            var expected = new Dictionary<string, byte[]>();
            var count = random.Next(1, 12);
            for (var i = 0; i < count; i++)
            {
                var size = random.Next(5) switch { 0 => 0, 1 => 1, 2 => random.Next(1, 100), 3 => random.Next(ZipWriter.CopyBuffer - 3, ZipWriter.CopyBuffer + 4), _ => random.Next(1, 3 * ZipWriter.CopyBuffer) };
                var content = new byte[size];
                if (random.Next(2) == 0) random.NextBytes(content); else for (var j = 0; j < size; j++) content[j] = (byte)(j % 7);
                var name = $"d{random.Next(3)}/f{i}-{random.Next(1000)}.bin";
                expected[name] = content;
                await Add(zip, name, Source(content), size, compress: random.Next(2) == 0);
            }
            zip.Finish();
            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            Assert.Equal(expected.Count, archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                using var s = entry.Open(); using var copy = new MemoryStream(); s.CopyTo(copy);
                Assert.True(expected[entry.FullName].AsSpan().SequenceEqual(copy.ToArray()), $"seed {seed}: {entry.FullName} differs");
            }
        }
    }

    [Theory]
    [InlineData(100, 50), InlineData(100, 0), InlineData(3 * 1024 * 1024, 1024 * 1024 + 5), InlineData(10, 11), InlineData(0, 5)]
    public async Task A_source_that_does_not_match_what_it_announced_never_corrupts_the_archive(int declared, int actual)
    {
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        await Add(zip, "before.txt", Source("before"u8.ToArray()), 6, false);
        var actualBytes = RandomNumberGenerator.GetBytes(actual);
        var changed = false;
        try { await Add(zip, "moving.bin", Source(actualBytes), declared, compress: declared % 2 == 0); }
        catch (ZipSourceChangedException) { changed = true; }
        await Add(zip, "after.txt", Source("after"u8.ToArray()), 5, false);
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("before.txt", names); Assert.Contains("after.txt", names);
        Assert.Equal(declared != actual, changed);   // any mismatch between what was announced and what was read is refused
        foreach (var entry in archive.Entries) { using var s = entry.Open(); using var sink = new MemoryStream(); s.CopyTo(sink); Assert.Equal(entry.Length, sink.Length); }
        if (changed) Assert.DoesNotContain("moving.bin", names);
    }

    [Fact]
    public async Task Cancelling_part_way_leaves_a_readable_archive_without_the_cancelled_entry()
    {
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        await Add(zip, "kept.txt", Source("kept"u8.ToArray()), 4, false);
        using var cancel = new CancellationTokenSource();
        using var handle = File.OpenHandle(Source(RandomNumberGenerator.GetBytes(4 * ZipWriter.CopyBuffer)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => zip.AddFileAsync("big.bin", DateTime.UtcNow, handle, 4 * ZipWriter.CopyBuffer, false, written => { if (written > 0) cancel.Cancel(); }, cancel.Token));
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(["kept.txt"], archive.Entries.Select(e => e.FullName).ToArray());
    }

    [Fact]
    public void Timestamps_outside_the_zip_range_are_clamped_not_thrown()
    {
        using var output = new MemoryStream();
        var zip = new ZipWriter(output);
        zip.AddDirectory("old", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        zip.AddDirectory("future", new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        zip.Finish();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(2, archive.Entries.Count);
    }
}
