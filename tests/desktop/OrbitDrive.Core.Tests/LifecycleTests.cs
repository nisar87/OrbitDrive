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
}
