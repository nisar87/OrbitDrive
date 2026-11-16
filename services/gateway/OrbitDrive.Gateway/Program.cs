using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;
using OrbitDrive.Gateway;
using OrbitDrive.Protocol;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("OrbitDrive").Get<GatewayOptions>() ?? new GatewayOptions();
try { options.Validate(builder.Environment); }
catch (InvalidOperationException e)
{
    Console.Error.WriteLine($"FATAL configuration error: {e.Message}");
    return 1;
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 64 * 1024; // JSON endpoints only; the agent uplink lifts this per request
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<DevIdentityVerifier>();
builder.Services.AddSingleton<IIdentityVerifier>(s => s.GetRequiredService<DevIdentityVerifier>());
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.AllowedWebOrigins)
    .WithMethods("GET", "POST", "DELETE", "HEAD")
    .WithHeaders("Authorization", "Content-Type", "X-OrbitDrive-Device", "Range", "If-Range")
    .WithExposedHeaders("Content-Range", "Accept-Ranges", "ETag", "Content-Length", "Content-Disposition")));

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
var hub = app.Services.GetRequiredService<AgentHub>();
var streams = app.Services.GetRequiredService<StreamRegistry>();
var verifier = app.Services.GetRequiredService<IIdentityVerifier>();
var agentTimeout = TimeSpan.FromSeconds(options.AgentRequestTimeoutSeconds);

static int StatusFor(ErrorCode code) => code switch
{
    ErrorCode.Unauthenticated => 401,
    ErrorCode.Forbidden or ErrorCode.AccessDeniedLocal or ErrorCode.LinkNotAllowed => 403,
    ErrorCode.NotFound => 404,
    ErrorCode.InvalidRequest or ErrorCode.UnknownAction => 400,
    ErrorCode.Expired => 410,
    ErrorCode.Conflict or ErrorCode.RevisionChanged => 409,
    ErrorCode.RangeNotSatisfiable => 416,
    ErrorCode.LimitExceeded => 429,
    ErrorCode.UnsupportedVersion => 426,
    ErrorCode.PcOffline or ErrorCode.Unavailable => 503,
    ErrorCode.Timeout => 504,
    _ => 500,
};

static Task WriteJson<T>(HttpContext context, T value, int status = 200)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    return context.Response.WriteAsync(ProtocolJson.Serialize(value));
}

static async Task<T> ReadJson<T>(HttpContext context)
{
    if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        throw new ProtocolException(ErrorCode.InvalidRequest, "Content-Type must be application/json.");
    try { return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, ProtocolJson.Options, context.RequestAborted) ?? throw new JsonException(); }
    catch (JsonException) { throw new ProtocolException(ErrorCode.InvalidRequest, "Malformed request body."); }
}

Identity Authenticate(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    return verifier.Verify(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null);
}

BrowserDeviceRecord AuthorizeBrowser(HttpContext context, string? pcId = null) =>
    store.Authorize(Authenticate(context), context.Request.Headers["X-OrbitDrive-Device"].ToString(), pcId);

// Uniform security headers and typed errors. Bodies/capabilities are never logged.
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try { await next(context); }
    catch (ProtocolException e) when (!context.Response.HasStarted)
    {
        await WriteJson(context, new ErrorResponse { Error = e.ToInfo() }, StatusFor(e.Code));
    }
    catch (BadHttpRequestException e) when (!context.Response.HasStarted)
    {
        await WriteJson(context, new ErrorResponse { Error = new ErrorInfo { Code = ErrorCode.InvalidRequest, Message = "Request rejected." } }, e.StatusCode);
    }
});
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/v1/meta", (HttpContext context) => WriteJson(context, new GatewayMeta
{
    ProtocolVersion = ProtocolInfo.Version, MinProtocolVersion = ProtocolInfo.MinVersion,
    RelayTerminatesTls = true, EndToEndEncrypted = false, Environment = app.Environment.EnvironmentName,
}));

if (options.DevIdentity.Enabled)
{
    // Development/test sign-in only. GatewayOptions.Validate guarantees this is never mapped in Production.
    app.MapPost("/v1/dev/sign-in", async (HttpContext context, DevIdentityVerifier dev) =>
    {
        var request = await ReadJson<DevSignInRequest>(context);
        if (request.Email.Length is < 3 or > 200 || !request.Email.Contains('@')) throw new ProtocolException(ErrorCode.InvalidRequest, "Enter an email address.");
        var token = dev.Issue(request.Email, TimeSpan.FromHours(8));
        await WriteJson(context, new DevSignInResponse { IdToken = token, Uid = DevIdentityVerifier.UidFor(token) });
    });
}

