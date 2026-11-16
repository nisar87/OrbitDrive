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
    public required string Name { get; init; }
    public required byte[] SecretHash { get; init; }
    public bool Active { get; set; }
    public bool Revoked { get; set; }
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
    public required DateTimeOffset ExpiresAt { get; init; }
    public CancellationTokenSource Revoked { get; } = new();
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
/// Durable-metadata boundary. This slice keeps state in memory on the single relay instance: a restart
/// forgets pairings and grants (fail closed). The Firestore-backed implementation is phase W3.
/// </summary>
public sealed class Store(GatewayOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PcRecord> _pcs = [];
    private readonly Dictionary<string, BrowserDeviceRecord> _devices = [];
    private readonly Dictionary<string, PairingRecord> _pairings = [];
    private readonly Dictionary<string, DownloadSessionRecord> _downloads = [];

    public PcRecord UpsertPc(string pcId, byte[] spki, string name, string agentVersion)
    {
        lock (_gate)
        {
            if (!_pcs.TryGetValue(pcId, out var pc)) _pcs[pcId] = pc = new PcRecord { PcId = pcId, PublicKeySpki = spki };
            pc.Name = name; pc.AgentVersion = agentVersion; pc.LastSeen = time.GetUtcNow();
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
    public (PairingRecord Pairing, BrowserDeviceRecord Device, string DeviceSecret, PcRecord Pc) ClaimPairing(string id, string secret, Identity identity, string browserName)
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
            var deviceSecret = Secrets.Token();
            var device = new BrowserDeviceRecord { Id = Secrets.Token(12), OwnerUid = identity.Uid, Name = browserName, SecretHash = Secrets.Hash(deviceSecret) };
            _devices[device.Id] = device;
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
            if (pairing.BrowserDeviceId is not null) _devices.Remove(pairing.BrowserDeviceId);
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
                _devices.Remove(device.Id);
                if (approve) throw new ProtocolException(ErrorCode.Conflict, "This PC belongs to a different account.");
                return pairing.State;
            }
            pc.OwnerUid ??= pairing.ClaimUid;
            device.Active = true;
            device.PcIds.Add(pcId);
            return pairing.State = PairingState.Approved;
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
            return device;
        }
    }

    public List<PcRecord> PcsFor(BrowserDeviceRecord device)
    {
        lock (_gate) return [.. _pcs.Values.Where(p => p.OwnerUid == device.OwnerUid && device.PcIds.Contains(p.PcId))];
    }

    public string AddDownload(DownloadSessionRecord session)
    {
        var capability = Secrets.Token();
        lock (_gate)
        {
            foreach (var key in _downloads.Where(d => d.Value.ExpiresAt <= time.GetUtcNow()).Select(d => d.Key).ToList()) _downloads.Remove(key);
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
        }
    }
}
