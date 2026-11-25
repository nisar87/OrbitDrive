using System.Net;
using OrbitDrive.Gateway;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway.Tests;

/// <summary>Audit fixes for the relay's open edges: unpaired PCs (F-001), waiting connections (F-002), the rate limiter (F-017), the data-directory lock (F-019).</summary>
public class RelayHardeningTests
{
    private static Store Open(ISnapshotBackend? backend = null, GatewayOptions? options = null) => new(options ?? new GatewayOptions(), TimeProvider.System, backend);

    private sealed class CountingBackend : ISnapshotBackend
    {
        public int Saves; public Store.Snapshot? Last;
        public Store.Snapshot? Load() => null;
        public void Save(Store.Snapshot snapshot) { Saves++; Last = snapshot; }
        public void QuarantineUnreadable() { }
    }

    [Fact]
    public void An_unpaired_pc_is_never_saved_and_a_paired_one_is()
    {
        var backend = new CountingBackend();
        var store = Open(backend);
        for (var i = 0; i < 50; i++) store.UpsertPc("pc" + i, [1], "PC", "0.1.0");
        Assert.Equal(0, backend.Saves);                                                   // fifty hellos from PCs nobody owns write nothing
        store.UpsertPc("pc0", [1], "PC", "0.1.0");                                         // and a repeat hello does not either
        Assert.Equal(0, backend.Saves);
        var (id, secret, _) = store.CreatePairing("pc0");
        store.ClaimPairing(id, secret, new Identity("alice", "a@example.test"), "phone");
        store.ConfirmPairing(id, "pc0", approve: true);
        Assert.Equal(["pc0"], backend.Last!.Pcs.Select(p => p.PcId));                      // only the paired PC is in the saved state
    }

    [Fact]
    public void Unpaired_pcs_are_capped_and_an_idle_offline_one_is_forgotten()
    {
        var options = new GatewayOptions { MaxUnownedPcs = 3, UnownedPcIdleMinutes = 30 };
        var time = new FakeTime();
        var store = new Store(options, time);
        for (var i = 0; i < 3; i++) store.UpsertPc("pc" + i, [1], "PC", "0.1.0");
        Assert.Equal(ErrorCode.LimitExceeded, Assert.Throws<ProtocolException>(() => store.UpsertPc("pc9", [1], "PC", "0.1.0")).Code);
        time.Now = time.Now.AddMinutes(31);
        store.UpsertPc("pc9", [1], "PC", "0.1.0", isOnline: id => id == "pc0");           // pc0 is still connected, the other two idle PCs are forgotten
        Assert.Equal(2, store.UnownedPcCount());
        Assert.Null(store.PcKey("pc1"));
        Assert.NotNull(store.PcKey("pc0"));
    }

