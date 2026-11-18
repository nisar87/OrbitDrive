using OrbitDrive.Core;
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

    private readonly FileAccessService _files;
    private readonly string _receiverCache;
    private readonly List<JsonObject> _offers = [];                   // newest last, bounded; what "Send to my phone" did, success or not
    private readonly SupportBundle _bundle;
    private readonly SemaphoreSlim _outboxGate = new(1, 1);
    private FileSystemWatcher? _outboxWatcher;
    private Timer? _outboxTimer;
    public static readonly TimeSpan SpoolMaxAge = TimeSpan.FromMinutes(5);
    public string OutboxDirectory => Path.Join(_state.Directory_, "outbox");
    public const int MaxFilesPerSend = 500;
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromHours(24);

    public AgentHost(AgentState state, string pcName, Action<string, object?> log)
    {
        _state = state; _pcName = pcName; _log = log;
        _files = state.Files;
        _receiverCache = Path.Join(state.Directory_, "receivers.json");
        _bundle = new SupportBundle(state.Activity, SupportFacts, Path.Join(state.Directory_, "support"));
        _ipc = new LocalIpcServer(LocalIpc.PipeName(state.Directory_), HandleAsync);
    }

    private JsonObject ThumbnailStatus()
    {
        var (files, bytes) = _state.Thumbnails.Cache.Stats();
        return new JsonObject { ["enabled"] = _state.ThumbnailsEnabled, ["available"] = _state.Thumbnails.Available, ["files"] = files, ["bytes"] = bytes };
    }

    /// <summary>Non-identifying facts for a support bundle: versions, system type, state and counts. No names, paths or accounts.</summary>
    private IReadOnlyDictionary<string, string> SupportFacts()
    {
        var (files, bytes) = _state.Activity.Footprint();
        string connection; bool paused;
        lock (_gate) { connection = _paused && _connection == "online" ? "paused" : _connection; paused = _paused; }
        return new Dictionary<string, string>
        {
            ["app_version"] = ControlClient.AgentVersion,
            ["protocol_version"] = ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["system"] = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}",
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = Environment.Version.ToString(),
            ["connection"] = connection,
            ["paused"] = paused ? "yes" : "no",
            ["shared_folders"] = _state.Roots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["folders_with_uploads_on"] = _state.Roots.Count((r) => r.UploadsEnabled).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["thumbnails_enabled"] = _state.ThumbnailsEnabled ? "yes" : "no",
            ["activity_files"] = files.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["activity_bytes"] = bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
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
        if (name is "connected" or "pairing_result" or "device_revoked") _ = Task.Run(RefreshReceiversAsync);
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
                ["maxUploadKBps"] = _state.MaxUploadKBps,
                ["maxConcurrentTransfers"] = _state.MaxConcurrentTransfers,
                ["agentVersion"] = ControlClient.AgentVersion,
                ["activeStreams"] = _client?.ActiveStreams ?? 0,
                ["transfers"] = _client?.RecentTransfers() ?? [],
                ["offers"] = new JsonArray([.. _offers.Select(o => o.DeepClone())]),
                ["roots"] = new JsonArray([.. _state.Roots.Select(r => (JsonNode)new JsonObject { ["rootId"] = r.RootId, ["name"] = r.Name, ["path"] = r.Path, ["available"] = _files.IsAvailable(r), ["readOnly"] = !r.UploadsEnabled, ["showHidden"] = r.ShowHidden, ["uploadsEnabled"] = r.UploadsEnabled, ["uploadQuotaBytes"] = r.UploadQuotaBytes.ToString(), ["uploadUsedBytes"] = r.UploadUsedBytes.ToString(), ["uploadMaxFileBytes"] = r.UploadMaxFileBytes.ToString() })]),
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
                case "activity":
                    return new JsonObject { ["entries"] = new JsonArray([.. _state.Activity.Recent(args["count"]?.GetValue<int>() ?? 100).Select((e) => (JsonNode)new JsonObject
                        { ["time"] = e.Time.ToString("O"), ["kind"] = e.Kind, ["detail"] = e.Detail, ["bytes"] = e.Bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) })]) };
                case "clear_activity": _state.Activity.Clear(); return null;
                case "support_bundle_preview":
                    return new JsonObject { ["items"] = new JsonArray([.. _bundle.Preview().Select((i) => (JsonNode)new JsonObject
                        { ["name"] = i.Name, ["description"] = i.Description, ["bytes"] = i.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), ["preview"] = i.Preview })]) };
                case "support_bundle_create":
                    return new JsonObject { ["path"] = _bundle.Create(args["consent"]?.GetValue<bool>() ?? false) };
                case "add_root":
                    var root = _state.AddRoot(Arg(args, "name", 64), Arg(args, "path", 4096), args["showHidden"]?.GetValue<bool>() ?? false);
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return new JsonObject { ["rootId"] = root.RootId };
                case "set_root_hidden":
                    if (!_state.SetShowHidden(Arg(args, "rootId", 64), args["showHidden"]?.GetValue<bool>() ?? false)) throw new IpcException("That folder is not shared.");
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return null;
                case "set_root_uploads":
                    // Owner decision on this PC only. Sizes come in MiB so a typo cannot silently mean bytes.
                    var quotaMiB = args["quotaMiB"]?.GetValue<long>() ?? 0; var maxMiB = args["maxFileMiB"]?.GetValue<long>() ?? 0;
                    if (quotaMiB is < 0 or > (1L << 30) || maxMiB is < 0 or > (1L << 30)) throw new IpcException("Sizes must be between 0 and 1,073,741,824 MiB.");
                    try
                    {
                        if (!_state.SetUploads(Arg(args, "rootId", 64), args["enabled"]?.GetValue<bool>() ?? false, quotaMiB << 20, maxMiB << 20, args["resetUsed"]?.GetValue<bool>() ?? false))
                            throw new IpcException("That folder is not shared.");
                    }
                    catch (ArgumentException e) { throw new IpcException(e.Message); }
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return null;
                case "remove_root":
                    if (!_state.RemoveRoot(Arg(args, "rootId", 64))) throw new IpcException("That folder is not shared.");
                    Emit("roots_changed", new { count = _state.Roots.Count });
                    return null;
                case "pair": return await PairAsync(cancellation);
                case "approve": return new JsonObject { ["state"] = await ConfirmAsync(true, cancellation) };
                case "deny": return new JsonObject { ["state"] = await ConfirmAsync(false, cancellation) };
                case "devices":
                    var devices = await Client.ListDevicesAsync(cancellation);
                    WriteReceiverCache(devices);
                    return new JsonArray([.. devices.Devices.Select(d => (JsonNode)new JsonObject { ["deviceId"] = d.DeviceId, ["name"] = d.Name, ["lastSeenUnixMs"] = d.LastSeenUnixMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) })]);
                case "thumbnails": return ThumbnailStatus();
                case "set_thumbnails":
                    _state.SetThumbnailsEnabled(args["enabled"]?.GetValue<bool>() ?? false);
                    return ThumbnailStatus();
                case "clear_thumbnails":
                    _state.Thumbnails.Cache.Clear();
                    return ThumbnailStatus();
                case "set_sendto":
                    _state.SetSendToShortcuts(args["enabled"]?.GetValue<bool>() ?? false);
                    await SyncSendToAsync(cancellation);
                    return new JsonObject { ["enabled"] = _state.SendToShortcuts };
                case "send_files":
                    var sent = await SendFilesAsync(args, cancellation);
                    RecordOffers((sent as JsonObject)?["results"], "");
                    return sent;
                case "invitations":
                    return new JsonArray([.. (await Client.ListInvitationsAsync(cancellation)).Invitations.Select(i => (JsonNode)new JsonObject
                    {
                        ["invitationId"] = i.InvitationId, ["name"] = i.Name, ["size"] = i.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["state"] = JsonSerializer.SerializeToNode(i.State), ["receiverName"] = i.ReceiverName, ["failure"] = i.Failure,
                        ["expiresAtUnixMs"] = i.ExpiresAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    })]);
                case "cancel_invitation":
                    await Client.CancelInvitationAsync(Arg(args, "invitationId", 64), cancellation);
                    return null;
                case "revoke_device":
                    var revoking = Arg(args, "deviceId", 128);
                    await Client.RevokeDeviceAsync(revoking, cancellation);
                    _state.Uploads.CancelForDevice(revoking);      // whatever that browser was still sending is discarded
                    Emit("device_revoked", null);
                    return null;
                case "pause": SetPaused(true); return null;
                case "resume": SetPaused(false); return null;
                case "set_limits":
                    try { _state.SetLimits(args["maxUploadKBps"]?.GetValue<int>() ?? -1, args["maxConcurrentTransfers"]?.GetValue<int>() ?? -1); }
                    catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException) { throw new IpcException(e is ArgumentException ? e.Message : "Limits must be whole numbers."); }
                    return null;
                case "set_gateway":
                    _state.SetGateway(Arg(args, "gateway", 512), args["webOrigin"]?.GetValue<string>());
                    StartClient();
                    return null;
                case "quit":
                    Emit("quitting", null); // lets an attached console tell a deliberate stop from a crash
                    _ = Task.Run(async () => { await Task.Delay(100); Quit(); }, CancellationToken.None); // let the reply reach the console first
                    return null;
                default: throw new IpcException("Unknown command.");
            }
        }
        catch (ProtocolException e) { throw new IpcException(e.Message); }
        catch (Exception e) when (e is ArgumentException or DirectoryNotFoundException or UriFormatException) { throw new IpcException(e.Message); }
        catch (TimeoutException) { throw new IpcException("The service did not answer in time."); }
    }

    /// <summary>
    /// Offers explicitly chosen files to one paired receiver. Each file becomes a durable invitation; nothing is copied to the relay.
    /// Paths come only from the owner's own Explorer selection over the per-user pipe, are never put in a command line, and every
    /// outcome is reported per file: a bad name or folder in the selection does not stop the others.
    /// </summary>
    private async Task<JsonNode?> SendFilesAsync(JsonObject args, CancellationToken cancellation)
    {
        var receiverId = Arg(args, "receiverId", 128);
        if (args["paths"] is not JsonArray list || list.Count is 0 or > MaxFilesPerSend) throw new IpcException($"Choose between 1 and {MaxFilesPerSend} files.");
        var requestId = args["requestId"]?.GetValue<string>() is { Length: > 0 and <= 64 } given ? given : Guid.NewGuid().ToString("N");
        var results = new JsonArray();
        for (var i = 0; i < list.Count; i++)
        {
            var path = list[i]?.GetValue<string>() ?? "";
            var entry = new JsonObject { ["path"] = path };
            try
            {
                var offer = _files.IssueOneShot(path, OfferLifetime);
                var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestId + "\n" + i + "\n" + path)))[..32];
                var created = await Client.CreateInvitationAsync(new CreateInvitationRequest
                {
                    ReceiverId = receiverId, Name = offer.Name, Size = offer.Size, ModifiedUnixMs = offer.ModifiedUnixMs, Grant = offer.Grant, IdempotencyKey = key,
                }, cancellation);
                entry["ok"] = true; entry["invitationId"] = created.InvitationId; entry["name"] = offer.Name;
                Emit("invitation_created", new { invitationId = created.InvitationId, name = offer.Name });
            }
            catch (ProtocolException e)
            {
                entry["ok"] = false; entry["code"] = JsonSerializer.SerializeToNode(e.Code); entry["message"] = e.Message;
            }
            results.Add(entry);
        }
        return new JsonObject { ["results"] = results };
    }

    private void RecordOffers(JsonNode? results, string receiverId)
    {
        if (results is not JsonArray list) return;
        lock (_gate)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                var ok = item["ok"]?.GetValue<bool>() ?? false;
                _offers.Add(new JsonObject
                {
                    ["atUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["name"] = ok ? item["name"]?.GetValue<string>() : System.IO.Path.GetFileName(item["path"]?.GetValue<string>() ?? ""),
                    ["ok"] = ok, ["message"] = ok ? "Offered. It waits in the phone's Inbox." : item["message"]?.GetValue<string>() ?? "Could not be offered.",
                });
            }
            if (_offers.Count > 20) _offers.RemoveRange(0, _offers.Count - 20);
        }
        Emit("offers_changed", null);
    }

    /// <summary>
    /// The Explorer command hands selections over here when the pipe is not available (the app was not running yet): a small
    /// per-user spool file holding paths as DATA (never a command line). A file older than five minutes is discarded rather than
    /// sent late; a file written while the relay is unreachable waits for the connection until then.
    /// </summary>
    public void StartOutbox()
    {
        Directory.CreateDirectory(OutboxDirectory);
        _outboxWatcher = new FileSystemWatcher(OutboxDirectory, "*.send") { EnableRaisingEvents = true };
        _outboxWatcher.Created += (_, _) => _ = ProcessOutboxAsync();
        _outboxWatcher.Renamed += (_, _) => _ = ProcessOutboxAsync();
        _outboxTimer = new Timer(_ => _ = ProcessOutboxAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    public async Task ProcessOutboxAsync()
    {
        if (!await _outboxGate.WaitAsync(0)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(OutboxDirectory, "*.send").Order().Take(20))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                JsonObject? request = null;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is > 0 and <= 1 << 20) request = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* unreadable or half-written */ }
                if (request is null) { if (age > TimeSpan.FromSeconds(30)) TryDelete(file); continue; }
                if (age > SpoolMaxAge)
                {
                    RecordOffers(new JsonArray([.. (request["paths"] as JsonArray ?? []).Select(p => (JsonNode)new JsonObject { ["ok"] = false, ["path"] = p?.GetValue<string>(), ["message"] = "Not sent: OrbitDrive was not running or not connected in time. Send it again." })]), "");
                    TryDelete(file);
                    continue;
                }
                JsonNode? done;
                try { done = await SendFilesAsync(request, _shutdown.Token); }
                catch (IpcException e) { RecordOffers(new JsonArray([.. (request["paths"] as JsonArray ?? []).Select(p => (JsonNode)new JsonObject { ["ok"] = false, ["path"] = p?.GetValue<string>(), ["message"] = e.Message })]), ""); TryDelete(file); continue; }
                var results = (done as JsonObject)?["results"] as JsonArray;
                var offline = results is { Count: > 0 } && results.All(r => r?["code"]?.ToString() == "pc_offline");
                if (offline) continue;                                      // not connected yet: keep it until it is stale
                RecordOffers(results, request["receiverId"]?.GetValue<string>() ?? "");
                TryDelete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { /* try again on the next tick */ }
        finally { _outboxGate.Release(); }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Windows only. Keeps the classic Send to shortcuts in step with the paired receivers when the owner turned them on.</summary>
    private async Task SyncSendToAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) { if (_state.SendToShortcuts) throw new IpcException("Explorer shortcuts are only available on Windows."); return; }
        try
        {
            var folder = SendToFolderOverride ?? SendToShortcuts.DefaultFolder;
            if (!_state.SendToShortcuts) { SendToShortcuts.RemoveAll(folder); return; }
            var devices = await Client.ListDevicesAsync(cancellation);
            WriteReceiverCache(devices);
            var exe = Environment.ProcessPath ?? throw new IpcException("Cannot find the OrbitDrive program.");
            var custom = Path.GetFullPath(_state.Directory_) != Path.GetFullPath(DefaultStateDirectory);
            // Explorer appends the file paths after the arguments, so a non-default state folder (tests only) cannot be expressed: skip it.
            if (custom && SendToFolderOverride is null) return;
            SendToShortcuts.Sync(folder, devices.Devices.Select(d => new SendToShortcuts.Receiver(d.DeviceId, d.Name)), exe, "ctl");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new IpcException("Could not update the Explorer \"Send to\" shortcuts: " + e.Message); }
    }

    public static string DefaultStateDirectory => Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitDrive", "agent");
    /// <summary>Tests point the shortcut folder somewhere harmless.</summary>
    public static string? SendToFolderOverride => Environment.GetEnvironmentVariable("ORBITDRIVE_SENDTO_FOLDER");

    private async Task RefreshReceiversAsync()
    {
        try { WriteReceiverCache(await Client.ListDevicesAsync(_shutdown.Token)); if (_state.SendToShortcuts) await SyncSendToAsync(_shutdown.Token); }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or TimeoutException) { /* offline: keep the last list, its age is shown */ }
    }

    /// <summary>
    /// The Explorer command must not do network or IPC work while Explorer builds its menu, so it reads this small per-user file.
    /// It lists receiver names and ids only (no secrets) and says when it was written; the command shows that age honestly.
    /// </summary>
    private void WriteReceiverCache(BrowserDeviceListResponse devices)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                writtenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), pipe = LocalIpc.PipeName(_state.Directory_),
                receivers = devices.Devices.Select(d => new { deviceId = d.DeviceId, name = d.Name, lastSeenUnixMs = d.LastSeenUnixMs }),
            });
            var temp = _receiverCache + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _receiverCache, overwrite: true);
            // The native Explorer command reads this simpler file: no JSON parser inside Explorer's process.
            var lines = new List<string> { "v1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), LocalIpc.PipeName(_state.Directory_) };
            foreach (var d in devices.Devices.Where(d => d.DeviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                lines.Add($"{d.DeviceId}\t{d.LastSeenUnixMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}\t{new string([.. d.Name.Select(c => char.IsControl(c) ? ' ' : c)])}");
            var plain = Path.Join(_state.Directory_, "receivers.txt");
            File.WriteAllText(plain + ".tmp", string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(plain + ".tmp", plain, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a stale cache only makes the menu list older names */ }
    }

    public void Dispose() { _outboxTimer?.Dispose(); _outboxWatcher?.Dispose(); _shutdown.Dispose(); _clientCancel?.Dispose(); }
}
