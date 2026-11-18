using System.Globalization;
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
builder.Services.AddSingleton<Invitations>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<DevIdentityVerifier>();
builder.Services.AddHttpClient();
if (options.DevIdentity.Enabled) builder.Services.AddSingleton<IIdentityVerifier>(s => s.GetRequiredService<DevIdentityVerifier>());
else
{
    builder.Services.AddSingleton<FirebaseIdentityVerifier>();
    builder.Services.AddSingleton<IIdentityVerifier>(s => s.GetRequiredService<FirebaseIdentityVerifier>());
    builder.Services.AddHostedService(s => s.GetRequiredService<FirebaseIdentityVerifier>());
}
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.AllowedWebOrigins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD")
    .WithHeaders("Authorization", "Content-Type", "X-OrbitDrive-Device", "X-Chunk-Sha256", "Range", "If-Range")
    .WithExposedHeaders("Content-Range", "Accept-Ranges", "ETag", "Content-Length", "Content-Disposition", "Retry-After")));

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
var limiter = app.Services.GetRequiredService<RateLimiter>();
var invitations = app.Services.GetRequiredService<Invitations>();
var hub = app.Services.GetRequiredService<AgentHub>();
app.Lifetime.ApplicationStopping.Register(hub.Shutdown);
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
    ErrorCode.Conflict or ErrorCode.RevisionChanged or ErrorCode.NameExists => 409,
    ErrorCode.TooLarge => 413,
    ErrorCode.ChecksumMismatch => 422,
    ErrorCode.DiskFull => 507,
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

// Refuses with 429 + Retry-After once a key has used up its attempts for the current minute.
void Throttle(HttpContext context, string key, int perMinute, string message)
{
    var wait = limiter.Hit(key, perMinute);
    if (wait == 0) return;
    context.Response.Headers.RetryAfter = wait.ToString();
    throw new ProtocolException(ErrorCode.LimitExceeded, message);
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
var gatewayVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/v1/meta", (HttpContext context) => WriteJson(context, new GatewayMeta
{
    Version = gatewayVersion, // deployments are verified against this, not against "the process started"
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
    // Guessing defence in depth: each code dies after 5 wrong secrets (Store), and every attempt - right or wrong -
    // is also counted per client address and per account, so cycling through codes or accounts does not help.
    const string slowDown = "Too many pairing attempts. Wait a minute, then create a new code on the PC.";
    Throttle(context, "claim-ip:" + ClientAddress.Of(context, options), options.PairingClaimsPerMinute, slowDown);
    var identity = Authenticate(context);
    Throttle(context, "claim-uid:" + identity.Uid, options.PairingClaimsPerMinute, slowDown);
    var request = await ReadJson<ClaimPairingRequest>(context);
    // A browser that is already approved (it proves it with its own device secret) is paired with this PC as the SAME device.
    BrowserDeviceRecord? existing = null;
    if (context.Request.Headers["X-OrbitDrive-Device"].ToString() is { Length: > 0 } header)
    {
        try { existing = store.Authorize(identity, header); } catch (ProtocolException) { /* an unknown or revoked device simply pairs as a new one */ }
    }
    var (pairing, device, deviceSecret, pc) = store.ClaimPairing(id.ToUpperInvariant(), request.Secret.ToUpperInvariant(), identity, DeviceNames.Clean(request.BrowserName), existing);
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
{
    var identity = Authenticate(context);
    Throttle(context, "poll-uid:" + identity.Uid, options.PairingPollsPerMinute, "Checking too often. Wait a moment.");
    return WriteJson(context, store.PairingStatus(id.ToUpperInvariant(), identity));
});

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

// A selection of many items carries many sealed identifiers; these two routes accept a larger body than other JSON routes (still under the 256 KiB control-message cap).
static void AllowLargerBody(HttpContext context) => context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 192 * 1024;

// ----- D8: archives, search and entry checks. The PC does the work; the gateway only authorises the browser for that PC. -----
app.MapPost("/v1/pcs/{pcId}/archives", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "archive:" + device.Id, 20, "Too many archive requests. Wait a moment.");
    AllowLargerBody(context);
    var request = await ReadJson<ArchiveRequest>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<ArchiveRef>(Actions.StartArchive, request, agentTimeout, context.RequestAborted), 202);
});
app.MapPost("/v1/pcs/{pcId}/archive-status", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<ArchiveStatus>(Actions.ArchiveStatus, await ReadJson<ArchiveRef>(context), agentTimeout, context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/archive-cancel", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<Ack>(Actions.CancelArchive, await ReadJson<ArchiveRef>(context), agentTimeout, context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/searches", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "search:" + device.Id, 60, "Too many searches. Wait a moment.");
    var request = await ReadJson<SearchRequest>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<SearchResult>(Actions.Search, request, agentTimeout, context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/search-cancel", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<Ack>(Actions.CancelSearch, await ReadJson<CancelSearchRequest>(context), agentTimeout, context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/entry-checks", async (HttpContext context, string pcId) =>
{
    AuthorizeBrowser(context, pcId);
    AllowLargerBody(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<CheckEntriesResult>(Actions.CheckEntries, await ReadJson<CheckEntriesRequest>(context), agentTimeout, context.RequestAborted));
});

// ----- D9: uploads into a folder the owner enabled on the PC. The browser names a folder it was shown and a file name; the PC decides everything else. -----
// The relay authorises the browser for this PC and stamps its own device id on every request (a browser cannot name another device's upload).
app.MapPost("/v1/pcs/{pcId}/uploads", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "upload-start:" + device.Id, options.UploadStartsPerMinute, "Too many uploads were started. Wait a moment.");
    var request = await ReadJson<StartUploadRequest>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<UploadStarted>(Actions.StartUpload, request with { DeviceId = device.Id }, agentTimeout, context.RequestAborted), 201);
});
app.MapPost("/v1/pcs/{pcId}/upload-status", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "upload-status:" + device.Id, 600, "Too many requests. Wait a moment.");
    var request = await ReadJson<UploadRef>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<UploadStatus>(Actions.UploadStatus, request with { DeviceId = device.Id }, agentTimeout, context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/upload-finish", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "upload-finish:" + device.Id, 300, "Too many requests. Wait a moment.");
    var request = await ReadJson<UploadRef>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<UploadStatus>(Actions.FinishUpload, request with { DeviceId = device.Id }, TimeSpan.FromSeconds(options.PrepareTimeoutSeconds), context.RequestAborted));
});
app.MapPost("/v1/pcs/{pcId}/upload-cancel", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    var request = await ReadJson<UploadRef>(context);
    await WriteJson(context, await hub.Get(pcId).RequestAsync<Ack>(Actions.CancelUpload, request with { DeviceId = device.Id }, agentTimeout, context.RequestAborted));
});

