using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// Where billing state lives. Entries are small JSON documents under a slash-separated key ("licence/{id}", "usage/{subject}/{day}", "config/plans").
/// A null JSON value deletes the entry. Implementations must tolerate being called from a background thread and must not throw for a normal write
/// (the store retries failures). Like the rest of the gateway's durable state, one gateway instance owns the database (the Firestore writer lease).
/// </summary>
public interface IBillingPersistence
{
    IReadOnlyList<KeyValuePair<string, string>> LoadAll();
    void Write(IReadOnlyList<KeyValuePair<string, string?>> changes);
}

/// <summary>One atomically replaced JSON file (owner-only on Linux): the development relay's billing backend.</summary>
public sealed class FileBillingPersistence : IBillingPersistence
{
    private readonly string _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _all;

    public FileBillingPersistence(string directory)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Join(directory, "billing.json");
        _all = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [] : [];
    }

    public IReadOnlyList<KeyValuePair<string, string>> LoadAll() { lock (_gate) return [.. _all]; }

    public void Write(IReadOnlyList<KeyValuePair<string, string?>> changes)
    {
        lock (_gate)
        {
            foreach (var (key, value) in changes) { if (value is null) _all.Remove(key); else _all[key] = value; }
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_all));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, _file, overwrite: true);
        }
    }
}

/// <summary>Keeps everything in memory only (tests, and a relay run without a data directory).</summary>
public sealed class MemoryBillingPersistence : IBillingPersistence
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _all = [];
    public int Writes { get; private set; }
    public IReadOnlyList<KeyValuePair<string, string>> LoadAll() { lock (_gate) return [.. _all]; }
    public void Write(IReadOnlyList<KeyValuePair<string, string?>> changes)
    {
        lock (_gate) { Writes++; foreach (var (key, value) in changes) { if (value is null) _all.Remove(key); else _all[key] = value; } }
    }
}

/// <summary>What the gateway tells a caller about today's allowance (counts only, never names).</summary>
public sealed record Allowance(string PlanId, string PlanName, EntitlementState State, DateTimeOffset? EndsAt, PlanLimits Limits, int FilesToday, long BytesToday, DateTimeOffset ResetsAt);

/// <summary>
/// Plans, passes and per-day usage for accounts. Everything is held in memory and mirrored to <see cref="IBillingPersistence"/> in the background (changed entries
/// only), so a restart keeps passes and today's counts. If the database cannot be reached the gateway keeps serving from memory and retries; it never blocks people
/// from their own files because of a bookkeeping failure (fail open for cost limits). The usage day is the Pakistan day (UTC+5, no daylight saving).
/// </summary>
public sealed class BillingStore : IDisposable
{
    public static readonly TimeSpan PakistanOffset = TimeSpan.FromHours(5);
    private const int KeepUsageDays = 40;
    private readonly IBillingPersistence _persistence;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Licence> _licences = [];
    private readonly Dictionary<string, (int Files, long Bytes)> _usage = [];     // key: subject|day
    private readonly Dictionary<string, string?> _dirty = [];
    private IReadOnlyList<Plan> _plans = PlanCatalog.Defaults();
    private ExchangeRate _rate = PlanCatalog.DefaultRate;
    private readonly Timer? _timer;

