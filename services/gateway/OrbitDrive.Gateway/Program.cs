using System.Net.WebSockets;
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

// One relay per data directory: a second process on the same directory would overwrite the first one's files (F-019).
DataDirectoryLock? dataLock = null;
if (options.DataDirectory.Length > 0)
{
    try { dataLock = DataDirectoryLock.Acquire(options.DataDirectory); }
    catch (InvalidOperationException e) { Console.Error.WriteLine($"FATAL: {e.Message}"); return 1; }
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 64 * 1024; // JSON endpoints only; the agent uplink lifts this per request
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
if (options.Firestore.ProjectId.Length > 0)
{
    // Durable control metadata in Firestore (write-behind; flushed on shutdown). The file backend is used otherwise.
    builder.Services.AddSingleton(sp => new FirestoreSnapshotBackend(FirestoreSnapshotBackend.Connect(options.Firestore.ProjectId), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Firestore"),
        writerLease: TimeSpan.FromSeconds(60), onLeaseLost: () => sp.GetRequiredService<IHostApplicationLifetime>().StopApplication(),
        legacy: options.Firestore.ImportFromDataDirectory.Length > 0 && File.Exists(Path.Join(options.Firestore.ImportFromDataDirectory, "registrations.json")) ? new FileSnapshotBackend(options.Firestore.ImportFromDataDirectory, sp.GetRequiredService<TimeProvider>()) : null));
    builder.Services.AddSingleton<ISnapshotBackend>(sp => sp.GetRequiredService<FirestoreSnapshotBackend>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FirestoreSnapshotBackend>());
}
builder.Services.AddSingleton<Store>();
if (options.Firestore.ProjectId.Length > 0)
{
    // The audit trail, operator settings and "send to phone" offers are kept in the database too (they were lost at every restart), in their own collections.
    builder.Services.AddSingleton(sp => new AdminDocuments(new FirestoreBillingPersistence(FirestoreSnapshotBackend.Connect(options.Firestore.ProjectId), "admin")));
    builder.Services.AddSingleton(sp => new InvitationDocuments(new FirestoreBillingPersistence(FirestoreSnapshotBackend.Connect(options.Firestore.ProjectId), "invitations")));
}
builder.Services.AddSingleton<IBillingPersistence>(sp => options.Firestore.ProjectId.Length > 0
    ? new ImportOnceBillingPersistence(new FirestoreBillingPersistence(FirestoreSnapshotBackend.Connect(options.Firestore.ProjectId)),
        () => options.Firestore.ImportFromDataDirectory.Length > 0 ? FileBillingPersistence.ReadOnly(options.Firestore.ImportFromDataDirectory) : [], sp.GetRequiredService<ILoggerFactory>().CreateLogger("Billing"))
    : options.DataDirectory.Length > 0 ? new FileBillingPersistence(options.DataDirectory, sp.GetRequiredService<TimeProvider>()) : new MemoryBillingPersistence());
builder.Services.AddSingleton(sp => new BillingStore(sp.GetRequiredService<IBillingPersistence>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Billing"), TimeSpan.FromSeconds(2), options.PlanLimitsEnabled));
builder.Services.AddSingleton<Invitations>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSingleton<AdminLog>();
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<DevIdentityVerifier>();
builder.Services.AddHttpClient();
if (options.DevIdentity.Enabled) builder.Services.AddSingleton<IIdentityVerifier>(s => s.GetRequiredService<DevIdentityVerifier>());
else
{
    if (options.Firebase.CheckRevocation)
    {
        builder.Services.AddSingleton<IAccountStatusSource, IdentityToolkitAccountStatusSource>();
        builder.Services.AddSingleton(sp => new RevocationGate(sp.GetRequiredService<IAccountStatusSource>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<RevocationGate>>(), TimeSpan.FromSeconds(options.Firebase.RevocationCacheSeconds)));
    }
    builder.Services.AddSingleton<FirebaseIdentityVerifier>();
    builder.Services.AddSingleton<IIdentityVerifier>(s => s.GetRequiredService<FirebaseIdentityVerifier>());
    builder.Services.AddHostedService(s => s.GetRequiredService<FirebaseIdentityVerifier>());
}
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.AllowedWebOrigins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD")
    .WithHeaders("Authorization", "Content-Type", "X-OrbitDrive-Device", "X-Chunk-Sha256", "Range", "If-Range", IdempotencyStore.Header)
    .WithExposedHeaders("X-OrbitDrive-Device-Rotated", "Content-Range", "Accept-Ranges", "ETag", "Content-Length", "Content-Disposition", "Retry-After", RequestLogging.Header, IdempotencyStore.ReplayHeader)));

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
var limiter = app.Services.GetRequiredService<RateLimiter>();
var idempotency = app.Services.GetRequiredService<IdempotencyStore>();
var maintenance = new MaintenanceMode(options);
var invitations = app.Services.GetRequiredService<Invitations>();
var metrics = app.Services.GetRequiredService<Metrics>();
var billing = app.Services.GetRequiredService<BillingStore>();
var countedUploads = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
var adminLog = app.Services.GetRequiredService<AdminLog>();
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

// Who is asking. A verified sign-in also refreshes what the operators see of the account (email and display name from the token; saved in the background, never in a URL or a log).
// Account deletion passes noteAccount: false, so the request that erases the account cannot write the email again.
Identity Authenticate(HttpContext context, bool noteAccount = true)
{
    var header = context.Request.Headers.Authorization.ToString();
    var identity = verifier.Verify(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null);
    if (noteAccount) billing.NoteAccount(identity.Uid, identity.Email, identity.Name);
    return identity;
}

// Refuses with 429 + Retry-After once a key has used up its attempts for the current minute.
void Throttle(HttpContext context, string key, int perMinute, string message, bool anonymous = false)
{
    var wait = limiter.Hit(key, perMinute, anonymous);
    if (wait == 0) return;
    context.Response.Headers.RetryAfter = wait.ToString();
    throw new ProtocolException(ErrorCode.LimitExceeded, message);
}

BrowserDeviceRecord AuthorizeBrowser(HttpContext context, string? pcId = null)
{
    var header = context.Request.Headers["X-OrbitDrive-Device"].ToString();
    var device = store.Authorize(Authenticate(context), header, pcId);
    // A browser's secret is replaced on a schedule: the new one travels once, back to the browser that just proved it holds the current one.
    if (header.IndexOf('.') is > 0 and var dot && store.RotateIfDue(device, header[(dot + 1)..]) is { } fresh)
        context.Response.Headers["X-OrbitDrive-Device-Rotated"] = $"{device.Id}.{fresh}";
    return device;
}

// A retried "start work" request with the same Idempotency-Key returns the first answer instead of starting the work again (see IdempotencyStore).
async Task RunIdempotent(HttpContext context, string key, Func<Task> next)
{
    var identity = Authenticate(context);                       // who is asking comes first; an anonymous caller never reaches the key table
    if (!IdempotencyStore.ValidKey(key)) throw new ProtocolException(ErrorCode.InvalidRequest, "The Idempotency-Key must be 8 to 128 letters, digits, '-', '_', '.' or ':'.");
    var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
    if (!feature.IsReadOnly && context.Request.Path.Value!.EndsWith("/archives", StringComparison.Ordinal)) feature.MaxRequestBodySize = 192 * 1024;
    var limit = feature.MaxRequestBodySize ?? 64 * 1024;
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int read;
    while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
    {
        if (buffer.Length + read > limit) throw new ProtocolException(ErrorCode.TooLarge, "The request is too large.");
        buffer.Write(chunk, 0, read);
    }
    var body = buffer.ToArray();
    context.Request.Body = new MemoryStream(body);
    var scope = IdempotencyStore.Scope(identity.Uid, context.Request.Headers["X-OrbitDrive-Device"].ToString(), key);
    var decision = idempotency.Begin(scope, identity.Uid, IdempotencyStore.Fingerprint(context.Request.Method, context.Request.Path.Value!, body));
    switch (decision.Outcome)
    {
        case IdempotencyStore.Outcome.Replay:
            context.Response.Headers[IdempotencyStore.ReplayHeader] = "true";
            context.Response.StatusCode = decision.Status;
            context.Response.ContentType = decision.ContentType;
            await context.Response.Body.WriteAsync(decision.Body!, context.RequestAborted);
            return;
        case IdempotencyStore.Outcome.Mismatch:
            throw new ProtocolException(ErrorCode.Conflict, "This Idempotency-Key was already used for a different request. Use a new key for a new request.");
        case IdempotencyStore.Outcome.InFlight:
            context.Response.Headers.RetryAfter = "1";
            throw new ProtocolException(ErrorCode.Conflict, "The first request with this Idempotency-Key is still running. Wait a moment and retry.");
        case IdempotencyStore.Outcome.Full:
            context.Response.Headers.RetryAfter = "60";
            throw new ProtocolException(ErrorCode.LimitExceeded, "Too many different Idempotency-Keys are in use. Wait before starting more.");
    }
    var original = context.Response.Body;
    var tee = new TeeStream(original, IdempotencyStore.MaxStoredBody);
    context.Response.Body = tee;
    var stored = false;
    try
    {
        await next();
        if (context.Response.StatusCode is >= 200 and < 300 && !tee.Overflowed)
        {
            idempotency.Complete(scope, context.Response.StatusCode, context.Response.ContentType ?? "application/json; charset=utf-8", tee.Captured);
            stored = true;
        }
    }
    finally
    {
        context.Response.Body = original;
        if (!stored) idempotency.Abandon(scope);
    }
}

// Uniform security headers and typed errors. Bodies/capabilities are never logged.
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    // The API serves data, never pages: if a browser is ever pointed at a response directly, it may not run, frame or embed anything.
    // (Inline previews tighten this further and, being shown by the app's own origin, relax only Cross-Origin-Resource-Policy.)
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers.Append("Vary", "Origin");               // the cross-origin answer depends on who asks
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    try
    {
        if (maintenance.Active && MaintenanceMode.IsNewWork(context.Request))
        {
            context.Response.Headers.RetryAfter = "60";
            throw new ProtocolException(ErrorCode.Unavailable, "MyOrbitDrive is being maintained. Transfers already running continue; please try again in a minute.");
        }
        if (IdempotencyStore.Applies(context.Request) && context.Request.Headers[IdempotencyStore.Header].ToString() is { Length: > 0 } idempotencyKey)
            await RunIdempotent(context, idempotencyKey, () => next(context));
        else
            await next(context);
    }
    catch (ProtocolException e) when (!context.Response.HasStarted)
    {
        await WriteJson(context, new ErrorResponse { Error = e.ToInfo() }, StatusFor(e.Code));
    }
    catch (BadHttpRequestException e) when (!context.Response.HasStarted)
    {
        await WriteJson(context, new ErrorResponse { Error = new ErrorInfo { Code = ErrorCode.InvalidRequest, Message = "Request rejected." } }, e.StatusCode);
    }
});
app.UseMiddleware<RequestLogging>();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", () => maintenance.Active ? Results.Text("maintenance", statusCode: 503) : Results.Text("ready"));
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
        // Test control: pretend the person signed in this many seconds ago (a long-lived refreshed token), to exercise recent-sign-in checks.
        var ago = int.TryParse(context.Request.Query["signedInAgoSeconds"], out var seconds) && seconds is > 0 and < 86400 * 30 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var token = dev.Issue(request.Email, TimeSpan.FromHours(8), ago, secondFactor: context.Request.Query["secondFactor"] == "true", name: context.Request.Query["name"].ToString() is { Length: > 0 } devName ? devName : null);   // test control: pretend the sign-in used a second factor
        await WriteJson(context, new DevSignInResponse { IdToken = token, Uid = DevIdentityVerifier.UidFor(token) });
    });
}

