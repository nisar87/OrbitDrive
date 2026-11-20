using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

/// <summary>The in-app "Check for updates / Update now" path against a fake release feed: nothing from the network is believed until the signature, freshness and hash checks pass.</summary>
public sealed class UpdateClientTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request.RequestUri!); return Task.FromResult(respond(request)); }
    }

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Path.Join(Path.GetTempPath(), "orbit-updclient-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Zip = Encoding.UTF8.GetBytes(new string('z', 200_000));
    private static readonly string ZipHash = Convert.ToHexStringLower(SHA256.HashData(Zip));
    private static readonly Uri Feed = new("https://updates.example.test/update.json");
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private UpdateManifest Manifest(Action<UpdateManifest>? tweak = null)
    {
        var m = new UpdateManifest { Format = 1, Channel = "dev", Version = "0.1.9", MinProtocol = 1, MaxProtocol = 0, Published = Now.AddDays(-1).ToUnixTimeSeconds(), Expires = Now.AddDays(30).ToUnixTimeSeconds(),
            Files = [new UpdateFile { Name = "OrbitDrive-0.1.9-win-x64.zip", Arch = "x64", Sha256 = ZipHash, Size = Zip.Length, Url = "https://updates.example.test/OrbitDrive-0.1.9-win-x64.zip" }], Notes = "n" };
        tweak?.Invoke(m);
        return m;
    }
    private byte[] Sign(UpdateManifest m, ECDsa? signer = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(m);
        var sig = (signer ?? _key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope { Manifest = Convert.ToBase64String(bytes), Signatures = [new EnvelopeSignature { KeyId = "k1", Sig = Convert.ToBase64String(sig) }] });
    }
    private (UpdateClient client, Fake http) Client(Func<HttpRequestMessage, HttpResponseMessage> respond, bool keys = true)
    {
        var http = new Fake(respond);
        var verifier = new UpdateVerifier(new Dictionary<string, byte[]> { ["k1"] = _key.ExportSubjectPublicKeyInfo() }, ["updates.example.test"], "dev", "x64", 1, new Clock());
        return (new UpdateClient(new HttpClient(http), verifier, new UpdateStager(_dir), Feed, keys), http);
    }
    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static readonly Version Running = new(0, 1, 5);

    [Fact]
    public async Task A_build_without_an_update_key_never_contacts_anyone()
    {
        var (client, http) = Client(_ => Ok([]), keys: false);
        var check = await client.CheckAsync(Running, default);
        Assert.Equal(UpdateState.NotSetUp, check.State);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_newer_signed_version_is_offered_and_the_same_or_older_one_is_not()
    {
        var (client, _) = Client(_ => Ok(Sign(Manifest())));
        var found = await client.CheckAsync(Running, default);
        Assert.Equal(UpdateState.Available, found.State);
        Assert.Equal(new Version(0, 1, 9), found.Update!.Version);
        Assert.Equal(UpdateState.UpToDate, (await client.CheckAsync(new Version(0, 1, 9), default)).State);
        Assert.Equal(UpdateState.UpToDate, (await client.CheckAsync(new Version(0, 2, 0), default)).State);
    }

    [Fact]
    public async Task Nothing_published_yet_expired_information_and_network_trouble_are_told_apart_in_plain_words()
    {
        Assert.Equal(UpdateState.UpToDate, (await Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).client.CheckAsync(Running, default)).State);
        Assert.Equal(UpdateState.UpToDate, (await Client(_ => Ok(Sign(Manifest(m => m.Expires = Now.AddDays(-1).ToUnixTimeSeconds())))).client.CheckAsync(Running, default)).State);
        var down = await Client(_ => throw new HttpRequestException("boom")).client.CheckAsync(Running, default);
        Assert.Equal(UpdateState.Unavailable, down.State);
        Assert.DoesNotContain("boom", down.Message);
        Assert.Equal(UpdateState.Unavailable, (await Client(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).client.CheckAsync(Running, default)).State);
    }

    [Fact]
    public async Task Security_a_forged_signature_an_untrusted_host_a_huge_feed_and_the_wrong_channel_are_refused()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = await Client(_ => Ok(Sign(Manifest(), attacker))).client.CheckAsync(Running, default);
        Assert.Equal(UpdateState.Rejected, forged.State); Assert.Null(forged.Update);
        var evilHost = await Client(_ => Ok(Sign(Manifest(m => m.Files[0].Url = "https://evil.example.test/OrbitDrive-0.1.9-win-x64.zip")))).client.CheckAsync(Running, default);
        Assert.Equal(UpdateState.Rejected, evilHost.State);
        Assert.Equal(UpdateState.Rejected, (await Client(_ => Ok(new byte[UpdateVerifier.MaxEnvelopeBytes + 1])).client.CheckAsync(Running, default)).State);
        Assert.Equal(UpdateState.Rejected, (await Client(_ => Ok(Sign(Manifest(m => m.Channel = "stable")))).client.CheckAsync(Running, default)).State);
        Assert.Equal(UpdateState.Rejected, (await Client(_ => Ok(Encoding.UTF8.GetBytes("{not json"))).client.CheckAsync(Running, default)).State);
    }

    [Fact]
    public async Task Only_the_portable_zip_and_installer_formats_are_accepted_scripts_and_libraries_never()
    {
        foreach (var name in new[] { "a.zip", "a.msix", "a.exe" })
            Assert.Equal(UpdateState.Available, (await Client(_ => Ok(Sign(Manifest(m => m.Files[0].Name = name)))).client.CheckAsync(Running, default)).State);
        foreach (var name in new[] { "a.ps1", "a.bat", "a.cmd", "a.dll", "a.lnk", "a.zip.exe.", "..\\a.zip", "a/b.zip" })
            Assert.Equal(UpdateState.Rejected, (await Client(_ => Ok(Sign(Manifest(m => m.Files[0].Name = name)))).client.CheckAsync(Running, default)).State);
    }

    [Fact]
    public async Task The_download_is_checked_while_it_arrives_a_wrong_file_leaves_nothing_behind()
    {
        var (client, http) = Client(r => r.RequestUri == Feed ? Ok(Sign(Manifest())) : Ok(Zip));
        var update = (await client.CheckAsync(Running, default)).Update!;
        var path = await client.DownloadAsync(update, default);
        Assert.Equal(ZipHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Contains(http.Requests, u => u.AbsoluteUri.EndsWith("OrbitDrive-0.1.9-win-x64.zip"));

        var tampered = Zip.ToArray(); tampered[100] ^= 1;
        var (bad, _) = Client(r => r.RequestUri == Feed ? Ok(Sign(Manifest())) : Ok(tampered));
        var badUpdate = (await bad.CheckAsync(Running, default)).Update!;
        File.Delete(path);
        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => bad.DownloadAsync(badUpdate, default));
        Assert.Equal(UpdateRejection.HashMismatch, error.Reason);
        Assert.False(File.Exists(path)); Assert.Empty(Directory.GetFiles(_dir, "*.part"));

        var (short1, _) = Client(r => r.RequestUri == Feed ? Ok(Sign(Manifest())) : Ok(Zip[..1000]));
        Assert.Equal(UpdateRejection.SizeMismatch, (await Assert.ThrowsAsync<UpdateRejectedException>(async () => await short1.DownloadAsync((await short1.CheckAsync(Running, default)).Update!, default))).Reason);
        var (gone, _) = Client(r => r.RequestUri == Feed ? Ok(Sign(Manifest())) : throw new HttpRequestException("offline"));
        Assert.Equal(UpdateRejection.Interrupted, (await Assert.ThrowsAsync<UpdateRejectedException>(async () => await gone.DownloadAsync((await gone.CheckAsync(Running, default)).Update!, default))).Reason);
    }

    [Fact]
    public async Task A_replayed_older_signed_manifest_is_refused_after_a_newer_one_was_seen()
    {
        var (client, _) = Client(r => r.RequestUri == Feed ? Ok(Sign(Manifest())) : Ok(Zip));
        await client.DownloadAsync((await client.CheckAsync(Running, default)).Update!, default);          // 0.1.9 accepted: highest ever = 0.1.9
        var (old, _) = Client(_ => Ok(Sign(Manifest(m => { m.Version = "0.1.7"; m.Files[0].Name = "OrbitDrive-0.1.7-win-x64.zip"; m.Files[0].Url = "https://updates.example.test/OrbitDrive-0.1.7-win-x64.zip"; }))));
        var check = await old.CheckAsync(Running, default);
        Assert.Equal(UpdateState.UpToDate, check.State);
        Assert.Null(check.Update);
    }

    [Fact]
    public void Launcher_passes_each_value_as_one_argument_even_with_spaces_and_quotes_and_never_without_a_hash()
    {
        var launcher = new PortableUpdateLauncher(@"C:\Users\A B\Or""bit Drive", @"C:\Users\A B\state", ZipHash, new Version(0, 1, 9), @"C:\r.json");
        var args = launcher.Arguments(@"C:\t\apply.ps1", @"C:\s\x.zip");
        Assert.Contains(@"C:\Users\A B\Or""bit Drive", args);
        Assert.Equal(ZipHash, args[args.ToList().IndexOf("-Sha256") + 1]);
        Assert.Contains("-Relaunch", args);
        Assert.Contains("Bypass", args);     // only for the embedded script this process wrote itself
        Assert.DoesNotContain(args, a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var script = PortableUpdateLauncher.Script();
        Assert.Contains("Get-FileHash", script);
        Assert.Contains("Unsafe path in the update", script);
        Assert.DoesNotContain("Stop-Process", script);   // never kills anything
        Assert.DoesNotContain("Invoke-WebRequest", script);
        Assert.DoesNotContain("-ArgumentList $args", script);              // an array is joined with plain spaces: a profile path with a space would be split in two
        Assert.Contains("'--state \"' + $StateDir", script);               // the state folder is passed as ONE quoted argument
    }

    [Fact]
    public void Apply_results_are_read_back_and_garbage_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Join(_dir, "r.json");
        Assert.Null(PortableUpdateLauncher.ReadResult(file));
        File.WriteAllText(file, "{\"ok\":true,\"version\":\"0.1.9\",\"message\":\"Updated to 0.1.9.\"}");
        Assert.Equal(new ApplyResult(true, "0.1.9", "Updated to 0.1.9."), PortableUpdateLauncher.ReadResult(file));
        File.WriteAllText(file, "garbage");
        Assert.Null(PortableUpdateLauncher.ReadResult(file));
    }

    [Theory]
    [InlineData("0.1.12+abcdef", 0, 1, 12)]
    [InlineData("0.1.12", 0, 1, 12)]
    [InlineData("nonsense", 0, 0, 0)]
    public void The_running_version_is_read_from_the_build_label(string label, int a, int b, int c)
    {
        Assert.Equal(new Version(a, b, c), UpdateTrust.ParseLabel(label));
        Assert.True(UpdateTrust.RunningVersion() >= new Version(0, 0, 0));
    }

    [Fact]
    public void Shipped_defaults_are_read_defensively()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Join(_dir, "defaults.json");
        Assert.Null(ReleaseDefaults.Read(file));
        File.WriteAllText(file, "{\"gateway\":\"https://relay.example.test\",\"webOrigin\":\"https://web.example.test\"}");
        Assert.Equal(new ReleaseDefaults.Addresses("https://relay.example.test", "https://web.example.test"), ReleaseDefaults.Read(file));
        foreach (var bad in new[] { "garbage", "[]", "{\"gateway\":5}", "{}", new string('x', 5000) }) { File.WriteAllText(file, bad); Assert.Null(ReleaseDefaults.Read(file)); }
    }

    [Fact]
    public void Superseded_addresses_are_read_bounded_and_only_from_text()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Join(_dir, "defaults.json");
        File.WriteAllText(file, "{\"gateway\":\"https://r.example.test\",\"webOrigin\":\"https://w.example.test\",\"supersedes\":[\"https://old.example.test\",5,\"\",null]}");
        Assert.Equal(["https://old.example.test"], ReleaseDefaults.Read(file)!.Superseded);
        File.WriteAllText(file, "{\"gateway\":\"https://r.example.test\",\"supersedes\":\"https://old.example.test\"}");
        Assert.Null(ReleaseDefaults.Read(file)!.Superseded);
        File.WriteAllText(file, "{\"gateway\":\"https://r.example.test\",\"supersedes\":[" + string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"https://o{i}.example.test\"")) + "]}");
        Assert.Equal(16, ReleaseDefaults.Read(file)!.Superseded!.Count);
    }

    [Fact]
    public void TheBuiltInUpdateKeyIsOneValidP256PublicKey()
    {
        var keys = UpdateTrust.Load();
        var key = Assert.Single(keys);
        Assert.Equal("orbitdrive-update-1", key.Key);
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.Value, out var read);
        Assert.Equal(key.Value.Length, read);
        Assert.Equal(256, ecdsa.KeySize);
    }
}
