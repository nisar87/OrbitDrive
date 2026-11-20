using System.IO.Compression;
using System.Text;
using OrbitDrive.Core;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    // Built from parts so this file holds no literal personal-looking path (the repository secret scan would, rightly, flag one).
    private const string Home = "C:" + "\\" + "Users" + "\\" + "Ana";

    private readonly string _dir = Path.Join(Path.GetTempPath(), "orbit-diag-" + Guid.NewGuid().ToString("N"));
    public DiagnosticsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // ---------------------------------------------------------------- redaction
    // The second argument lists, separated by "|", the pieces that must not survive redaction.
    [Theory]
    [InlineData("Cannot open " + Home + @"\Documents\taxes 2025.pdf now", "Ana|taxes|Documents|pdf")]
    [InlineData(@"\\fileserver\finance\payroll.xlsx failed", "fileserver|finance|payroll|xlsx")]
    [InlineData("denied /home/ana/photos/holiday.jpg for user", "ana|photos|holiday|jpg")]
    [InlineData("opened ~/notes/diary.txt", "notes|diary")]
    [InlineData("mail ana.k+work@example.com bounced", "ana|example.com")]
    [InlineData("GET https://relay.example.org/v1/downloads/AbCdEf?token=secret#frag failed", "relay.example.org|secret|frag|downloads")]
    [InlineData("token AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-abc used", "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig failed", "eyJhbGciOi|payload")]
    // A name that contains spaces can only be partly recognised (the extension part). That is why events never hand names to the history at all.
    [InlineData("a file named holiday photo 2024.jpg is already there", "2024.jpg|jpg")]
    [InlineData("report.final.docx was skipped", "report|docx")]
    public void Private_parts_are_replaced_and_nothing_of_them_remains(string input, string mustNotRemain)
    {
        var cleaned = Redactor.Text(input);
        foreach (var piece in mustNotRemain.Split('|')) Assert.DoesNotContain(piece, cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.True(Redactor.LooksPrivate(input), "the original should be recognised as private");
        Assert.False(Redactor.LooksPrivate(cleaned), $"still looks private: {cleaned}");
    }

    [Theory]
    [InlineData("state approved")]
    [InlineData("3 shared folders")]
    [InlineData("version 0.1.0 on 10.0.26100")]
    [InlineData("1048576 bytes in 2 pieces")]
    [InlineData("code LimitExceeded: too many requests")]
    [InlineData("download interrupted")]
    public void Ordinary_facts_are_left_alone(string input)
    {
        Assert.Equal(input, Redactor.Text(input));
        Assert.False(Redactor.LooksPrivate(input));
    }

    [Fact]
    public void Control_characters_cannot_be_used_to_forge_lines_or_hide_text()
    {
        var cleaned = Redactor.Text("one\r\ntwo\u0000three\u001b[31mred");
        Assert.DoesNotContain('\n', cleaned); Assert.DoesNotContain('\r', cleaned); Assert.DoesNotContain('\u0000', cleaned); Assert.DoesNotContain('\u001b', cleaned);
    }

    [Fact] public void Null_and_empty_are_empty() { Assert.Equal("", Redactor.Text(null)); Assert.Equal("", Redactor.Text("")); Assert.False(Redactor.LooksPrivate(null)); }

    // ---------------------------------------------------------------- activity history
    [Fact]
    public void Entries_come_back_newest_first_with_their_byte_counts()
    {
        var clock = new Clock(); var log = new ActivityLog(_dir, clock: clock);
        log.Record("connected"); clock.Advance(TimeSpan.FromSeconds(5)); log.Record("download", "sent_to_relay", 4096); clock.Advance(TimeSpan.FromSeconds(5)); log.Record("disconnected");
        var entries = log.Recent(10);
        Assert.Equal(["disconnected", "download", "connected"], entries.Select((e) => e.Kind));
        Assert.Equal(4096, entries[1].Bytes); Assert.Equal("sent_to_relay", entries[1].Detail); Assert.Null(entries[0].Bytes);
        Assert.Equal(2, log.Recent(2).Count);
    }

    [Fact]
    public void A_careless_caller_cannot_put_a_path_a_name_or_a_secret_in_the_history_not_even_on_disk()
    {
        var log = new ActivityLog(_dir);
        log.Record("upload_failed", "could not write " + Home + @"\Private\will.docx for ana@example.com with token AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abc");
        log.Record("download", "/home/ana/secret/plan.pdf");
        var onDisk = File.ReadAllText(Path.Join(_dir, "activity.jsonl"));
        foreach (var piece in new[] { "Ana", "ana", "will", "docx", "example.com", "AbCdEf", "plan", "pdf", "Private", "/secret", "secret/" }) Assert.DoesNotContain(piece, onDisk);
        Assert.Contains("<path>", onDisk);
    }

    [Theory]
    [InlineData("../../etc/passwd")] [InlineData("Download")] [InlineData("a b")] [InlineData("")] [InlineData("x\ny")] [InlineData("kind-with-dash")]
    public void A_kind_that_is_not_a_short_lowercase_word_is_recorded_as_unknown(string kind)
    {
        var log = new ActivityLog(_dir);
        log.Record(kind);
        Assert.Equal("unknown", log.Recent(1)[0].Kind);
    }

    [Fact]
    public void A_very_long_detail_is_shortened()
    {
        var log = new ActivityLog(_dir, new ActivityLogOptions { MaxDetailChars = 50 });
        log.Record("error", new string('x', 10_000).Replace("xxxxxxxxxx", "word word "));
        Assert.True(log.Recent(1)[0].Detail.Length <= 51);
    }

    [Fact]
    public void The_history_has_a_fixed_ceiling_and_keeps_the_newest()
    {
        var options = new ActivityLogOptions { MaxFileBytes = 1024, MaxFiles = 3 };
        var log = new ActivityLog(_dir, options);
        for (var i = 0; i < 400; i++) log.Record("download", $"outcome {i}", i);
        var (files, bytes) = log.Footprint();
        Assert.InRange(files, 2, 3);
        Assert.True(bytes <= 3 * (1024 + 200), $"{bytes} bytes");
        Assert.InRange(Directory.GetFiles(_dir, "activity*.jsonl").Length, 2, 3);
        var newest = log.Recent(5); Assert.Equal(399, newest[0].Bytes); Assert.Equal(395, newest[4].Bytes);
        Assert.DoesNotContain(log.Recent(2000), (e) => e.Bytes == 0);                       // the oldest ones were dropped
    }

    [Fact]
    public void A_damaged_or_half_written_line_is_skipped_and_the_rest_is_still_read()
    {
        var log = new ActivityLog(_dir);
        log.Record("connected");
        File.AppendAllText(Path.Join(_dir, "activity.jsonl"), "{\"time\":\"not a time\n{broken\n\u0000\u0000\n");
        log.Record("disconnected");
        Assert.Equal(["disconnected", "connected"], log.Recent(10).Select((e) => e.Kind));
    }

    [Fact]
    public void An_entry_tampered_to_hold_private_text_is_cleaned_again_when_read()
    {
        File.WriteAllText(Path.Join(_dir, "activity.jsonl"), "{\"time\":\"2026-11-18T12:00:00+00:00\",\"kind\":\"error\",\"detail\":\"C:\\\\Users\\\\Ana\\\\x.txt\",\"bytes\":null}\n");
        var entry = new ActivityLog(_dir).Recent(1)[0];
        Assert.DoesNotContain("Ana", entry.Detail); Assert.DoesNotContain("x.txt", entry.Detail);
    }

    [Fact]
    public void Recording_never_throws_even_when_the_folder_cannot_be_used()
    {
        var blocker = Path.Join(_dir, "blocker"); File.WriteAllText(blocker, "a file where a folder should be");
        var log = new ActivityLog(Path.Join(blocker, "inside"));
        log.Record("connected");                                                              // must not throw
        Assert.Empty(log.Recent(5));
    }

    [Fact]
    public void Many_threads_recording_at_once_produce_whole_lines_only()
    {
        var log = new ActivityLog(_dir, new ActivityLogOptions { MaxFileBytes = 10 * 1024 * 1024 });
        Parallel.For(0, 8, (t) => { for (var i = 0; i < 100; i++) log.Record("download", $"thread {t} item {i}", i); });
        var lines = File.ReadAllLines(Path.Join(_dir, "activity.jsonl"));
        Assert.Equal(800, lines.Length);
        Assert.Equal(800, log.Recent(2000).Count);
    }

    [Fact] public void Clear_removes_everything() { var log = new ActivityLog(_dir); log.Record("connected"); log.Clear(); Assert.Empty(log.Recent(5)); Assert.Equal((0, 0L), log.Footprint()); }

    [Fact]
    public void Limits_that_would_make_the_history_useless_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new ActivityLog(_dir, new ActivityLogOptions { MaxFiles = 0 }));
        Assert.Throws<ArgumentException>(() => new ActivityLog(_dir, new ActivityLogOptions { MaxFileBytes = 10 }));
    }

    // ---------------------------------------------------------------- support bundle
    private static IReadOnlyDictionary<string, string> Facts() => new Dictionary<string, string> { ["app_version"] = "0.1.0", ["system"] = "Win32NT 10.0.26100.0", ["shared_folders"] = "3" };
    private SupportBundle Bundle(ActivityLog log, Func<IReadOnlyDictionary<string, string>>? facts = null, TimeProvider? clock = null) => new(log, facts ?? Facts, Path.Join(_dir, "support"), clock);

    [Fact]
    public void Previewing_shows_what_would_be_included_and_writes_nothing()
    {
        var log = new ActivityLog(_dir); log.Record("connected"); log.Record("download", "sent_to_relay", 100);
        var items = Bundle(log).Preview();
        Assert.Equal(["activity.jsonl", "about.txt", "README.txt"], items.Select((i) => i.Name));
        Assert.All(items, (i) => { Assert.NotEmpty(i.Description); Assert.True(i.Bytes > 0); Assert.NotEmpty(i.Preview); });
        Assert.Contains("app_version: 0.1.0", items[1].Preview);
        Assert.False(Directory.Exists(Path.Join(_dir, "support")));
    }

    [Fact]
    public void Without_a_clear_yes_no_bundle_is_made()
    {
        var log = new ActivityLog(_dir); log.Record("connected");
        Assert.Throws<ArgumentException>(() => Bundle(log).Create(false));
        Assert.False(Directory.Exists(Path.Join(_dir, "support")));
    }

    [Fact]
    public void A_bundle_holds_exactly_what_was_previewed_and_nothing_private()
    {
        var log = new ActivityLog(_dir);
        log.Record("connected"); log.Record("download", "failed for " + Home + @"\Secret\a.txt", 5); log.Record("error", "mail ana@example.com");
        var bundle = Bundle(log);
        var preview = bundle.Preview();
        var path = bundle.Create(true);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(["README.txt", "about.txt", "activity.jsonl"], zip.Entries.Select((e) => e.FullName).Order(StringComparer.Ordinal));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            Assert.False(Redactor.LooksPrivate(text), $"{entry.FullName} looks private");
            foreach (var piece in new[] { "Ana", "Secret", "a.txt", "example.com" }) Assert.DoesNotContain(piece, text);
            Assert.StartsWith(preview.Single((i) => i.Name == entry.FullName).Preview[..Math.Min(40, text.Length)], text);
        }
    }

    [Fact]
    public void If_anything_in_a_bundle_would_look_private_nothing_is_made_at_all()
    {
        var log = new ActivityLog(_dir); log.Record("connected");
        foreach (var leak in new[] { Home + @"\Desktop", "ana@example.com", "/home/ana/work", "owner-file.docx" })
        {
            var bundle = Bundle(log, () => new Dictionary<string, string> { ["folder"] = leak });
            Assert.Throws<InvalidOperationException>(() => bundle.Preview());
            Assert.Throws<InvalidOperationException>(() => bundle.Create(true));
        }
        Assert.False(Directory.Exists(Path.Join(_dir, "support")) && Directory.GetFiles(Path.Join(_dir, "support")).Length > 0);
    }

    [Fact]
    public void Bundles_asked_for_in_the_same_instant_get_different_files()
    {
        var clock = new Clock(); var log = new ActivityLog(_dir, clock: clock); log.Record("connected");
        var bundle = Bundle(log, clock: clock);                                   // the clock never moves: same millisecond every time
        var paths = Enumerable.Range(0, 8).Select(_ => bundle.Create(true)).ToList();
        Assert.Equal(8, paths.Distinct().Count());
        Assert.All(paths.Take(3), p => Assert.False(File.Exists(p)));           // only the oldest beyond the limit are removed
        Assert.All(paths.Skip(3), p => Assert.True(File.Exists(p)));            // the newest five are kept
        Assert.Equal(SupportBundle.KeepBundles, Directory.GetFiles(Path.Join(_dir, "support"), "*.zip").Length);
    }

    [Fact]
    public void Old_bundles_are_deleted_so_the_support_folder_stays_small()
    {
        var clock = new Clock(); var log = new ActivityLog(_dir, clock: clock); log.Record("connected");
        var bundle = Bundle(log, clock: clock);
        for (var i = 0; i < SupportBundle.KeepBundles + 3; i++) { bundle.Create(true); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(SupportBundle.KeepBundles, Directory.GetFiles(Path.Join(_dir, "support"), "*.zip").Length);
        Assert.Empty(Directory.GetFiles(Path.Join(_dir, "support"), "*.part"));
    }
}
