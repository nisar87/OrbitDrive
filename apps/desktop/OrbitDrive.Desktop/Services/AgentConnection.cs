using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>Why there is no status to show. Moved unchanged from the old console window.</summary>
internal enum AgentPhase { KeepsStopping, Stopped, Starting, NotRunning }

/// <summary>
/// The console's link to the per-user agent over the local same-user channel: connecting (launching the agent only when permitted), noticing an agent that
/// has gone or stopped answering, and the bounded automatic restart. Behaviour is the old console window's, unchanged; only its home moved.
/// </summary>
internal sealed class AgentConnection
{
    private readonly string _stateDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly OrbitDrive.Core.RestartBudget _restarts = new(3, TimeSpan.FromMinutes(10), TimeProvider.System);
    private LocalIpcClient? _client;
    // _mayStart: the console may launch the agent on its next connection attempt (at launch, after a crash within the restart budget, or when the owner
    // presses Start). _stoppedOnPurpose: the agent announced a deliberate quit.
    private bool _connecting, _mayStart = true, _stoppedOnPurpose, _gaveUp, _quitting;
    private int _unanswered;

    public AgentConnection(string stateDirectory, Dispatcher dispatcher)
    {
        _stateDirectory = stateDirectory;
        _dispatcher = dispatcher;
    }

    /// <summary>A plain-words line for the session log (starting the agent, a crash, a restart).</summary>
    public event Action<string>? Note;
    /// <summary>An event announced by the agent (always raised on the UI thread).</summary>
    public event Action<string, JsonNode?>? AgentEvent;
    /// <summary>The connection changed in a way that makes a fresh status worth fetching now.</summary>
    public event Action? RefreshWanted;

    public LocalIpcClient? Client => _client;
    public bool MayStart => _mayStart;
    public int RestartsUsed => _restarts.Used;
    public int MaxRestarts => _restarts.MaxRestarts;

    /// <summary>What to say while there is no status, in the same priority the console always used.</summary>
    public AgentPhase Phase => _gaveUp ? AgentPhase.KeepsStopping : _stoppedOnPurpose ? AgentPhase.Stopped : _mayStart ? AgentPhase.Starting : AgentPhase.NotRunning;

    public void MarkQuitting() => _quitting = true;

    public async Task<LocalIpcClient?> ConnectAsync()
    {
        if (_client is not null) return _client;
        if (_connecting) return null;
        _connecting = true;
        try
        {
            var name = LocalIpc.PipeName(_stateDirectory);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var client = await LocalIpcClient.ConnectAsync(name, TimeSpan.FromSeconds(attempt == 0 ? 1 : 10));
                    client.EventReceived += (eventName, data) => _dispatcher.InvokeAsync(() => OnClientEvent(eventName, data));
                    client.Disconnected += () => _dispatcher.InvokeAsync(() => OnAgentGone(client));
                    _mayStart = false; _stoppedOnPurpose = false; _gaveUp = false;
                    return _client = client;
                }
                catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    // The agent is launched only when explicitly permitted (see _mayStart), never in an open loop.
                    if (attempt != 0 || !_mayStart) break;
                    Note?.Invoke("Starting the agent.");
                    var launched = await Program.StartAgentAsync(_stateDirectory);
                    if (Program.StartNote.Length > 0) Note?.Invoke(Program.StartNote);
                    if (!launched) Note?.Invoke("The agent could not be started.");
                    _mayStart = false;                    // one attempt per permission, also when it failed: never an open loop
                    if (!launched) break;
                }
            }
            return null;
        }
        finally { _connecting = false; }
    }

    /// <summary>
    /// The agent's channel closed. A deliberate stop (Quit here, or the agent announcing "quitting") is respected; an unexpected exit is recovered
    /// automatically, but only a bounded number of times so a crash loop cannot spin.
    /// </summary>
    private void OnAgentGone(LocalIpcClient client)
    {
        if (!ReferenceEquals(_client, client)) return;
        _client = null;
        if (_quitting || _stoppedOnPurpose) return;
        if (_restarts.TryConsume())
        {
            _mayStart = true;
            Note?.Invoke($"The agent stopped unexpectedly; restarting it ({_restarts.Used} of {_restarts.MaxRestarts})");
        }
        else
        {
            _gaveUp = true;
            Note?.Invoke("The agent keeps stopping; it was not restarted again");
        }
        RefreshWanted?.Invoke();
    }

    private void OnClientEvent(string name, JsonNode? data)
    {
        if (name == "quitting") _stoppedOnPurpose = true;
        AgentEvent?.Invoke(name, data);
    }

    /// <summary>Connects if needed and asks for the status. Null when the agent cannot be reached or did not answer.</summary>
    public async Task<JsonObject?> PollAsync()
    {
        JsonObject? status = null;
        try { if (await ConnectAsync() is { } client) { status = await client.SendAsync("status") as JsonObject; _unanswered = 0; } }
        catch (Exception e) when (e is IpcException or TimeoutException) { if (e is TimeoutException) _unanswered++; }
        // An agent that accepts the connection but never answers (frozen, or stuck while shutting down) would otherwise stay "connected" for ever, and a
        // replacement would never be started. Two unanswered checks in a row end that connection; the normal bounded restart path takes over from there.
        if (_unanswered >= 2 && _client is { } silent)
        {
            _unanswered = 0;
            Note?.Invoke("The agent stopped answering; replacing it.");
            _ = silent.DisposeAsync().AsTask();
        }
        return status;
    }

    /// <summary>The owner pressed Start: an explicit decision, so it also clears the crash-restart budget.</summary>
    public void StartExplicitly()
    {
        _restarts.Reset(); _gaveUp = false; _stoppedOnPurpose = false; _mayStart = true; _unanswered = 0;
        if (_client is { } silent) { _client = null; _ = silent.DisposeAsync().AsTask(); }
    }

    /// <summary>Runs a command on the agent. Returns null on success, or a plain-words reason (never an exception message from deeper code).</summary>
    public async Task<string?> TryRunAsync(Func<LocalIpcClient, Task> action)
    {
        try
        {
            var client = await ConnectAsync() ?? throw new IpcException("The OrbitDrive agent is not running.");
            await action(client);
            return null;
        }
        catch (IpcException e) { return e.Message; }
        catch (TimeoutException) { return "The agent did not answer in time."; }
    }

    /// <summary>Asks the agent to quit (best effort).</summary>
    public async Task QuitAgentAsync()
    {
        try { if (_client is not null) await _client.SendAsync("quit"); } catch (Exception e) when (e is IpcException or TimeoutException) { }
    }
}
