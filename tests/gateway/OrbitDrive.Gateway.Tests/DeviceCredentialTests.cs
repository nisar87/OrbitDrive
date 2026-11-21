using OrbitDrive.Gateway;
using OrbitDrive.Protocol;
using Xunit;

namespace OrbitDrive.Gateway.Tests;

/// <summary>A browser's approval: its secret is replaced on a schedule (old one briefly still valid) and an unused approval lapses. A fake clock drives every case.</summary>
public sealed class DeviceCredentialTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Memory : ISnapshotBackend
    {
        public Store.Snapshot? Saved;
        public Store.Snapshot? Load() => Saved;
        public void Save(Store.Snapshot snapshot) => Saved = snapshot;
        public void QuarantineUnreadable() { }
    }

    private static readonly Identity Alice = new("alice", "alice@example.test");
    private static GatewayOptions Options() => new() { PairingTtlSeconds = 120, DeviceIdleDays = 90, DeviceRotateDays = 30, DeviceRotationGraceMinutes = 60 };

    private static (Store Store, Clock Clock, Memory Backend, BrowserDeviceRecord Device, string Secret) Paired()
    {
        var clock = new Clock(); var backend = new Memory();
        var store = new Store(Options(), clock, backend);
        store.UpsertPc("pc1", [1], "PC", "0.1.0");
        var (id, secret, _) = store.CreatePairing("pc1");
        var (_, device, deviceSecret, _) = store.ClaimPairing(id, secret, Alice, "phone");
        store.ConfirmPairing(id, "pc1", approve: true);
        return (store, clock, backend, device, deviceSecret);
    }
    private static string Header(BrowserDeviceRecord d, string secret) => $"{d.Id}.{secret}";
    private static ErrorCode Denied(Store store, string header) => Assert.Throws<ProtocolException>(() => store.Authorize(Alice, header)).Code;

    [Fact]
    public void A_secret_in_its_first_month_is_not_rotated()
    {
        var (store, clock, _, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(29));
        store.Authorize(Alice, Header(device, secret));
        Assert.Null(store.RotateIfDue(device, secret));
    }

    [Fact]
    public void After_the_rotation_age_a_new_secret_is_issued_once_and_the_old_one_works_only_for_the_grace_window()
    {
        var (store, clock, _, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(31));
        store.Authorize(Alice, Header(device, secret));
        var fresh = store.RotateIfDue(device, secret);
        Assert.NotNull(fresh);
        Assert.NotEqual(secret, fresh);
        store.Authorize(Alice, Header(device, fresh!));                       // the new one works
        Assert.Null(store.RotateIfDue(device, fresh!));                       // and is not rotated again straight away
        store.Authorize(Alice, Header(device, secret));                       // the old one still works inside the grace window
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(ErrorCode.Forbidden, Denied(store, Header(device, secret)));   // and not after it
        store.Authorize(Alice, Header(device, fresh!));
    }

    [Fact]
    public void A_second_tab_still_holding_the_old_secret_does_not_invalidate_the_new_one()
    {
        var (store, clock, _, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(31));
        var fresh = store.RotateIfDue(device, secret)!;
        clock.Advance(TimeSpan.FromMinutes(5));
        store.Authorize(Alice, Header(device, secret));                        // the other tab, still on the old secret
        Assert.Null(store.RotateIfDue(device, secret));                        // gets nothing new, so the first tab's secret survives
        store.Authorize(Alice, Header(device, fresh));
    }

    [Fact]
    public void Only_the_holder_of_a_valid_secret_can_trigger_a_rotation()
    {
        var (store, clock, _, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Null(store.RotateIfDue(device, "not-the-secret"));
        Assert.Equal(ErrorCode.Forbidden, Denied(store, Header(device, "not-the-secret")));
    }

    [Fact]
    public void An_approval_unused_for_the_idle_limit_lapses_and_stays_lapsed()
    {
        var (store, clock, _, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal(ErrorCode.Forbidden, Denied(store, Header(device, secret)));
        Assert.Empty(store.DevicesForOwner("alice"));
        Assert.Equal(ErrorCode.Forbidden, Denied(store, Header(device, secret)));
        Assert.Equal("This browser is not approved. Pair it with your PC again.", Assert.Throws<ProtocolException>(() => store.Authorize(Alice, Header(device, secret))).Message);
    }

    [Fact]
    public void A_stranger_guessing_the_device_id_learns_nothing_about_idleness()
    {
        var (store, clock, _, device, _) = Paired();
        clock.Advance(TimeSpan.FromDays(91));
        Assert.Equal("This browser is not approved. Pair it with your PC again.", Assert.Throws<ProtocolException>(() => store.Authorize(Alice, Header(device, "guess"))).Message);
    }

    [Fact]
    public void Regular_use_keeps_an_approval_alive_for_ever()
    {
        var (store, clock, _, device, secret) = Paired();
        for (var week = 0; week < 60; week++)                                  // well over a year of weekly use
        {
            clock.Advance(TimeSpan.FromDays(7));
            store.Authorize(Alice, Header(device, secret));
            if (store.RotateIfDue(device, secret) is { } fresh) secret = fresh;
        }
        store.Authorize(Alice, Header(device, secret));
    }

    [Fact]
    public void Rotation_and_the_last_use_survive_a_restart()
    {
        var (store, clock, backend, device, secret) = Paired();
        clock.Advance(TimeSpan.FromDays(31));
        store.Authorize(Alice, Header(device, secret));
        var fresh = store.RotateIfDue(device, secret)!;
        var reopened = new Store(Options(), clock, backend);                   // a gateway restart
        reopened.Authorize(Alice, Header(device, fresh));
        reopened.Authorize(Alice, Header(device, secret));                     // the previous secret's grace window is kept too
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(ErrorCode.Forbidden, Assert.Throws<ProtocolException>(() => reopened.Authorize(Alice, Header(device, secret))).Code);
        Assert.Equal(device.Id, reopened.Authorize(Alice, Header(device, fresh)).Id);
    }

    [Fact]
    public void Approvals_saved_before_these_clocks_existed_are_not_locked_out_by_the_upgrade()
    {
        var (store, clock, backend, device, secret) = Paired();
        var old = backend.Saved!;
        backend.Saved = old with { Devices = [.. old.Devices.Select(d => d with { CreatedAtUnixMs = null, SecretIssuedAtUnixMs = null, LastUsedAtUnixMs = null, PreviousSecretHash = null, PreviousValidUntilUnixMs = null })] };
        clock.Advance(TimeSpan.FromDays(400));                                 // the gateway was down for over a year, say
        var reopened = new Store(Options(), clock, backend);
        reopened.Authorize(Alice, Header(device, secret));                     // their clocks start now
    }

    [Fact]
    public void Another_account_cannot_use_a_device_it_does_not_own()
    {
        var (store, _, _, device, secret) = Paired();
        Assert.Equal(ErrorCode.Forbidden, Assert.Throws<ProtocolException>(() => store.Authorize(new Identity("mallory", "m@example.test"), Header(device, secret))).Code);
    }
}
