using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Agent;

/// <summary>Streams one validated byte range from the already-open handle with a single pooled buffer.</summary>
internal sealed class FileRangeContent(ValidatedRead read, CancellationToken cancellation) : HttpContent
{
    public const int BufferBytes = 64 * 1024;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, cancellation);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken requestCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, requestCancellation);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long position = read.Offset, end = read.Offset + read.Length;
            while (position < end)
            {
                var count = await RandomAccess.ReadAsync(read.Handle, buffer.AsMemory(0, (int)Math.Min(BufferBytes, end - position)), position, linked.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("Source file ended unexpectedly.");
                await stream.WriteAsync(buffer.AsMemory(0, count), linked.Token).ConfigureAwait(false); // awaits the relay: slow receivers throttle disk reads
                position += count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    protected override bool TryComputeLength(out long length) { length = read.Length; return true; }
}

/// <summary>Outbound-only control connection: authenticates, serves typed requests and reconnects with backoff.</summary>
internal sealed class ControlClient(Uri gateway, AgentState state, string pcName, Action<string, object?> emit) : IDisposable
{
    public const string AgentVersion = "0.1.0";
    private const int MaxMessageBytes = 262144;
    private readonly FileAccessService _files = new(() => state.Roots, state.Tokens);
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(16, 16);
    private readonly SemaphoreSlim _streamSlots = new(4, 4);
    private ClientWebSocket? _socket;
    private volatile bool _paused;

    public string? PendingPairingId { get; private set; }
    public int ActiveStreams => _streams.Count;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value) foreach (var stream in _streams.Values) stream.Cancel(); // pausing stops active transfers immediately
        }
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        var attempt = 0;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                var builder = new UriBuilder(gateway) { Scheme = gateway.Scheme == "https" ? "wss" : "ws", Path = "/v1/agent-control" };
                await socket.ConnectAsync(builder.Uri, cancellation);
                var welcome = await HandshakeAsync(socket, cancellation);
                _socket = socket;
                attempt = 0;
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                try
                {
                    if (welcome.HeartbeatSeconds is > 0 and var beat) _ = HeartbeatAsync(socket, TimeSpan.FromSeconds(beat), connection.Token);
                    await ReceiveLoopAsync(socket, welcome.LeaseSeconds is > 0 and var lease ? TimeSpan.FromSeconds(lease) : null, cancellation);
                }
                finally { connection.Cancel(); }
            }
            catch (TimeoutException)
            {
                // Nothing at all from the service for a whole lease (it answers every heartbeat): the path is dead even if
                // the socket still looks open. Drop it and reconnect.
                emit("disconnected", new { reason = "service_silent" });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (ProtocolException e) when (e.Code == ErrorCode.UnsupportedVersion)
            {
                emit("update_required", new { message = e.Message });
                attempt = Math.Max(attempt, 5);
            }
            catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException or ProtocolException or JsonException or OperationCanceledException)
            {
                emit("disconnected", new { reason = e.GetType().Name });
            }
            finally
            {
                _socket = null;
                foreach (var pending in _pending.Values) pending.TrySetException(new ProtocolException(ErrorCode.PcOffline, "Not connected."));
            }
            attempt++;
            var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, Math.Min(attempt, 6))) + RandomNumberGenerator.GetInt32(0, 500));
            try { await Task.Delay(delay, cancellation); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, TimeSpan interval, CancellationToken cancellation)
    {
        try
        {
            for (var n = 0; ; n++)
            {
                await Task.Delay(interval, cancellation);
                await SendAsync(socket, new Envelope { V = ProtocolInfo.Version, Id = $"hb-{n}", Kind = EnvelopeKind.Req, Action = Actions.Heartbeat, Payload = ProtocolJson.ToElement(new Ack { Ok = true }) }, cancellation);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task<AgentWelcome> HandshakeAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var challengeEnvelope = await ReceiveAsync(socket, cancellation) ?? throw new IOException("Gateway closed during handshake.");
        if (challengeEnvelope.Action != Actions.Challenge || challengeEnvelope.Payload is null) throw new ProtocolException(ErrorCode.Internal, "Unexpected handshake.");
        var challenge = ProtocolJson.FromElement<AgentChallenge>(challengeEnvelope.Payload.Value);
        if (ProtocolInfo.Version < challenge.MinProtocolVersion || ProtocolInfo.Version > challenge.MaxProtocolVersion)
            throw new ProtocolException(ErrorCode.UnsupportedVersion, "This version of OrbitDrive is no longer supported by the service. Please update.");
        var signature = state.Identity.SignData(Encoding.UTF8.GetBytes($"orbitdrive-agent-auth-v1\n{challenge.Nonce}\n{state.PcId}"), HashAlgorithmName.SHA256);
        await SendAsync(socket, new Envelope
        {
            V = ProtocolInfo.Version, Id = "hello", Kind = EnvelopeKind.Req, Action = Actions.Hello,
            Payload = ProtocolJson.ToElement(new AgentHello
            {
                ProtocolVersion = ProtocolInfo.Version, PcId = state.PcId, PublicKeySpki = state.PublicKeySpki, Signature = Base64Url.EncodeToString(signature),
                Name = pcName, AgentVersion = AgentVersion, Capabilities = ["browse", "range-read", "heartbeat"],
            }),
        }, cancellation);
        var welcome = await ReceiveAsync(socket, cancellation) ?? throw new IOException("Gateway closed during handshake.");
        if (welcome.Error is not null) throw new ProtocolException(welcome.Error.Code, welcome.Error.Message);
        var info = ProtocolJson.FromElement<AgentWelcome>(welcome.Payload ?? throw new JsonException());
        emit("connected", new { pcId = info.PcId, generation = info.Generation.ToString(), ownerBound = info.OwnerBound });
        return info;
    }

    private static async Task<Envelope?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaxMessageBytes) throw new IOException("Oversized or non-text control message.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonSerializer.Deserialize<Envelope>(message.GetBuffer().AsSpan(0, (int)message.Length), ProtocolJson.Options);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, Envelope envelope, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        await _sendLock.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, TimeSpan? lease, CancellationToken cancellation)
    {
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        while (true)
        {
            if (lease is { } limit) silence.CancelAfter(limit);
            Envelope? envelope;
            try { envelope = await ReceiveAsync(socket, silence.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { socket.Abort(); throw new TimeoutException(); }
            if (envelope is null) return;
            if (envelope.V != ProtocolInfo.Version) continue;
            switch (envelope.Kind)
            {
                case EnvelopeKind.Res:
                    if (_pending.TryRemove(envelope.Id, out var pending)) pending.TrySetResult(envelope);
                    break;
                case EnvelopeKind.Evt when envelope.Action == Actions.PairingClaimed && envelope.Payload is not null:
                    var claimed = ProtocolJson.FromElement<PairingClaimedEvent>(envelope.Payload.Value);
                    PendingPairingId = claimed.PairingId;
                    emit("pairing_claimed", new { accountLabel = claimed.AccountLabel, browserName = claimed.BrowserName, confirmationCode = claimed.ConfirmationCode });
                    break;
                case EnvelopeKind.Req:
                    if (!await _requestSlots.WaitAsync(0, cancellation))
                    {
                        await SendAsync(socket, Reply(envelope, error: new ProtocolException(ErrorCode.LimitExceeded, "The PC is busy. Try again shortly.")), cancellation);
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        Envelope reply;
                        try { reply = Reply(envelope, await HandleAsync(envelope, cancellation)); }
                        catch (ProtocolException e) { reply = Reply(envelope, error: e); }
                        catch (JsonException) { reply = Reply(envelope, error: new ProtocolException(ErrorCode.InvalidRequest, "Malformed request.")); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { reply = Reply(envelope, error: new ProtocolException(ErrorCode.Unavailable, "The PC could not read that item.")); }
                        finally { _requestSlots.Release(); }
                        try { await SendAsync(socket, reply, cancellation); } catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                    }, cancellation);
                    break;
            }
        }
    }

    private static Envelope Reply(Envelope request, object? payload = null, ProtocolException? error = null) => new()
    {
        V = ProtocolInfo.Version, Id = request.Id, Kind = EnvelopeKind.Res, Action = request.Action, Error = error?.ToInfo(),
        Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), ProtocolJson.Options),
    };

    /// <summary>Closed set of typed actions. Anything else — including any notion of "run a command" — is rejected.</summary>
    private async Task<object> HandleAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.DeadlineUnixMs is { } deadline && deadline < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new ProtocolException(ErrorCode.Timeout, "Request deadline passed.");
        if (envelope.Action is null || !Actions.All.Contains(envelope.Action)) throw new ProtocolException(ErrorCode.UnknownAction, "Unknown action.");
        if (envelope.Action == Actions.CancelStream)
        {
            var cancel = ProtocolJson.FromElement<CancelStreamRequest>(envelope.Payload ?? throw new JsonException());
            if (_streams.TryGetValue(cancel.StreamId, out var active)) active.Cancel();
            return new Ack { Ok = true };
        }
        if (_paused) throw new ProtocolException(ErrorCode.Unavailable, "Remote access is paused on the PC.");
        var payload = envelope.Payload ?? throw new JsonException();
        switch (envelope.Action)
        {
            case Actions.ListRoots: return _files.ListRoots();
            case Actions.ListDirectory: return _files.ListDirectory(ProtocolJson.FromElement<DirectoryQuery>(payload));
            case Actions.PrepareRead: return await _files.PrepareReadAsync(ProtocolJson.FromElement<PrepareReadRequest>(payload), cancellation);
            case Actions.OpenRead:
                var open = ProtocolJson.FromElement<OpenReadRequest>(payload);
                if (!await _streamSlots.WaitAsync(0, cancellation)) throw new ProtocolException(ErrorCode.LimitExceeded, "The PC is already sending the maximum number of files.");
                ValidatedRead read;
                try { read = _files.OpenRead(open.ReadToken, open.Offset, open.Length); }
                catch { _streamSlots.Release(); throw; }
                _ = UplinkAsync(open, read, cancellation);
                return new Ack { Ok = true };
            default: throw new ProtocolException(ErrorCode.UnknownAction, "Unknown action.");
        }
    }

    private async Task UplinkAsync(OpenReadRequest open, ValidatedRead read, CancellationToken cancellation)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _streams[open.StreamId] = cancel;
        var outcome = "sent_to_relay";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(gateway, $"/v1/agent-streams/{Uri.EscapeDataString(open.StreamId)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", open.UplinkToken);
            request.Content = new FileRangeContent(read, cancel.Token);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            if (response.StatusCode != HttpStatusCode.NoContent) outcome = "interrupted";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { outcome = cancel.IsCancellationRequested ? "cancelled" : "interrupted"; }
        finally
        {
            _streams.TryRemove(open.StreamId, out _);
            read.Dispose();
            _streamSlots.Release();
            emit("stream_finished", new { outcome, bytes = read.Length.ToString() }); // bytes handed to the relay, not "saved on phone"
        }
    }

    private async Task<TResult> RequestAsync<TResult>(string action, object payload, CancellationToken cancellation)
    {
        var socket = _socket ?? throw new ProtocolException(ErrorCode.PcOffline, "Not connected to the OrbitDrive service.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(socket, new Envelope { V = ProtocolInfo.Version, Id = id, Kind = EnvelopeKind.Req, Action = action, Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), ProtocolJson.Options) }, cancellation);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
            if (response.Error is not null) throw new ProtocolException(response.Error.Code, response.Error.Message);
            return ProtocolJson.FromElement<TResult>(response.Payload ?? throw new JsonException());
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task<CreatePairingResult> CreatePairingAsync(CancellationToken cancellation) =>
        RequestAsync<CreatePairingResult>(Actions.CreatePairing, new Ack { Ok = true }, cancellation);

    public async Task<PairingState> ConfirmPairingAsync(bool approve, CancellationToken cancellation)
    {
        var id = PendingPairingId ?? throw new ProtocolException(ErrorCode.Conflict, "Nothing is waiting for approval.");
        PendingPairingId = null;
        return (await RequestAsync<ConfirmPairingResult>(Actions.ConfirmPairing, new ConfirmPairingRequest { PairingId = id, Approve = approve }, cancellation)).State;
    }

    public Task<BrowserDeviceListResponse> ListDevicesAsync(CancellationToken cancellation) =>
        RequestAsync<BrowserDeviceListResponse>(Actions.ListDevices, new Ack { Ok = true }, cancellation);

    public Task RevokeDeviceAsync(string deviceId, CancellationToken cancellation) =>
        RequestAsync<Ack>(Actions.RevokeDevice, new RevokeDeviceRequest { DeviceId = deviceId }, cancellation);

    public void Dispose()
    {
        _http.Dispose();
        _sendLock.Dispose();
        _requestSlots.Dispose();
        _streamSlots.Dispose();
    }
}
