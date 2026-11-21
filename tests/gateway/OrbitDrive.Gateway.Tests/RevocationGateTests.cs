using Microsoft.Extensions.Logging.Abstractions;
using OrbitDrive.Gateway;
using OrbitDrive.Protocol;
using Xunit;

namespace OrbitDrive.Gateway.Tests;

public sealed class RevocationGateTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSource : IAccountStatusSource
    {
        public AccountStatus Status = new(true, false, 0);
        public bool Down; public int Calls;
        public Task<AccountStatus> GetAsync(string uid, CancellationToken cancellation)
        {
            Calls++;
            return Down ? throw new HttpRequestException("offline") : Task.FromResult(Status);
        }
    }

    private static (RevocationGate Gate, FakeSource Source, Clock Clock) Make()
    {
        var source = new FakeSource(); var clock = new Clock();
        return (new RevocationGate(source, clock, NullLogger<RevocationGate>.Instance), source, clock);
    }
    private static readonly DateTimeOffset SignedIn = new(2026, 11, 21, 11, 0, 0, TimeSpan.Zero);
    private static void Refused(RevocationGate gate, ErrorCode code) => Assert.Equal(code, Assert.Throws<ProtocolException>(() => gate.EnsureNotRevoked("alice", SignedIn)).Code);

    [Fact]
    public void A_healthy_account_passes_and_the_answer_is_reused_for_a_minute()
    {
        var (gate, source, clock) = Make();
        gate.EnsureNotRevoked("alice", SignedIn); gate.EnsureNotRevoked("alice", SignedIn);
        Assert.Equal(1, source.Calls);
        clock.Advance(RevocationGate.Fresh + TimeSpan.FromSeconds(1));
        gate.EnsureNotRevoked("alice", SignedIn);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public void A_disabled_account_is_refused_once_the_cached_answer_expires()
    {
        var (gate, source, clock) = Make();
        gate.EnsureNotRevoked("alice", SignedIn);
        source.Status = new(true, true, 0);
        gate.EnsureNotRevoked("alice", SignedIn);                       // still inside the one-minute window: bounded delay, by design
        clock.Advance(RevocationGate.Fresh + TimeSpan.FromSeconds(1));
        Refused(gate, ErrorCode.Unauthenticated);
    }

    [Fact]
    public void A_deleted_account_is_refused()
    {
        var (gate, source, _) = Make();
        source.Status = new(false, false, 0);
        Refused(gate, ErrorCode.Unauthenticated);
    }

    [Fact]
    public void Sessions_revoked_after_the_sign_in_are_refused_and_a_newer_sign_in_passes()
    {
        var (gate, source, clock) = Make();
        source.Status = new(true, false, SignedIn.ToUnixTimeSeconds() + 1);   // sessions revoked one second after this person signed in
        Refused(gate, ErrorCode.Unauthenticated);
        clock.Advance(RevocationGate.Fresh + TimeSpan.FromSeconds(1));
        gate.EnsureNotRevoked("alice", SignedIn.AddMinutes(30));             // signed in again afterwards
        source.Status = new(true, false, SignedIn.ToUnixTimeSeconds());      // a sign-in exactly at the revocation instant is accepted
        clock.Advance(RevocationGate.Fresh + TimeSpan.FromSeconds(1));
        gate.EnsureNotRevoked("alice", SignedIn);
    }

    [Fact]
    public void An_outage_uses_a_recent_answer_but_never_an_old_one_and_never_none()
    {
        var (gate, source, clock) = Make();
        Refused2(gate, source);                                              // no answer yet and Firebase down: refused as unavailable, not allowed
        source.Down = false;
        gate.EnsureNotRevoked("alice", SignedIn);
        source.Down = true;
        clock.Advance(TimeSpan.FromMinutes(10));
        gate.EnsureNotRevoked("alice", SignedIn);                            // 10 minutes old answer: accepted during the outage
        clock.Advance(TimeSpan.FromMinutes(6));
        Refused(gate, ErrorCode.Unavailable);                                // 16 minutes: too old
    }
    private static void Refused2(RevocationGate gate, FakeSource source) { source.Down = true; Refused(gate, ErrorCode.Unavailable); }

    [Fact]
    public void A_stale_answer_that_says_revoked_still_refuses_during_an_outage()
    {
        var (gate, source, clock) = Make();
        source.Status = new(true, true, 0);
        Refused(gate, ErrorCode.Unauthenticated);
        source.Down = true;
        clock.Advance(TimeSpan.FromMinutes(5));
        Refused(gate, ErrorCode.Unauthenticated);
    }

    [Fact]
    public void Accounts_are_checked_separately()
    {
        var (gate, source, _) = Make();
        gate.EnsureNotRevoked("alice", SignedIn); gate.EnsureNotRevoked("bob", SignedIn);
        Assert.Equal(2, source.Calls);
    }
}
