using Google.Cloud.Firestore;
using OrbitDrive.Gateway;
using OrbitDrive.Protocol;
using Xunit;

namespace OrbitDrive.Gateway.Tests;

/// <summary>
/// The Firestore backend against the real Firestore emulator. CI runs these under `firebase emulators:exec` with ORBIT_REQUIRE_FIRESTORE=1, so a missing
/// emulator FAILS there; on a machine without the emulator they return early (nothing is skipped silently in CI).
/// </summary>
public sealed class FirestoreBackendTests
{
    private static bool EmulatorMissing()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"))) return false;
        Assert.True(Environment.GetEnvironmentVariable("ORBIT_REQUIRE_FIRESTORE") != "1", "ORBIT_REQUIRE_FIRESTORE=1 but FIRESTORE_EMULATOR_HOST is not set: the Firestore emulator is not running.");
        return true;
    }

    private static FirestoreDb NewProject() => FirestoreSnapshotBackend.Connect("demo-orbit-" + Guid.NewGuid().ToString("N")[..12]);
    private static Store Open(FirestoreSnapshotBackend backend) => new(new GatewayOptions { PairingTtlSeconds = 120 }, TimeProvider.System, backend);
    private static readonly Identity Alice = new("alice", "alice@example.test");

    private static async Task<(Store Store, BrowserDeviceRecord Device)> PairedAsync(FirestoreSnapshotBackend backend)
    {
        var store = Open(backend);
        store.UpsertPc("pc1", [1, 2, 3], "Office PC", "0.1.0");
        var (id, secret, _) = store.CreatePairing("pc1");
        var (_, device, _, _) = store.ClaimPairing(id, secret, Alice, "phone");
        store.ConfirmPairing(id, "pc1", approve: true);
        store.SetPreferences("alice", new OwnerPreferences { HistoryRetentionDays = 7, NotifyInvitations = false, DiagnosticsConsent = true });
        await backend.FlushAsync(TimeSpan.FromSeconds(20));
        return (store, device);
    }

    [Fact]
    public async Task Pcs_grants_and_preferences_survive_a_restart()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var first = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var (_, device) = await PairedAsync(first);

        await using var second = new FirestoreSnapshotBackend(db, TimeProvider.System);   // a new process reading the same database
        var reopened = Open(second);
        Assert.Contains(reopened.DevicesForOwner("alice"), d => d.Id == device.Id && d.PcIds.Contains("pc1"));
        Assert.Contains(reopened.PcsOwnedBy("alice"), p => p.PcId == "pc1" && p.Name == "Office PC");
        var prefs = reopened.Preferences("alice");
        Assert.Equal(7, prefs.HistoryRetentionDays);
        Assert.False(prefs.NotifyInvitations);
        Assert.True(prefs.DiagnosticsConsent);
    }

    [Fact]
    public async Task A_revocation_and_a_suspension_still_hold_after_a_restart()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var first = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var (store, device) = await PairedAsync(first);
        store.RevokeDevice(Alice, device.Id);
        store.SetSuspended("alice", true);
        await first.FlushAsync(TimeSpan.FromSeconds(20));

        await using var second = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var reopened = Open(second);
        Assert.Empty(reopened.DevicesForOwner("alice"));
        Assert.Empty(reopened.DevicesForPc("pc1"));
        Assert.True(reopened.IsSuspended("alice"));
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_documents()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var first = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var (store, _) = await PairedAsync(first);
        store.DeleteAccount("alice");
        await first.FlushAsync(TimeSpan.FromSeconds(20));

        Assert.DoesNotContain((await db.Collection("devices").GetSnapshotAsync()).Documents, d => d.GetValue<string>("ownerUid") == "alice" && d.GetValue<bool>("active"));
        await using var second = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var reopened = Open(second);
        Assert.Empty(reopened.PcsOwnedBy("alice"));
        Assert.Empty(reopened.DevicesForOwner("alice"));
    }

    [Fact]
    public async Task Only_control_metadata_is_stored_and_never_a_secret_in_the_clear()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        store.UpsertPc("pc1", [1, 2, 3], "Office PC", "0.1.0");
        var (id, secret, _) = store.CreatePairing("pc1");
        var (_, device, deviceSecret, _) = store.ClaimPairing(id, secret, Alice, "phone");
        store.ConfirmPairing(id, "pc1", approve: true);
        await backend.FlushAsync(TimeSpan.FromSeconds(20));

        var collections = new List<string>();
        await foreach (var c in db.ListRootCollectionsAsync()) collections.Add(c.Id);
        Assert.All(collections, c => Assert.Contains(c, new[] { "devices", "meta", "owners", "pcs" }));
        var stored = string.Join("\n", (await db.Collection("devices").GetSnapshotAsync()).Documents.Select(d => string.Join(",", d.ToDictionary().Select(e => $"{e.Key}={e.Value}"))));
        Assert.DoesNotContain(deviceSecret, stored);      // only a hash of the device secret is stored
        Assert.DoesNotContain(secret, stored);            // and never the pairing secret
    }

    [Fact]
    public async Task Unreadable_documents_grant_nothing_and_are_kept_aside()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await db.Collection("devices").Document("broken").SetAsync(new Dictionary<string, object> { ["ownerUid"] = "alice" });   // missing name, secretHash, flags
        await using var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        Assert.Empty(store.DevicesForOwner("alice"));

        var kept = new List<string>();
        await foreach (var c in db.ListRootCollectionsAsync()) if (c.Id.StartsWith("quarantine-")) kept.Add(c.Id);
        Assert.NotEmpty(kept);
    }

    [Fact]
    public async Task An_existing_file_store_is_imported_once_when_firestore_is_empty()
    {
        if (EmulatorMissing()) return;
        var dir = Path.Join(Path.GetTempPath(), "orbit-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fileOptions = new GatewayOptions { PairingTtlSeconds = 120, DataDirectory = dir };
            var old = new Store(fileOptions, TimeProvider.System);
            old.UpsertPc("pc1", [7], "Old PC", "0.1.0");
            var (id, secret, _) = old.CreatePairing("pc1");
            var (_, device, _, _) = old.ClaimPairing(id, secret, Alice, "phone");
            old.ConfirmPairing(id, "pc1", approve: true);

            var db = NewProject();
            await using var backend = new FirestoreSnapshotBackend(db, TimeProvider.System, legacy: new FileSnapshotBackend(dir, TimeProvider.System));
            var imported = Open(backend);
            Assert.Contains(imported.DevicesForOwner("alice"), d => d.Id == device.Id);
            await backend.FlushAsync(TimeSpan.FromSeconds(20));

            await using var again = new FirestoreSnapshotBackend(db, TimeProvider.System);   // no legacy now: the data is in Firestore itself
            Assert.Contains(Open(again).DevicesForOwner("alice"), d => d.Id == device.Id);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Many_changes_in_a_row_end_with_the_latest_state_stored()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        for (var i = 0; i < 30; i++) store.UpsertPc($"pc{i}", [(byte)i], $"PC {i}", "0.1.0");
        store.SetPreferences("alice", new OwnerPreferences { HistoryRetentionDays = 30, NotifyInvitations = true, DiagnosticsConsent = false });
        store.SetPreferences("alice", new OwnerPreferences { HistoryRetentionDays = 90, NotifyInvitations = true, DiagnosticsConsent = false });
        await backend.FlushAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(90, (await db.Collection("owners").Document("alice").GetSnapshotAsync()).GetValue<int>("historyRetentionDays"));
    }
}