// Outbound agent control channel. Browsers always send Origin; agents never do.
app.Map("/v1/agent-control", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.ContainsKey("Origin"))
        throw new ProtocolException(ErrorCode.InvalidRequest, "Agent WebSocket connections only.");
    var address = ClientAddress.Of(context, options);
    // Per client address first (cheap, and an address that opens connections endlessly never reaches the shared places), then the shared limit.
    Throttle(context, "agent-conn:" + address, options.AgentConnectionsPerAddressPerMinute, "Too many connections from this network. Try again in a minute.", anonymous: true);
    if (!hub.TryReserveHandshake(options.MaxPendingHandshakes, address, options.MaxPendingHandshakesPerAddress))      // atomic: concurrent arrivals cannot overshoot the limit
    {
        context.Response.Headers.RetryAfter = "5";
        throw new ProtocolException(ErrorCode.LimitExceeded, "Too many connections are waiting to identify themselves. Try again shortly.");
    }
    WebSocket socket;
    try { socket = await context.WebSockets.AcceptWebSocketAsync(); }
    catch { hub.ReleaseHandshake(address); throw; }                    // never accepted, so RunAsync will not give the place back
    using (socket) await hub.RunAsync(socket, context.RequestAborted, address);
});

// The web app address the claim came from. Browsers always send it and CORS has already limited it to the allowed web origins; the PC shows it so the owner can see which site is asking.
static string? ClaimOrigin(HttpContext context) =>
    context.Request.Headers.Origin.ToString() is { Length: > 0 and <= 200 } origin && origin.All(c => c is >= ' ' and < '\u007f') ? origin : null;

