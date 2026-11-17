using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

public class LifecycleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void RestartBudgetAllowsABoundedNumberOfRestartsThenStops()
    {
        var clock = new Clock();
        var budget = new RestartBudget(3, TimeSpan.FromMinutes(10), clock);
        for (var i = 0; i < 3; i++) { Assert.True(budget.TryConsume()); clock.Now += TimeSpan.FromSeconds(5); }
        Assert.False(budget.TryConsume());                 // a crash loop is not relaunched forever
        Assert.False(budget.TryConsume());
        Assert.Equal(3, budget.Used);
    }

    [Fact]
    public void RestartBudgetRecoversAfterTheWindowAndOnOwnerReset()
    {
        var clock = new Clock();
        var budget = new RestartBudget(3, TimeSpan.FromMinutes(10), clock);
        for (var i = 0; i < 3; i++) Assert.True(budget.TryConsume());
        clock.Now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59);
        Assert.False(budget.TryConsume());
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(budget.TryConsume());                  // an occasional crash hours apart is always recovered
        Assert.Equal(1, budget.Used);
        budget.TryConsume(); budget.TryConsume();
        Assert.False(budget.TryConsume());
        budget.Reset();                                    // the owner pressed Start
        Assert.True(budget.TryConsume());
    }

    [Fact]
    public void LockPausesAndUnlockResumesOnlyWhenEnabled()
    {
        var policy = new LockPolicy();
        Assert.False(policy.OnLocked(pausedNow: false));   // off by default: locking changes nothing
        Assert.False(policy.OnUnlocked(pausedNow: false));
        policy.Enabled = true;
        Assert.True(policy.OnLocked(pausedNow: false));
        Assert.True(policy.OnUnlocked(pausedNow: true));
        Assert.False(policy.OnUnlocked(pausedNow: true));  // a second unlock event does nothing
    }

    [Fact]
    public void UnlockNeverOverridesTheOwnersOwnPauseDecision()
    {
        var policy = new LockPolicy { Enabled = true };
        Assert.False(policy.OnLocked(pausedNow: true));    // already paused by hand before locking
        Assert.False(policy.OnUnlocked(pausedNow: true));  // stays paused

        Assert.True(policy.OnLocked(pausedNow: false));
        policy.OnOwnerChangedPause();                      // e.g. resumed from another session tool, then paused again
        Assert.False(policy.OnUnlocked(pausedNow: true));

        Assert.True(policy.OnLocked(pausedNow: false));
        Assert.False(policy.OnUnlocked(pausedNow: false)); // something else already resumed it
    }

    [Fact]
    public void RateLimiterKeepsAllTransfersTogetherAtOrBelowTheLimit()
    {
        var clock = new Clock();
        long rate = 100_000;                                           // bytes per second
        var limiter = new TransferRateLimiter(() => rate, clock);
        Assert.Equal(TimeSpan.FromSeconds(0.5), limiter.Reserve(50_000));
        Assert.Equal(TimeSpan.FromSeconds(1.0), limiter.Reserve(50_000));   // a second transfer queues behind the first: shared, not per transfer
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(0.2), limiter.Reserve(20_000));
        clock.Now += TimeSpan.FromSeconds(60);                         // idle time is not saved up for a later burst
        Assert.Equal(TimeSpan.FromSeconds(1.0), limiter.Reserve(100_000));
        rate = 400_000;                                                // the owner raises the limit while a transfer runs
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(0.25), limiter.Reserve(100_000));
    }

    [Fact]
    public void RateLimiterWithNoLimitNeverDelays()
    {
        var limiter = new TransferRateLimiter(() => 0, new Clock());
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(10_000_000));
        Assert.Equal(TimeSpan.Zero, new TransferRateLimiter(() => 1000, new Clock()).Reserve(0));
    }

    [Theory]
    [InlineData(0x1u, false, "unrestricted")]
    [InlineData(0x2u, true, "metered with a data allowance")]
    [InlineData(0x4u, true, "metered, charged by use")]
    [InlineData(0x40001u, true, "unrestricted, roaming")]             // roaming is treated as metered even on an unlimited plan
    [InlineData(0x10002u, true, "metered with a data allowance, over its data limit")]
    [InlineData(0x80002u, true, "metered with a data allowance, approaching its data limit")]
    [InlineData(0x20001u, false, "unrestricted, congested")]
    [InlineData(0u, false, "unknown")]
    public void ConnectionCostFlagsAreReadAsWindowsDefinesThem(uint cost, bool metered, string description)
    {
        Assert.Equal(metered, ConnectionCost.IsMetered(cost));
        Assert.Equal(description, ConnectionCost.Describe(cost));
    }

    [Fact]
    public void AnUnreadableConnectionCostIsReportedAsUnknownNotAsUnmetered()
    {
        Assert.Equal("unknown (Windows did not report it)", ConnectionCost.Describe(null));
    }
}
