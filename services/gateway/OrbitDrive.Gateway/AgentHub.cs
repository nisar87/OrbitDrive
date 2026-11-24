using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>One authenticated outbound control connection from a PC agent.</summary>
public sealed class AgentConnection(WebSocket socket, string pcId, long generation)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    public string PcId { get; } = pcId;
    public long Generation { get; } = generation;
    public const int MaxPendingRequests = 64;

    public async Task SendAsync(Envelope envelope, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        await _sendLock.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation); }
        finally { _sendLock.Release(); }
    }

    public async Task<TResult> RequestAsync<TResult>(string action, object payload, TimeSpan timeout, CancellationToken cancellation)
    {
        if (_pending.Count >= MaxPendingRequests) throw new ProtocolException(ErrorCode.LimitExceeded, "The PC is busy. Try again shortly.");
        var id = Secrets.Token(12);
        var completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds();
            try
            {
                await SendAsync(new Envelope { V = ProtocolInfo.Version, Id = id, Kind = EnvelopeKind.Req, Action = action, DeadlineUnixMs = deadline, Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), ProtocolJson.Options) }, cancellation);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            { throw new ProtocolException(ErrorCode.PcOffline, "The PC is offline."); }
            Envelope response;
            try { response = await completion.Task.WaitAsync(timeout, cancellation); }
            catch (TimeoutException) { throw new ProtocolException(ErrorCode.Timeout, "The PC did not answer in time."); }
            if (response.Error is not null) throw new ProtocolException(response.Error.Code, response.Error.Message);
            if (response.Payload is not { } payloadElement) throw new ProtocolException(ErrorCode.Internal, "The PC sent an empty answer.");
            try { return ProtocolJson.FromElement<TResult>(payloadElement); }
            catch (JsonException) { throw new ProtocolException(ErrorCode.Internal, "The PC sent an answer this service does not understand."); }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Complete(Envelope response)
    {
        if (_pending.TryGetValue(response.Id, out var completion)) completion.TrySetResult(response);
    }

    public void FailAll()
    {
        foreach (var completion in _pending.Values)
            completion.TrySetException(new ProtocolException(ErrorCode.PcOffline, "The PC went offline."));
    }

    public void Abort() { try { socket.Abort(); } catch (ObjectDisposedException) { } }
}

/// <summary>Tracks the single authoritative connection generation per PC and runs the control protocol.</summary>
public sealed class AgentHub(Store store, Invitations invitations, GatewayOptions options, AdminLog admin, ILogger<AgentHub> logger)
{
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new();
    private long _generation;
    private int _pendingHandshakes;

    /// <summary>Control connections that have not yet proven who they are.</summary>
    public int PendingHandshakes => Volatile.Read(ref _pendingHandshakes);

