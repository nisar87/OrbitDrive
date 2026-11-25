using Google.Cloud.Firestore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>A PC that an account has paired. An unpaired PC is held in memory only and is never stored (F-001), so tests about storing a PC must pair it.</summary>
    private static void UpsertPaired(Store store, string pcId, byte key, string name)
    {
        store.UpsertPc(pcId, [key], name, "0.1.0");
        var (id, secret, _) = store.CreatePairing(pcId);
        store.ClaimPairing(id, secret, Alice, "phone " + pcId);
        store.ConfirmPairing(id, pcId, approve: true);
    }

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
        store.SetPreferences("alice", new OwnerPreferences { HistoryRetentionDays = 1, NotifyInvitations = true, DiagnosticsConsent = false });
        store.SetPreferences("alice", new OwnerPreferences { HistoryRetentionDays = 30, NotifyInvitations = true, DiagnosticsConsent = false });
        await backend.FlushAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(30, (await db.Collection("owners").Document("alice").GetSnapshotAsync()).GetValue<int>("historyRetentionDays"));
    }

    // ---- shutdown lifecycle (regression: 0.1.25 crashed with ObjectDisposedException because the container disposed one instance three times) ----

    [Fact]
    public async Task Disposing_repeatedly_and_concurrently_is_safe()
    {
        if (EmulatorMissing()) return;
        var backend = new FirestoreSnapshotBackend(NewProject(), TimeProvider.System);
        await backend.DisposeAsync();
        await backend.DisposeAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await backend.DisposeAsync())));
        await backend.StopAsync(CancellationToken.None);   // stopping a stopped backend is a no-op too
    }

    [Fact]
    public async Task The_container_disposing_the_same_instance_through_every_registration_does_not_crash()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new FirestoreSnapshotBackend(db, TimeProvider.System));
        services.AddSingleton<ISnapshotBackend>(sp => sp.GetRequiredService<FirestoreSnapshotBackend>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<FirestoreSnapshotBackend>());   // the same three registrations Program.cs makes
        var provider = services.BuildServiceProvider();
        var backend = provider.GetRequiredService<FirestoreSnapshotBackend>();
        _ = provider.GetRequiredService<ISnapshotBackend>();
        _ = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        backend.Load();
        await provider.DisposeAsync();                       // used to throw ObjectDisposedException from the second disposal
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_while_writes_are_in_flight_stores_everything_accepted_before_it()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        for (var i = 0; i < 40; i++) UpsertPaired(store, $"pc{i}", (byte)i, $"PC {i}");
        store.SetSuspended("alice", true);
        var dispose = backend.DisposeAsync().AsTask();       // no flush first: shutdown itself must finish the writes
        await dispose.WaitAsync(TimeSpan.FromSeconds(30));

        await using var again = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var reopened = Open(again);
        Assert.True(reopened.IsSuspended("alice"));
        Assert.Equal(40, (await db.Collection("pcs").GetSnapshotAsync()).Count);
    }

    [Fact]
    public async Task A_change_after_shutdown_is_refused_without_throwing_and_starts_no_writer()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        UpsertPaired(store, "pc1", 1, "Before");
        await backend.DisposeAsync();
        UpsertPaired(store, "pc2", 2, "After");        // the request that raced shutdown: must not throw into the caller
        await Task.Delay(300);
        var stored = (await db.Collection("pcs").GetSnapshotAsync()).Select(d => d.Id).ToList();
        Assert.Equal(["pc1"], stored);
    }

    [Fact]
    public async Task Concurrent_saves_and_shutdown_never_throw_or_hang()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var backend = new FirestoreSnapshotBackend(db, TimeProvider.System);
        var store = Open(backend);
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() => { for (var i = 0; i < 25; i++) store.UpsertPc($"w{w}-{i}", [(byte)i], "PC", "0.1.0"); })).ToArray();
        await Task.WhenAll(writers.Append(Task.Run(async () => { await Task.Delay(5); await backend.DisposeAsync(); }))).WaitAsync(TimeSpan.FromSeconds(60));
        await backend.DisposeAsync();
    }

    // ---- R41: one writer per database ----

    private static FirestoreSnapshotBackend Leased(FirestoreDb db, TimeSpan lease, Action? lost = null) => new(db, TimeProvider.System, writerLease: lease, onLeaseLost: lost);

    [Fact]
    public async Task A_second_live_gateway_on_the_same_database_refuses_to_start_and_the_first_is_unharmed()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using var first = Leased(db, TimeSpan.FromSeconds(3));
        first.Load();
        var second = Leased(db, TimeSpan.FromSeconds(3));
        var refused = Assert.Throws<InvalidOperationException>(() => second.Load());
        Assert.Contains("Two gateways must not share one database", refused.Message);
        var (store, device) = await PairedAsync(first);       // the first keeps working and can still save
        Assert.Single(store.PcsFor(device));
        Assert.False(first.LeaseLost);
    }

    [Fact]
    public async Task A_clean_shutdown_gives_the_lease_back_so_a_restart_starts_at_once()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var first = Leased(db, TimeSpan.FromSeconds(30));
        first.Load();
        await first.DisposeAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var second = Leased(db, TimeSpan.FromSeconds(30));
        second.Load();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a clean restart must not wait for the old lease to expire");
    }

    [Fact]
    public async Task After_a_crash_the_next_instance_waits_out_the_dead_holders_lease_and_takes_over_without_losing_data()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using (var before = new FirestoreSnapshotBackend(db, TimeProvider.System)) { await PairedAsync(before); }
        // A killed process leaves its lease behind, expiring in two seconds, and never renews it.
        await db.Document("meta/writer").SetAsync(new Dictionary<string, object> { ["holder"] = "dead-instance", ["expiresUnixMs"] = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(), ["epoch"] = 5L });
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var next = Leased(db, TimeSpan.FromSeconds(30));
        var snapshot = next.Load();
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1.5), "it must wait for the old lease, not steal it");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Pcs);
    }

    [Fact]
    public async Task An_instance_whose_lease_is_taken_over_stops_writing_and_tells_its_host()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        var lost = new TaskCompletionSource();
        await using var old = Leased(db, TimeSpan.FromSeconds(3), () => lost.TrySetResult());
        old.Load();
        // Another instance takes the lease while the old one is "stalled": rewrite the lease document the way a takeover would.
        await db.Document("meta/writer").SetAsync(new Dictionary<string, object> { ["holder"] = "someone-else", ["expiresUnixMs"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(), ["epoch"] = 99L });
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(old.LeaseLost);
        var store = Open(old);
        UpsertPaired(store, "pc-after-loss", 7, "Late PC");
        await old.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.False((await db.Document("pcs/pc-after-loss").GetSnapshotAsync()).Exists, "a gateway that lost the lease must not write");
    }

    [Fact]
    public async Task A_restart_on_the_same_machine_takes_over_at_once_from_a_holder_whose_process_is_gone()
    {
        if (EmulatorMissing()) return;
        var db = NewProject();
        await using (var before = new FirestoreSnapshotBackend(db, TimeProvider.System)) { await PairedAsync(before); }
        // A process that no longer exists: start one and let it finish, then record it as the holder with a lease that is still far from expiring.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        var deadHolder = $"{Environment.MachineName}|{child.Id}|{child.StartTime.ToUniversalTime().Ticks}|deadbeef";
        await child.WaitForExitAsync();
        Assert.True(FirestoreSnapshotBackend.HolderIsGone(deadHolder));
        await db.Document("meta/writer").SetAsync(new Dictionary<string, object> { ["holder"] = deadHolder, ["expiresUnixMs"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(), ["epoch"] = 3L });
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var next = Leased(db, TimeSpan.FromSeconds(60));
        var snapshot = next.Load();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "a dead holder on this machine must not make the restart wait for its lease");
        Assert.Single(snapshot!.Pcs);
    }

    [Fact]
    public void A_holder_that_is_alive_unknown_or_on_another_machine_is_never_declared_gone()
    {
        var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(FirestoreSnapshotBackend.HolderIsGone($"{Environment.MachineName}|{me.Id}|{me.StartTime.ToUniversalTime().Ticks}|x"), "this very process is alive");
        Assert.False(FirestoreSnapshotBackend.HolderIsGone($"some-other-machine|999999|1|x"), "another machine: only the lease can tell");
        Assert.False(FirestoreSnapshotBackend.HolderIsGone("dead-instance"), "an unrecognised format is treated as alive");
        Assert.False(FirestoreSnapshotBackend.HolderIsGone($"{Environment.MachineName}|abc|1|x"));
        // The same process id with a different start time is a reused id, i.e. the old process is gone.
        Assert.True(FirestoreSnapshotBackend.HolderIsGone($"{Environment.MachineName}|{me.Id}|{me.StartTime.ToUniversalTime().Ticks + 1}|x"));
    }
}
