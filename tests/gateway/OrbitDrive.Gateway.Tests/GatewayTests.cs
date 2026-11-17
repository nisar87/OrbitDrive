using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using OrbitDrive.Gateway;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway.Tests;

internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "test";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public class RangeParserTests
{
    [Theory]
    [InlineData(null, 0L, 1000L, false)]
    [InlineData("", 0L, 1000L, false)]
    [InlineData("bytes=0-99", 0L, 100L, true)]
    [InlineData("bytes=0-0", 0L, 1L, true)]
    [InlineData("bytes=999-999", 999L, 1L, true)]
    [InlineData("bytes=500-", 500L, 500L, true)]
    [InlineData("bytes=-1", 999L, 1L, true)]
    [InlineData("bytes=-5000", 0L, 1000L, true)]
    [InlineData("bytes=990-5000", 990L, 10L, true)]
    [InlineData("bytes=0-10,20-30", 0L, 1000L, false)]
    [InlineData("bytes=10-5", 0L, 1000L, false)]
    [InlineData("bytes=a-b", 0L, 1000L, false)]
    [InlineData("bytes=1-2-3", 0L, 1000L, false)]
    [InlineData("bytes=-", 0L, 1000L, false)]
    [InlineData("bytes= 1-2", 0L, 1000L, false)]
    [InlineData("bytes=+1-2", 0L, 1000L, false)]
    [InlineData("items=0-5", 0L, 1000L, false)]
    [InlineData("bytes=99999999999999999999-", 0L, 1000L, false)]
    public void ParsesOrIgnores(string? header, long start, long length, bool partial) =>
        Assert.Equal(new ByteRange(start, length, partial), RangeParser.Parse(header, 1000));

    [Theory]
    [InlineData("bytes=1000-", 1000L)]
    [InlineData("bytes=5000-6000", 1000L)]
    [InlineData("bytes=-0", 1000L)]
    [InlineData("bytes=0-0", 0L)]
    [InlineData("bytes=-10", 0L)]
    public void Unsatisfiable(string header, long size) => Assert.Null(RangeParser.Parse(header, size));

    [Fact]
    public void HandlesSizesAboveFourGiBAndNearInt64Max()
    {
        const long size = 6L * 1024 * 1024 * 1024;
        Assert.Equal(new ByteRange(size - 1, 1, true), RangeParser.Parse($"bytes={size - 1}-", size));
        Assert.Equal(new ByteRange(4294967296, 2, true), RangeParser.Parse("bytes=4294967296-4294967297", size));
        Assert.Equal(new ByteRange(long.MaxValue - 1, 1, true), RangeParser.Parse($"bytes={long.MaxValue - 1}-{long.MaxValue}", long.MaxValue));
    }
}

public class IdentityAndConfigurationTests
{
    private static GatewayOptions Dev() => new() { DevIdentity = new DevIdentityOptions { Enabled = true, Secret = new string('k', 40) } };

    [Fact]
    public void DevTokensVerifyExpireAndResistTampering()
    {
        var time = new FakeTime();
        var verifier = new DevIdentityVerifier(Dev(), time);
        var token = verifier.Issue("Person@Example.test", TimeSpan.FromMinutes(5));
        var identity = verifier.Verify(token);
        Assert.Equal("Person@Example.test", identity.Email);
        Assert.Equal(identity.Uid, verifier.Verify(verifier.Issue("person@example.test", TimeSpan.FromMinutes(5))).Uid);

        foreach (var bad in new[] { null, "", "x", "dev.a.b", token + "x", token[..^2], token.Replace("dev.", "prod."), "a.b.c.d" })
            Assert.Equal(ErrorCode.Unauthenticated, Assert.Throws<ProtocolException>(() => verifier.Verify(bad)).Code);
        var otherKey = new DevIdentityVerifier(new GatewayOptions { DevIdentity = new DevIdentityOptions { Enabled = true, Secret = new string('z', 40) } }, time);
        Assert.Throws<ProtocolException>(() => otherKey.Verify(token));   // "wrong project"

        time.Now = time.Now.AddMinutes(6);
        Assert.Equal(ErrorCode.Unauthenticated, Assert.Throws<ProtocolException>(() => verifier.Verify(token)).Code);
    }

    [Fact]
    public void RateLimiterCountsPerKeyPerMinuteAndReopens()
    {
        var time = new FakeTime();
        var limiter = new RateLimiter(time);
        for (var i = 0; i < 3; i++) Assert.Equal(0, limiter.Hit("a", 3));
        Assert.InRange(limiter.Hit("a", 3), 1, 60);
        Assert.Equal(0, limiter.Hit("b", 3));                      // another key is unaffected
        time.Now += TimeSpan.FromSeconds(40);
        Assert.InRange(limiter.Hit("a", 3), 1, 20);                // refused attempts do not extend the lock-out
        time.Now += TimeSpan.FromSeconds(21);
        Assert.Equal(0, limiter.Hit("a", 3));                      // a new minute
    }

