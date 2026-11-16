using System.Text.Json;
using System.Text.Json.Nodes;
using OrbitDrive.Core.Ipc;
using OrbitDrive.Protocol;

namespace OrbitDrive.Agent;

/// <summary>
/// The per-user worker: owns the relay connection and answers the local owner console. Every owner action
/// (share a folder, approve a browser, pause, quit) arrives through here from a same-user local channel.
/// </summary>
internal sealed class AgentHost : IDisposable
{
    private readonly AgentState _state;
    private readonly string _pcName;
    private readonly Action<string, object?> _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly LocalIpcServer _ipc;
    private readonly Lock _gate = new();
    private ControlClient? _client;
    private CancellationTokenSource? _clientCancel;
    private Task _clientTask = Task.CompletedTask;
    private string _connection = "not_configured";
    private bool _paused;
    private JsonObject? _activePairing;
    private JsonObject? _pendingApproval;

    public CancellationToken Shutdown => _shutdown.Token;

    public AgentHost(AgentState state, string pcName, Action<string, object?> log)
    {
        _state = state; _pcName = pcName; _log = log;
        _ipc = new LocalIpcServer(LocalIpc.PipeName(state.Directory_), HandleAsync);
    }

    private void Emit(string name, object? data)
    {
        lock (_gate)
        {
            switch (name)
            {
                case "connected": _connection = "online"; break;
                case "disconnected": _connection = "connecting"; break;
                case "update_required": _connection = "update_required"; break;
                case "pairing_claimed": _pendingApproval = JsonSerializer.SerializeToNode(data) as JsonObject; break;
                case "pairing_result": _pendingApproval = null; _activePairing = null; break;
            }
        }
        _log(name, data);
        _ipc.Broadcast(name, data is null ? null : JsonSerializer.SerializeToNode(data));
    }

    public Task RunIpcAsync() => _ipc.RunAsync(_shutdown.Token);

    public void StartClient()
    {
        lock (_gate)
        {
            _clientCancel?.Cancel();
            _client = null;
            if (_state.Gateway is null) { _connection = "not_configured"; return; }
            _connection = "connecting";
            _clientCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var client = new ControlClient(new Uri(_state.Gateway), _state, _pcName, Emit) { Paused = _paused };
            _client = client;
            var token = _clientCancel.Token;
            _clientTask = Task.Run(async () => { try { await client.RunAsync(token); } finally { client.Dispose(); } }, CancellationToken.None);
        }
    }

    private ControlClient Client => _client ?? throw new IpcException("Set the relay address first.");

    public JsonObject Status()
    {
        lock (_gate)
        {
            var expired = _activePairing is not null && long.Parse(_activePairing["expiresAtUnixMs"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (expired) _activePairing = null;
            return new JsonObject
            {
                ["state"] = _paused && _connection == "online" ? "paused" : _connection,
                ["paused"] = _paused,
                ["pcId"] = _state.PcId,
                ["pcName"] = _pcName,
                ["gateway"] = _state.Gateway,
                ["agentVersion"] = ControlClient.AgentVersion,
                ["activeStreams"] = _client?.ActiveStreams ?? 0,
                ["roots"] = new JsonArray([.. _state.Roots.Select(r => (JsonNode)new JsonObject { ["rootId"] = r.RootId, ["name"] = r.Name, ["path"] = r.Path, ["available"] = Directory.Exists(r.Path), ["readOnly"] = true })]),
                ["activePairing"] = _activePairing?.DeepClone(),
                ["pendingApproval"] = _pendingApproval?.DeepClone(),
            };
        }
    }

    public async Task<JsonObject> PairAsync(CancellationToken cancellation)
    {
        var pairing = await Client.CreatePairingAsync(cancellation);
        var origin = _state.WebOrigin ?? _state.Gateway!;
        // The secret travels in the URL fragment so it is never sent to a web server, proxy log or Referer header.
        var result = new JsonObject { ["url"] = $"{origin}/pair#id={pairing.PairingId}&s={pairing.Secret}", ["code"] = $"{pairing.PairingId}-{pairing.Secret}", ["expiresAtUnixMs"] = pairing.ExpiresAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        lock (_gate) { _activePairing = result; _pendingApproval = null; }
        Emit("pairing", JsonSerializer.Deserialize<object>(result.ToJsonString()));
        return (JsonObject)result.DeepClone();
    }

    public async Task<string> ConfirmAsync(bool approve, CancellationToken cancellation)
    {
        var state = (await Client.ConfirmPairingAsync(approve, cancellation)).ToString().ToLowerInvariant();
        Emit("pairing_result", new { state });
        return state;
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) { _paused = paused; if (_client is not null) _client.Paused = paused; }
        Emit(paused ? "paused" : "resumed", null);
    }

    public void Quit() => _shutdown.Cancel();

    public async Task WaitAsync()
    {
        try { await Task.Delay(Timeout.Infinite, _shutdown.Token); } catch (OperationCanceledException) { }
        try { await _clientTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
    }

    private static string Arg(JsonObject args, string name, int max)
    {
        var value = args[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new IpcException($"'{name}' is required.");
        return value;
    }

    /// <summary>Closed command set (validated by LocalIpcServer before it gets here).</summary>
    private async Task<JsonNode?> HandleAsync(string command, JsonObject args, CancellationToken cancellation)
    {
        try
        {
            switch (command)
            {
                case "status": return Status();
                case "add_root":
                    var root = _state.AddRoot(Arg(args, "name", 64), Arg(args, "path", 4096));
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return new JsonObject { ["rootId"] = root.RootId };
                case "remove_root":
                    if (!_state.RemoveRoot(Arg(args, "rootId", 64))) throw new IpcException("That folder is not shared.");
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return null;
                case "pair": return await PairAsync(cancellation);
                case "approve": return new JsonObject { ["state"] = await ConfirmAsync(true, cancellation) };
                case "deny": return new JsonObject { ["state"] = await ConfirmAsync(false, cancellation) };
                case "pause": SetPaused(true); return null;
                case "resume": SetPaused(false); return null;
                case "set_gateway":
                    _state.SetGateway(Arg(args, "gateway", 512), args["webOrigin"]?.GetValue<string>());
                    StartClient();
                    return null;
                case "quit":
                    _ = Task.Run(async () => { await Task.Delay(100); Quit(); }, CancellationToken.None); // let the reply reach the console first
                    return null;
                default: throw new IpcException("Unknown command.");
            }
        }
        catch (ProtocolException e) { throw new IpcException(e.Message); }
        catch (Exception e) when (e is ArgumentException or DirectoryNotFoundException or UriFormatException) { throw new IpcException(e.Message); }
        catch (TimeoutException) { throw new IpcException("The service did not answer in time."); }
    }

    public void Dispose() { _shutdown.Dispose(); _clientCancel?.Dispose(); }
}