// Outbound agent control channel. Browsers always send Origin; agents never do.
app.Map("/v1/agent-control", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin"))
        throw new ProtocolException(ErrorCode.InvalidRequest, "Agent WebSocket connections only.");
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.RunAsync(socket, context.RequestAborted);
});

app.MapPost("/v1/pairing-requests/{id}/claim", async (HttpContext context, string id) =>
{
    var identity = Authenticate(context);
    var request = await ReadJson<ClaimPairingRequest>(context);
    if (request.BrowserName.Length is < 1 or > 64) throw new ProtocolException(ErrorCode.InvalidRequest, "Give this browser a short name.");
    var (pairing, device, deviceSecret, pc) = store.ClaimPairing(id.ToUpperInvariant(), request.Secret.ToUpperInvariant(), identity, request.BrowserName);
    var code = (BitConverter.ToUInt32(Secrets.Hash($"{pairing.Id}|{identity.Uid}|{device.Id}|{pc.PcId}")) % 1_000_000).ToString("D6");
    try
    {
        await hub.Get(pc.PcId).SendAsync(new Envelope
        {
            V = ProtocolInfo.Version, Id = Secrets.Token(12), Kind = EnvelopeKind.Evt, Action = Actions.PairingClaimed,
            Payload = ProtocolJson.ToElement(new PairingClaimedEvent { PairingId = pairing.Id, AccountLabel = identity.Email, BrowserName = device.Name, ConfirmationCode = code }),
        }, context.RequestAborted);
    }
    catch (Exception e) when (e is ProtocolException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
    {
        store.AbandonClaim(pairing.Id);
        throw new ProtocolException(ErrorCode.PcOffline, "The PC went offline before it could confirm. Create a new pairing code.");
    }
    await WriteJson(context, new ClaimPairingResponse { PairingId = pairing.Id, State = pairing.State, ConfirmationCode = code, PcName = pc.Name, BrowserDeviceId = device.Id, DeviceSecret = deviceSecret });
});

app.MapGet("/v1/pairing-requests/{id}", (HttpContext context, string id) =>
    WriteJson(context, store.PairingStatus(id.ToUpperInvariant(), Authenticate(context))));

app.MapGet("/v1/pcs", (HttpContext context) =>
{
    var device = AuthorizeBrowser(context);
    return WriteJson(context, new PcListResponse
    {
        Pcs = [.. store.PcsFor(device).Select(p => new PcSummary { PcId = p.PcId, Name = p.Name, Online = hub.IsOnline(p.PcId), LastSeenUnixMs = p.LastSeen.ToUnixTimeMilliseconds(), AgentVersion = p.AgentVersion })],
    });
});

app.MapGet("/v1/pcs/{pcId}/roots", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<RootListResponse>(Actions.ListRoots, new Ack { Ok = true }, agentTimeout, context.RequestAborted));
});

app.MapPost("/v1/pcs/{pcId}/directory-queries", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    var query = await ReadJson<DirectoryQuery>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<DirectoryPage>(Actions.ListDirectory, query, agentTimeout, context.RequestAborted));
});

app.MapPost("/v1/download-sessions", async (HttpContext context) =>
{
    var request = await ReadJson<CreateDownloadSessionRequest>(context);
    var device = AuthorizeBrowser(context, request.PcId);
    var prepared = await hub.Get(request.PcId).RequestAsync<PrepareReadResult>(Actions.PrepareRead, new PrepareReadRequest { EntryId = request.EntryId },
        TimeSpan.FromSeconds(options.PrepareTimeoutSeconds), context.RequestAborted);
    if (prepared.Size < 0 || !prepared.Revision.StartsWith("sha256-", StringComparison.Ordinal) || prepared.Revision.Length != 71 || prepared.Name.Length is 0 or > 255)
        throw new ProtocolException(ErrorCode.Internal, "The PC sent an answer this service does not understand.");
    var expires = DateTimeOffset.UtcNow.AddSeconds(options.DownloadSessionTtlSeconds);
    var capability = store.AddDownload(new DownloadSessionRecord
    {
        OwnerUid = device.OwnerUid, BrowserDeviceId = device.Id, PcId = request.PcId, Name = prepared.Name, Size = prepared.Size,
        Revision = prepared.Revision, ReadToken = prepared.ReadToken, ExpiresAt = expires,
    });
    await WriteJson(context, new DownloadSession { Capability = capability, UrlPath = $"/v1/downloads/{capability}", Name = prepared.Name, Size = prepared.Size, Revision = prepared.Revision, ExpiresAtUnixMs = expires.ToUnixTimeMilliseconds() }, 201);
});

