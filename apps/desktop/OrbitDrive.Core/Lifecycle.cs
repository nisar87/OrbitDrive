namespace OrbitDrive.Core;

/// <summary>
/// Crash-recovery policy for the background agent: a bounded number of automatic restarts inside a sliding window, so
/// a worker that keeps dying is not relaunched in a loop. An explicit owner action resets it.
/// </summary>
public sealed class RestartBudget(int maxRestarts, TimeSpan window, TimeProvider time)
{
    private readonly List<DateTimeOffset> _restarts = [];

    public int MaxRestarts => maxRestarts;
    public int Used { get { Prune(); return _restarts.Count; } }

    /// <summary>Records one automatic restart if the budget allows it.</summary>
    public bool TryConsume()
    {
        Prune();
        if (_restarts.Count >= maxRestarts) return false;
        _restarts.Add(time.GetUtcNow());
        return true;
    }

    public void Reset() => _restarts.Clear();

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - window;
        _restarts.RemoveAll(at => at <= cutoff);
    }
}

/// <summary>
/// "Pause remote access while this PC is locked". Unlocking resumes only a pause that locking itself caused: an owner
/// who paused by hand before locking, or changed the pause state while locked, is never overridden.
/// </summary>
public sealed class LockPolicy
{
    private bool _pausedByLock;

    public bool Enabled { get; set; }

    /// <summary>The session was locked. Returns true when remote access should be paused now.</summary>
    public bool OnLocked(bool pausedNow)
    {
        _pausedByLock = Enabled && !pausedNow;
        return _pausedByLock;
    }

    /// <summary>The session was unlocked. Returns true when remote access should be resumed now.</summary>
    public bool OnUnlocked(bool pausedNow)
    {
        var resume = _pausedByLock && pausedNow;
        _pausedByLock = false;
        return resume;
    }

    /// <summary>The owner paused or resumed by hand: the lock no longer owns the pause.</summary>
    public void OnOwnerChangedPause() => _pausedByLock = false;
}
