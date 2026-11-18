using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// What the relay really did since this process started: counters only, no names, paths, file contents or addresses. The operator dashboard shows
/// these and nothing else, so every number on it is a measurement. Per-account relay bytes are kept for the current UTC day only, to enforce the
/// daily cap, and are forgotten when the day ends.
/// </summary>
public sealed class Metrics(GatewayOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Day, long Bytes)> _daily = [];
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();
    private long _down, _up, _downloadsOk, _downloadsFailed, _piecesOk, _piecesFailed, _refusals;

    public long Down => Interlocked.Read(ref _down);
    public long Up => Interlocked.Read(ref _up);
    public long DownloadsCompleted => Interlocked.Read(ref _downloadsOk);
    public long DownloadsFailed => Interlocked.Read(ref _downloadsFailed);
    public long PiecesRelayed => Interlocked.Read(ref _piecesOk);
    public long PiecesFailed => Interlocked.Read(ref _piecesFailed);
    public long QuotaRefusals => Interlocked.Read(ref _refusals);

    private int Today => (int)(time.GetUtcNow().ToUnixTimeSeconds() / 86400);

    /// <summary>Refuses new relay work for an account that already used its day's allowance. The check is before the work, so one transfer may run slightly past the cap.</summary>
    public void RequireRelayAllowance(string ownerUid)
    {
        if (options.MaxRelayBytesPerOwnerPerDay <= 0) return;
        if (RelayBytesToday(ownerUid) < options.MaxRelayBytesPerOwnerPerDay) return;
        Interlocked.Increment(ref _refusals);
        throw new ProtocolException(ErrorCode.LimitExceeded, "This account has used its transfer allowance for today. It resets at midnight UTC.");
    }

    public long RelayBytesToday(string ownerUid)
    {
        lock (_gate) return _daily.TryGetValue(ownerUid, out var entry) && entry.Day == Today ? entry.Bytes : 0;
    }

    private void Add(string ownerUid, long bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            var day = Today;
            _daily.TryGetValue(ownerUid, out var entry);
            _daily[ownerUid] = (day, (entry.Day == day ? entry.Bytes : 0) + bytes);
            if (_daily.Count > 4096) foreach (var stale in _daily.Where(e => e.Value.Day != day).Select(e => e.Key).ToList()) _daily.Remove(stale);
        }
    }

    public void Downloaded(string ownerUid, long bytes, bool completed)
    {
        Interlocked.Add(ref _down, bytes); Add(ownerUid, bytes);
        if (completed) Interlocked.Increment(ref _downloadsOk); else Interlocked.Increment(ref _downloadsFailed);
    }

    public void PieceRelayed(string ownerUid, long bytes, bool stored)
    {
        if (stored) { Interlocked.Add(ref _up, bytes); Add(ownerUid, bytes); Interlocked.Increment(ref _piecesOk); }
        else Interlocked.Increment(ref _piecesFailed);
    }

    /// <summary>Test/support: forget one account's day count (the account was deleted).</summary>
    public void Forget(string ownerUid) { lock (_gate) _daily.Remove(ownerUid); }
}

/// <summary>
/// Operator-set values that must survive a restart (the minimum PC version) and the audit trail of privileged actions. The trail names the
/// operator and the account acted on (by id, never by file or path) and is the only operator history kept; it is retained until the operator
/// deletes the data directory, even after an account is deleted. Appends go to a file the process only ever adds to.
/// </summary>
public sealed class AdminLog
{
    private const int KeepInMemory = 500;
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _recent = [];
    private readonly TimeProvider _time;
    private readonly string? _auditFile, _settingsFile;
    private string _minAgentVersion;

    public AdminLog(GatewayOptions options, TimeProvider time)
    {
        _time = time; _minAgentVersion = options.MinAgentVersion;
        if (options.DataDirectory.Length == 0) return;
        Directory.CreateDirectory(options.DataDirectory);
        _auditFile = Path.Join(options.DataDirectory, "admin-audit.jsonl");
        _settingsFile = Path.Join(options.DataDirectory, "admin-settings.json");
        try { if (File.Exists(_settingsFile) && JsonSerializer.Deserialize<AdminSettings>(File.ReadAllText(_settingsFile), ProtocolJson.Options) is { } saved && IsValidVersion(saved.MinAgentVersion)) _minAgentVersion = saved.MinAgentVersion; }
        catch (Exception e) when (e is JsonException or IOException) { /* keep the configured value */ }
        try
        {
            if (File.Exists(_auditFile))
                foreach (var line in File.ReadLines(_auditFile).TakeLast(KeepInMemory))
                    try { if (JsonSerializer.Deserialize<AuditEntry>(line, ProtocolJson.Options) is { } entry) _recent.Add(entry); } catch (JsonException) { }
        }
        catch (IOException) { }
    }

    public static bool IsValidVersion(string value) => value.Length == 0 || (value.Length <= 32 && Version.TryParse(value, out _));

    public string MinAgentVersion { get { lock (_gate) return _minAgentVersion; } }

    /// <summary>True when a PC's reported version is below the minimum. A version that cannot be read counts as too old once a minimum is set.</summary>
    public bool IsBelowMinimum(string agentVersion)
    {
        var minimum = MinAgentVersion;
        if (minimum.Length == 0) return false;
        var parsed = Version.TryParse(agentVersion.Split('+', '-')[0], out var reported) ? reported : null;
        return parsed is null || parsed < Version.Parse(minimum);
    }

    public void SetMinAgentVersion(string value)
    {
        if (!IsValidVersion(value)) throw new ProtocolException(ErrorCode.InvalidRequest, "The minimum version must look like 1.2.3, or be empty for no minimum.");
        lock (_gate)
        {
            _minAgentVersion = value;
            if (_settingsFile is null) return;
            var temp = _settingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new AdminSettings { MinAgentVersion = value }, ProtocolJson.Options));
            File.Move(temp, _settingsFile, overwrite: true);
        }
    }

    public void Record(string operatorUid, string action, string target, string detail)
    {
        var entry = new AuditEntry { AtUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds(), Operator = operatorUid, Action = action, Target = target, Detail = detail.Length > 200 ? detail[..200] : detail };
        lock (_gate)
        {
            _recent.Add(entry);
            if (_recent.Count > KeepInMemory) _recent.RemoveAt(0);
            if (_auditFile is not null) File.AppendAllText(_auditFile, JsonSerializer.Serialize(entry, ProtocolJson.Options) + "\n");
        }
    }

    public List<AuditEntry> Recent(int count) { lock (_gate) return [.. _recent.TakeLast(count).Reverse()]; }
}