app.MapPost("/v1/pairing-requests/{id}/claim", async (HttpContext context, string id) =>
{
    // Guessing defence in depth: each code dies after 5 wrong secrets (Store), and every attempt - right or wrong -
    // is also counted per client address and per account, so cycling through codes or accounts does not help.
    const string slowDown = "Too many pairing attempts. Wait a minute, then create a new code on the PC.";
    Throttle(context, "claim-ip:" + ClientAddress.Of(context, options), options.PairingClaimsPerMinute, slowDown, anonymous: true);
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
    if (billing.LimitsEnabled)
    {
        var limits = billing.EntitlementFor(billing.SubjectFor(identity.Uid)).Limits;
        var pool = billing.PoolOf(identity.Uid);
        string? refusal = null;
        if (limits.Pcs is { } maxPcs && !pool.Contains(pc.OwnerUid) && pool.Sum(u => store.PcsOwnedBy(u).Count) >= maxPcs)
            refusal = $"Your plan allows {maxPcs} PC{(maxPcs == 1 ? "" : "s")} and you already have that many. Remove one or choose a bigger plan, then create a new code on the PC.";
        else if (limits.Devices is { } maxDevices && !(existing is not null && existing.Id == device.Id) && pool.Sum(u => store.DevicesForOwner(u).Count(d => d.Id != device.Id)) >= maxDevices)
            refusal = $"Your plan allows {maxDevices} paired device{(maxDevices == 1 ? "" : "s")} and you already have that many. Remove one or choose a bigger plan, then create a new code on the PC.";
        if (refusal is not null) { store.AbandonClaim(pairing.Id); throw new ProtocolException(ErrorCode.LimitExceeded, refusal); }
    }
    var code = (BitConverter.ToUInt32(Secrets.Hash($"{pairing.Id}|{identity.Uid}|{device.Id}|{pc.PcId}")) % 1_000_000).ToString("D6");
    try
    {
        await hub.Get(pc.PcId).SendAsync(new Envelope
        {
            V = ProtocolInfo.Version, Id = Secrets.Token(12), Kind = EnvelopeKind.Evt, Action = Actions.PairingClaimed,
            Payload = ProtocolJson.ToElement(new PairingClaimedEvent { PairingId = pairing.Id, AccountLabel = identity.Email, BrowserName = device.Name, ConfirmationCode = code, BrowserOrigin = ClaimOrigin(context) }),
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
static void AllowLargerBody(HttpContext context)
{
    var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
    if (!feature.IsReadOnly) feature.MaxRequestBodySize = 192 * 1024;   // already read (and capped) by the idempotency step when a key was sent
}

// ----- D8: archives, search and entry checks. The PC does the work; the gateway only authorises the browser for that PC. -----
app.MapPost("/v1/pcs/{pcId}/archives", async (HttpContext context, string pcId) =>
{
    var device = AuthorizeBrowser(context, pcId);
    Throttle(context, "archive:" + device.Id, 20, "Too many archive requests. Wait a moment.");
    Throttle(context, "archive-owner:" + device.OwnerUid, options.ArchiveStartsPerOwnerPerMinute, "This account is preparing archives too fast. Wait a moment.");
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
    if (billing.LimitsEnabled) billing.RequireTransferFor(device.OwnerUid, request.Size);
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
    var finished = await hub.Get(pcId).RequestAsync<UploadStatus>(Actions.FinishUpload, request with { DeviceId = device.Id }, TimeSpan.FromSeconds(options.PrepareTimeoutSeconds), context.RequestAborted);
    if (finished.State == "committed" && countedUploads.TryAdd(finished.UploadId, DateTimeOffset.UtcNow))
    {
        billing.RecordTransferFor(device.OwnerUid, 0, fileCompleted: true);    // a file counts once, when it is saved on the PC
        if (countedUploads.Count > 10_000) foreach (var old in countedUploads.Where(e => DateTimeOffset.UtcNow - e.Value > TimeSpan.FromDays(1)).Select(e => e.Key).ToList()) countedUploads.TryRemove(old, out _);
    }
    await WriteJson(context, finished);
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

    metrics.RequireRelayAllowance(device.OwnerUid);
    var (stream, uplinkToken) = streams.Create(device.OwnerUid);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var success = false;
    var stored = false;
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
        var answer = await agentTask.WaitAsync(TimeSpan.FromSeconds(options.UploadChunkTimeoutSeconds), cancel.Token);
        stored = answer.Accepted;
        await WriteJson(context, answer);
    }
    catch (TimeoutException) { throw new ProtocolException(ErrorCode.Timeout, "The PC did not take this piece in time."); }
    finally
    {
        metrics.PieceRelayed(device.OwnerUid, length, stored);
        if (stored) billing.RecordTransferFor(device.OwnerUid, length, fileCompleted: false);
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
    Authenticate(context);                                        // who is asking comes before anything about what they sent
    var request = await ReadJson<CreateDownloadSessionRequest>(context);
    var device = AuthorizeBrowser(context, request.PcId);
    await WriteJson(context, await StartDownload(device, request.PcId, request.EntryId, request.Variant, context.RequestAborted), 201);
});

// "Send to my phone": the Inbox of this browser (never another browser's, never the PC's grant), and its decisions.
// The relay keeps no file names (F-003): each offer holds a name sealed by the PC. When the Inbox is shown the relay asks the PC to open them, so a name exists
// here only in this response. A PC that is offline shows its offers without names until it is back.
async Task<Dictionary<string, string>> ResolveNames(IEnumerable<InvitationRecord> items, CancellationToken cancellation)
{
    var names = new Dictionary<string, string>();
    foreach (var group in items.Where(i => i.SealedName.Length > 0).GroupBy(i => i.PcId))
    {
        if (!hub.IsOnline(group.Key)) continue;
        foreach (var chunk in group.Chunk(50))
        {
            try
            {
                var answer = await hub.Get(group.Key).RequestAsync<CheckEntriesResult>(Actions.CheckEntries, new CheckEntriesRequest { EntryIds = [.. chunk.Select(i => i.SealedName)] }, TimeSpan.FromSeconds(Math.Min(agentTimeout.TotalSeconds, 8)), cancellation);
                foreach (var check in answer.Entries.Where(c => c.Available && c.Entry is not null)) names[check.EntryId] = check.Entry!.Name;
            }
            catch (ProtocolException) { /* the PC went away or is busy: those offers show without names */ }
        }
    }
    return names;
}

app.MapGet("/v1/invitations", async (HttpContext context) =>
{
    var device = AuthorizeBrowser(context);
    var mine = invitations.ForReceiver(device.Id).Where(i => device.PcIds.Contains(i.PcId)).ToList();
    var names = await ResolveNames(mine, context.RequestAborted);
    await WriteJson(context, new InvitationList { Invitations = [.. mine.Select(i => invitations.Summarize(i, hub.IsOnline(i.PcId), names.GetValueOrDefault(i.SealedName) ?? i.LegacyName ?? ""))] });
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

    // Per link, on top of the per-browser and per-account limits: a leaked or scripted link cannot keep the PC busy.
    Throttle(context, "download-start:" + session.Id, options.DownloadStartsPerCapabilityPerMinute, "This download link is being used too fast. Wait a moment.");
    if (session.ActiveStreams >= options.MaxStreamsPerCapability)
    {
        context.Response.Headers.RetryAfter = "5";
        throw new ProtocolException(ErrorCode.LimitExceeded, "This download link already has the most transfers it allows running. Wait for one to finish.");
    }
    metrics.RequireRelayAllowance(session.OwnerUid);
    // Thumbnails are free. Opening a file to view it returns the original bytes, so it counts like a download towards the day's data, the relay cap and the
    // per-file size limit (it does not use up one of the day's files: only a finished download does). F-004.
    var isThumbnail = session.Variant is { } v && v.StartsWith("thumb:", StringComparison.Ordinal);
    var isView = session.IsPreview && !isThumbnail;
    if (billing.LimitsEnabled && !isThumbnail) billing.RequireTransferFor(session.OwnerUid, session.Size, countsAsFile: !isView);
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
        if (!session.IsPreview)
        {
            metrics.Downloaded(session.OwnerUid, relayed, success);
            // A file counts once per account, revision and day when the bytes delivered over ALL its links cover it, so splitting a download cannot avoid the count (F-014).
            var fileKey = $"{session.PcId}|{session.Revision}|{session.Size}|{session.Name}";
            billing.RecordTransferFor(session.OwnerUid, relayed, billing.NoteDelivery(session.OwnerUid, fileKey, session.Size, selected.Start, relayed, emptyDelivered: success && session.Size == 0));
        }
        else if (isView)
        {
            metrics.Viewed(session.OwnerUid, relayed);
            billing.RecordTransferFor(session.OwnerUid, relayed, fileCompleted: false);
        }
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

// ---- Owner controls: the signed-in person's own browsers, PCs and choices. Every call needs an approved browser. ----
AccountSummary Summarize(Identity identity, string ownerUid, string? currentDeviceId) => new()
{
    Devices = [.. store.DevicesForOwner(ownerUid).Select(d => new BrowserDeviceSummary { DeviceId = d.Id, Name = d.Name, Current = d.Id == currentDeviceId, PcIds = [.. d.PcIds], LastSeenUnixMs = d.LastSeenUnixMs })],
    Pcs = [.. store.PcsOwnedBy(ownerUid).Select(p => new AccountPc { PcId = p.PcId, Name = p.Name, AgentVersion = p.AgentVersion, Online = hub.IsOnline(p.PcId), LastSeenUnixMs = p.LastSeen.ToUnixTimeMilliseconds() })],
    Preferences = store.Preferences(ownerUid),
    IsOperator = IsOperator(identity),
    RetentionNote = "The service keeps a transfer in memory for at most an hour after it is prepared, so this choice can only shorten how long finished transfers stay listed. It never saves file contents, file names or paths to disk.",
};

app.MapGet("/v1/account", (HttpContext context) =>
{
    var identity = Authenticate(context);
    var current = store.Authorize(identity, context.Request.Headers["X-OrbitDrive-Device"].ToString());
    return WriteJson(context, Summarize(identity, current.OwnerUid, current.Id));
});

app.MapPut("/v1/account/preferences", async (HttpContext context) =>
{
    var current = AuthorizeBrowser(context);
    store.SetPreferences(current.OwnerUid, await ReadJson<OwnerPreferences>(context));
    await WriteJson(context, new Ack { Ok = true });
});

void EndAllWork(IEnumerable<BrowserDeviceRecord> devices) { foreach (var device in devices) DiscardUploads(device.Id, [.. device.PcIds]); }

app.MapPost("/v1/account/revoke-all", (HttpContext context) =>
{
    var current = AuthorizeBrowser(context);
    var revoked = store.RevokeAllDevices(current.OwnerUid);
    EndAllWork(revoked);
    return WriteJson(context, new AccountRevokeResult { DevicesRevoked = revoked.Count });
});

// What the service holds about this account, as a file the person keeps. Never includes secrets, file names or file contents. It needs only a sign-in that is
// recent (no paired browser): a person who has lost their browsers can still see and take what the service holds (F-012).
app.MapGet("/v1/account/export", async (HttpContext context) =>
{
    var identity = Authenticate(context);
    RequireRecentSignIn(identity, "Sign out and sign in again before exporting your account data.");
    Throttle(context, "export:" + identity.Uid, 10, "Too many requests. Wait a moment.");
    // The account summary stays at the top level (as before), with the billing records and a note added beside it.
    var export = System.Text.Json.Nodes.JsonNode.Parse(ProtocolJson.Serialize(Summarize(identity, identity.Uid, null)))!.AsObject();
    export["exportedAt"] = DateTimeOffset.UtcNow.ToString("O");
    export["email"] = identity.Email;
    export["billing"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(billing.ExportFor(identity.Uid), BillingJson.Options));
    export["note"] = "Paid orders are kept after an account is deleted, cut down to the order id, plan, length, amounts, payment method and reference, and dates. Operator audit records name the account by id only.";
    context.Response.Headers.ContentDisposition = "attachment; filename=\"orbitdrive-account.json\"";
    context.Response.StatusCode = 200;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(export.ToJsonString());
});

// Deleting needs only a recent sign-in (no paired browser), because the service already holds this account's orders and payment references.
app.MapDelete("/v1/account", async (HttpContext context) =>
{
    var identity = Authenticate(context, noteAccount: false);
    RequireRecentSignIn(identity, "Sign out and sign in again before deleting your account.");
    var request = await ReadJson<DeleteAccountRequest>(context);
    if (request.Confirm != "delete my account") throw new ProtocolException(ErrorCode.InvalidRequest, "Type the confirmation phrase exactly to delete your account.");
    var uid = identity.Uid;
    var emailAtDeletion = billing.AccountFor(uid)?.Email ?? (identity.Email.Length > 0 ? identity.Email : null);   // for the audit note below: it keeps the address as it was, so the entry stays readable
    var (devices, pcs) = store.DeleteAccount(uid);
    EndAllWork(devices);
    invitations.ForgetOwner(uid);
    metrics.Forget(uid);
    billing.Forget(BillingStore.AccountSubject(uid));
    try { adminLog.Record(uid, "account-deleted", uid, $"{devices.Count} browsers revoked, {pcs.Count} PCs released", emailAtDeletion, emailAtDeletion); }
    catch (ProtocolException) { /* the person's own deletion is done; the note is best effort */ }
    await WriteJson(context, new Ack { Ok = true });
});

// ---- Operator administration. The operator list comes from deployment configuration, checked on every call; being signed in as an operator
// is not enough: the sign-in must also be recent. There is no endpoint that lists files, folders or paths, or acts as a user. ----
bool IsOperator(Identity identity) => options.OperatorUids.Contains(identity.Uid, StringComparer.Ordinal);

void RequireRecentSignIn(Identity identity, string message)
{
    if (identity.AuthTime is not { } at || TimeProvider.System.GetUtcNow() - at > TimeSpan.FromSeconds(options.OperatorMaxAuthAgeSeconds))
        throw new ProtocolException(ErrorCode.Unauthenticated, message);
}

Identity AuthorizeOperator(HttpContext context)
{
    var identity = Authenticate(context);
    if (!IsOperator(identity)) throw new ProtocolException(ErrorCode.Forbidden, "This area is for the people who run the service.");
    RequireRecentSignIn(identity, "Sign in again to use administration. It needs a recent sign-in.");
    // When the owner has switched it on, administration needs a sign-in that used a second factor (F-005). Off by default; see docs/operations/relay.md.
    if (options.RequireOperatorSecondFactor && !identity.SecondFactor)
        throw new ProtocolException(ErrorCode.Forbidden, "Administration needs a sign-in with your second factor. Turn on two-step sign-in for this account, then sign in again.");
    Throttle(context, "admin:" + identity.Uid, 120, "Too many administration requests. Wait a moment.");
    return identity;
}

string? TargetEmail(string target) => billing.EmailForTarget(target) ?? (billing.SubjectOfLicence(target) is { } owner ? billing.EmailForTarget(owner) : null);

// One operator change, in the order the audit asks for: the reason is checked first (3 to 200 characters), the audit record is written BEFORE the change and the change
// does not happen when the record cannot be written, and a change that then fails leaves a second record saying so (F-006).
async Task Changing(Identity operatorIdentity, string action, string target, string? reason, Func<Task> change, string detail = "")
{
    var text = AdminLog.RequireReason(reason);
    // The audit entry keeps the operator's and the target account's email as they are now, so it stays readable later (even after the account is deleted).
    var operatorEmail = operatorIdentity.Email.Length > 0 ? operatorIdentity.Email : null;
    var targetEmail = TargetEmail(target);
    adminLog.Record(operatorIdentity.Uid, action, target, detail.Length > 0 ? $"{detail}: {text}" : text, operatorEmail, targetEmail);
    try { await change(); }
    catch (Exception)
    {
        try { adminLog.Record(operatorIdentity.Uid, action + "-failed", target, "the change was refused or failed", operatorEmail, targetEmail); } catch (ProtocolException) { /* the first record already stands */ }
        throw;
    }
}

void RequireOrderingOpen()
{
    if (!billing.LimitsEnabled) throw new ProtocolException(ErrorCode.Conflict, "Ordering is not open yet. It opens on the start date.");
}

app.MapGet("/v1/admin/overview", (HttpContext context) =>
{
    var identity = AuthorizeOperator(context);
    var owners = store.Owners();
    return WriteJson(context, new AdminOverview
    {
        GeneratedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), StartedUnixMs = metrics.StartedAt.ToUnixTimeMilliseconds(), GatewayVersion = gatewayVersion,
        Users = owners.Count, SuspendedUsers = owners.Count(o => o.Suspended), BrowserDevices = store.DeviceCount(), Pcs = store.PcCount(),
        PcsOnline = store.PcsOnlineCount(hub), ActiveStreams = streams.Count,
        RelayBytesDown = metrics.Down, RelayBytesUp = metrics.Up, DownloadsCompleted = metrics.DownloadsCompleted, DownloadsFailed = metrics.DownloadsFailed,
        UploadPiecesRelayed = metrics.PiecesRelayed, UploadPiecesFailed = metrics.PiecesFailed, QuotaRefusals = metrics.QuotaRefusals,
        MinAgentVersion = adminLog.MinAgentVersion, RelayBytesPerOwnerPerDay = options.MaxRelayBytesPerOwnerPerDay,
        RelayBytesToday = metrics.RelayBytesTodayAll, RelayBytesDailyAlert = options.RelayBytesDailyAlert, CostAlert = metrics.CostAlert,
        SecondFactorRequired = options.RequireOperatorSecondFactor, SecondFactorPresent = identity.SecondFactor, BillingDamaged = billing.Damaged, UnpairedPcs = store.UnownedPcCount(),
    });
});

app.MapGet("/v1/admin/users", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteJson(context, new AdminUserList { Users = [.. store.Owners().Take(500).Select(o => new AdminUser { Uid = o.Uid, Devices = o.Devices, Pcs = o.Pcs, Suspended = o.Suspended, RelayBytesToday = metrics.RelayBytesToday(o.Uid) })] });
});

static string CleanUid(string uid) => uid.Length is > 0 and <= 128 && uid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':') ? uid : throw new ProtocolException(ErrorCode.InvalidRequest, "That is not an account id.");

app.MapPost("/v1/admin/users/{uid}/suspend", async (HttpContext context, string uid) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    uid = CleanUid(uid);
    if (options.OperatorUids.Contains(uid, StringComparer.Ordinal)) throw new ProtocolException(ErrorCode.Conflict, "An operator account cannot be suspended here. Remove it from the operator list first.");
    var request = await ReadJson<AdminReasonRequest>(context);
    await Changing(operatorIdentity, "suspend", uid, request.Reason, () => { EndAllWork(store.SetSuspended(uid, true)); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPost("/v1/admin/users/{uid}/unsuspend", async (HttpContext context, string uid) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    uid = CleanUid(uid);
    var request = await ReadJson<AdminReasonRequest>(context);
    await Changing(operatorIdentity, "unsuspend", uid, request.Reason, () => { store.SetSuspended(uid, false); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPost("/v1/admin/users/{uid}/revoke-all", async (HttpContext context, string uid) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    uid = CleanUid(uid);
    var request = await ReadJson<AdminReasonRequest>(context);
    var count = 0;
    await Changing(operatorIdentity, "revoke-all", uid, request.Reason, () => { var revoked = store.RevokeAllDevices(uid); EndAllWork(revoked); count = revoked.Count; return Task.CompletedTask; });
    await WriteJson(context, new AccountRevokeResult { DevicesRevoked = count });
});

app.MapPost("/v1/admin/users/{uid}/reset-relay-allowance", async (HttpContext context, string uid) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    uid = CleanUid(uid);
    var request = await ReadJson<AdminReasonRequest>(context);
    await Changing(operatorIdentity, "reset-relay-allowance", uid, request.Reason, () =>
    {
        billing.ResetToday(BillingStore.AccountSubject(uid));
        metrics.Forget(uid);                              // an operator's way to give one account its day's allowance back before midnight UTC
        return Task.CompletedTask;
    });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapGet("/v1/admin/settings", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteJson(context, new AdminSettings { MinAgentVersion = adminLog.MinAgentVersion });
});

app.MapPut("/v1/admin/settings", async (HttpContext context) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadJson<AdminSettings>(context);
    if (!AdminLog.IsValidVersion(request.MinAgentVersion ?? "")) throw new ProtocolException(ErrorCode.InvalidRequest, "The minimum version must look like 1.2.3, or be empty for no minimum.");
    var version = request.MinAgentVersion ?? "";
    await Changing(operatorIdentity, "set-min-agent-version", "service", request.Reason, () => { adminLog.SetMinAgentVersion(version); return Task.CompletedTask; }, version.Length == 0 ? "none" : version);
    await WriteJson(context, new Ack { Ok = true });
});

app.MapGet("/v1/admin/audit", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteJson(context, new AuditList { Entries = adminLog.Recent(200) });
});

// ---- Plans, orders and the owner's billing tools. Prices are public; everything else needs sign-in; the owner's tools need an operator with a recent sign-in. ----
static Task WriteBilling<T>(HttpContext context, T value, int status = 200)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    return context.Response.WriteAsync(JsonSerializer.Serialize(value, BillingJson.Options));
}

static async Task<T> ReadBilling<T>(HttpContext context)
{
    if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        throw new ProtocolException(ErrorCode.InvalidRequest, "Content-Type must be application/json.");
    try { return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, BillingJson.Request, context.RequestAborted) ?? throw new JsonException(); }
    catch (JsonException) { throw new ProtocolException(ErrorCode.InvalidRequest, "Malformed request body."); }
}

string SignedInSubject(HttpContext context)
{
    var identity = Authenticate(context);
    if (store.IsSuspended(identity.Uid)) throw new ProtocolException(ErrorCode.Forbidden, "This account is suspended.");
    return BillingStore.AccountSubject(identity.Uid);
}

AccountPlanView AccountPlan(string subject)
{
    var uid = subject["acct:".Length..];
    return BillingViews.Account(billing, subject, DateTimeOffset.UtcNow, store.PcsOwnedBy(uid).Count, store.DevicesForOwner(uid).Count);
}

app.MapGet("/v1/plans", (HttpContext context) =>
{
    Throttle(context, "plans:" + ClientAddress.Of(context, options), 120, "Too many requests. Wait a moment.", anonymous: true);
    context.Response.Headers.CacheControl = "public, max-age=60";
    context.Response.Headers.AccessControlAllowOrigin = "*";            // public prices, no credentials: any page may show them
    return WriteBilling(context, BillingViews.Public(billing));
});

app.MapGet("/v1/account/plan", (HttpContext context) => WriteBilling(context, AccountPlan(SignedInSubject(context))));

app.MapGet("/v1/account/usage", (HttpContext context) => WriteBilling(context, billing.UsageHistory(SignedInSubject(context), 30)));

app.MapGet("/v1/account/orders", (HttpContext context) => WriteBilling(context, billing.OrdersFor(SignedInSubject(context))));

app.MapPost("/v1/account/quote", async (HttpContext context) =>
{
    var subject = SignedInSubject(context);
    Throttle(context, "quote:" + subject, 30, "Too many price checks. Wait a moment.");
    RequireOrderingOpen();
    var request = await ReadBilling<QuoteRequest>(context);
    await WriteBilling(context, billing.QuoteFor(request.PlanId, request.Months, request.Coupon, subject));
});

app.MapPost("/v1/account/orders", async (HttpContext context) =>
{
    var subject = SignedInSubject(context);
    Throttle(context, "order:" + subject, 10, "Too many orders. Wait a moment.");
    RequireOrderingOpen();
    var request = await ReadBilling<OrderRequest>(context);
    var order = billing.CreateOrder(subject, request.PlanId, request.Months, request.Coupon, request.Method);
    await WriteBilling(context, order, 201);
});

app.MapPost("/v1/account/orders/{id}/proof", async (HttpContext context, string id) =>
{
    var subject = SignedInSubject(context);
    Throttle(context, "proof:" + subject, 20, "Too many requests. Wait a moment.");
    RequireOrderingOpen();
    var request = await ReadBilling<ProofRequest>(context);
    await WriteBilling(context, billing.SubmitProof(subject, id, request.Method, request.Reference));
});

app.MapPost("/v1/account/orders/{id}/cancel", async (HttpContext context, string id) =>
{
    var subject = SignedInSubject(context);
    await WriteBilling(context, billing.CancelOrder(subject, id));
});

// ---- Organisations: an Office owner invites people; they share the Office plan and its limits. Admins see members, counts and who-did-what, never file or folder names.

OrgView OrgViewFor(string uid)
{
    if (billing.OrgOf(uid) is not { } mine) return new OrgView(null, null, [], 0, 0);
    var (org, me) = mine;
    var admin = me.Role is "owner" or "admin";
    var members = billing.MembersOf(org.Id, uid).Select(m =>
    {
        var (files, bytes) = admin ? billing.UsageToday("acct:" + m.Uid) : (0, 0L);
        return new OrgMemberView(m.Uid == uid || admin ? m.Uid : "", m.Label, m.Role, m.JoinedAt, admin ? store.PcsOwnedBy(m.Uid).Count : 0, admin ? store.DevicesForOwner(m.Uid).Count : 0, files, bytes);
    }).ToList();
    var (poolFiles, poolBytes) = billing.UsageToday("org:" + org.Id);
    return new OrgView(new OrgInfo(org.Id, org.Name, me.Role), billing.EntitlementFor("org:" + org.Id).PlanId, members, poolFiles, poolBytes);
}

string OrgIdOf(string uid) => billing.OrgOf(uid)?.Org.Id ?? throw new ProtocolException(ErrorCode.NotFound, "You are not in an organisation.");

app.MapGet("/v1/account/org", (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    return WriteBilling(context, OrgViewFor(uid));
});

app.MapPost("/v1/account/org", async (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    Throttle(context, "org:" + uid, 10, "Too many requests. Wait a moment.");
    var request = await ReadBilling<OrgCreateRequest>(context);
    billing.CreateOrg(uid, request.Name);
    await WriteBilling(context, OrgViewFor(uid), 201);
});

app.MapPost("/v1/account/org/invite", async (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    Throttle(context, "orginvite:" + uid, 20, "Too many invitations. Wait a moment.");
    var invite = billing.Invite(OrgIdOf(uid), uid);
    await WriteBilling(context, new OrgInviteView(invite.Code, invite.ExpiresAt), 201);
});

app.MapPost("/v1/account/org/join", async (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    Throttle(context, "orgjoin:" + uid, 10, "Too many attempts. Wait a moment.");
    var request = await ReadBilling<OrgJoinRequest>(context);
    billing.Join(uid, request.Code, request.Label);
    await WriteBilling(context, OrgViewFor(uid));
});

app.MapPost("/v1/account/org/members/{target}/remove", async (HttpContext context, string target) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    billing.RemoveMember(OrgIdOf(uid), uid, CleanUid(target));
    await WriteBilling(context, OrgViewFor(uid));
});

app.MapPost("/v1/account/org/members/{target}/role", async (HttpContext context, string target) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    var request = await ReadBilling<OrgRoleRequest>(context);
    billing.SetRole(OrgIdOf(uid), uid, CleanUid(target), request.Role);
    await WriteBilling(context, OrgViewFor(uid));
});

app.MapPost("/v1/account/org/leave", async (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    billing.Leave(uid);
    await WriteBilling(context, OrgViewFor(uid));
});

app.MapDelete("/v1/account/org", async (HttpContext context) =>
{
    var identity = Authenticate(context);
    RequireRecentSignIn(identity, "Sign out and sign in again before deleting the organisation.");
    billing.DeleteOrg(OrgIdOf(identity.Uid), identity.Uid);
    await WriteBilling(context, OrgViewFor(identity.Uid));
});

app.MapGet("/v1/account/org/audit", (HttpContext context) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    var events = billing.OrgAudit(OrgIdOf(uid), uid).Select(e => new OrgEventView(e.At, e.Action, e.Detail, e.By == uid ? "you" : billing.OrgOf(uid) is { } o && billing.MembersOf(o.Org.Id, uid).FirstOrDefault(m => m.Uid == e.By)?.Label is { } l ? l : "someone")).ToList();
    return WriteBilling(context, events);
});

// ---- Operator views of accounts and organisations: counts, plans, PCs and browsers; never file or folder names. Every change needs a reason and is audited.

AdminOrgRow OrgRow(Organisation o)
{
    var (files, bytes) = billing.UsageToday("org:" + o.Id);
    var owner = billing.AccountFor(o.OwnerUid);
    return new AdminOrgRow(o.Id, o.Name, o.OwnerUid, billing.MembersUnchecked(o.Id).Count, billing.EntitlementFor(BillingStore.AccountSubject(o.OwnerUid)).PlanId, files, bytes, o.Suspended, o.CreatedAt, owner?.Email, owner?.Name);
}

app.MapGet("/v1/admin/accounts", (HttpContext context) =>
{
    AuthorizeOperator(context);
    var rows = store.Owners().Take(500).Select(o =>
    {
        var plan = AccountPlan(BillingStore.AccountSubject(o.Uid));
        var who = billing.AccountFor(o.Uid);
        return new AdminAccountRow(o.Uid, o.Suspended, plan.PlanId, plan.PlanName, plan.State, plan.EndsAt, billing.OrgOf(o.Uid)?.Org.Name, o.Pcs, o.Devices, plan.FilesToday, plan.BytesToday, who?.Email, who?.Name);
    }).ToList();
    return WriteBilling(context, rows);
});

// Who is who, for every place the console names an account (orders, passes, audit log, organisation members). Operators only: AuthorizeOperator re-checks the operator list and a fresh sign-in.
app.MapGet("/v1/admin/directory", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteBilling(context, new AdminDirectory([.. billing.KnownAccounts().Take(5000).Select(a => new AdminPerson(a.Uid, a.Email, a.Name))]));
});

app.MapGet("/v1/admin/users/{uid}/detail", (HttpContext context, string uid) =>
{
    AuthorizeOperator(context);
    uid = CleanUid(uid);
    var subject = BillingStore.AccountSubject(uid);
    var org = billing.OrgOf(uid);
    var who = billing.AccountFor(uid);
    var detail = new AdminUserDetail(uid, store.IsSuspended(uid), AccountPlan(subject), org?.Org.Name, org?.Me.Role, billing.UsageHistory(subject, 30),
        [.. store.PcsOwnedBy(uid).Select(p => new AdminPcView(p.Name, p.AgentVersion, p.LastSeen))],
        [.. store.DevicesForOwner(uid).Select(d => new AdminDeviceView(d.Name, d.CreatedAt, d.LastUsedAt))], who?.Email, who?.Name);
    return WriteBilling(context, detail);
});

app.MapGet("/v1/admin/orgs", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteBilling(context, billing.AllOrgs().Take(500).Select(OrgRow).ToList());
});