// One piece of an upload: the browser's request body is carried to the PC through a one-use stream the PC dials out to fetch. The body is never
// buffered or model-bound here; the PC verifies the SHA-256 and the position, and only its answer decides what the browser is told.
app.MapPut("/v1/pcs/{pcId}/uploads/{uploadId}/chunk", async (HttpContext context, string pcId, string uploadId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "upload-chunk:" + device.Id, 1200, "Too many pieces were sent. Wait a moment.");
    var query = context.Request.Query["offset"].ToString();
    var checksum = context.Request.Headers["X-Chunk-Sha256"].ToString();
    var length = context.Request.ContentLength ?? -1;
    if (uploadId.Length != 32 || !uploadId.All(char.IsAsciiHexDigit) || query.Length is 0 or > 19 || !query.All(char.IsAsciiDigit) ||
        !long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || checksum.Length != 64 || !checksum.All(char.IsAsciiHexDigit))
        throw new ProtocolException(ErrorCode.InvalidRequest, "Malformed upload piece.");
    if (length <= 0 || length > options.MaxUploadChunkBytes)
        throw new ProtocolException(length > options.MaxUploadChunkBytes ? ErrorCode.TooLarge : ErrorCode.InvalidRequest, "An upload piece must declare a size within the allowed limit.");
    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = length;
    context.Features.Get<IHttpMinRequestBodyDataRateFeature>()?.MinDataRate = null;          // slow phones are throttled, not dropped

    var (stream, uplinkToken) = streams.Create(device.OwnerUid);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var success = false;
    Task<WriteChunkResult>? agentTask = null;
    try
    {
        agentTask = hub.Get(pcId).RequestAsync<WriteChunkResult>(Actions.WriteChunk, new WriteChunkRequest
        {
            UploadId = uploadId, DeviceId = device.Id, Offset = offset, Length = (int)length, Sha256 = checksum, StreamId = stream.Id, UplinkToken = uplinkToken,
        }, TimeSpan.FromSeconds(options.UploadChunkTimeoutSeconds), cancel.Token);
        var arrival = stream.Arrival.Task;
        var first = await Task.WhenAny(arrival, agentTask).WaitAsync(TimeSpan.FromSeconds(options.AgentRequestTimeoutSeconds), cancel.Token);
        if (first == agentTask)
        {
            // The PC answered without asking for the data (wrong position, a rule it enforces, an error). That answer is the result.
            success = true;
            await WriteJson(context, await agentTask);
            return;
        }
        var agentContext = await arrival;
        var downlink = agentContext.Response;
        downlink.StatusCode = 200;
        downlink.ContentLength = length;
        downlink.ContentType = "application/octet-stream";
        await StreamRegistry.RelayAsync(context.Request.Body, downlink.Body, length, cancel.Token);
        await downlink.Body.FlushAsync(cancel.Token);
        success = true;
        streams.Remove(stream, true);                                                       // lets the PC's fetch finish
        await WriteJson(context, await agentTask.WaitAsync(TimeSpan.FromSeconds(options.UploadChunkTimeoutSeconds), cancel.Token));
    }
    catch (TimeoutException) { throw new ProtocolException(ErrorCode.Timeout, "The PC did not take this piece in time."); }
    finally
    {
        streams.Remove(stream, success);
        if (!success && agentTask is not null)
        {
            _ = agentTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);       // the abandoned request's failure is expected
            if (hub.IsOnline(pcId))
                _ = hub.Get(pcId).RequestAsync<Ack>(Actions.CancelStream, new CancelStreamRequest { StreamId = stream.Id }, TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }
});

