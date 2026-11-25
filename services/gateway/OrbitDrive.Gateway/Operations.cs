using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// What the relay really did since this process started: counters only, no names, paths, file contents or addresses. The operator dashboard shows
/// these and nothing else, so every number on it is a measurement. Per-account relay bytes are kept for the current UTC day only, to enforce the
/// daily cap, and are forgotten when the day ends.
/// </summary>
public sealed class Metrics(GatewayOptions options, TimeProvider time, BillingStore? ledger = null)
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

    /// <summary>Bytes relayed today (UTC) by every account together: the number to watch for cost.</summary>
    public long RelayBytesTodayAll { get { if (ledger is not null) return ledger.RelayBytesAllToday(Today); lock (_gate) return _daily.Where(e => e.Value.Day == Today).Sum(e => e.Value.Bytes); } }
    public bool CostAlert => options.RelayBytesDailyAlert > 0 && RelayBytesTodayAll >= options.RelayBytesDailyAlert;

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
        if (ledger is not null) return ledger.RelayBytesToday(ownerUid, Today);
        lock (_gate) return _daily.TryGetValue(ownerUid, out var entry) && entry.Day == Today ? entry.Bytes : 0;
    }

    private void Add(string ownerUid, long bytes)
    {
        if (bytes <= 0) return;
        if (ledger is not null) { ledger.AddRelayBytes(ownerUid, Today, bytes); return; }   // kept in the billing store, so a restart does not give everyone their allowance back (F-018)
        lock (_gate)
        {
            var day = Today;
            _daily.TryGetValue(ownerUid, out var entry);
            _daily[ownerUid] = (day, (entry.Day == day ? entry.Bytes : 0) + bytes);
            if (_daily.Count > 4096) foreach (var stale in _daily.Where(e => e.Value.Day != day).Select(e => e.Key).ToList()) _daily.Remove(stale);
        }
    }

    /// <summary>A file opened to be viewed (not a download): its bytes count towards the relay's cost and the day's allowance, but not as a completed or failed download.</summary>
    public void Viewed(string ownerUid, long bytes)
    {
        Interlocked.Add(ref _down, bytes); Add(ownerUid, bytes);
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
    public void Forget(string ownerUid) { ledger?.ForgetRelayToday(ownerUid, Today); lock (_gate) _daily.Remove(ownerUid); }
}

/// <summary>Where the audit trail and operator settings live when there is no data directory: a database collection (Firestore mode). Registered only then.</summary>
public sealed record AdminDocuments(IBillingPersistence Store);

/// <summary>
/// Operator-set values that must survive a restart (the minimum PC version) and the audit trail of privileged actions. The trail names the
/// operator and the account acted on (by id, never by file or path) and is the only operator history kept; it is retained until the operator
/// deletes the data (data directory or the "admin" collection), even after an account is deleted. A record is written BEFORE the change it describes,
/// and when it cannot be written the change must not happen (<see cref="Record"/> throws).
/// </summary>
public sealed class AdminLog
{
    private const int KeepInMemory = 500;
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _recent = [];
    private readonly TimeProvider _time;
    private readonly string? _auditFile, _settingsFile;
    private readonly IBillingPersistence? _documents;
    private string _minAgentVersion;
    private long _sequence;

    public AdminLog(GatewayOptions options, TimeProvider time, AdminDocuments? documents = null)
    {
        _time = time; _minAgentVersion = options.MinAgentVersion; _documents = documents?.Store;
        if (_documents is not null)
        {
            var entries = new List<AuditEntry>();
            foreach (var (key, json) in _documents.LoadAll())
            {
                try
                {
                    if (key.StartsWith("audit/", StringComparison.Ordinal) && JsonSerializer.Deserialize<AuditEntry>(json, ProtocolJson.Options) is { } entry) entries.Add(entry);
                    else if (key == "setting/minagent" && JsonSerializer.Deserialize<string>(json) is { } saved && IsValidVersion(saved)) _minAgentVersion = saved;
                }
                catch (JsonException) { /* an unreadable entry is skipped, never trusted */ }
            }
            _recent.AddRange(entries.OrderBy(e => e.AtUnixMs).TakeLast(KeepInMemory));
            return;
        }
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

    /// <summary>The reason an operator gives for a change: present, 3 to 200 characters once trimmed, no control characters. Checked before anything is done.</summary>
    public static string RequireReason(string? reason)
    {
        var text = (reason ?? "").Trim();
        if (text.Length is < 3 or > 200 || text.Any(char.IsControl)) throw new ProtocolException(ErrorCode.InvalidRequest, "Give a reason of 3 to 200 characters.");
        return text;
    }

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
            try
            {
                if (_documents is not null) _documents.Write([new("setting/minagent", JsonSerializer.Serialize(value))]);
                else if (_settingsFile is not null)
                {
                    var temp = _settingsFile + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(new AdminSettings { MinAgentVersion = value }, ProtocolJson.Options));
                    File.Move(temp, _settingsFile, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException || _documents is not null) { throw new ProtocolException(ErrorCode.Unavailable, "The setting could not be saved, so nothing was changed."); }
            _minAgentVersion = value;
        }
    }

    /// <summary>
    /// Writes one audit entry. Throws <see cref="ProtocolException"/> (Unavailable) when it cannot be stored; the caller has not changed anything yet, so
    /// the operator action does not happen without a record.
    /// </summary>
    public void Record(string operatorUid, string action, string target, string detail)
    {
        detail ??= "";
        var entry = new AuditEntry { AtUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds(), Operator = operatorUid, Action = action, Target = target, Detail = detail.Length > 200 ? detail[..200] : detail };
        lock (_gate)
        {
            try
            {
                if (_documents is not null) _documents.Write([new($"audit/{entry.AtUnixMs:D13}-{++_sequence:D6}-{Guid.NewGuid().ToString("N")[..6]}", JsonSerializer.Serialize(entry, ProtocolJson.Options))]);
                else if (_auditFile is not null) File.AppendAllText(_auditFile, JsonSerializer.Serialize(entry, ProtocolJson.Options) + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException || _documents is not null)
            {
                throw new ProtocolException(ErrorCode.Unavailable, "The action could not be recorded in the audit log, so nothing was changed. Try again.");
            }
            _recent.Add(entry);
            if (_recent.Count > KeepInMemory) _recent.RemoveAt(0);
        }
    }

    public List<AuditEntry> Recent(int count) { lock (_gate) return [.. _recent.TakeLast(count).Reverse()]; }
}
