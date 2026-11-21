using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

public sealed class PcRecord
{
    public required string PcId { get; init; }
    public required byte[] PublicKeySpki { get; init; }
    public string Name { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string? OwnerUid { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class BrowserDeviceRecord
{
    public required string Id { get; init; }
    public required string OwnerUid { get; init; }
    public required string Name { get; set; }
    public required byte[] SecretHash { get; set; }
    /// <summary>When this approval was made, when its secret was last replaced, and when the browser last used it (durable, updated at most hourly). The secret is replaced on a schedule and an unused approval lapses: see Store.Authorize / RotateIfDue.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset SecretIssuedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    /// <summary>The secret before the last rotation stays valid for a short grace window, so a second open tab or an in-flight request that still holds it is not locked out.</summary>
    public byte[]? PreviousSecretHash { get; set; }
    public DateTimeOffset PreviousValidUntil { get; set; }
    public bool Active { get; set; }
    public bool Revoked { get; set; }
    /// <summary>Last authenticated request, in memory only (best effort: it says nothing about notification delivery).</summary>
    public long? LastSeenUnixMs { get; set; }
    public HashSet<string> PcIds { get; } = [];
}

public sealed class PairingRecord
{
    public required string Id { get; init; }
    public required string PcId { get; init; }
    public required byte[] SecretHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public PairingState State { get; set; } = PairingState.Issued;
    public int FailedAttempts { get; set; }
    public string? ClaimUid { get; set; }
    public string? BrowserDeviceId { get; set; }
    /// <summary>The claim reused this browser's existing device (it is being paired with one more PC) rather than creating one.</summary>
    public bool ReusedDevice { get; set; }
}

public sealed class DownloadSessionRecord
{
    public required string OwnerUid { get; init; }
    public required string BrowserDeviceId { get; init; }
    public required string PcId { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required string Revision { get; init; }
    public required string ReadToken { get; init; }
    /// <summary>"preview" or "thumb:N" for in-page previews; null for an ordinary download. Previews are never listed as transfers.</summary>
    public string? Variant { get; init; }
    public string? ContentType { get; init; }
    public bool IsPreview => Variant is not null;
    public required DateTimeOffset ExpiresAt { get; init; }
    public CancellationTokenSource Revoked { get; } = new();

    // What the relay itself observed. It knows what it wrote towards the browser; it cannot know what was saved.
    public string Id { get; } = Secrets.Token(12);
    public DateTimeOffset CreatedAt { get; init; }
    public bool Cancelled { get; set; }
    private readonly Lock _progress = new();
    private readonly List<(long Start, long End)> _covered = [];   // merged byte ranges fully written to a browser
    private long _bytesSent;
    private int _active;

    public void StreamStarted() { lock (_progress) _active++; }

    /// <summary>Records bytes [start, start+written) as written to the browser; ranges are merged and bounded.</summary>
    public void StreamEnded(long start, long written)
    {
        lock (_progress)
        {
            _active--;
            _bytesSent += written;
            if (written <= 0) return;
            _covered.Add((start, start + written));
            _covered.Sort();
            var merged = new List<(long Start, long End)>();
            foreach (var range in _covered)
            {
                if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
                else merged.Add(range);
            }
            _covered.Clear();
            _covered.AddRange(merged.Count > 64 ? merged.OrderByDescending(r => r.End - r.Start).Take(64).OrderBy(r => r.Start) : merged);
        }
    }

    public DownloadSessionSummary Summarize()
    {
        lock (_progress)
        {
            var covered = _covered.Sum(r => r.End - r.Start);
            var state = Cancelled ? DownloadState.Cancelled
                : _active > 0 ? DownloadState.Sending
                : (covered >= Size && (Size > 0 || _bytesSent == 0 && _completedEmpty)) ? DownloadState.SentToBrowser
                : _bytesSent > 0 ? DownloadState.Interrupted
                : DownloadState.Prepared;
            return new DownloadSessionSummary
            {
                SessionId = Id, PcId = PcId, Name = Name, Size = Size, Revision = Revision, State = state, BytesSentToBrowser = _bytesSent, BytesCovered = covered,
                CreatedAtUnixMs = CreatedAt.ToUnixTimeMilliseconds(), ExpiresAtUnixMs = ExpiresAt.ToUnixTimeMilliseconds(),
            };
        }
    }

    private bool _completedEmpty;
    /// <summary>A zero-byte file has nothing to cover; a completed empty response is what "sent" means for it.</summary>
    public void EmptyFileDelivered() { lock (_progress) _completedEmpty = true; }
}

public static class Secrets
{
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no look-alikes

    public static string Token(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
    public static string Code(int length) => RandomNumberGenerator.GetString(CodeAlphabet, length);
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    public static string HashHex(string secret) => Convert.ToHexStringLower(Hash(secret));
    public static bool Matches(byte[] expectedHash, string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(expectedHash, Hash(candidate));
}

/// <summary>
/// Durable-metadata boundary. PC registrations, ownership and browser-device grants survive a restart when
/// OrbitDrive:DataDirectory is set (an atomically replaced JSON snapshot; Firestore replaces it later). Pairing
/// requests, download capabilities and live streams are deliberately NOT persisted: after a restart they are gone
/// and must be re-established (fail closed). Device secrets are stored only as hashes.
/// </summary>
public sealed class Store
{
    public sealed record PcDto(string PcId, string Spki, string Name, string AgentVersion, string? OwnerUid, long LastSeenUnixMs);
    public sealed record DeviceDto(string Id, string OwnerUid, string Name, string SecretHash, bool Active, bool Revoked, string[] PcIds,
        long? CreatedAtUnixMs = null, long? SecretIssuedAtUnixMs = null, long? LastUsedAtUnixMs = null, string? PreviousSecretHash = null, long? PreviousValidUntilUnixMs = null);
    public sealed record PrefDto(string OwnerUid, int HistoryRetentionDays, bool NotifyInvitations, bool DiagnosticsConsent);
    public sealed record Snapshot(int Version, PcDto[] Pcs, DeviceDto[] Devices, string[]? Suspended = null, PrefDto[]? Prefs = null);

    private readonly GatewayOptions options;
    private readonly TimeProvider time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PcRecord> _pcs = [];
    private readonly Dictionary<string, BrowserDeviceRecord> _devices = [];
    private readonly Dictionary<string, PairingRecord> _pairings = [];
    private readonly Dictionary<string, DownloadSessionRecord> _downloads = [];
    private readonly HashSet<string> _suspended = [];
    private readonly Dictionary<string, OwnerPreferences> _prefs = [];

    private readonly ISnapshotBackend? _backend;

    /// <summary>The store keeps every record in memory (one gateway instance); <paramref name="backend"/> makes the durable parts survive a restart. Null = a file under DataDirectory when configured, otherwise nothing persists.</summary>
    public Store(GatewayOptions options, TimeProvider time, ISnapshotBackend? backend = null)
    {
        this.options = options; this.time = time;
        _backend = backend ?? (options.DataDirectory.Length == 0 ? null : new FileSnapshotBackend(options.DataDirectory, time));
        if (_backend is null) return;
        try
        {
            if (_backend.Load() is not { } snapshot) return;
            if (snapshot.Version != 1) throw new System.Text.Json.JsonException("unknown snapshot version");
            foreach (var pc in snapshot.Pcs)
                _pcs[pc.PcId] = new PcRecord { PcId = pc.PcId, PublicKeySpki = Convert.FromBase64String(pc.Spki), Name = pc.Name, AgentVersion = pc.AgentVersion, OwnerUid = pc.OwnerUid, LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(pc.LastSeenUnixMs) };
            foreach (var d in snapshot.Devices)
            {
                var device = new BrowserDeviceRecord { Id = d.Id, OwnerUid = d.OwnerUid, Name = d.Name, SecretHash = Convert.FromBase64String(d.SecretHash), Active = d.Active, Revoked = d.Revoked };
                // Approvals saved before these fields existed start their clocks now: nobody is locked out by an upgrade.
                DateTimeOffset At(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : time.GetUtcNow();
                device.CreatedAt = At(d.CreatedAtUnixMs); device.SecretIssuedAt = At(d.SecretIssuedAtUnixMs); device.LastUsedAt = At(d.LastUsedAtUnixMs);
                if (d.PreviousSecretHash is { Length: > 0 } previous) { device.PreviousSecretHash = Convert.FromBase64String(previous); device.PreviousValidUntil = At(d.PreviousValidUntilUnixMs); }
                foreach (var pcId in d.PcIds) if (_pcs.ContainsKey(pcId)) device.PcIds.Add(pcId);
                _devices[d.Id] = device;
            }
            foreach (var uid in snapshot.Suspended ?? []) _suspended.Add(uid);
            foreach (var pref in snapshot.Prefs ?? []) _prefs[pref.OwnerUid] = new OwnerPreferences { HistoryRetentionDays = pref.HistoryRetentionDays, NotifyInvitations = pref.NotifyInvitations, DiagnosticsConsent = pref.DiagnosticsConsent };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or ArgumentException or NullReferenceException)
        {
            // An unreadable snapshot never grants access: keep it aside for investigation and start with no grants.
            _pcs.Clear(); _devices.Clear(); _suspended.Clear(); _prefs.Clear();
            _backend.QuarantineUnreadable();
        }
    }

    /// <summary>Hands the backend the full durable state. Called with the gate held, after every durable change.</summary>
    private void Save()
    {
        if (_backend is null) return;
        _backend.Save(new Snapshot(1,
            [.. _pcs.Values.Select(p => new PcDto(p.PcId, Convert.ToBase64String(p.PublicKeySpki), p.Name, p.AgentVersion, p.OwnerUid, p.LastSeen.ToUnixTimeMilliseconds()))],
            [.. _devices.Values.Where(d => d.Active || d.Revoked).Select(d => new DeviceDto(d.Id, d.OwnerUid, d.Name, Convert.ToBase64String(d.SecretHash), d.Active, d.Revoked, [.. d.PcIds],
                d.CreatedAt.ToUnixTimeMilliseconds(), d.SecretIssuedAt.ToUnixTimeMilliseconds(), d.LastUsedAt.ToUnixTimeMilliseconds(),
                d.PreviousSecretHash is null ? null : Convert.ToBase64String(d.PreviousSecretHash), d.PreviousSecretHash is null ? null : d.PreviousValidUntil.ToUnixTimeMilliseconds()))],
            [.. _suspended], [.. _prefs.Select(e => new PrefDto(e.Key, e.Value.HistoryRetentionDays, e.Value.NotifyInvitations, e.Value.DiagnosticsConsent))]));
    }

    public PcRecord UpsertPc(string pcId, byte[] spki, string name, string agentVersion)
    {
        lock (_gate)
        {
            var known = _pcs.TryGetValue(pcId, out var pc);
            if (!known) _pcs[pcId] = pc = new PcRecord { PcId = pcId, PublicKeySpki = spki };
            var changed = !known || pc!.Name != name || pc.AgentVersion != agentVersion;
            pc!.Name = name; pc.AgentVersion = agentVersion; pc.LastSeen = time.GetUtcNow();
            if (changed || pc.OwnerUid is not null) Save(); // last-seen is kept for owned PCs so "offline since" survives a restart
            return pc;
        }
    }

    public void Touch(string pcId) { lock (_gate) if (_pcs.TryGetValue(pcId, out var pc)) pc.LastSeen = time.GetUtcNow(); }

    public (string Id, string Secret, DateTimeOffset ExpiresAt) CreatePairing(string pcId)
    {
        lock (_gate)
        {
            foreach (var old in _pairings.Values.Where(p => p.PcId == pcId && p.State is PairingState.Issued or PairingState.Claimed))
                old.State = PairingState.Expired; // one live request per PC
            string id;
            do id = Secrets.Code(6); while (_pairings.ContainsKey(id));
            var secret = Secrets.Code(10);
            var expires = time.GetUtcNow().AddSeconds(options.PairingTtlSeconds);
            _pairings[id] = new PairingRecord { Id = id, PcId = pcId, SecretHash = Secrets.Hash(secret), ExpiresAt = expires };
            return (id, secret, expires);
        }
    }

    private PairingRecord LivePairing(string id)
    {
        if (!_pairings.TryGetValue(id, out var pairing)) throw new ProtocolException(ErrorCode.NotFound, "Pairing request not found.");
        if (pairing.State is PairingState.Issued or PairingState.Claimed && pairing.ExpiresAt <= time.GetUtcNow()) pairing.State = PairingState.Expired;
        return pairing;
    }

    /// <summary>Atomically claims a one-use pairing request for an account and creates a pending (inactive) browser device.</summary>
    /// <param name="existing">
    /// This browser's own approved device, already proven by its secret and owned by the same account. Pairing it with another PC adds that
    /// PC to the SAME device, so one browser keeps working with every PC it was paired with. The secret is not issued again.
    /// </param>
    public (PairingRecord Pairing, BrowserDeviceRecord Device, string DeviceSecret, PcRecord Pc) ClaimPairing(string id, string secret, Identity identity, string browserName, BrowserDeviceRecord? existing = null)
    {
        lock (_gate)
        {
            if (_suspended.Contains(identity.Uid)) throw Suspension;
            var pairing = LivePairing(id);
            if (pairing.State == PairingState.Expired) throw new ProtocolException(ErrorCode.Expired, "This pairing code has expired. Create a new one on the PC.");
            if (pairing.State != PairingState.Issued) throw new ProtocolException(ErrorCode.Conflict, "This pairing code was already used.");
            if (!Secrets.Matches(pairing.SecretHash, secret))
            {
                if (++pairing.FailedAttempts >= 5) pairing.State = PairingState.Expired;
                throw new ProtocolException(ErrorCode.Forbidden, "Pairing code is not correct.");
            }
            var pc = _pcs[pairing.PcId];
            if (pc.OwnerUid is not null && pc.OwnerUid != identity.Uid)
            {
                pairing.State = PairingState.Expired;
                throw new ProtocolException(ErrorCode.Forbidden, "This PC belongs to a different account.");
            }
            string deviceSecret;
            BrowserDeviceRecord device;
            if (existing is not null && existing.OwnerUid == identity.Uid && existing.Active && !existing.Revoked && _devices.ContainsKey(existing.Id))
            {
                device = existing; deviceSecret = ""; pairing.ReusedDevice = true;
            }
            else
            {
                deviceSecret = Secrets.Token();
                device = new BrowserDeviceRecord { Id = Secrets.Token(12), OwnerUid = identity.Uid, Name = browserName, SecretHash = Secrets.Hash(deviceSecret), CreatedAt = time.GetUtcNow(), SecretIssuedAt = time.GetUtcNow(), LastUsedAt = time.GetUtcNow() };
                _devices[device.Id] = device;
            }
            pairing.State = PairingState.Claimed;
            pairing.ClaimUid = identity.Uid;
            pairing.BrowserDeviceId = device.Id;
            return (pairing, device, deviceSecret, pc);
        }
    }

    /// <summary>Undo a claim whose notification never reached the PC, so the browser is not left with a dangling device.</summary>
    public void AbandonClaim(string id)
    {
        lock (_gate)
        {
            if (!_pairings.TryGetValue(id, out var pairing) || pairing.State != PairingState.Claimed) return;
            pairing.State = PairingState.Expired;
            if (pairing.BrowserDeviceId is not null && !pairing.ReusedDevice) _devices.Remove(pairing.BrowserDeviceId);
        }
    }

    /// <summary>The PC's local decision. Ownership and the device grant are bound in one step, only on approval.</summary>
    public PairingState ConfirmPairing(string id, string pcId, bool approve)
    {
        lock (_gate)
        {
            var pairing = LivePairing(id);
            if (pairing.PcId != pcId) throw new ProtocolException(ErrorCode.NotFound, "Pairing request not found.");
            if (pairing.State == PairingState.Expired) throw new ProtocolException(ErrorCode.Expired, "The pairing request expired.");
            if (pairing.State != PairingState.Claimed) throw new ProtocolException(ErrorCode.Conflict, "Nothing is waiting for approval.");
            var device = _devices[pairing.BrowserDeviceId!];
            var pc = _pcs[pcId];
            if (!approve || (pc.OwnerUid is not null && pc.OwnerUid != pairing.ClaimUid))
            {
                pairing.State = PairingState.Denied;
                if (!pairing.ReusedDevice) _devices.Remove(device.Id);       // a device that already worked with other PCs stays
                if (approve) throw new ProtocolException(ErrorCode.Conflict, "This PC belongs to a different account.");
                return pairing.State;
            }
            pc.OwnerUid ??= pairing.ClaimUid;
            device.Active = true;
            device.PcIds.Add(pcId);
            pairing.State = PairingState.Approved;
            Save();
            return pairing.State;
        }
    }

    public PairingStatusResponse PairingStatus(string id, Identity identity)
    {
        lock (_gate)
        {
            var pairing = LivePairing(id);
            if (pairing.ClaimUid != identity.Uid) throw new ProtocolException(ErrorCode.NotFound, "Pairing request not found.");
            return new PairingStatusResponse { PairingId = id, State = pairing.State, PcId = pairing.State == PairingState.Approved ? pairing.PcId : null };
        }
    }

    /// <summary>Checks account + approved, unrevoked browser device and (optionally) its grant for one PC.</summary>
    public BrowserDeviceRecord Authorize(Identity identity, string? deviceHeader, string? pcId = null)
    {
        var denied = new ProtocolException(ErrorCode.Forbidden, "This browser is not approved. Pair it with your PC again.");
        var parts = deviceHeader?.Split('.');
        if (parts is not { Length: 2 }) throw denied;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_devices.TryGetValue(parts[0], out var device) || device.OwnerUid != identity.Uid || !device.Active || device.Revoked) throw denied;
            var current = Secrets.Matches(device.SecretHash, parts[1]);
            var previous = !current && device.PreviousSecretHash is not null && now < device.PreviousValidUntil && Secrets.Matches(device.PreviousSecretHash, parts[1]);
            if (!current && !previous) throw denied;
            if (now - device.LastUsedAt > TimeSpan.FromDays(options.DeviceIdleDays))
            {
                // An approval nobody has used for this long lapses: the browser must be paired again. (Checked after the secret, so a stranger learns nothing.)
                device.Active = false; Save();
                throw new ProtocolException(ErrorCode.Forbidden, "This browser has not been used for a long time, so its approval ended. Pair it with your PC again.");
            }
            if (_suspended.Contains(identity.Uid)) throw Suspension;
            if (pcId is not null && (!_pcs.TryGetValue(pcId, out var pc) || pc.OwnerUid != identity.Uid || !device.PcIds.Contains(pcId)))
                throw new ProtocolException(ErrorCode.NotFound, "PC not found.");
            device.LastSeenUnixMs = now.ToUnixTimeMilliseconds();
            if (now - device.LastUsedAt > TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(1).Ticks, TimeSpan.FromDays(options.DeviceIdleDays).Ticks / 4))) { device.LastUsedAt = now; Save(); }   // coarse on purpose: this is a durable write
            return device;
        }
    }

    /// <summary>
    /// Returns a NEW secret (shown once, to that browser, in a response header) when the CURRENT secret is older than DeviceRotateDays; otherwise null.
    /// A browser still presenting the previous secret (a second tab, a request in flight) is NOT given another one: that would invalidate the secret its sibling already holds.
    /// If the one answer carrying a new secret is lost, the previous secret keeps working for the grace window and the browser then pairs again.
    /// Only called for a request that has just been authorised.
    /// </summary>
    public string? RotateIfDue(BrowserDeviceRecord device, string presentedSecret)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (now - device.SecretIssuedAt < TimeSpan.FromDays(options.DeviceRotateDays) || !Secrets.Matches(device.SecretHash, presentedSecret)) return null;
            var secret = Secrets.Token();
            device.PreviousSecretHash = device.SecretHash; device.PreviousValidUntil = now.AddMinutes(options.DeviceRotationGraceMinutes);
            device.SecretHash = Secrets.Hash(secret); device.SecretIssuedAt = now;
            Save();
            return secret;
        }
    }

    public List<PcRecord> PcsFor(BrowserDeviceRecord device)
    {
        lock (_gate) return [.. _pcs.Values.Where(p => p.OwnerUid == device.OwnerUid && device.PcIds.Contains(p.PcId))];
    }

    public List<BrowserDeviceRecord> DevicesForOwner(string ownerUid)
    {
        lock (_gate) return [.. _devices.Values.Where(d => d.OwnerUid == ownerUid && d.Active && !d.Revoked)];
    }

    public string PcName(string pcId) { lock (_gate) return _pcs.TryGetValue(pcId, out var pc) ? pc.Name : ""; }

    public List<BrowserDeviceRecord> DevicesForPc(string pcId)
    {
        lock (_gate) return [.. _devices.Values.Where(d => d.Active && !d.Revoked && d.PcIds.Contains(pcId))];
    }

    /// <summary>The PC owner, at the PC, withdraws one browser's grant for that PC only. Live transfers to it are cut.</summary>
    public void RevokeGrant(string pcId, string deviceId)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || !device.PcIds.Remove(pcId))
                throw new ProtocolException(ErrorCode.NotFound, "That device is not paired with this PC.");
            foreach (var session in _downloads.Values.Where(d => d.BrowserDeviceId == deviceId && d.PcId == pcId)) session.Revoked.Cancel();
            Save();
        }
    }

    /// <summary>Disconnects a PC from its account: every grant and download for it ends and ownership is released.</summary>
    public void RemovePc(string pcId)
    {
        lock (_gate)
        {
            if (!_pcs.TryGetValue(pcId, out var pc)) throw new ProtocolException(ErrorCode.NotFound, "PC not found.");
            pc.OwnerUid = null;
            foreach (var device in _devices.Values) device.PcIds.Remove(pcId);
            foreach (var session in _downloads.Values.Where(d => d.PcId == pcId)) session.Revoked.Cancel();
            foreach (var pairing in _pairings.Values.Where(p => p.PcId == pcId && p.State is PairingState.Issued or PairingState.Claimed)) pairing.State = PairingState.Expired;
            Save();
        }
    }

    /// <summary>This browser's download sessions as the relay saw them, newest first.</summary>
    public List<DownloadSessionSummary> ListDownloads(string deviceId)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            return [.. _downloads.Values.Where(d => d.BrowserDeviceId == deviceId && !d.IsPreview && d.ExpiresAt > now).OrderByDescending(d => d.CreatedAt)
                .Select(d => (Session: d, Summary: d.Summarize()))
                // The owner's retention choice: a transfer that has ended is listed only for that many days (0 = only transfers still going or waiting).
                .Where(e => e.Summary.State is DownloadState.Sending or DownloadState.Prepared || now - e.Session.CreatedAt <= TimeSpan.FromDays(Preferences(e.Session.OwnerUid).HistoryRetentionDays))
                .Select(e => e.Summary)];
        }
    }

    /// <summary>Cancels one of this browser's download sessions: its link stops working and running streams end.</summary>
    public void CancelDownload(string deviceId, string sessionId)
    {
        lock (_gate)
        {
            var session = _downloads.Values.FirstOrDefault(d => d.Id == sessionId && d.BrowserDeviceId == deviceId)
                ?? throw new ProtocolException(ErrorCode.NotFound, "That download is not in this browser's list.");
            session.Cancelled = true;
            session.Revoked.Cancel();
        }
    }

    public string AddDownload(DownloadSessionRecord session)
    {
        var capability = Secrets.Token();
        lock (_gate)
        {
            foreach (var key in _downloads.Where(d => d.Value.ExpiresAt <= time.GetUtcNow()).Select(d => d.Key).ToList()) _downloads.Remove(key);
            var preview = session.IsPreview;
            var limit = preview ? options.MaxPreviewSessionsPerDevice : options.MaxDownloadSessionsPerDevice;
            var mine = _downloads.Where(d => d.Value.BrowserDeviceId == session.BrowserDeviceId && d.Value.IsPreview == preview).ToList();
            if (mine.Count >= limit)
            {
                // A session stays valid for its whole lifetime so a transfer can be resumed, so finished ones used to pile up until the limit refused a
                // browser that had simply saved a lot of files. Make room by retiring the oldest sessions that are over (cancelled, or completely sent);
                // sessions that are waiting, running or resumable are never taken away, and the limit still holds against those.
                foreach (var finished in mine.Where(d => d.Value.Summarize().State is DownloadState.SentToBrowser or DownloadState.Cancelled).OrderBy(d => d.Value.CreatedAt).Take(mine.Count - limit + 1).ToList())
                { _downloads.Remove(finished.Key); mine.Remove(finished); }
            }
            if (mine.Count >= limit)
                throw new ProtocolException(ErrorCode.LimitExceeded, preview ? "Too many previews are open in this browser. Close some and try again." : "This browser has too many downloads waiting. Cancel some in Transfers or wait for them to expire.");
            _downloads[Secrets.HashHex(capability)] = session;
        }
        return capability;
    }

    /// <summary>Resolves a bearer download capability and re-checks that its device grant is still live.</summary>
    public DownloadSessionRecord ResolveDownload(string capability)
    {
        var gone = new ProtocolException(ErrorCode.NotFound, "This download link is not valid.");
        if (capability.Length is < 20 or > 128) throw gone;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(Secrets.HashHex(capability), out var session)) throw gone;
            if (session.ExpiresAt <= time.GetUtcNow()) throw new ProtocolException(ErrorCode.Expired, "This download link has expired.");
            if (session.Revoked.IsCancellationRequested || !_devices.TryGetValue(session.BrowserDeviceId, out var device) || device.Revoked || !device.Active ||
                !device.PcIds.Contains(session.PcId) || !_pcs.TryGetValue(session.PcId, out var pc) || pc.OwnerUid != session.OwnerUid) throw gone;
            return session;
        }
    }

    /// <summary>Revokes a browser device: future requests fail and every download capability/stream it holds is cancelled.</summary>
    public void RevokeDevice(Identity identity, string deviceId)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || device.OwnerUid != identity.Uid)
                throw new ProtocolException(ErrorCode.NotFound, "Device not found.");
            device.Revoked = true;
            foreach (var session in _downloads.Values.Where(d => d.BrowserDeviceId == deviceId)) session.Revoked.Cancel();
            Save();
        }
    }

    /// <summary>Renames one of the account's own approved browsers. Revoked or never-approved devices cannot be renamed.</summary>
    public void RenameDevice(Identity identity, string deviceId, string name)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var device) || device.OwnerUid != identity.Uid || !device.Active || device.Revoked)
                throw new ProtocolException(ErrorCode.NotFound, "Device not found.");
            device.Name = name;
            Save();
        }
    }

    private static ProtocolException Suspension => new(ErrorCode.Forbidden, "This account is suspended. Contact the person who runs this service.");

    public bool IsSuspended(string uid) { lock (_gate) return _suspended.Contains(uid); }

    /// <summary>
    /// Blocks an account without deleting anything (reversible). Live downloads end now; the caller ends uploads and PC-side work.
    /// Returns the browser devices that were affected, so their uploads can be cancelled.
    /// </summary>
    public List<BrowserDeviceRecord> SetSuspended(string uid, bool suspended)
    {
        lock (_gate)
        {
            var owned = _devices.Values.Where(d => d.OwnerUid == uid && d.Active && !d.Revoked).ToList();
            if (suspended)
            {
                _suspended.Add(uid);
                foreach (var session in _downloads.Values.Where(d => owned.Any(o => o.Id == d.BrowserDeviceId))) session.Revoked.Cancel();
            }
            else _suspended.Remove(uid);
            Save();
            return owned;
        }
    }

    /// <summary>Every approved browser of this account stops working at once, and the account's live downloads end. Nothing on any PC is touched.</summary>
    public List<BrowserDeviceRecord> RevokeAllDevices(string uid)
    {
        lock (_gate)
        {
            var owned = _devices.Values.Where(d => d.OwnerUid == uid && d.Active && !d.Revoked).ToList();
            foreach (var device in owned) device.Revoked = true;
            foreach (var session in _downloads.Values.Where(d => owned.Any(o => o.Id == d.BrowserDeviceId))) session.Revoked.Cancel();
            Save();
            return owned;
        }
    }

    public List<PcRecord> PcsOwnedBy(string uid) { lock (_gate) return [.. _pcs.Values.Where(p => p.OwnerUid == uid)]; }

    public OwnerPreferences Preferences(string uid)
    {
        lock (_gate) return _prefs.TryGetValue(uid, out var saved) ? saved : new OwnerPreferences { HistoryRetentionDays = 7, NotifyInvitations = true, DiagnosticsConsent = false };
    }

    public static readonly int[] RetentionChoices = [0, 1, 7, 30];

    public void SetPreferences(string uid, OwnerPreferences preferences)
    {
        if (!RetentionChoices.Contains(preferences.HistoryRetentionDays)) throw new ProtocolException(ErrorCode.InvalidRequest, "Choose 0, 1, 7 or 30 days for transfer history.");
        lock (_gate) { _prefs[uid] = preferences; Save(); }
    }

    /// <summary>
    /// Account removal: every browser is revoked, every PC is released (its link to the account is cut, it keeps all of its own files and settings),
    /// and what the relay kept about the account is erased. A suspension stays, so deleting an account cannot undo a block.
    /// </summary>
    public (List<BrowserDeviceRecord> Devices, List<PcRecord> Pcs) DeleteAccount(string uid)
    {
        lock (_gate)
        {
            var devices = _devices.Values.Where(d => d.OwnerUid == uid).ToList();
            var pcs = _pcs.Values.Where(p => p.OwnerUid == uid).ToList();
            foreach (var session in _downloads.Values.Where(d => devices.Any(o => o.Id == d.BrowserDeviceId) || pcs.Any(p => p.PcId == d.PcId))) session.Revoked.Cancel();
            foreach (var device in devices) _devices.Remove(device.Id);
            foreach (var pc in pcs) pc.OwnerUid = null;
            foreach (var pairing in _pairings.Values.Where(p => p.ClaimUid == uid || pcs.Any(pc => pc.PcId == p.PcId) && p.State is PairingState.Issued or PairingState.Claimed)) pairing.State = PairingState.Expired;
            _prefs.Remove(uid);
            Save();
            return (devices.Where(d => d.Active).ToList(), pcs);
        }
    }

    public record OwnerCounts(string Uid, int Devices, int Pcs, bool Suspended);

    /// <summary>Aggregate, per account: ids and counts only. Names of browsers, PCs, folders and files are not part of this.</summary>
    public List<OwnerCounts> Owners()
    {
        lock (_gate)
        {
            var uids = _devices.Values.Where(d => d.Active && !d.Revoked).Select(d => d.OwnerUid).Concat(_pcs.Values.Where(p => p.OwnerUid is not null).Select(p => p.OwnerUid!)).Concat(_suspended).Distinct();
            return [.. uids.Select(u => new OwnerCounts(u, _devices.Values.Count(d => d.OwnerUid == u && d.Active && !d.Revoked), _pcs.Values.Count(p => p.OwnerUid == u), _suspended.Contains(u))).OrderBy(o => o.Uid, StringComparer.Ordinal)];
        }
    }

    public int PcsOnlineCount(AgentHub hub) { List<string> ids; lock (_gate) ids = [.. _pcs.Values.Where(p => p.OwnerUid is not null).Select(p => p.PcId)]; return ids.Count(hub.IsOnline); }
    public int PcCount() { lock (_gate) return _pcs.Values.Count(p => p.OwnerUid is not null); }
    public int DeviceCount() { lock (_gate) return _devices.Values.Count(d => d.Active && !d.Revoked); }
}
