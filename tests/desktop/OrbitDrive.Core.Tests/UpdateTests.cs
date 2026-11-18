using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public DateTimeOffset At = Now; public override DateTimeOffset GetUtcNow() => At; }
    private sealed class Launcher : IInstallerLauncher { public List<string> Launched { get; } = []; public void Launch(string verifiedPath) => Launched.Add(verifiedPath); }

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Clock _time = new();
    private readonly string _dir = Path.Join(Path.GetTempPath(), "orbit-update-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(new string('p', 300_000));
    private static readonly string PayloadHash = Convert.ToHexStringLower(SHA256.HashData(Payload));

    public void Dispose() { _key.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private UpdateVerifier Verifier(string channel = "stable", string arch = "x64", int protocol = 1, params string[] hosts) =>
        new(new Dictionary<string, byte[]> { ["k1"] = _key.ExportSubjectPublicKeyInfo() }, hosts.Length == 0 ? ["updates.example.test"] : hosts, channel, arch, protocol, _time);

    private static UpdateManifest Manifest(Action<UpdateManifest>? tweak = null)
    {
        var manifest = new UpdateManifest
        {
            Format = 1, Channel = "stable", Version = "1.2.0", MinProtocol = 1, MaxProtocol = 0, Published = Now.AddDays(-1).ToUnixTimeSeconds(), Expires = Now.AddDays(30).ToUnixTimeSeconds(),
            Files = [new UpdateFile { Name = "OrbitDrive-1.2.0-x64.msix", Arch = "x64", Sha256 = PayloadHash, Size = Payload.Length, Url = "https://updates.example.test/OrbitDrive-1.2.0-x64.msix" }],
            Notes = "Fixes",
        };
        tweak?.Invoke(manifest);
        return manifest;
    }

    private byte[] Sign(UpdateManifest manifest, string keyId = "k1", ECDsa? signer = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signature = (signer ?? _key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope { Manifest = Convert.ToBase64String(bytes), Signatures = [new EnvelopeSignature { KeyId = keyId, Sig = Convert.ToBase64String(signature) }] });
    }

    private static UpdateRejection Why(Action action) => Assert.Throws<UpdateRejectedException>(action).Reason;
    private static readonly Version Running = new(1, 1, 0);

    [Fact]
    public void A_correctly_signed_current_newer_manifest_is_accepted()
    {
        var update = Verifier().Verify(Sign(Manifest()), Running);
        Assert.Equal(new Version(1, 2, 0), update.Version);
        Assert.Equal("OrbitDrive-1.2.0-x64.msix", update.File.Name);
    }

    [Fact]
    public void A_manifest_signed_by_node_with_the_release_tool_verifies_here()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Join(dir, "tests", "fixtures", "updates"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var fixtures = Path.Join(dir, "tests", "fixtures", "updates");
        var spki = Convert.FromBase64String(File.ReadAllText(Path.Join(fixtures, "test-public-key.spki.b64")).Trim());
        var verifier = new UpdateVerifier(new Dictionary<string, byte[]> { ["test-key-1"] = spki }, ["updates.example.test"], "stable", "x64", 1, new Clock { At = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero) });
        var envelope = File.ReadAllBytes(Path.Join(fixtures, "signed-1.2.0.json"));
        Assert.Equal(new Version(1, 2, 0), verifier.Verify(envelope, Running).Version);
        envelope[envelope.Length / 2] ^= 1;                                  // one changed byte anywhere is refused
        Assert.Throws<UpdateRejectedException>(() => verifier.Verify(envelope, Running));
    }

    [Fact]
    public void Tampering_a_signed_manifest_is_refused()
    {
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(Sign(Manifest()))!;
        var tampered = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Manifest)).Replace("updates.example.test", "evil.example.test");
        envelope.Manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes(tampered));
        Assert.Equal(UpdateRejection.BadSignature, Why(() => Verifier().Verify(JsonSerializer.SerializeToUtf8Bytes(envelope), Running)));
    }

    [Fact]
    public void Wrong_unknown_and_missing_signatures_are_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(UpdateRejection.BadSignature, Why(() => Verifier().Verify(Sign(Manifest(), "k1", other), Running)));
        Assert.Equal(UpdateRejection.UnknownKey, Why(() => Verifier().Verify(Sign(Manifest(), "attacker-key", other), Running)));
        var unsigned = JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope { Manifest = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Manifest())) });
        Assert.Equal(UpdateRejection.BadSignature, Why(() => Verifier().Verify(unsigned, Running)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"manifest\":\"!!!\",\"signatures\":[]}")]
    public void Garbage_is_refused_without_crashing(string text) => Assert.True(Enum.IsDefined(Why(() => Verifier().Verify(Encoding.UTF8.GetBytes(text), Running))));

    [Fact]
    public void An_oversized_envelope_is_refused_before_it_is_parsed() =>
        Assert.Equal(UpdateRejection.TooLarge, Why(() => Verifier().Verify(new byte[UpdateVerifier.MaxEnvelopeBytes + 1], Running)));

    [Fact]
    public void Expired_future_dated_and_overlong_manifests_are_refused()
    {
        Assert.Equal(UpdateRejection.Expired, Why(() => Verifier().Verify(Sign(Manifest(m => m.Expires = Now.AddSeconds(-1).ToUnixTimeSeconds())), Running)));
        Assert.Equal(UpdateRejection.NotYetValid, Why(() => Verifier().Verify(Sign(Manifest(m => { m.Published = Now.AddDays(2).ToUnixTimeSeconds(); m.Expires = Now.AddDays(9).ToUnixTimeSeconds(); })), Running)));
        Assert.Equal(UpdateRejection.ValidityTooLong, Why(() => Verifier().Verify(Sign(Manifest(m => m.Expires = Now.AddDays(400).ToUnixTimeSeconds())), Running)));
        _time.At = Now.AddDays(31);                                           // the same manifest, a month later
        Assert.Equal(UpdateRejection.Expired, Why(() => Verifier().Verify(Sign(Manifest()), Running)));
    }

    [Fact]
    public void Same_older_and_replayed_versions_are_never_installed()
    {
        Assert.Equal(UpdateRejection.NotNewer, Why(() => Verifier().Verify(Sign(Manifest()), new Version(1, 2, 0))));
        Assert.Equal(UpdateRejection.NotNewer, Why(() => Verifier().Verify(Sign(Manifest()), new Version(2, 0, 0))));
        Assert.Equal(UpdateRejection.RolledBack, Why(() => Verifier().Verify(Sign(Manifest()), Running, highestEverAccepted: new Version(1, 5, 0))));
        Assert.Equal(UpdateRejection.Malformed, Why(() => Verifier().Verify(Sign(Manifest(m => m.Version = "latest")), Running)));
    }

    [Fact]
    public void Channel_protocol_and_architecture_must_match()
    {
        Assert.Equal(UpdateRejection.WrongChannel, Why(() => Verifier(channel: "beta").Verify(Sign(Manifest()), Running)));
        Assert.Equal(UpdateRejection.IncompatibleProtocol, Why(() => Verifier(protocol: 1).Verify(Sign(Manifest(m => m.MinProtocol = 2)), Running)));
        Assert.Equal(UpdateRejection.IncompatibleProtocol, Why(() => Verifier(protocol: 3).Verify(Sign(Manifest(m => m.MaxProtocol = 2)), Running)));
        Assert.Equal(UpdateRejection.NoFileForThisPc, Why(() => Verifier(arch: "arm64").Verify(Sign(Manifest()), Running)));
        Assert.Equal(UpdateRejection.UnsupportedFormat, Why(() => Verifier().Verify(Sign(Manifest(m => m.Format = 2)), Running)));
    }

    [Theory]
    [InlineData("http://updates.example.test/a.msix")]
    [InlineData("https://evil.example.test/a.msix")]
    [InlineData("https://user:pw@updates.example.test/a.msix")]
    [InlineData("https://updates.example.test:8443/a.msix")]
    [InlineData("https://updates.example.test/a.msix?token=1")]
    [InlineData("https://updates.example.test/a.msix#x")]
    [InlineData("https://updates.example.test.evil.test/a.msix")]
    [InlineData("ftp://updates.example.test/a.msix")]
    [InlineData("file:///c:/a.msix")]
    [InlineData("//updates.example.test/a.msix")]
    public void Only_https_downloads_from_a_listed_host_are_followed(string url) =>
        Assert.Contains(Why(() => Verifier().Verify(Sign(Manifest(m => m.Files[0].Url = url)), Running)), new[] { UpdateRejection.BadUrl, UpdateRejection.UntrustedHost });

    [Theory]
    [InlineData("..\\evil.msix")]
    [InlineData("../evil.msix")]
    [InlineData("C:evil.msix")]
    [InlineData("evil.ps1")]
    [InlineData("evil")]
    [InlineData("evil.msix.")]
    [InlineData("evil.msix ")]
    [InlineData("")]
    public void File_names_cannot_escape_the_staging_folder_or_be_scripts(string name) =>
        Assert.Equal(UpdateRejection.BadFile, Why(() => Verifier().Verify(Sign(Manifest(m => m.Files[0].Name = name)), Running)));

    [Theory]
    [InlineData("abc", 10L)]
    [InlineData("zz", 10L)]
    public void A_checksum_must_be_64_hex_digits(string hash, long size) =>
        Assert.Equal(UpdateRejection.BadFile, Why(() => Verifier().Verify(Sign(Manifest(m => { m.Files[0].Sha256 = hash; m.Files[0].Size = size; })), Running)));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(UpdateVerifier.MaxFileBytes + 1)]
    public void A_file_size_must_be_plausible(long size) =>
        Assert.Equal(UpdateRejection.BadFile, Why(() => Verifier().Verify(Sign(Manifest(m => m.Files[0].Size = size)), Running)));

    // ---- Staging, applying and recovery ----

    private VerifiedUpdate Verified() => Verifier().Verify(Sign(Manifest()), Running);

    [Fact]
    public async Task A_matching_download_is_staged_and_remembered_as_the_newest_seen()
    {
        var stager = new UpdateStager(_dir);
        var path = await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(new Version(1, 2, 0), stager.HighestAccepted);
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_bytes_size_or_hash_leaves_nothing_behind()
    {
        var stager = new UpdateStager(_dir);
        var flipped = (byte[])Payload.Clone(); flipped[1000] ^= 1;
        var reasons = new[]
        {
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.StageAsync(Verified(), new MemoryStream(flipped), CancellationToken.None))).Reason,
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.StageAsync(Verified(), new MemoryStream(Payload[..^1]), CancellationToken.None))).Reason,
            (await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.StageAsync(Verified(), new MemoryStream([.. Payload, 0]), CancellationToken.None))).Reason,
        };
        Assert.Equal(new[] { UpdateRejection.HashMismatch, UpdateRejection.SizeMismatch, UpdateRejection.SizeMismatch }, reasons);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Null(stager.HighestAccepted);
    }

    private sealed class BreakingStream(byte[] data, int breakAt) : Stream
    {
        private int _position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= breakAt) throw new IOException("connection lost");
            var n = Math.Min(Math.Min(count, 50_000), data.Length - _position); Array.Copy(data, _position, buffer, offset, n); _position += n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_connection_lost_mid_download_leaves_no_file_with_its_real_name()
    {
        var stager = new UpdateStager(_dir);
        var failure = await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.StageAsync(Verified(), new BreakingStream(Payload, 120_000), CancellationToken.None));
        Assert.Equal(UpdateRejection.Interrupted, failure.Reason);
        Assert.False(File.Exists(Path.Join(_dir, "OrbitDrive-1.2.0-x64.msix")));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task Applying_drains_work_first_checks_the_file_again_and_only_then_launches_the_installer()
    {
        var stager = new UpdateStager(_dir);
        var path = await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        var launcher = new Launcher();
        var order = new List<string>();
        await stager.ApplyAsync(_ => { order.Add("drain"); return Task.CompletedTask; }, new OrderedLauncher(launcher, order), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(new[] { "drain", "launch" }, order);
        Assert.Equal(new[] { path }, launcher.Launched);
        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(launcher.Launched[0]));
    }
    private sealed class OrderedLauncher(Launcher inner, List<string> order) : IInstallerLauncher { public void Launch(string p) { order.Add("launch"); inner.Launch(p); } }

    [Fact]
    public async Task A_staged_file_changed_after_download_is_removed_and_never_launched()
    {
        var stager = new UpdateStager(_dir);
        var path = await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) { stream.Seek(7, SeekOrigin.Begin); stream.WriteByte(0xFF); }
        var launcher = new Launcher();
        var failure = await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.ApplyAsync(_ => Task.CompletedTask, launcher, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(UpdateRejection.HashMismatch, failure.Reason);
        Assert.Empty(launcher.Launched);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task If_running_work_cannot_be_paused_nothing_is_launched()
    {
        var stager = new UpdateStager(_dir);
        await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        var launcher = new Launcher();
        var failure = await Assert.ThrowsAsync<UpdateRejectedException>(() => stager.ApplyAsync(async token => await Task.Delay(Timeout.Infinite, token), launcher, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Equal(UpdateRejection.DrainFailed, failure.Reason);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Applying_without_a_staged_update_is_refused()
    {
        var failure = await Assert.ThrowsAsync<UpdateRejectedException>(() => new UpdateStager(_dir).ApplyAsync(_ => Task.CompletedTask, new Launcher(), TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(UpdateRejection.NotStaged, failure.Reason);
    }

    [Fact]
    public async Task A_crash_while_applying_is_noticed_at_the_next_start_and_the_update_can_be_retried()
    {
        var stager = new UpdateStager(_dir);
        await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        await stager.ApplyAsync(_ => Task.CompletedTask, new Launcher(), TimeSpan.FromSeconds(5), CancellationToken.None);   // journal now says "applying"; the process dies here
        File.WriteAllText(Path.Join(_dir, "half.msix.part"), "partial");
        var restarted = new UpdateStager(_dir);
        Assert.Equal(UpdateStager.RecoveryOutcome.ApplyInterrupted, restarted.Recover(Running));    // still the old version
        Assert.False(File.Exists(Path.Join(_dir, "half.msix.part")));
        var launcher = new Launcher();
        await restarted.ApplyAsync(_ => Task.CompletedTask, launcher, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task After_a_successful_update_the_staged_file_is_cleaned_up()
    {
        var stager = new UpdateStager(_dir);
        var path = await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        await stager.ApplyAsync(_ => Task.CompletedTask, new Launcher(), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(UpdateStager.RecoveryOutcome.Nothing, new UpdateStager(_dir).Recover(new Version(1, 2, 0)));        // now running the new version
        Assert.False(File.Exists(path));
        Assert.Equal(new Version(1, 2, 0), new UpdateStager(_dir).HighestAccepted);                                  // and an old manifest still cannot be replayed
    }

    [Fact]
    public async Task A_damaged_staged_file_or_journal_is_never_trusted_at_start()
    {
        var stager = new UpdateStager(_dir);
        var path = await stager.StageAsync(Verified(), new MemoryStream(Payload), CancellationToken.None);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Equal(UpdateStager.RecoveryOutcome.StagedDamaged, new UpdateStager(_dir).Recover(Running));
        Assert.False(File.Exists(path));
        File.WriteAllText(Path.Join(_dir, "journal.json"), "{ not json");
        Assert.Equal(UpdateStager.RecoveryOutcome.Nothing, new UpdateStager(_dir).Recover(Running));
        Assert.Null(new UpdateStager(_dir).HighestAccepted);
    }

    [Fact]
    public void Recovery_never_touches_anything_outside_its_own_folder()
    {
        var outside = Path.Join(Path.GetTempPath(), "orbit-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "user file");
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Join(_dir, "journal.json"), JsonSerializer.Serialize(new { phase = "staged", version = "9.0.0", name = "..\\..\\" + Path.GetFileName(outside), sha256 = "00", size = 9 }));
            new UpdateStager(_dir).Recover(Running);
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }
}
