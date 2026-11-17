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

/// <summary>
/// Shares one upload-speed limit between all running transfers. After bytes are written, the writer waits long
/// enough that the total stays at or below the limit. A limit of 0 means unlimited. The limit is read on every call,
/// so changing it takes effect on transfers that are already running.
/// </summary>
public sealed class TransferRateLimiter(Func<long> bytesPerSecond, TimeProvider time)
{
    private readonly Lock _gate = new();
    private DateTimeOffset _busyUntil = DateTimeOffset.MinValue;

    /// <summary>How long the caller must wait after having sent <paramref name="bytes"/>.</summary>
    public TimeSpan Reserve(int bytes)
    {
        var rate = bytesPerSecond();
        if (rate <= 0 || bytes <= 0) return TimeSpan.Zero;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var start = _busyUntil > now ? _busyUntil : now;
            // Unused time is not saved up: after an idle period transfers do not burst above the limit.
            _busyUntil = start + TimeSpan.FromSeconds((double)bytes / rate);
            return _busyUntil - now;
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellation)
    {
        var delay = Reserve(bytes);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, time, cancellation).ConfigureAwait(false);
    }
}

/// <summary>Interprets the Windows network-cost flags (NLM_CONNECTION_COST) the console reads for the active connection.</summary>
public static class ConnectionCost
{
    private const uint Unrestricted = 0x1, Fixed = 0x2, Variable = 0x4, OverDataLimit = 0x10000, Congested = 0x20000, Roaming = 0x40000, ApproachingDataLimit = 0x80000;

    /// <summary>Metered: a data allowance or per-use charges apply, or the connection is roaming or over its limit.</summary>
    public static bool IsMetered(uint cost) => (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;

    public static string Describe(uint? cost)
    {
        if (cost is not { } value) return "unknown (Windows did not report it)";
        if (value == 0) return "unknown";
        var parts = new List<string>();
        if ((value & Unrestricted) != 0) parts.Add("unrestricted");
        if ((value & Fixed) != 0) parts.Add("metered with a data allowance");
        if ((value & Variable) != 0) parts.Add("metered, charged by use");
        if ((value & Roaming) != 0) parts.Add("roaming");
        if ((value & ApproachingDataLimit) != 0) parts.Add("approaching its data limit");
        if ((value & OverDataLimit) != 0) parts.Add("over its data limit");
        if ((value & Congested) != 0) parts.Add("congested");
        return parts.Count == 0 ? $"unrecognised (0x{value:X})" : string.Join(", ", parts);
    }
}