    [Fact]
    public void RateLimiterMemoryIsBoundedUnderAKeyFlood()
    {
        var time = new FakeTime();
        var limiter = new RateLimiter(time);
        for (var i = 0; i < 50_000; i++) Assert.Equal(0, limiter.Hit("flood-" + i, 5));
        Assert.True(limiter.Hit("one-more", 5) > 0);               // table full: refuse rather than grow or forget counters
        time.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(0, limiter.Hit("one-more", 5));               // stale windows are swept
    }

    [Theory]
    [InlineData("10.0.0.9", "", "203.0.113.5", "10.0.0.9")]                                // header ignored without a trusted proxy
    [InlineData("127.0.0.1", "127.0.0.1", "203.0.113.5", "203.0.113.5")]
    [InlineData("127.0.0.1", "127.0.0.1", "6.6.6.6, 203.0.113.5", "203.0.113.5")]          // only the proxy's own entry counts
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", "2001:db8::7", "2001:db8::7")]
    [InlineData("127.0.0.1", "127.0.0.1", "not-an-address", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1", "", "127.0.0.1")]
    [InlineData("198.51.100.1", "127.0.0.1", "203.0.113.5", "198.51.100.1")]               // a direct caller cannot pose as the proxy
    public void ClientAddressTrustsOnlyTheConfiguredProxy(string peer, string trusted, string forwarded, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
        if (forwarded.Length > 0) context.Request.Headers["X-Forwarded-For"] = forwarded;
        Assert.Equal(expected, ClientAddress.Of(context, new GatewayOptions { TrustedProxies = trusted.Length > 0 ? [trusted] : [] }));
    }

    [Theory]
    [InlineData(10, 30, true)]
    [InlineData(1, 2, true)]
    [InlineData(0, 30, false)]     // no heartbeat at all
    [InlineData(10, 19, false)]    // one late heartbeat would drop a healthy PC
    [InlineData(301, 900, false)]
    [InlineData(300, 901, false)]  // a dead PC would look online for too long
    public void LeaseSettingsMustTolerateOneLateHeartbeatAndStayBounded(int heartbeat, int lease, bool valid)
    {
        var options = Dev();
        options.AgentHeartbeatSeconds = heartbeat; options.AgentLeaseSeconds = lease;
        var development = new FakeEnvironment("Development");
        if (valid) options.Validate(development);
        else Assert.Contains("AgentLeaseSeconds", Assert.Throws<InvalidOperationException>(() => options.Validate(development)).Message);
    }

    [Fact]
    public void ProductionNeverAcceptsDevelopmentIdentitiesOrWildcardOrigins()
    {
        static GatewayOptions Firebase() => new() { Firebase = new FirebaseOptions { ProjectId = "orbitdrive-prod" }, AllowedWebOrigins = ["https://orbitdrive.example"] };
        var production = new FakeEnvironment("Production");
        Assert.Contains("Development identities", Assert.Throws<InvalidOperationException>(() => Dev().Validate(production)).Message);
        Assert.Contains("No production identity verifier", Assert.Throws<InvalidOperationException>(() => new GatewayOptions().Validate(production)).Message);
        Firebase().Validate(production);
        var fakeKeys = Firebase(); fakeKeys.Firebase.KeysUrl = "https://attacker.example/keys";
        Assert.Contains("Google's signing keys", Assert.Throws<InvalidOperationException>(() => fakeKeys.Validate(production)).Message);
        var unverified = Firebase(); unverified.Firebase.RequireVerifiedEmail = false;
        Assert.Throws<InvalidOperationException>(() => unverified.Validate(production));
        var noOrigins = Firebase(); noOrigins.AllowedWebOrigins = [];
        Assert.Throws<InvalidOperationException>(() => noOrigins.Validate(production));
        var httpOrigin = Firebase(); httpOrigin.AllowedWebOrigins = ["http://orbitdrive.example"];
        Assert.Throws<InvalidOperationException>(() => httpOrigin.Validate(production));
        var both = Dev(); both.Firebase.ProjectId = "orbitdrive-dev";
        Assert.Throws<InvalidOperationException>(() => both.Validate(new FakeEnvironment("Development")));
        var badProject = new GatewayOptions { Firebase = new FirebaseOptions { ProjectId = "Not A Project!" } };
        Assert.Throws<InvalidOperationException>(() => badProject.Validate(new FakeEnvironment("Development")));

        Assert.Throws<InvalidOperationException>(() => new GatewayOptions().Validate(new FakeEnvironment("Development")));
        Assert.Throws<InvalidOperationException>(() => new GatewayOptions { DevIdentity = new DevIdentityOptions { Enabled = true, Secret = "short" } }.Validate(new FakeEnvironment("Development")));
        var wildcard = Dev(); wildcard.AllowedWebOrigins = ["https://*.web.app"];
        Assert.Throws<InvalidOperationException>(() => wildcard.Validate(new FakeEnvironment("Development")));
        var exact = Dev(); exact.AllowedWebOrigins = ["https://orbitdrive-staging.web.app"];
        exact.Validate(new FakeEnvironment("Development"));
    }
}

public class StoreTests
{
    private readonly FakeTime _time = new();
    private readonly Store _store;
    private readonly Identity _alice = new("alice", "alice@example.test");
    private readonly Identity _bob = new("bob", "bob@example.test");

    public StoreTests()
    {
        _store = new Store(new GatewayOptions { PairingTtlSeconds = 120, DownloadSessionTtlSeconds = 60 }, _time);
        _store.UpsertPc("pc1", [1], "PC One", "0.1.0");
        _store.UpsertPc("pc2", [2], "PC One", "0.1.0"); // same display name, different identity
    }

    private (BrowserDeviceRecord Device, string Header) Pair(string pcId, Identity identity)
    {
        var (id, secret, _) = _store.CreatePairing(pcId);
        var (_, device, deviceSecret, _) = _store.ClaimPairing(id, secret, identity, "phone");
        Assert.Equal(PairingState.Approved, _store.ConfirmPairing(id, pcId, approve: true));
        return (device, $"{device.Id}.{deviceSecret}");
    }

    private static ErrorCode CodeOf(Action action) => Assert.Throws<ProtocolException>(action).Code;

    [Fact]
    public async Task ConcurrentClaimsOfOneRequestProduceExactlyOneWinner()
    {
        var (id, secret, _) = _store.CreatePairing("pc1");
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            try { _store.ClaimPairing(id, secret, i % 2 == 0 ? _alice : _bob, "racer"); return true; }
            catch (ProtocolException e) when (e.Code == ErrorCode.Conflict) { return false; }
        })));
        Assert.Single(results, won => won);
    }

    [Fact]
    public void ExpiryIsEnforcedAtClaimAndAtConfirmation()
    {
        var (id, secret, _) = _store.CreatePairing("pc1");
        _time.Now = _time.Now.AddSeconds(121);
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _store.ClaimPairing(id, secret, _alice, "late")));

        var (id2, secret2, _) = _store.CreatePairing("pc1");
        var claim = _store.ClaimPairing(id2, secret2, _alice, "phone");
        _time.Now = _time.Now.AddSeconds(121);
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _store.ConfirmPairing(id2, "pc1", true)));
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_alice, $"{claim.Device.Id}.{claim.DeviceSecret}")));
    }

    [Fact]
    public void ANewRequestInvalidatesThePreviousOneAndAnotherPcCannotConfirm()
    {
        var (first, firstSecret, _) = _store.CreatePairing("pc1");
        var (second, secondSecret, _) = _store.CreatePairing("pc1");
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _store.ClaimPairing(first, firstSecret, _alice, "old QR")));
        _store.ClaimPairing(second, secondSecret, _alice, "phone");
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _store.ConfirmPairing(second, "pc2", true)));   // substituted PC
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _store.ConfirmPairing(first, "pc1", true)));              // superseded request
        Assert.Equal(PairingState.Approved, _store.ConfirmPairing(second, "pc1", true));
        Assert.Equal(ErrorCode.Conflict, CodeOf(() => _store.ConfirmPairing(second, "pc1", true)));    // replayed approval
    }

    [Fact]
    public void GrantsAreScopedPerAccountDeviceAndPc()
    {
        var (aliceDevice, aliceHeader) = Pair("pc1", _alice);
        var (_, bobHeader) = Pair("pc2", _bob);
        Assert.Equal("pc1", Assert.Single(_store.PcsFor(_store.Authorize(_alice, aliceHeader, "pc1"))).PcId);
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _store.Authorize(_alice, aliceHeader, "pc2")));   // same name, other owner
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_bob, aliceHeader, "pc1")));    // stolen device credential, wrong account
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_alice, bobHeader)));
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_alice, aliceDevice.Id + ".wrong")));
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_alice, null)));
        var (id, secret, _) = _store.CreatePairing("pc1");
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.ClaimPairing(id, secret, _bob, "takeover")));
    }

    [Fact]
    public void RevocationKillsDeviceCapabilitiesAndCancelsStreamsButNotOtherDevices()
    {
        var (device, header) = Pair("pc1", _alice);
        var (other, otherHeader) = Pair("pc1", _alice);
        DownloadSessionRecord Session(string deviceId) => new()
        {
            OwnerUid = "alice", BrowserDeviceId = deviceId, PcId = "pc1", Name = "f", Size = 1, Revision = "sha256-x", ReadToken = "t", ExpiresAt = _time.Now.AddSeconds(60),
        };
        var capability = _store.AddDownload(Session(device.Id));
        var otherCapability = _store.AddDownload(Session(other.Id));
        var session = _store.ResolveDownload(capability);
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _store.RevokeDevice(_bob, device.Id)));           // only the owner can revoke
        _store.RevokeDevice(_alice, device.Id);
        Assert.True(session.Revoked.IsCancellationRequested);
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _store.ResolveDownload(capability)));
        Assert.Equal(ErrorCode.Forbidden, CodeOf(() => _store.Authorize(_alice, header)));
        Assert.NotNull(_store.ResolveDownload(otherCapability));
        Assert.NotNull(_store.Authorize(_alice, otherHeader, "pc1"));
        _time.Now = _time.Now.AddSeconds(61);
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _store.ResolveDownload(otherCapability)));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _store.ResolveDownload(new string('x', 43))));
    }
}