// One-use fetch by the PC of exactly one pending upload piece (the mirror image of the download uplink below).
app.MapGet("/v1/agent-downlinks/{streamId}", async (HttpContext context, string streamId) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    var stream = streams.Find(streamId);
    if (stream is null || !header.StartsWith("Bearer ", StringComparison.Ordinal) || !Secrets.Matches(stream.UplinkTokenHash, header[7..]) ||
        Interlocked.Exchange(ref stream.Claimed, 1) != 0)
        throw new ProtocolException(ErrorCode.Forbidden, "Stream authorisation rejected.");
    stream.Arrival.TrySetResult(context);
    var delivered = await stream.Done.Task.WaitAsync(context.RequestAborted);
    if (!delivered) context.Abort();                                                        // a truncated piece must look truncated to the PC
});

/// <summary>Tells a PC to throw away anything a browser still had in flight. Best effort: the PC also refuses every later request from that browser.</summary>
void DiscardUploads(string deviceId, IEnumerable<string> pcIds)
{
    foreach (var pcId in pcIds)
        if (hub.IsOnline(pcId))
            _ = hub.Get(pcId).RequestAsync<Ack>(Actions.CancelDeviceUploads, new CancelDeviceUploadsRequest { DeviceId = deviceId }, TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
}

// What a browser may ask the PC for besides the original bytes. Anything else is refused before it reaches the PC.
static string? NormalizeVariant(string? variant) => variant switch
{
    null or "" or "original" => null,
    "preview" or "thumb:128" or "thumb:256" or "thumb:512" => variant,
    _ => throw new ProtocolException(ErrorCode.InvalidRequest, "That kind of preview is not offered."),
};

// Types the gateway will ever mark as displayable inline. The PC judges the type from the file's bytes; this list is the second lock.
var InlineTypes = new HashSet<string>(StringComparer.Ordinal)
{
    "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "application/pdf",
    "audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/flac", "audio/webm", "video/mp4", "video/webm", "video/ogg", "text/plain; charset=utf-8",
};

async Task<DownloadSession> StartDownload(BrowserDeviceRecord device, string pcId, string entryId, string? requestedVariant, CancellationToken cancellation)
{
    // "archive": the entry is a finished archive of this PC. It is a normal download (it shows in Transfers, never inline).
    var isArchive = requestedVariant == "archive";
    var variant = isArchive ? null : NormalizeVariant(requestedVariant);
    var prepared = await hub.Get(pcId).RequestAsync<PrepareReadResult>(Actions.PrepareRead, new PrepareReadRequest { EntryId = entryId, Variant = isArchive ? "archive" : variant },
        TimeSpan.FromSeconds(options.PrepareTimeoutSeconds), cancellation);
    if (prepared.Size < 0 || !prepared.Revision.StartsWith("sha256-", StringComparison.Ordinal) || prepared.Revision.Length != 71 || prepared.Name.Length is 0 or > 255)
        throw new ProtocolException(ErrorCode.Internal, "The PC sent an answer this service does not understand.");
    if (variant is not null && (prepared.ContentType is null || !InlineTypes.Contains(prepared.ContentType)))
        throw new ProtocolException(ErrorCode.InvalidRequest, "This kind of file has no preview. Download it instead.");
    var expires = DateTimeOffset.UtcNow.AddSeconds(variant is not null && variant.StartsWith("thumb:", StringComparison.Ordinal) ? options.PreviewSessionTtlSeconds : options.DownloadSessionTtlSeconds);
    var record = new DownloadSessionRecord
    {
        OwnerUid = device.OwnerUid, BrowserDeviceId = device.Id, PcId = pcId, Name = prepared.Name, Size = prepared.Size,
        Revision = prepared.Revision, ReadToken = prepared.ReadToken, Variant = variant, ContentType = variant is null ? null : prepared.ContentType, ExpiresAt = expires, CreatedAt = DateTimeOffset.UtcNow,
    };
    var capability = store.AddDownload(record);
    return new DownloadSession { SessionId = record.Id, Capability = capability, UrlPath = $"/v1/downloads/{capability}", Name = prepared.Name, Size = prepared.Size, Revision = prepared.Revision, Variant = variant, ContentType = variant is null ? null : prepared.ContentType, ExpiresAtUnixMs = expires.ToUnixTimeMilliseconds() };
}

app.MapPost("/v1/download-sessions", async (HttpContext context) =>
{
    var request = await ReadJson<CreateDownloadSessionRequest>(context);
    var device = AuthorizeBrowser(context, request.PcId);
    await WriteJson(context, await StartDownload(device, request.PcId, request.EntryId, request.Variant, context.RequestAborted), 201);
});

// "Send to my phone": the Inbox of this browser (never another browser's, never the PC's grant), and its decisions.
app.MapGet("/v1/invitations", (HttpContext context) =>
{
    var device = AuthorizeBrowser(context);
    return WriteJson(context, new InvitationList { Invitations = [.. invitations.ForReceiver(device.Id).Where(i => device.PcIds.Contains(i.PcId)).Select(i => invitations.Summarize(i, hub.IsOnline(i.PcId)))] });
});

app.MapPost("/v1/invitations/{id}/accept", async (HttpContext context, string id) =>
{
    var device = AuthorizeBrowser(context);
    var item = invitations.ForReceiver(device.Id, id);
    if (item.State is InvitationState.Declined or InvitationState.Cancelled or InvitationState.Expired)
        throw new ProtocolException(item.State == InvitationState.Expired ? ErrorCode.Expired : ErrorCode.Conflict,
            item.Failure ?? (item.State == InvitationState.Expired ? "This offer has expired. Ask for the file to be sent again." : "This offer is no longer available."));
    // Offline PC: nothing is copied anywhere, so the offer simply waits (503 pc_offline) and stays in the Inbox.
    AuthorizeBrowser(context, item.PcId);   // the grant for that PC must still stand
    try
    {
        var session = await StartDownload(device, item.PcId, item.Grant, null, context.RequestAborted);
        invitations.Accepted(item);
        await WriteJson(context, session, 201);
    }
    catch (ProtocolException e) when (e.Code is ErrorCode.RevisionChanged or ErrorCode.NotFound or ErrorCode.LinkNotAllowed or ErrorCode.AccessDeniedLocal or ErrorCode.Expired)
    {
        invitations.Failed(item, e.Code == ErrorCode.RevisionChanged
            ? "The file changed on the PC after it was offered. Ask for it to be sent again."
            : e.Code == ErrorCode.Expired ? "The PC's permission for this file ran out. Ask for it to be sent again." : "The file is no longer available on the PC. Ask for it to be sent again.");
        throw;
    }
});

app.MapPost("/v1/invitations/{id}/decline", (HttpContext context, string id) =>
{
    invitations.Decline(AuthorizeBrowser(context).Id, id);
    return WriteJson(context, new Ack { Ok = true });
});

// What the relay observed for this browser's downloads (never the capability itself), and cancelling one.
app.MapGet("/v1/download-sessions", (HttpContext context) =>
    WriteJson(context, new DownloadSessionList { Sessions = store.ListDownloads(AuthorizeBrowser(context).Id) }));

app.MapDelete("/v1/download-sessions/{sessionId}", (HttpContext context, string sessionId) =>
{
    store.CancelDownload(AuthorizeBrowser(context).Id, sessionId);
    return WriteJson(context, new Ack { Ok = true });
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
        response.ContentLength = selected.Length;
        // An ordinary download is always an attachment of opaque bytes. A preview is shown inline only when the PC judged the bytes to be
        // one of the closed list of passive types; even then the response may not run or load anything of its own.
        var inline = session.IsPreview && session.ContentType is not null && InlineTypes.Contains(session.ContentType);
        response.ContentType = inline ? session.ContentType : "application/octet-stream";
        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(session.Name); // ASCII fallback + RFC 5987 filename*
        response.Headers.ContentDisposition = disposition.ToString();
        if (inline)
        {
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";   // shown by the app's origin; nothing else gets credentials from it
        }
        if (selected.Partial) response.Headers.ContentRange = $"bytes {selected.Start}-{selected.Start + selected.Length - 1}/{session.Size}";
    }

    if (HttpMethods.IsHead(context.Request.Method)) { WriteHeaders(); return; }

    var (stream, uplinkToken) = streams.Create(session.OwnerUid, session.IsPreview);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Revoked.Token);
    var success = false;
    long relayed = 0;
    session.StreamStarted();
    try
    {
        var agent = hub.Get(session.PcId);
        await agent.RequestAsync<Ack>(Actions.OpenRead, new OpenReadRequest { ReadToken = session.ReadToken, Offset = selected.Start, Length = selected.Length, StreamId = stream.Id, UplinkToken = uplinkToken }, agentTimeout, cancel.Token);
        HttpContext uplink;
        try { uplink = await stream.Arrival.Task.WaitAsync(agentTimeout, cancel.Token); }
        catch (TimeoutException) { throw new ProtocolException(ErrorCode.Timeout, "The PC did not start sending in time."); }
        context.Features.Get<IHttpMinResponseDataRateFeature>()?.MinDataRate = null; // slow phones are throttled, not dropped
        WriteHeaders();
        await StreamRegistry.RelayAsync(uplink.Request.Body, response.Body, selected.Length, cancel.Token, count => relayed += count);
        await response.Body.FlushAsync(cancel.Token);
        success = true;
        if (session.Size == 0) session.EmptyFileDelivered();
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
        session.StreamEnded(selected.Start, relayed);
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
    var removing = AuthorizeBrowser(context, pcId);
    DiscardUploads(removing.Id, [pcId]);
    store.RemovePc(pcId);
    return WriteJson(context, new Ack { Ok = true });
});

app.MapPatch("/v1/browser-devices/{deviceId}", async (HttpContext context, string deviceId) =>
{
    var device = AuthorizeBrowser(context);
    var request = await ReadJson<RenameDeviceRequest>(context);
    store.RenameDevice(Authenticate(context), deviceId == "current" ? device.Id : deviceId, DeviceNames.Clean(request.Name));
    await WriteJson(context, new Ack { Ok = true });
});

app.MapDelete("/v1/browser-devices/{deviceId}", (HttpContext context, string deviceId) =>
{
    var device = AuthorizeBrowser(context);
    var targetId = deviceId == "current" ? device.Id : deviceId;
    var target = store.DevicesForOwner(device.OwnerUid).FirstOrDefault(d => d.Id == targetId);
    store.RevokeDevice(Authenticate(context), targetId);
    if (target is not null) DiscardUploads(target.Id, target.PcIds);
    return WriteJson(context, new Ack { Ok = true });
});

app.Run();
return 0;

public partial class Program;
