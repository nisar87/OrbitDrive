using System.Text.Json.Nodes;
using System.Windows.Threading;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// The console's engine, with no windows in it: the one-second refresh, the agent connection, pause/resume, the pause policies, update draining and Quit.
/// Views and the tray subscribe to it. Everything shown is the agent's real state read over the local channel; the console holds no sharing state of its own.
/// Closing the window leaves the agent (and remote access) running; Quit ends both.
/// </summary>
internal sealed class ConsoleHost
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Func<JsonObject?, Task>> _consumers = [];
    private readonly List<Func<ConsoleStatus?, Task>> _typedConsumers = [];
    private bool _quitting;
    private int _deviceTick;

    public ConsoleHost(string stateDirectory, Dispatcher dispatcher)
    {
        StateDirectory = stateDirectory;
        Settings = new ConsoleSettings(stateDirectory);
        Connection = new AgentConnection(stateDirectory, dispatcher);
        Policies = new PausePolicies(this, dispatcher, Settings);
        Connection.Note += Log;
        Connection.RefreshWanted += () => _ = RefreshAsync();
        Connection.AgentEvent += OnAgentEvent;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public string StateDirectory { get; }
    public AgentConnection Connection { get; }
    public ConsoleSettings Settings { get; }
    public PausePolicies Policies { get; }
    public bool Paused { get; private set; }
    /// <summary>The latest status the agent reported; null while the agent cannot be reached.</summary>
    public ConsoleStatus? Status { get; private set; }
    /// <summary>The paired devices as the relay last listed them; null until the first list arrives.</summary>
    public IReadOnlyList<DeviceInfo>? Devices { get; private set; }
    public bool IsQuitting => _quitting;

    /// <summary>A plain-words line for the session log.</summary>
    public event Action<string>? Note;
    /// <summary>An agent event (after the console has already logged it).</summary>
    public event Action<string, JsonNode?>? AgentEvent;
    /// <summary>Quit finished stopping the agent; the shell closes the tray icon and ends the process.</summary>
    public event Action? Quit;

    public void Log(string line) => Note?.Invoke(line);

    /// <summary>Registers something that wants every fresh status (null when there is none). Called in order, on the UI thread.</summary>
    public void AddStatusConsumer(Func<JsonObject?, Task> consumer) => _consumers.Add(consumer);

    /// <summary>Same, with the typed status. Called after <see cref="Status"/> and <see cref="Devices"/> are current.</summary>
    public void AddStatusConsumer(Func<ConsoleStatus?, Task> consumer) => _typedConsumers.Add(consumer);

    public void Start()
    {
        _timer.Start();                 // also when started hidden at sign-in (no window is shown, so no Loaded event)
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_quitting) return;
        var status = await Connection.PollAsync();
        Status = status is null ? null : ConsoleStatus.Parse(status);
        if (status is not null)
        {
            Paused = status["paused"]!.GetValue<bool>();
            Policies.OnTick(Paused);
            await RefreshDevicesAsync(status["state"]!.GetValue<string>());
        }
        foreach (var consumer in _consumers) await consumer(status);
        foreach (var consumer in _typedConsumers) await consumer(Status);
    }

    /// <summary>The device list is a relay round trip, so it is fetched every fifth refresh (and straight away after a pairing or a removal).</summary>
    private async Task RefreshDevicesAsync(string state)
    {
        if (state is not ("online" or "paused") || _deviceTick++ % 5 != 0) return;
        try
        {
            if (Connection.Client is { } client && await client.SendAsync("devices") is JsonArray list) Devices = ConsoleStatus.ParseDevices(list);
        }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
    }

    private void OnAgentEvent(string name, JsonNode? data)
    {
        var line = name switch
        {
            "connected" => "Connected to the relay",
            "disconnected" => "Connection to the relay lost; retrying",
            "paused" => "Remote access paused",
            "resumed" => "Remote access resumed",
            "pairing" => "Pairing code created",
            "pairing_claimed" => $"{data?["browserName"]} asked to pair",
            "pairing_result" => $"Pairing {data?["state"]}",
            "roots_changed" => "Shared folders changed",
            "device_revoked" => "A device's access was removed",
            "stream_finished" => $"Transfer {data?["outcome"]} ({data?["bytes"]} bytes sent to the relay)",
            "update_required" => "This version is too old for the service",
            "quitting" => "The agent was stopped",
            _ => name,
        };
        Log(line);
        if (name is "pairing_result" or "device_revoked") _deviceTick = 0;
        AgentEvent?.Invoke(name, data);
        _ = RefreshAsync();
    }

    /// <summary>Runs a command on the agent, then refreshes. Returns a plain-words problem, or null.</summary>
    public async Task<string?> RunAsync(Func<LocalIpcClient, Task> action)
    {
        var error = await Connection.TryRunAsync(action);
        await RefreshAsync();
        return error;
    }

    internal async Task SendPauseAsync(bool pause)
    {
        var error = await RunAsync(client => client.SendAsync(pause ? "pause" : "resume"));
        if (error is not null) Log(error);
    }

    public Task TogglePauseAsync()
    {
        Policies.OwnerChangedPause();
        return SendPauseAsync(!Paused);
    }

    /// <summary>Before an update: stop remote access and wait until no file is being sent (bounded by the caller's token).</summary>
    public async Task DrainForUpdateAsync(CancellationToken cancellation)
    {
        var client = await Connection.ConnectAsync();
        if (client is null) return;                       // the agent is not running: nothing to stop
        try
        {
            await client.SendAsync("pause");
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (await client.SendAsync("status") is JsonObject status && (int)status["activeStreams"]! == 0) return;
                await Task.Delay(500, cancellation);
            }
        }
        catch (IpcException e) { throw new InvalidOperationException(e.Message); }
    }

    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _timer.Stop();
        Policies.Dispose();
        Connection.MarkQuitting();
        await Connection.QuitAgentAsync();
        Quit?.Invoke();
    }
}