    public BillingStore(IBillingPersistence persistence, TimeProvider time, ILogger? log = null, TimeSpan? flushEvery = null)
    {
        _persistence = persistence; _time = time; _log = log;
        foreach (var (key, json) in persistence.LoadAll())
        {
            try { LoadEntry(key, json); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { _log?.LogError("Skipped an unreadable billing entry ({Kind})", key.Split('/')[0]); }
        }
        if (flushEvery is { } every) _timer = new Timer(_ => Flush(), null, every, every);
    }

    private void LoadEntry(string key, string json)
    {
        var parts = key.Split('/');
        switch (parts[0])
        {
            case "licence" when JsonSerializer.Deserialize<Licence>(json) is { } licence: _licences[licence.Id] = licence; break;
            case "usage" when parts.Length == 3 && JsonSerializer.Deserialize<UsageDay>(json) is { } day: _usage[parts[1] + "|" + parts[2]] = (day.Files, day.Bytes); break;
            case "config" when parts.Length == 2 && parts[1] == "plans" && JsonSerializer.Deserialize<List<Plan>>(json) is { } plans && plans.Any(p => p.Id == PlanCatalog.Free): _plans = plans; break;
            case "config" when parts.Length == 2 && parts[1] == "rate" && JsonSerializer.Deserialize<ExchangeRate>(json) is { PkrPerUsd: > 0 } rate: _rate = rate; break;
        }
    }

    private sealed record UsageDay(int Files, long Bytes);

    // ---- plans and rate ------------------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<Plan> Plans { get { lock (_gate) return _plans; } }
    public ExchangeRate Rate { get { lock (_gate) return _rate; } }

    public void SetPlans(IReadOnlyList<Plan> plans)
    {
        Validate(plans);
        lock (_gate) { _plans = plans; Mark("config/plans", JsonSerializer.Serialize(plans)); }
    }

    public void SetRate(decimal pkrPerUsd)
    {
        if (pkrPerUsd is < 1 or > 100_000) throw new ProtocolException(ErrorCode.InvalidRequest, "The exchange rate must be between 1 and 100000 rupees per dollar.");
        var rate = new ExchangeRate(pkrPerUsd, _time.GetUtcNow());
        lock (_gate) { _rate = rate; Mark("config/rate", JsonSerializer.Serialize(rate)); }
    }

    /// <summary>Plans an owner edits must stay sane: Free present and not purchasable, unique ids, positive prices, non-negative limits.</summary>
    public static void Validate(IReadOnlyList<Plan> plans)
    {
        if (plans.Count is < 1 or > 12 || plans.Select(p => p.Id).Distinct().Count() != plans.Count) throw new ProtocolException(ErrorCode.InvalidRequest, "Plan list is not valid.");
        if (plans.SingleOrDefault(p => p.Id == PlanCatalog.Free) is not { Purchasable: false }) throw new ProtocolException(ErrorCode.InvalidRequest, "The Free plan must exist and cannot be sold.");
        foreach (var plan in plans)
        {
            if (plan.Id.Length is < 1 or > 32 || !plan.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || plan.Name.Length is < 1 or > 40) throw new ProtocolException(ErrorCode.InvalidRequest, "Plan name or id is not valid.");
            var l = plan.Limits;
            if (l.Pcs is < 0 || l.Devices is < 0 || l.FilesPerDay is < 0 || l.BytesPerFile is < 0 || l.BytesPerDay is < 0) throw new ProtocolException(ErrorCode.InvalidRequest, "Limits cannot be negative.");
            foreach (var price in plan.Prices)
                if (price.Months is < 1 or > 36 || price.Usd <= 0 || price.Usd > 100_000 || price.PkrOverride is <= 0) throw new ProtocolException(ErrorCode.InvalidRequest, "Prices must be positive and passes 1 to 36 months.");
            if (plan.Prices.Select(p => p.Months).Distinct().Count() != plan.Prices.Count) throw new ProtocolException(ErrorCode.InvalidRequest, "Each pass length may be priced once.");
        }
    }

    // ---- passes --------------------------------------------------------------------------------------------------------------------------------

    public static string AccountSubject(string uid) => "acct:" + uid;

    public Entitlement EntitlementFor(string subject)
    {
        lock (_gate) return Entitlements.Resolve(_licences.Values.Where(l => l.Subject == subject), _plans, _time.GetUtcNow());
    }

    public IReadOnlyList<Licence> LicencesFor(string subject)
    {
        lock (_gate) return [.. _licences.Values.Where(l => l.Subject == subject).OrderBy(l => l.StartsAt)];
    }

    /// <summary>Adds a pass for a subject, stacking after a running pass of the same plan. Returns the stored pass.</summary>
    public Licence AddPass(string subject, string planId, int months, string source, string? note = null)
    {
        lock (_gate)
        {
            if (!_plans.Any(p => p.Id == planId && p.Id != PlanCatalog.Free)) throw new ProtocolException(ErrorCode.InvalidRequest, "Unknown plan.");
            var licence = Entitlements.Extend(_licences.Values, subject, planId, months, _time.GetUtcNow(), source, Guid.NewGuid().ToString("N"), note);
            _licences[licence.Id] = licence;
            Mark("licence/" + licence.Id, JsonSerializer.Serialize(licence));
            return licence;
        }
    }

    /// <summary>Ends a pass now (cancel). The account falls back to the grace window, then Free. Returns false for an unknown id.</summary>
    public bool EndPass(string licenceId, bool skipGrace)
    {
        lock (_gate)
        {
            if (!_licences.TryGetValue(licenceId, out var licence)) return false;
            var now = _time.GetUtcNow();
            var ended = licence with { EndsAt = skipGrace ? now - Entitlements.Grace : now };
            if (ended.EndsAt <= ended.StartsAt) ended = ended with { StartsAt = ended.EndsAt.AddSeconds(-1) };
            _licences[licenceId] = ended;
            Mark("licence/" + licenceId, JsonSerializer.Serialize(ended));
            return true;
        }
    }

    // ---- usage ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The Pakistan calendar day number (days since 1970-01-01 in UTC+5).</summary>
    public static int DayOf(DateTimeOffset moment) => (int)Math.Floor((moment.ToUniversalTime() + PakistanOffset).ToUnixTimeSeconds() / 86400.0);

    public static DateTimeOffset NextReset(DateTimeOffset moment) => DateTimeOffset.FromUnixTimeSeconds((DayOf(moment) + 1) * 86400L) - PakistanOffset;

    private static string UsageKey(string subject, int day) => subject + "|" + day;

    public (int Files, long Bytes) UsageToday(string subject)
    {
        lock (_gate) return _usage.GetValueOrDefault(UsageKey(subject, DayOf(_time.GetUtcNow())));
    }

    public Allowance AllowanceFor(string subject)
    {
        var e = EntitlementFor(subject);
        var (files, bytes) = UsageToday(subject);
        return new Allowance(e.PlanId, e.PlanName, e.State, e.EndsAt, e.Limits, files, bytes, NextReset(_time.GetUtcNow()));
    }

    /// <summary>
    /// Refuses a transfer that would break the plan: a file larger than the per-file limit, or no files / bytes left today. Called before the work starts.
    /// <paramref name="size"/> is the file's size when known (0 when not known yet). The check is before the work, so one transfer may run slightly past the daily bytes.
    /// </summary>
    public void RequireTransfer(string subject, long size)
    {
        var a = AllowanceFor(subject);
        var l = a.Limits;
        if (l.BytesPerFile is { } perFile && size > perFile)
            throw new ProtocolException(ErrorCode.LimitExceeded, $"This file is larger than the {Describe(perFile)} per-file limit of the {a.PlanName} plan.");
        if (l.FilesPerDay is { } files && a.FilesToday >= files)
            throw new ProtocolException(ErrorCode.LimitExceeded, $"The {a.PlanName} plan allows {files} files a day and today's are used. It resets at midnight Pakistan time.");
        if (l.BytesPerDay is { } bytes && a.BytesToday >= bytes)
            throw new ProtocolException(ErrorCode.LimitExceeded, $"The {a.PlanName} plan's data allowance for today is used. It resets at midnight Pakistan time.");
    }

    /// <summary>Records bytes moved. A file counts once, when it completes: failed or cancelled transfers add bytes but not a file.</summary>
    public void RecordTransfer(string subject, long bytes, bool fileCompleted)
    {
        if (bytes < 0) bytes = 0;
        if (bytes == 0 && !fileCompleted) return;
        lock (_gate)
        {
            var day = DayOf(_time.GetUtcNow());
            var key = UsageKey(subject, day);
            var (files, total) = _usage.GetValueOrDefault(key);
            files += fileCompleted ? 1 : 0;
            total = long.MaxValue - total < bytes ? long.MaxValue : total + bytes;
            _usage[key] = (files, total);
            Mark($"usage/{subject}/{day}", JsonSerializer.Serialize(new UsageDay(files, total)));
        }
    }

    /// <summary>Test/support: forget today's count for a subject (the owner reset it).</summary>
    public void ResetToday(string subject)
    {
        lock (_gate)
        {
            var day = DayOf(_time.GetUtcNow());
            if (_usage.Remove(UsageKey(subject, day))) Mark($"usage/{subject}/{day}", null);
        }
    }

    /// <summary>Removes everything held for an account (account deletion): passes and usage.</summary>
    public void Forget(string subject)
    {
        lock (_gate)
        {
            foreach (var id in _licences.Where(l => l.Value.Subject == subject).Select(l => l.Key).ToList()) { _licences.Remove(id); Mark("licence/" + id, null); }
            foreach (var key in _usage.Keys.Where(k => k.StartsWith(subject + "|", StringComparison.Ordinal)).ToList())
            {
                _usage.Remove(key);
                Mark($"usage/{subject}/{key[(subject.Length + 1)..]}", null);
            }
        }
    }

    private static string Describe(long bytes) => bytes >= PlanCatalog.GiB ? $"{bytes / (double)PlanCatalog.GiB:0.#} GB" : $"{bytes / PlanCatalog.MiB} MB";

    // ---- persistence ---------------------------------------------------------------------------------------------------------------------------

    private void Mark(string key, string? json) { _dirty[key] = json; }

    /// <summary>Writes every changed entry. On failure the entries stay marked and the next flush retries. Old usage days are dropped first.</summary>
    public void Flush()
    {
        List<KeyValuePair<string, string?>> batch;
        lock (_gate)
        {
            var oldest = DayOf(_time.GetUtcNow()) - KeepUsageDays;
            foreach (var key in _usage.Keys.Where(k => int.TryParse(k[(k.LastIndexOf('|') + 1)..], out var d) && d < oldest).ToList())
            {
                _usage.Remove(key);
                _dirty[$"usage/{key[..key.LastIndexOf('|')]}/{key[(key.LastIndexOf('|') + 1)..]}"] = null;
            }
            if (_dirty.Count == 0) return;
            batch = [.. _dirty];
        }
        try
        {
            _persistence.Write(batch);
            lock (_gate) foreach (var (key, value) in batch) if (_dirty.TryGetValue(key, out var current) && current == value) _dirty.Remove(key);
        }
        catch (Exception e)
        {
            _log?.LogWarning("Could not save billing state ({Reason}); will retry", e.GetType().Name);
        }
    }

    public void Dispose() { _timer?.Dispose(); Flush(); }
}