    [Fact]
    public void An_oversized_agent_version_is_refused()
    {
        var store = Open();
        Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ProtocolException>(() => store.UpsertPc("pc", [1], "PC", new string('9', Store.MaxAgentVersionLength + 1))).Code);
        store.UpsertPc("pc", [1], "PC", new string('9', Store.MaxAgentVersionLength));
    }

    [Fact]
    public void An_address_cannot_hold_more_than_its_share_of_waiting_places()
    {
        var hub = new AgentHub(Open(), new Invitations(new GatewayOptions(), TimeProvider.System, Open()), new GatewayOptions(), new AdminLog(new GatewayOptions(), TimeProvider.System), new RateLimiter(TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentHub>.Instance);
        for (var i = 0; i < 4; i++) Assert.True(hub.TryReserveHandshake(64, "198.51.100.1", 4));
        Assert.False(hub.TryReserveHandshake(64, "198.51.100.1", 4));                   // the fifth from the same address is refused
        Assert.True(hub.TryReserveHandshake(64, "203.0.113.9", 4));                    // another address still gets in: one client cannot lock everyone out
        hub.ReleaseHandshake("198.51.100.1");
        Assert.True(hub.TryReserveHandshake(64, "198.51.100.1", 4));
        Assert.Equal(5, hub.PendingHandshakes);
        var granted = 0;
        for (var i = 0; i < 100; i++) if (hub.TryReserveHandshake(64, "10.0." + (i / 4) + "." + (i % 4), 4)) granted++;
        Assert.Equal(64, hub.PendingHandshakes);                                        // the overall limit still holds
        Assert.Equal(59, granted);
    }

    [Fact]
    public void Anonymous_addresses_and_signed_in_keys_have_separate_tables_so_a_flood_cannot_shut_out_a_signed_in_person()
    {
        var limiter = new RateLimiter(new FakeTime());
        for (var i = 0; i < 50_000; i++) Assert.Equal(0, limiter.Hit("claim-ip:addr" + i, 10, anonymous: true));
        Assert.True(limiter.Hit("claim-ip:one-more", 10, anonymous: true) > 0);          // the anonymous table is full: new addresses are refused...
        Assert.Equal(0, limiter.Hit("claim-uid:alice", 10));                              // ...but a signed-in account is not
        Assert.Equal((50_000, 1), limiter.Counts);
    }

    [Theory]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1111:2222:3333:4444", "2001:db8:1:2::/64")]               // same /64, same key
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("203.0.113.5", "203.0.113.5")]
    public void An_ipv6_address_is_limited_by_its_slash_64(string address, string expected) => Assert.Equal(expected, ClientAddress.Normalize(IPAddress.Parse(address)));

    [Fact]
    public void A_second_process_cannot_take_the_data_directory_and_the_first_can_be_restarted()
    {
        var dir = Path.Join(Path.GetTempPath(), "orbit-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = DataDirectoryLock.Acquire(dir))
            {
                var refused = Assert.Throws<InvalidOperationException>(() => DataDirectoryLock.Acquire(dir));
                Assert.Contains("already using the data directory", refused.Message);
            }
            using var again = DataDirectoryLock.Acquire(dir);                              // released when the first one ended
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_damaged_billing_file_is_set_aside_and_the_relay_starts_with_limits_off()
    {
        var dir = Path.Join(Path.GetTempPath(), "orbit-bill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Join(dir, "billing.json"), "{ this is not json");
            var persistence = new FileBillingPersistence(dir, new FakeTime());
            Assert.NotNull(persistence.SetAsideNote);
            Assert.Empty(persistence.LoadAll());
            Assert.Single(Directory.GetFiles(dir, "billing.json.damaged-*"));
            Assert.False(File.Exists(Path.Join(dir, "billing.json")));
            var billing = new BillingStore(persistence, new FakeTime(), null, null, limitsEnabled: true);
            Assert.True(billing.Damaged);
            Assert.False(billing.LimitsEnabled);                                           // nobody is held to Free because the file was damaged
            billing.AddPass("acct:a", "plus", 1, "grant");                                  // and the store works and saves again
            billing.Flush();
            Assert.True(new FileBillingPersistence(dir, new FakeTime()).LoadAll().Count > 0);
            Assert.Null(new FileBillingPersistence(dir, new FakeTime()).SetAsideNote);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Switching_to_firestore_imports_the_old_billing_file_once_and_never_overwrites_what_is_there()
    {
        var primary = new MemoryBillingPersistence();
        var old = new List<KeyValuePair<string, string>> { new("coupon/X", "{}"), new("order/1", "{}") };
        var import = new ImportOnceBillingPersistence(primary, () => old);
        Assert.Equal(2, import.LoadAll().Count);
        Assert.Equal(2, primary.LoadAll().Count);                                          // copied into the database
        Assert.Equal(2, import.Imported);
        old.Clear(); old.Add(new("order/NEW", "{}"));                                      // a later start: the database now has data, the file is ignored
        var again = new ImportOnceBillingPersistence(primary, () => old);
        Assert.Equal(["coupon/X", "order/1"], again.LoadAll().Select(e => e.Key).OrderBy(k => k).ToList());
        Assert.Equal(0, again.Imported);
    }

    [Fact]
    public void The_relay_cap_survives_a_restart_because_it_lives_in_the_billing_store()
    {
        var time = new FakeTime();
        var persistence = new MemoryBillingPersistence();
        var options = new GatewayOptions { MaxRelayBytesPerOwnerPerDay = 1000 };
        var first = new BillingStore(persistence, time);
        new Metrics(options, time, first).Downloaded("a", 1000, completed: true);
        first.Flush();
        var second = new BillingStore(persistence, time);                                  // the relay restarted
        var metrics = new Metrics(options, time, second);
        Assert.Equal(1000, metrics.RelayBytesToday("a"));
        Assert.Equal(ErrorCode.LimitExceeded, Assert.Throws<ProtocolException>(() => metrics.RequireRelayAllowance("a")).Code);
        Assert.Equal(1000, metrics.RelayBytesTodayAll);
        time.Now = time.Now.AddDays(1);
        metrics.RequireRelayAllowance("a");                                                // and it still resets at midnight UTC
    }
}
