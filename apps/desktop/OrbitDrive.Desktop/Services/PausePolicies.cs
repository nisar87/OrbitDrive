using System.Windows.Threading;

namespace OrbitDrive.Desktop;

/// <summary>
/// Pause while the PC is locked, and pause on a metered connection. Both are the owner's choices (off by default) and both only undo a pause they caused
/// themselves. Moved unchanged from the old console window.
/// </summary>
internal sealed class PausePolicies : IDisposable
{
    private readonly ConsoleHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly OrbitDrive.Core.LockPolicy _lock = new();
    private readonly OrbitDrive.Core.LockPolicy _metered = new();   // same rule as locking: only undo a pause it caused itself
    private int _ticks;
    private bool _isMetered;

    public PausePolicies(ConsoleHost host, Dispatcher dispatcher, ConsoleSettings settings)
    {
        _host = host;
        _dispatcher = dispatcher;
        _lock.Enabled = settings.PauseWhenLocked;
        _metered.Enabled = settings.PauseOnMetered;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool LockEnabled { get => _lock.Enabled; set => _lock.Enabled = value; }
    public bool MeteredEnabled { get => _metered.Enabled; set => _metered.Enabled = value; }
    public uint? CostFlags { get; private set; }
    public bool IsMetered => _isMetered;

    public void OwnerChangedPause() { _lock.OnOwnerChangedPause(); _metered.OnOwnerChangedPause(); }

    /// <summary>Windows lock/unlock. Applies the owner's "pause while locked" choice; never touches sleep settings.</summary>
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e) => _dispatcher.InvokeAsync(() =>
    {
        if (_host.IsQuitting || _host.Connection.Client is null) return;
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock && _lock.OnLocked(_host.Paused))
        {
            _host.Log("PC locked: remote access paused");
            _ = _host.SendPauseAsync(pause: true);
        }
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock && _lock.OnUnlocked(_host.Paused))
        {
            _host.Log("PC unlocked: remote access resumed");
            _ = _host.SendPauseAsync(pause: false);
        }
    });

    /// <summary>Called once per status refresh: ask Windows now and then about the network cost (a local query, no traffic).</summary>
    public void OnTick(bool paused)
    {
        if (_ticks++ % 15 != 0) return;
        CostFlags = OrbitDrive.Desktop.NetworkCost.Read();
        var metered = CostFlags is { } cost && OrbitDrive.Core.ConnectionCost.IsMetered(cost);
        if (metered == _isMetered) return;
        _isMetered = metered;
        if (metered && _metered.OnLocked(paused)) { _host.Log("Metered connection: remote access paused"); _ = _host.SendPauseAsync(pause: true); }
        else if (!metered && _metered.OnUnlocked(paused)) { _host.Log("Connection no longer metered: remote access resumed"); _ = _host.SendPauseAsync(pause: false); }
    }

    public void Dispose() => Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
}
