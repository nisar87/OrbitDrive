using System.Buffers.Binary;
using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

/// <summary>D7: what a file really is, how big an image claims to be, the private thumbnail cache, and the derived-read policy.</summary>
public sealed class PreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-preview-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static byte[] FakeWebP() { var b = new byte[40]; "RIFF"u8.CopyTo(b); b[4] = 32; "WEBPVP8 "u8.CopyTo(b.AsSpan(8)); return b; }

    private sealed class FakeRunner(Func<byte[]>? output = null) : IThumbnailRunner
    {
        public int Runs;
        public Task<byte[]> RunAsync(Func<Stream, CancellationToken, Task> feed, int edge, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult((output ?? FakeWebP)());
        }
    }

    private (FileAccessService Service, ThumbnailCache Cache, FakeRunner Runner) Make(FakeRunner? runner = null, ThumbnailOptions? options = null, bool withThumbnails = true)
    {
        options ??= new ThumbnailOptions();
        runner ??= new FakeRunner();
        var cache = new ThumbnailCache(Path.Join(_dir, "cache"), options);
        var service = new FileAccessService(() => [], _tokens);
        if (withThumbnails) service.Thumbnails = new ThumbnailService(cache, runner, options);
        return (service, cache, runner);
    }

    private string Write(string name, byte[] bytes) { var path = Path.Join(_dir, name); File.WriteAllBytes(path, bytes); return path; }
    private Task<PrepareReadResult> Prepare(FileAccessService service, string path, string? variant) =>
        service.PrepareReadAsync(new PrepareReadRequest { EntryId = service.IssueOneShot(path, TimeSpan.FromHours(1)).Grant, Variant = variant }, CancellationToken.None);
    private static ErrorCode CodeOf(Action action) => Assert.Throws<ProtocolException>(action).Code;
    private async Task<ErrorCode> CodeOfAsync(Func<Task> action) => (await Assert.ThrowsAsync<ProtocolException>(action)).Code;

    [Fact]
    public void Types_are_judged_by_content_and_active_types_are_never_inline()
    {
        Assert.Equal("image/png", ContentSniffer.Classify(Png(2, 2), "a.png").ContentType);
        Assert.Equal("image/jpeg", ContentSniffer.Classify([0xFF, 0xD8, 0xFF, 0xE0, 0, 0], "a.jpg").ContentType);
        Assert.Equal("image/gif", ContentSniffer.Classify("GIF89a...."u8, "a.gif").ContentType);
        Assert.Equal("image/webp", ContentSniffer.Classify(FakeWebP(), "a.webp").ContentType);
        Assert.Equal("application/pdf", ContentSniffer.Classify("%PDF-1.7\n1 0 obj"u8, "a.pdf").ContentType);
        Assert.Equal("video/mp4", ContentSniffer.Classify("\0\0\0\u0018ftypisom"u8, "a.mp4").ContentType);
        Assert.Equal("audio/mp4", ContentSniffer.Classify("\0\0\0\u0018ftypM4A "u8, "a.m4a").ContentType);
        Assert.Equal("audio/mpeg", ContentSniffer.Classify("ID3\u0004\0\0"u8, "a.mp3").ContentType);
        Assert.Equal("audio/ogg", ContentSniffer.Classify("OggS\0\u0002"u8, "a.ogg").ContentType);
        // A name is not evidence; markup is never an image or a document.
        Assert.False(ContentSniffer.Classify("<html><script>alert(1)</script>"u8, "photo.png").Inlineable);
        Assert.False(ContentSniffer.Classify("<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"u8, "logo.svg").Inlineable);
        Assert.False(ContentSniffer.Classify("<!doctype html><p>hi"u8, "page.html").Inlineable);
        Assert.False(ContentSniffer.Classify("MZ\u0090\0\u0003"u8, "tool.exe").Inlineable);
        Assert.Equal(PreviewKind.Text, ContentSniffer.Classify("plain words, ünïcödé ✓\n"u8, "notes.txt").Kind);
        Assert.False(ContentSniffer.Classify("binary\0inside"u8, "notes.txt").Inlineable);
        Assert.False(ContentSniffer.Classify([0xFF, 0xFE, 0xFD, 0x80, 0x81], "notes.txt").Inlineable);   // not UTF-8
    }

    [Fact]
    public void Image_bombs_and_unreadable_headers_are_refused_before_any_decoding()
    {
        Assert.Equal((100, 80), ImageProbe.Require(Png(100, 80), "image/png"));
        Assert.Equal(ErrorCode.LimitExceeded, CodeOf(() => ImageProbe.Require(Png(60_000, 60_000), "image/png")));
        Assert.Equal(ErrorCode.LimitExceeded, CodeOf(() => ImageProbe.Require(Png(9_000, 9_000), "image/png")));          // 81 MP
        Assert.Equal(ErrorCode.LimitExceeded, CodeOf(() => ImageProbe.Require(Png(uint.MaxValue, 1), "image/png")));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => ImageProbe.Require(Png(0, 10), "image/png")));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => ImageProbe.Require(Png(10, 10)[..10], "image/png")));           // truncated
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xC0, 0x00, 0x11, 8, 0x75, 0x30, 0x75, 0x30, 3, 1, 0x22, 0 };   // APP0 then SOF0 30000 x 30000
        Assert.Equal(ErrorCode.LimitExceeded, CodeOf(() => ImageProbe.Require(jpeg, "image/jpeg")));
        var small = (byte[])jpeg.Clone(); small[13] = 0; small[14] = 0x40; small[15] = 0; small[16] = 0x30;
        Assert.Equal((48, 64), ImageProbe.Require(small, "image/jpeg"));
        var gif = "GIF89a"u8.ToArray().Concat(new byte[] { 0x40, 0, 0x30, 0, 0 }).ToArray();
        Assert.Equal((64, 48), ImageProbe.Require(gif, "image/gif"));
        var webp = new byte[40]; "RIFF"u8.CopyTo(webp); "WEBPVP8X"u8.CopyTo(webp.AsSpan(8)); webp[24] = 0xFF; webp[25] = 0xFF; webp[26] = 0xFF; webp[27] = 0xFF; webp[28] = 0xFF; webp[29] = 0xFF;
        Assert.Equal(ErrorCode.LimitExceeded, CodeOf(() => ImageProbe.Require(webp, "image/webp")));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => ImageProbe.Require(new byte[100], "image/tiff")));
    }

    [Fact]
    public void The_cache_is_bounded_private_and_clearable()
    {
        var options = new ThumbnailOptions { CacheQuotaBytes = 250 };
        var cache = new ThumbnailCache(Path.Join(_dir, "c"), options);
        for (var i = 0; i < 5; i++) { cache.Put($"key{i}", new byte[100]); Thread.Sleep(30); }
        var (files, bytes) = cache.Stats();
        Assert.True(bytes <= 250 && files <= 2, $"{files} files, {bytes} bytes");
        using (var newest = cache.TryOpen("key4")) Assert.NotNull(newest);   // the newest survived (closed again: Windows cannot delete an open file)
        Assert.Null(cache.TryOpen("key0"));
        Assert.DoesNotContain(Directory.GetFiles(Path.Join(_dir, "c")), f => f.Contains("report", StringComparison.OrdinalIgnoreCase));
        Assert.True(cache.Clear() >= 1);
        Assert.Equal(0, cache.Stats().Files);
        var key = ThumbnailCache.KeyFor(256, "root", ["Private folder", "tax return.png"], 10, 20, 30);
        Assert.Matches("^[0-9a-f]{32}$", key);                      // a hash: no name, no path
        Assert.NotEqual(key, ThumbnailCache.KeyFor(256, "root", ["Private folder", "tax return.png"], 10, 21, 30));   // another revision, another key
    }

    [Fact]
    public async Task A_preview_of_any_size_is_prepared_without_reading_the_whole_file()
    {
        var (service, _, _) = Make();
        var text = Write("notes.txt", "<script>alert(1)</script> hello"u8.ToArray());
        var prepared = await Prepare(service, text, "preview");
        Assert.Equal("text/plain; charset=utf-8", prepared.ContentType);      // markup inside is just text
        Assert.NotEqual("sha256-" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(text))), prepared.Revision);   // a metadata revision, not a content hash
        using (var read = service.OpenRead(prepared.ReadToken, 0, prepared.Size)) { var b = new byte[prepared.Size]; RandomAccess.Read(read.Handle, b, 0); Assert.Contains("hello", System.Text.Encoding.UTF8.GetString(b)); }
        File.WriteAllText(text, "<script>alert(2)</script> hullo");          // same size, different bytes
        Assert.Equal(ErrorCode.RevisionChanged, CodeOf(() => service.OpenRead(prepared.ReadToken, 0, 4)));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, Write("page.html", "<html>"u8.ToArray()), "preview")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, Write("fake.png", "<html><script>"u8.ToArray()), "preview")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, text, "something-else")));
    }

    [Fact]
    public async Task Thumbnails_are_generated_once_cached_and_stop_being_served_when_the_source_changes()
    {
        var (service, cache, runner) = Make();
        var path = Write("photo.png", Png(400, 300));
        var first = await Prepare(service, path, "thumb:256");
        Assert.Equal("image/webp", first.ContentType);
        Assert.Equal("photo.thumb.webp", first.Name);
        var second = await Prepare(service, path, "thumb:256");
        Assert.Equal(1, runner.Runs);                                                   // second request came from the cache
        using (var read = service.OpenRead(second.ReadToken, 0, second.Size)) Assert.Equal(first.Size, RandomAccess.GetLength(read.Handle));
        Assert.Equal(ErrorCode.RangeNotSatisfiable, CodeOf(() => service.OpenRead(second.ReadToken, 0, second.Size + 1)));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => service.OpenRead(first.ReadToken.Replace(first.ReadToken[10], first.ReadToken[10] == 'A' ? 'B' : 'A'), 0, 1)));   // tampered
        File.WriteAllBytes(path, Png(400, 301));                                        // the picture changed on the PC
        Assert.Equal(ErrorCode.RevisionChanged, CodeOf(() => service.OpenRead(first.ReadToken, 0, 1)));
        File.Delete(path);
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => service.OpenRead(first.ReadToken, 0, 1)));
        Assert.True(cache.Stats().Files >= 1);
        cache.Clear();                                                                   // owner purge: a still-valid token has nothing left to serve
        var again = Write("photo2.png", Png(10, 10));
        var tok = await Prepare(service, again, "thumb:128");
        cache.Clear();
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => service.OpenRead(tok.ReadToken, 0, 1)));
    }

    [Fact]
    public async Task Hostile_or_unsuitable_files_never_reach_the_decoder()
    {
        var (service, _, runner) = Make();
        Assert.Equal(ErrorCode.LimitExceeded, await CodeOfAsync(() => Prepare(service, Write("bomb.png", Png(60_000, 60_000)), "thumb:256")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, Write("fake.png", "<svg onload=alert(1)>"u8.ToArray()), "thumb:256")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, Write("doc.pdf", "%PDF-1.4"u8.ToArray()), "thumb:256")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service, Write("ok.png", Png(10, 10)), "thumb:999")));       // sizes are a closed list
        Assert.Equal(0, runner.Runs);
        // A helper that answers with something that is not a picture is refused, and the failure is remembered instead of retried.
        var junk = new FakeRunner(() => "<html>not a picture</html>"u8.ToArray());
        var (service2, cache2, _) = Make(junk);
        var path = Write("weird.png", Png(50, 50));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service2, path, "thumb:256")));
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => Prepare(service2, path, "thumb:256")));
        Assert.Equal(1, junk.Runs);
        Assert.Equal(0, cache2.Stats().Files);
    }

    [Fact]
    public async Task Without_a_helper_or_when_switched_off_thumbnails_are_unavailable_and_nothing_else_changes()
    {
        var path = Write("photo.png", Png(40, 40));
        var (noThumbs, _, _) = Make(withThumbnails: false);
        Assert.Equal(ErrorCode.Unavailable, await CodeOfAsync(() => Prepare(noThumbs, path, "thumb:256")));
        Assert.Equal("image/png", (await Prepare(noThumbs, path, "preview")).ContentType);          // the original is still previewable
        var off = new ThumbnailOptions { Enabled = false };
        var (disabled, _, runner) = Make(options: off);
        Assert.Equal(ErrorCode.Unavailable, await CodeOfAsync(() => Prepare(disabled, path, "thumb:256")));
        Assert.Equal(0, runner.Runs);
    }

    private string Script(string body)
    {
        var path = Path.Join(_dir, "helper-" + Guid.NewGuid().ToString("N")[..6] + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static Task Feed(Stream s, CancellationToken ct) => s.WriteAsync(new byte[1000], ct).AsTask();

    [Fact]
    public async Task The_helper_process_is_time_boxed_size_boxed_and_its_failures_are_contained()
    {
        if (!OperatingSystem.IsLinux()) return;   // Windows runs the real helper in the integration job; scripts cannot start children inside its job object
        var options = new ThumbnailOptions { Timeout = TimeSpan.FromSeconds(2), MaxOutputBytes = 1000 };
        var ok = new ProcessThumbnailRunner(Script("cat >/dev/null; printf 'RIFF....WEBPVP8 data'"), options);
        Assert.StartsWith("RIFF", System.Text.Encoding.ASCII.GetString(await ok.RunAsync(Feed, 256, CancellationToken.None)));
        var hang = new ProcessThumbnailRunner(Script("sleep 30"), options);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(ErrorCode.Timeout, await CodeOfAsync(() => hang.RunAsync(Feed, 256, CancellationToken.None)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        var flood = new ProcessThumbnailRunner(Script("cat >/dev/null; head -c 100000 /dev/zero"), options);
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => flood.RunAsync(Feed, 256, CancellationToken.None)));
        var crash = new ProcessThumbnailRunner(Script("cat >/dev/null; exit 3"), options);
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => crash.RunAsync(Feed, 256, CancellationToken.None)));
        var silent = new ProcessThumbnailRunner(Script("cat >/dev/null; exit 0"), options);
        Assert.Equal(ErrorCode.InvalidRequest, await CodeOfAsync(() => silent.RunAsync(Feed, 256, CancellationToken.None)));
    }
}
