using OrbitDrive.Gateway;
using Xunit;

namespace OrbitDrive.Gateway.Tests;

/// <summary>The durable store talks to a backend; these tests use an in-memory one so the contract (every change saved, restart restores, unreadable data never trusted) is proven without any cloud service.</summary>
public sealed class SnapshotBackendTests
{
    private sealed class MemoryBackend : ISnapshotBackend
    {
        public Store.Snapshot? Saved; public int Saves; public bool Unreadable; public bool Quarantined;
        public Store.Snapshot? Load() => Unreadable ? throw new System.Text.Json.JsonException("bad") : Saved;
        public void Save(Store.Snapshot snapshot) { Saved = snapshot; Saves++; }
        public void QuarantineUnreadable() => Quarantined = true;
    }

    private static Store Open(ISnapshotBackend backend) => new(new GatewayOptions { PairingTtlSeconds = 120 }, TimeProvider.System, backend);

    [Fact]
    public void An_owned_pc_and_its_grant_survive_a_restart_through_the_backend()
    {
        var backend = new MemoryBackend();
        var first = Open(backend);
        first.UpsertPc("pc1", [1, 2, 3], "Office PC", "0.1.0");
        var alice = new Identity("alice", "alice@example.test");
        var (id, secret, _) = first.CreatePairing("pc1");
        var (_, device, _, _) = first.ClaimPairing(id, secret, alice, "phone");
        first.ConfirmPairing(id, "pc1", approve: true);
        Assert.True(backend.Saves >= 1);                              // an unpaired PC is not saved; pairing it is

        var second = Open(backend);                                   // a new process reading the same backend
        Assert.Contains(second.DevicesForOwner("alice"), d => d.Id == device.Id && d.PcIds.Contains("pc1"));
        Assert.Contains(second.PcsOwnedBy("alice"), p => p.PcId == "pc1" && p.Name == "Office PC");
    }

    [Fact]
    public void A_revocation_is_saved_and_still_holds_after_a_restart()
    {
        var backend = new MemoryBackend();
        var first = Open(backend);
        first.UpsertPc("pc1", [1], "PC", "0.1.0");
        var alice = new Identity("alice", "alice@example.test");
        var (id, secret, _) = first.CreatePairing("pc1");
        var (_, device, _, _) = first.ClaimPairing(id, secret, alice, "phone");
        first.ConfirmPairing(id, "pc1", approve: true);
        first.RevokeDevice(alice, device.Id);

        var second = Open(backend);
        Assert.Empty(first.DevicesForOwner("alice"));
        Assert.Empty(second.DevicesForOwner("alice"));              // revoked devices are not active after a restart either
        Assert.Empty(second.DevicesForPc("pc1"));
    }

    [Fact]
    public void Unreadable_data_grants_nothing_and_is_set_aside()
    {
        var backend = new MemoryBackend { Unreadable = true };
        var store = Open(backend);
        Assert.True(backend.Quarantined);
        Assert.Empty(store.DevicesForOwner("alice"));
        Assert.Empty(store.PcsOwnedBy("alice"));
    }

    [Fact]
    public void A_file_backend_round_trips_and_sets_aside_a_corrupt_file()
    {
        var dir = Path.Join(Path.GetTempPath(), "orbit-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GatewayOptions { PairingTtlSeconds = 120, DataDirectory = dir };
            var first = new Store(options, TimeProvider.System);
            first.UpsertPc("pc1", [9], "File PC", "0.1.0");
            var (id, secret, _) = first.CreatePairing("pc1");
            first.ClaimPairing(id, secret, new Identity("alice", "a@example.test"), "phone");
            first.ConfirmPairing(id, "pc1", approve: true);
            Assert.Single(new Store(options, TimeProvider.System).PcsOwnedBy("alice"));

            File.WriteAllText(Path.Join(dir, "registrations.json"), "{ not json");
            Assert.Empty(new Store(options, TimeProvider.System).PcsOwnedBy("alice"));
            Assert.Single(Directory.GetFiles(dir, "registrations.json.corrupt-*"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