    /// <summary>
    /// Takes one waiting slot, or says no when <paramref name="limit"/> are already taken. The check and the increment are one atomic step, so
    /// many connections arriving at once cannot all pass a check that only one of them should. A slot that was taken must reach
    /// <see cref="RunAsync"/> (which gives it back) or be given back with <see cref="ReleaseHandshake"/>.
    /// </summary>
    public bool TryReserveHandshake(int limit)
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingHandshakes);
            if (current >= limit) return false;
            if (Interlocked.CompareExchange(ref _pendingHandshakes, current + 1, current) == current) return true;
        }
    }

    public void ReleaseHandshake() => Interlocked.Decrement(ref _pendingHandshakes);

    public bool IsOnline(string pcId) => _connections.ContainsKey(pcId);

    /// <summary>Service is stopping: cut every control connection now so agents reconnect at once instead of waiting out a dead socket.</summary>
    public void Shutdown()
    {
        foreach (var connection in _connections.Values) { connection.FailAll(); connection.Abort(); }
    }

    public AgentConnection Get(string pcId) =>
        _connections.TryGetValue(pcId, out var connection) ? connection : throw new ProtocolException(ErrorCode.PcOffline, "The PC is offline. It must be on, awake and running MyOrbitDrive.");

    /// <summary>Reads and checks the optional rotation statement: the NEW key signed "rotate" for this PC and this challenge.</summary>
    private static bool ReadRotation(AgentHello hello, string nonce, out byte[] newSpki)
    {
        newSpki = [];
        if (hello.RotateToSpki is not { Length: > 0 } to || hello.RotateToSignature is not { Length: > 0 } sig) return false;
        try
        {
            newSpki = Base64Url.DecodeFromChars(to);
            using var next = ECDsa.Create();
            next.ImportSubjectPublicKeyInfo(newSpki, out _);
            return next.KeySize == 256 && next.VerifyData(Encoding.UTF8.GetBytes(RotateMessage(nonce, hello.PcId, newSpki)), Base64Url.DecodeFromChars(sig), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    public static string RotateMessage(string nonce, string pcId, byte[] newSpki) => $"orbitdrive-agent-rotate-v1\n{nonce}\n{pcId}\n{Base64Url.EncodeToString(SHA256.HashData(newSpki))}";

    public static string AuthMessage(string nonce, string pcId) => $"orbitdrive-agent-auth-v1\n{nonce}\n{pcId}";

    private async Task<Envelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > options.MaxControlMessageBytes)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "message rejected", cancellation);
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            try { return JsonSerializer.Deserialize<Envelope>(message.GetBuffer().AsSpan(0, (int)message.Length), ProtocolJson.Options); }
            catch (JsonException)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "malformed message", cancellation);
                return null;
            }
        }
    }

    private static Envelope Reply(Envelope request, object? payload = null, ProtocolException? error = null) => new()
    {
        V = ProtocolInfo.Version, Id = request.Id, Kind = EnvelopeKind.Res, Action = request.Action, Error = error?.ToInfo(),
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), ProtocolJson.Options),
    };

    public async Task RunAsync(WebSocket socket, CancellationToken cancellation)
    {
        // 1. Challenge -> signed hello. The PC id is the hash of its public key, so it cannot be claimed by another key.
        var nonce = Secrets.Token();
        var hello = new AgentConnection(socket, "", 0);
        Envelope? first;
        try                                                                  // the slot was reserved by the caller: held until it has said who it is (or been dropped)
        {
            await hello.SendAsync(new Envelope
            {
                V = ProtocolInfo.Version, Id = "challenge", Kind = EnvelopeKind.Evt, Action = Actions.Challenge,
                Payload = ProtocolJson.ToElement(new AgentChallenge { Nonce = nonce, MinProtocolVersion = ProtocolInfo.MinVersion, MaxProtocolVersion = ProtocolInfo.Version }),
            }, cancellation);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            first = await ReceiveAsync(socket, helloTimeout.Token);
        }
        finally { ReleaseHandshake(); }
        if (first is not { Kind: EnvelopeKind.Req, Action: Actions.Hello, Payload: not null }) { socket.Abort(); return; }
        AgentHello agent;
        try { agent = ProtocolJson.FromElement<AgentHello>(first.Payload.Value); }
        catch (JsonException) { socket.Abort(); return; }

        ProtocolException? rejection = null;
        byte[] spki = [];
        bool rotatedAlready = false, rotatedNow = false;
        if (agent.ProtocolVersion < ProtocolInfo.MinVersion || agent.ProtocolVersion > ProtocolInfo.Version)
            rejection = new ProtocolException(ErrorCode.UnsupportedVersion, $"Update required: this service speaks protocol {ProtocolInfo.MinVersion}-{ProtocolInfo.Version}.");
        else if (admin.IsBelowMinimum(agent.AgentVersion))
            rejection = new ProtocolException(ErrorCode.UnsupportedVersion, $"Update required: MyOrbitDrive on this PC must be version {admin.MinAgentVersion} or newer.");
        else
        {
            try
            {
                spki = Base64Url.DecodeFromChars(agent.PublicKeySpki);
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);
                var stored = store.PcKey(agent.PcId);
                // A PC the relay has never seen must use the hash of its key as its id. A known PC must present the key the relay holds for it, or - if it is
                // moving to a new key - prove it holds BOTH: the old key signs the hello, the new key signs the rotation statement.
                var identity = stored is null ? agent.PcId == Base64Url.EncodeToString(SHA256.HashData(spki)) : spki.AsSpan().SequenceEqual(stored);
                var rotation = ReadRotation(agent, nonce, out var newSpki);
                if (!identity && stored is not null && rotation && newSpki.AsSpan().SequenceEqual(stored)) { identity = true; rotatedAlready = true; }   // the move was applied but the PC never heard
                var valid = key.KeySize == 256 && identity && agent.Name.Length is > 0 and <= 64 &&
                            key.VerifyData(Encoding.UTF8.GetBytes(AuthMessage(nonce, agent.PcId)), Base64Url.DecodeFromChars(agent.Signature), HashAlgorithmName.SHA256);
                if (!valid) rejection = new ProtocolException(ErrorCode.Unauthenticated, "PC identity could not be verified.");
                else if (rotation && !rotatedAlready) { store.RotatePcKey(agent.PcId, newSpki); rotatedNow = true; logger.LogInformation("A PC moved to a new identity key"); }
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            { rejection = new ProtocolException(ErrorCode.Unauthenticated, "PC identity could not be verified."); }
        }
        if (rejection is not null)
        {
            await hello.SendAsync(Reply(first, error: rejection), cancellation);
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "rejected", cancellation);
            return;
        }

        // 2. This connection becomes the only valid generation; any older socket is cut off.
        var pc = store.UpsertPc(agent.PcId, spki, agent.Name, agent.AgentVersion);
        var connection = new AgentConnection(socket, agent.PcId, Interlocked.Increment(ref _generation));
        _connections.AddOrUpdate(agent.PcId, connection, (_, old) => { old.FailAll(); old.Abort(); return connection; });
        logger.LogInformation("Agent connected generation {Generation}", connection.Generation);
        try
        {
            // Lease: an agent that announces heartbeats must be heard from within the lease, or it is offline. A frozen PC,
            // a pulled cable or a half-open TCP connection would otherwise look "online" until the OS gives up.
            var leased = agent.Capabilities.Contains("heartbeat");
            var lease = TimeSpan.FromSeconds(options.AgentLeaseSeconds);
            await connection.SendAsync(Reply(first, new AgentWelcome
            {
                PcId = agent.PcId, Generation = connection.Generation, ProtocolVersion = agent.ProtocolVersion, OwnerBound = pc.OwnerUid is not null, Rotated = rotatedNow || rotatedAlready ? true : null,
                HeartbeatSeconds = leased ? options.AgentHeartbeatSeconds : null, LeaseSeconds = leased ? options.AgentLeaseSeconds : null,
            }), cancellation);
            using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            while (true)
            {
                if (leased) silence.CancelAfter(lease);
                Envelope? envelope;
                try { envelope = await ReceiveAsync(socket, silence.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    logger.LogWarning("Agent lease expired generation {Generation}", connection.Generation);
                    connection.Abort();
                    break;
                }
                if (envelope is null) break;
                if (!_connections.TryGetValue(agent.PcId, out var current) || current != connection) break; // superseded
                store.Touch(agent.PcId);
                if (envelope.V != ProtocolInfo.Version) continue;
                if (envelope.Kind == EnvelopeKind.Res) { connection.Complete(envelope); continue; }
                if (envelope.Kind != EnvelopeKind.Req) continue;
                Envelope reply;
                try { reply = Reply(envelope, HandleAgentRequest(connection, envelope)); }
                catch (ProtocolException e) { reply = Reply(envelope, error: e); }
                catch (JsonException) { reply = Reply(envelope, error: new ProtocolException(ErrorCode.InvalidRequest, "Malformed request.")); }
                await connection.SendAsync(reply, cancellation);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _connections.TryRemove(new KeyValuePair<string, AgentConnection>(agent.PcId, connection));
            connection.FailAll();
            logger.LogInformation("Agent disconnected generation {Generation}", connection.Generation);
        }
    }

    private object HandleAgentRequest(AgentConnection connection, Envelope envelope)
    {
        switch (envelope.Action)
        {
            case Actions.Heartbeat:
                return new Ack { Ok = true };
            case Actions.CreatePairing:
                var (id, secret, expires) = store.CreatePairing(connection.PcId);
                return new CreatePairingResult { PairingId = id, Secret = secret, ExpiresAtUnixMs = expires.ToUnixTimeMilliseconds() };
            case Actions.ConfirmPairing:
                var confirm = ProtocolJson.FromElement<ConfirmPairingRequest>(envelope.Payload ?? throw new JsonException());
                return new ConfirmPairingResult { State = store.ConfirmPairing(confirm.PairingId, connection.PcId, confirm.Approve) };
            case Actions.ListDevices:
                return new BrowserDeviceListResponse { Devices = [.. store.DevicesForPc(connection.PcId).Select(d => new BrowserDeviceSummary { DeviceId = d.Id, Name = d.Name, Current = false, PcIds = [connection.PcId], LastSeenUnixMs = d.LastSeenUnixMs })] };
            case Actions.CreateInvitation:
                var offer = invitations.Create(connection.PcId, ProtocolJson.FromElement<CreateInvitationRequest>(envelope.Payload ?? throw new JsonException()));
                return invitations.Summarize(offer, true);
            case Actions.ListInvitations:
                return new InvitationList { Invitations = [.. invitations.ForPc(connection.PcId).Select(i => invitations.Summarize(i, true))] };
            case Actions.CancelInvitation:
                invitations.Cancel(connection.PcId, ProtocolJson.FromElement<CancelInvitationRequest>(envelope.Payload ?? throw new JsonException()).InvitationId);
                return new Ack { Ok = true };
            case Actions.RevokeDevice:
                // Scoped to the calling PC's own grants: one PC can never revoke (or learn about) another PC's devices.
                store.RevokeGrant(connection.PcId, ProtocolJson.FromElement<RevokeDeviceRequest>(envelope.Payload ?? throw new JsonException()).DeviceId);
                return new Ack { Ok = true };
            default:
                throw new ProtocolException(ErrorCode.UnknownAction, "Unknown action.");
        }
    }
}