// Bearer download capability: scoped to one file revision and one device grant, reusable for HEAD/ranges until expiry or revocation.
app.MapMethods("/v1/downloads/{capability}", ["GET", "HEAD"], async (HttpContext context, string capability) =>
{
    var session = store.ResolveDownload(capability);
    var response = context.Response;
    var etag = $"\"{session.Revision}\"";
    var range = RangeParser.Parse(context.Request.Headers.Range, session.Size);
    var ifRange = context.Request.Headers.IfRange.ToString();
    if (ifRange.Length > 0 && ifRange != etag) range = new ByteRange(0, session.Size, false); // validator mismatch: never splice revisions
    response.Headers.AcceptRanges = "bytes";
    response.Headers.ETag = etag;
    if (range is null)
    {
        response.Headers.ContentRange = $"bytes */{session.Size}";
        throw new ProtocolException(ErrorCode.RangeNotSatisfiable, "Requested range is outside the file.");
    }
    var selected = range.Value;

    void WriteHeaders()
    {
        response.StatusCode = selected.Partial ? 206 : 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength = selected.Length;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(session.Name); // ASCII fallback + RFC 5987 filename*
        response.Headers.ContentDisposition = disposition.ToString();
        if (selected.Partial) response.Headers.ContentRange = $"bytes {selected.Start}-{selected.Start + selected.Length - 1}/{session.Size}";
    }

    if (HttpMethods.IsHead(context.Request.Method)) { WriteHeaders(); return; }

    var (stream, uplinkToken) = streams.Create(session.OwnerUid);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Revoked.Token);
    var success = false;
    try
    {
        var agent = hub.Get(session.PcId);
        await agent.RequestAsync<Ack>(Actions.OpenRead, new OpenReadRequest { ReadToken = session.ReadToken, Offset = selected.Start, Length = selected.Length, StreamId = stream.Id, UplinkToken = uplinkToken }, agentTimeout, cancel.Token);
        HttpContext uplink;
        try { uplink = await stream.Arrival.Task.WaitAsync(agentTimeout, cancel.Token); }
        catch (TimeoutException) { throw new ProtocolException(ErrorCode.Timeout, "The PC did not start sending in time."); }
        context.Features.Get<IHttpMinResponseDataRateFeature>()?.MinDataRate = null; // slow phones are throttled, not dropped
        WriteHeaders();
        await StreamRegistry.RelayAsync(uplink.Request.Body, response.Body, selected.Length, cancel.Token);
        await response.Body.FlushAsync(cancel.Token);
        success = true;
    }
    catch (Exception e) when (response.HasStarted && e is not ProtocolException)
    {
        context.Abort(); // never let a truncated body look like a completed download
    }
    catch (OperationCanceledException) when (session.Revoked.IsCancellationRequested)
    {
        throw new ProtocolException(ErrorCode.NotFound, "This download link is not valid.");
    }
    finally
    {
        streams.Remove(stream, success);
        if (!success && hub.IsOnline(session.PcId))
            _ = hub.Get(session.PcId).RequestAsync<Ack>(Actions.CancelStream, new CancelStreamRequest { StreamId = stream.Id }, TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
});

// One-use agent uplink for exactly one pending browser download on this instance.
app.MapPost("/v1/agent-streams/{streamId}", async (HttpContext context, string streamId) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var stream = streams.Find(streamId);
    if (stream is null || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !Secrets.Matches(stream.UplinkTokenHash, header[7..]) ||
        Interlocked.Exchange(ref stream.Claimed, 1) != 0)
        throw new ProtocolException(ErrorCode.Forbidden, "Stream authorisation rejected.");
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
    context.Features.Get<IHttpMinRequestBodyDataRateFeature>()?.MinDataRate = null;
    stream.Arrival.TrySetResult(context);
    var delivered = await stream.Done.Task.WaitAsync(context.RequestAborted);
    if (!delivered) { context.Abort(); return; }
    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
});

app.MapGet("/v1/browser-devices", (HttpContext context) =>
{
    var current = AuthorizeBrowser(context);
    return WriteJson(context, new BrowserDeviceListResponse
    {
        Devices = [.. store.DevicesForOwner(current.OwnerUid).Select(d => new BrowserDeviceSummary { DeviceId = d.Id, Name = d.Name, Current = d.Id == current.Id, PcIds = [.. d.PcIds] })],
    });
});

app.MapDelete("/v1/pcs/{pcId}", (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    store.RemovePc(pcId);
    return WriteJson(context, new Ack { Ok = true });
});

app.MapDelete("/v1/browser-devices/{deviceId}", (HttpContext context, string deviceId) =>
{
    var device = AuthorizeBrowser(context);
    store.RevokeDevice(Authenticate(context), deviceId == "current" ? device.Id : deviceId);
    return WriteJson(context, new Ack { Ok = true });
});

app.Run();
return 0;

public partial class Program;
