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
    public required byte[] SecretHash { get; init; }
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
    private sealed record PcDto(string PcId, string Spki, string Name, string AgentVersion, string? OwnerUid, long LastSeenUnixMs);
    private sealed record DeviceDto(string Id, string OwnerUid, string Name, string SecretHash, bool Active, bool Revoked, string[] PcIds);
    private sealed record Snapshot(int Version, PcDto[] Pcs, DeviceDto[] Devices);

    private readonly GatewayOptions options;
    private readonly TimeProvider time;
    private readonly string? _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PcRecord> _pcs = [];
    private readonly Dictionary<string, BrowserDeviceRecord> _devices = [];
    private readonly Dictionary<string, PairingRecord> _pairings = [];
    private readonly Dictionary<string, DownloadSessionRecord> _downloads = [];

    public Store(GatewayOptions options, TimeProvider time)
    {
        this.options = options; this.time = time;
        if (options.DataDirectory.Length == 0) return;
        Directory.CreateDirectory(options.DataDirectory);
        _file = Path.Join(options.DataDirectory, "registrations.json");
        if (!File.Exists(_file)) return;
        try
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file)) ?? throw new System.Text.Json.JsonException("empty");
            if (snapshot.Version != 1) throw new System.Text.Json.JsonException("unknown snapshot version");
            foreach (var pc in snapshot.Pcs)
                _pcs[pc.PcId] = new PcRecord { PcId = pc.PcId, PublicKeySpki = Convert.FromBase64String(pc.Spki), Name = pc.Name, AgentVersion = pc.AgentVersion, OwnerUid = pc.OwnerUid, LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(pc.LastSeenUnixMs) };
            foreach (var d in snapshot.Devices)
            {
                var device = new BrowserDeviceRecord { Id = d.Id, OwnerUid = d.OwnerUid, Name = d.Name, SecretHash = Convert.FromBase64String(d.SecretHash), Active = d.Active, Revoked = d.Revoked };
                foreach (var pcId in d.PcIds) if (_pcs.ContainsKey(pcId)) device.PcIds.Add(pcId);
                _devices[d.Id] = device;
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or ArgumentException or NullReferenceException)
        {
            // An unreadable snapshot never grants access: keep it aside for investigation and start with no grants.
            _pcs.Clear(); _devices.Clear();
            File.Move(_file, _file + ".corrupt-" + time.GetUtcNow().ToUnixTimeSeconds(), overwrite: true);
        }
    }

    /// <summary>Writes the snapshot atomically. Called with the gate held, after every durable change.</summary>
    private void Save()
    {
        if (_file is null) return;
        var snapshot = new Snapshot(1,
            [.. _pcs.Values.Select(p => new PcDto(p.PcId, Convert.ToBase64String(p.PublicKeySpki), p.Name, p.AgentVersion, p.OwnerUid, p.LastSeen.ToUnixTimeMilliseconds()))],
            [.. _devices.Values.Where(d => d.Active || d.Revoked).Select(d => new DeviceDto(d.Id, d.OwnerUid, d.Name, Convert.ToBase64String(d.SecretHash), d.Active, d.Revoked, [.. d.PcIds]))]);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(snapshot));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _file, overwrite: true);
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
                device = new BrowserDeviceRecord { Id = Secrets.Token(12), OwnerUid = identity.Uid, Name = browserName, SecretHash = Secrets.Hash(deviceSecret) };
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
            if (!_devices.TryGetValue(parts[0], out var device) || !Secrets.Matches(device.SecretHash, parts[1]) ||
                device.OwnerUid != identity.Uid || !device.Active || device.Revoked) throw denied;
            if (pcId is not null && (!_pcs.TryGetValue(pcId, out var pc) || pc.OwnerUid != identity.Uid || !device.PcIds.Contains(pcId)))
                throw new ProtocolException(ErrorCode.NotFound, "PC not found.");
            device.LastSeenUnixMs = time.GetUtcNow().ToUnixTimeMilliseconds();
            return device;
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
            return [.. _downloads.Values.Where(d => d.BrowserDeviceId == deviceId && !d.IsPreview && d.ExpiresAt > time.GetUtcNow()).OrderByDescending(d => d.CreatedAt).Select(d => d.Summarize())];
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
            if (_downloads.Values.Count(d => d.BrowserDeviceId == session.BrowserDeviceId && d.IsPreview == preview) >= (preview ? options.MaxPreviewSessionsPerDevice : options.MaxDownloadSessionsPerDevice))
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
}