app.MapGet("/v1/admin/orgs/{id}", (HttpContext context, string id) =>
{
    AuthorizeOperator(context);
    var org = billing.FindOrg(id) ?? throw new ProtocolException(ErrorCode.NotFound, "Organisation not found.");
    var members = billing.MembersUnchecked(id).Select(m =>
    {
        var (files, bytes) = billing.UsageToday("acct:" + m.Uid);
        var who = billing.AccountFor(m.Uid);
        return new AdminOrgMemberView(m.Uid, m.Label, m.Role, m.JoinedAt, store.PcsOwnedBy(m.Uid).Count, store.DevicesForOwner(m.Uid).Count, files, bytes, who?.Email, who?.Name);
    }).ToList();
    var events = billing.OrgEventsUnchecked(id).Select(e => new OrgEventView(e.At, e.Action, e.Detail, e.By)).ToList();
    return WriteBilling(context, new AdminOrgDetail(OrgRow(org), members, billing.UsageHistory("org:" + id, 30), events));
});

app.MapPost("/v1/admin/orgs/{id}/suspend", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadJson<AdminReasonRequest>(context);
    if (billing.FindOrg(id) is null) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found.");
    await Changing(operatorIdentity, "org-suspend", id, request.Reason, () => { if (!billing.SetOrgSuspended(id, true)) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found."); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPost("/v1/admin/orgs/{id}/unsuspend", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadJson<AdminReasonRequest>(context);
    if (billing.FindOrg(id) is null) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found.");
    await Changing(operatorIdentity, "org-unsuspend", id, request.Reason, () => { if (!billing.SetOrgSuspended(id, false)) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found."); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPost("/v1/admin/orgs/{id}/delete", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadJson<AdminReasonRequest>(context);
    if (billing.FindOrg(id) is null) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found.");
    await Changing(operatorIdentity, "org-delete", id, request.Reason, () => { if (!billing.DeleteOrgByOperator(id)) throw new ProtocolException(ErrorCode.NotFound, "Organisation not found."); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

// ---- Organisation admins: settings and counts for their own people. Never file names, never a feed of what someone moved.

app.MapGet("/v1/account/org/members/{target}/usage", (HttpContext context, string target) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    target = CleanUid(target);
    var orgId = OrgIdOf(uid);
    var history = billing.MemberHistory(orgId, uid, target, 30);
    var label = billing.MembersOf(orgId, uid).First(m => m.Uid == target).Label;
    return WriteBilling(context, new MemberHistoryView(label, history));
});

app.MapPost("/v1/account/org/members/{target}/revoke-devices", async (HttpContext context, string target) =>
{
    var uid = SignedInSubject(context)["acct:".Length..];
    target = CleanUid(target);
    var orgId = OrgIdOf(uid);
    var member = billing.RequireManage(orgId, uid, target);
    var revoked = store.RevokeAllDevices(target);
    EndAllWork(revoked);
    billing.NoteOrgAction(orgId, uid, "devices-revoked", $"{member.Label}: {revoked.Count} browser{(revoked.Count == 1 ? "" : "s")}");
    await WriteBilling(context, OrgViewFor(uid));
});

app.MapGet("/v1/admin/billing", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteBilling(context, new AdminBillingView(BillingViews.Public(billing), billing.Plans, billing.Coupons(), billing.AllOrders(null).Count(o => o.Status is "requested" or "proof_submitted"), billing.PaymentInstructions));
});

app.MapPut("/v1/admin/billing/plans", async (HttpContext context) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<PlansUpdate>(context);
    await Changing(operatorIdentity, "set-plans", "service", request.Reason, () => { billing.SetPlans(request.Plans); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPut("/v1/admin/billing/rate", async (HttpContext context) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<RateUpdate>(context);
    await Changing(operatorIdentity, "set-rate", "service", request.Reason, () => { billing.SetRate(request.PkrPerUsd); return Task.CompletedTask; }, $"{request.PkrPerUsd} PKR per USD");
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPut("/v1/admin/billing/limits", async (HttpContext context) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<LimitsUpdate>(context);
    await Changing(operatorIdentity, request.Enabled ? "limits-on" : "limits-off", "service", request.Reason, () => { billing.SetLimitsEnabled(request.Enabled); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapPut("/v1/admin/billing/instructions", async (HttpContext context) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<InstructionsUpdate>(context);
    await Changing(operatorIdentity, "set-payment-instructions", "service", request.Reason, () => { billing.SetPaymentInstructions(request.Text); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.MapGet("/v1/admin/coupons", (HttpContext context) =>
{
    AuthorizeOperator(context);
    return WriteBilling(context, billing.Coupons());
});

app.MapPut("/v1/admin/coupons/{code}", async (HttpContext context, string code) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<CouponUpdate>(context);
    var clean = BillingStore.CleanCode(code);
    Coupon? saved = null;
    await Changing(operatorIdentity, "save-coupon", clean, request.Reason, () => { saved = billing.SaveCoupon(clean, request.Kind, request.Value, request.PlanIds, request.ExpiresAt, request.MaxRedemptions, request.Active, request.Note); return Task.CompletedTask; });
    await WriteBilling(context, saved);
});

app.MapGet("/v1/admin/orders", (HttpContext context) =>
{
    AuthorizeOperator(context);
    var status = context.Request.Query["status"].ToString();
    return WriteBilling(context, billing.AllOrders(status.Length == 0 ? null : status));
});

app.MapPost("/v1/admin/orders/{id}/verify", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<DecisionRequest>(context);
    var before = billing.FindOrder(id) ?? throw new ProtocolException(ErrorCode.NotFound, "That order was not found.");
    Order? order = null;
    await Changing(operatorIdentity, "verify-order", before.Subject, request.Reason, () => { order = billing.VerifyOrder(id, operatorIdentity.Uid); return Task.CompletedTask; }, $"{before.PlanId} {before.Months}m {before.DuePkr} PKR");
    await WriteBilling(context, order);
});

app.MapPost("/v1/admin/orders/{id}/reject", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<DecisionRequest>(context);
    var before = billing.FindOrder(id) ?? throw new ProtocolException(ErrorCode.NotFound, "That order was not found.");
    Order? order = null;
    await Changing(operatorIdentity, "reject-order", before.Subject, request.Reason, () => { order = billing.RejectOrder(id, operatorIdentity.Uid, AdminLog.RequireReason(request.Reason)); return Task.CompletedTask; });
    await WriteBilling(context, order);
});

app.MapGet("/v1/admin/users/{uid}/billing", (HttpContext context, string uid) =>
{
    AuthorizeOperator(context);
    var subject = BillingStore.AccountSubject(CleanUid(uid));
    return WriteBilling(context, new UserBillingView(subject["acct:".Length..], AccountPlan(subject), billing.LicencesFor(subject), billing.OrdersFor(subject)));
});

app.MapPost("/v1/admin/users/{uid}/passes", async (HttpContext context, string uid) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var subject = BillingStore.AccountSubject(CleanUid(uid));
    var request = await ReadBilling<PassRequest>(context);
    var source = request.Source is "grant" or "manual" ? request.Source : "manual";
    Licence? pass = null;
    await Changing(operatorIdentity, "add-pass", subject, request.Reason, () => { pass = billing.AddPass(subject, request.PlanId, request.Months, source, AdminLog.RequireReason(request.Reason)); return Task.CompletedTask; }, $"{request.PlanId} {request.Months}m ({source})");
    await WriteBilling(context, pass, 201);
});

app.MapPost("/v1/admin/passes/{id}/end", async (HttpContext context, string id) =>
{
    var operatorIdentity = AuthorizeOperator(context);
    var request = await ReadBilling<EndPassRequest>(context);
    await Changing(operatorIdentity, "end-pass", id, request.Reason, () => { if (!billing.EndPass(id, request.SkipGrace)) throw new ProtocolException(ErrorCode.NotFound, "That pass was not found."); return Task.CompletedTask; });
    await WriteJson(context, new Ack { Ok = true });
});

app.Run();
GC.KeepAlive(dataLock);
return 0;

public partial class Program;
