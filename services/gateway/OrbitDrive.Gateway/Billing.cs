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

/// <summary>A discount code the owner creates. Kind is "percent" (Value 1-100) or "fixed" (Value is USD off). Redeemed counts orders that used it and are not rejected or cancelled.</summary>
public sealed record Coupon(string Code, string Kind, decimal Value, IReadOnlyList<string> PlanIds, DateTimeOffset? ExpiresAt, int? MaxRedemptions, int Redeemed, bool Active, string? Note);

/// <summary>
/// A request to buy a pass, with the price locked when it was made. Status: requested, proof_submitted, verified, rejected, cancelled.
/// Payment is checked by a person in v1 (bank transfer, JazzCash, Easypaisa); an online gateway later calls the same verify step.
/// </summary>
public sealed record Order(string Id, string Subject, string PlanId, int Months, decimal ListUsd, decimal DueUsd, int DuePkr, string? CouponCode, string Status,
    DateTimeOffset CreatedAt, string? Method, string? Reference, string? LicenceId, string? DecidedBy, DateTimeOffset? DecidedAt, string? Reason);

public sealed record Quote(string PlanId, string PlanName, int Months, decimal ListUsd, decimal DueUsd, int ListPkr, int DuePkr, string? CouponCode, decimal DiscountUsd);

/// <summary>An organisation (a company or office) sharing the owner's Office pass. Members keep their own sign-in; the owner and admins manage who belongs.</summary>
public sealed record Organisation(string Id, string Name, string OwnerUid, DateTimeOffset CreatedAt);
/// <summary>Role is owner, admin or member. Label is how the organisation refers to the person (a name they type when joining), never read from their files.</summary>
public sealed record OrgMember(string OrgId, string Uid, string Role, DateTimeOffset JoinedAt, string Label);
public sealed record OrgInvite(string Code, string OrgId, DateTimeOffset ExpiresAt);
/// <summary>What an organisation's admins did: who, what, when. Never file or folder names.</summary>
public sealed record OrgEvent(string Id, string OrgId, DateTimeOffset At, string By, string Action, string Detail);

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
    private readonly Dictionary<string, Coupon> _coupons = [];
    private readonly Dictionary<string, Order> _orders = [];
    private readonly Dictionary<string, Organisation> _orgs = [];
    private readonly Dictionary<string, OrgMember> _members = [];                 // key: orgId|uid
    private readonly Dictionary<string, OrgInvite> _invites = [];
    private readonly Dictionary<string, List<OrgEvent>> _orgEvents = [];
    private readonly Dictionary<string, string?> _dirty = [];
    private IReadOnlyList<Plan> _plans = PlanCatalog.Defaults();
    private ExchangeRate _rate = PlanCatalog.DefaultRate;
    private readonly Timer? _timer;
    private bool _limitsEnabled;
    private string _instructions = "";

    public BillingStore(IBillingPersistence persistence, TimeProvider time, ILogger? log = null, TimeSpan? flushEvery = null, bool limitsEnabled = false)
    {
        _persistence = persistence; _time = time; _log = log; _limitsEnabled = limitsEnabled;
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
            case "config" when parts.Length == 2 && parts[1] == "instructions" && JsonSerializer.Deserialize<string>(json) is { } text: _instructions = text; break;
            case "config" when parts.Length == 2 && parts[1] == "limits" && JsonSerializer.Deserialize<bool>(json) is var enabled: _limitsEnabled = enabled; break;
            case "org" when JsonSerializer.Deserialize<Organisation>(json) is { } org: _orgs[org.Id] = org; break;
            case "orgmember" when JsonSerializer.Deserialize<OrgMember>(json) is { } member: _members[member.OrgId + "|" + member.Uid] = member; break;
            case "orginvite" when JsonSerializer.Deserialize<OrgInvite>(json) is { } invite: _invites[invite.Code] = invite; break;
            case "orgevent" when JsonSerializer.Deserialize<OrgEvent>(json) is { } evt: (_orgEvents.TryGetValue(evt.OrgId, out var list) ? list : _orgEvents[evt.OrgId] = []).Add(evt); break;
            case "coupon" when JsonSerializer.Deserialize<Coupon>(json) is { } coupon: _coupons[coupon.Code] = coupon; break;
            case "order" when JsonSerializer.Deserialize<Order>(json) is { } order: _orders[order.Id] = order; break;
            case "usage" when parts.Length == 3 && JsonSerializer.Deserialize<UsageDay>(json) is { } day: _usage[parts[1] + "|" + parts[2]] = (day.Files, day.Bytes); break;
            case "config" when parts.Length == 2 && parts[1] == "plans" && JsonSerializer.Deserialize<List<Plan>>(json) is { } plans && plans.Any(p => p.Id == PlanCatalog.Free): _plans = plans; break;
            case "config" when parts.Length == 2 && parts[1] == "rate" && JsonSerializer.Deserialize<ExchangeRate>(json) is { PkrPerUsd: > 0 } rate: _rate = rate; break;
        }
    }

    private sealed record UsageDay(int Files, long Bytes);

    // ---- plans and rate ------------------------------------------------------------------------------------------------------------------------

    /// <summary>Whether the plan limits are enforced. Counting always happens; this is the owner's "start date" switch (set in the panel, kept across restarts).</summary>
    public bool LimitsEnabled { get { lock (_gate) return _limitsEnabled; } }

    /// <summary>How to pay (account title, number, steps), written by the owner and shown only to signed-in customers placing an order.</summary>
    public string PaymentInstructions { get { lock (_gate) return _instructions; } }

    public void SetPaymentInstructions(string text)
    {
        text = text.Trim();
        if (text.Length > 1500 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new ProtocolException(ErrorCode.InvalidRequest, "The payment instructions are too long or contain unusual characters.");
        lock (_gate) { _instructions = text; Mark("config/instructions", JsonSerializer.Serialize(text)); }
    }

    public void SetLimitsEnabled(bool enabled) { lock (_gate) { _limitsEnabled = enabled; Mark("config/limits", JsonSerializer.Serialize(enabled)); } }

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
        lock (_gate)
        {
            if (subject.StartsWith("org:", StringComparison.Ordinal) && _orgs.TryGetValue(subject[4..], out var org)) subject = AccountSubject(org.OwnerUid);   // an organisation has its owner's pass
            return Entitlements.Resolve(_licences.Values.Where(l => l.Subject == subject), _plans, _time.GetUtcNow());
        }
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
            if (subject.StartsWith("acct:", StringComparison.Ordinal) && MembershipLocked(subject[5..]) is { } mine)
            {
                if (mine.Role == "owner") RemoveOrgLocked(mine.OrgId); else DropMember(mine);
            }
            foreach (var id in _licences.Where(l => l.Value.Subject == subject).Select(l => l.Key).ToList()) { _licences.Remove(id); Mark("licence/" + id, null); }
            foreach (var id in _orders.Where(o => o.Value.Subject == subject).Select(o => o.Key).ToList()) { _orders.Remove(id); Mark("order/" + id, null); }
            foreach (var key in _usage.Keys.Where(k => k.StartsWith(subject + "|", StringComparison.Ordinal)).ToList())
            {
                _usage.Remove(key);
                Mark($"usage/{subject}/{key[(subject.Length + 1)..]}", null);
            }
        }
    }

    private static string Describe(long bytes) => bytes >= PlanCatalog.GiB ? $"{bytes / (double)PlanCatalog.GiB:0.#} GB" : $"{bytes / PlanCatalog.MiB} MB";

    // ---- coupons and orders --------------------------------------------------------------------------------------------------------------------

    private static readonly string[] Methods = ["bank", "jazzcash", "easypaisa", "raast", "other"];
    private const int MaxOpenOrders = 5;

    public static string CleanCode(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (c.Length is < 3 or > 32 || !c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw new ProtocolException(ErrorCode.InvalidRequest, "Codes are 3 to 32 letters, digits, dashes or underscores.");
        return c;
    }

    public IReadOnlyList<Coupon> Coupons() { lock (_gate) return [.. _coupons.Values.OrderBy(c => c.Code, StringComparer.Ordinal)]; }

    public Coupon SaveCoupon(string code, string kind, decimal value, IReadOnlyList<string>? planIds, DateTimeOffset? expiresAt, int? maxRedemptions, bool active, string? note)
    {
        code = CleanCode(code);
        if (kind == "percent" ? value is < 1 or > 100 : kind == "fixed" ? value is <= 0 or > 100_000 : true) throw new ProtocolException(ErrorCode.InvalidRequest, "A coupon is a percentage from 1 to 100 or a positive amount in dollars.");
        if (maxRedemptions is < 1 or > 1_000_000) throw new ProtocolException(ErrorCode.InvalidRequest, "The redemption limit must be at least 1.");
        if (note is { Length: > 200 }) throw new ProtocolException(ErrorCode.InvalidRequest, "The note is too long.");
        lock (_gate)
        {
            var plans = planIds ?? [];
            if (plans.Any(id => !_plans.Any(p => p.Id == id && p.Id != PlanCatalog.Free))) throw new ProtocolException(ErrorCode.InvalidRequest, "A coupon can only name plans that are for sale.");
            var redeemed = _coupons.TryGetValue(code, out var old) ? old.Redeemed : 0;
            var coupon = new Coupon(code, kind, value, [.. plans], expiresAt, maxRedemptions, redeemed, active, note);
            _coupons[code] = coupon;
            Mark("coupon/" + code, JsonSerializer.Serialize(coupon));
            return coupon;
        }
    }

    /// <summary>The price for a plan and length, with an optional coupon. Throws when the plan or length is not for sale or the code cannot be used now.</summary>
    public Quote QuoteFor(string planId, int months, string? couponCode)
    {
        lock (_gate) return QuoteLocked(planId, months, couponCode);
    }

    private Quote QuoteLocked(string planId, int months, string? couponCode)
    {
        var plan = _plans.FirstOrDefault(p => p.Id == planId && p.Purchasable) ?? throw new ProtocolException(ErrorCode.InvalidRequest, "That plan is not for sale.");
        var price = plan.Prices.FirstOrDefault(p => p.Months == months) ?? throw new ProtocolException(ErrorCode.InvalidRequest, "That pass length is not offered.");
        var listPkr = PlanCatalog.PkrFor(price, _rate);
        if (string.IsNullOrWhiteSpace(couponCode)) return new Quote(plan.Id, plan.Name, months, price.Usd, price.Usd, listPkr, listPkr, null, 0);
        var code = CleanCode(couponCode);
        var now = _time.GetUtcNow();
        if (!_coupons.TryGetValue(code, out var coupon) || !coupon.Active || (coupon.ExpiresAt is { } end && now >= end) || (coupon.MaxRedemptions is { } max && coupon.Redeemed >= max)
            || (coupon.PlanIds.Count > 0 && !coupon.PlanIds.Contains(planId)))
            throw new ProtocolException(ErrorCode.InvalidRequest, "That code is not valid for this plan.");
        var due = coupon.Kind == "percent" ? Math.Round(price.Usd * (100 - coupon.Value) / 100m, 2, MidpointRounding.AwayFromZero) : Math.Max(0m, price.Usd - coupon.Value);
        var duePkr = price.Usd == 0 ? 0 : (int)(Math.Round(listPkr * due / price.Usd / 50m, MidpointRounding.AwayFromZero) * 50m);
        return new Quote(plan.Id, plan.Name, months, price.Usd, due, listPkr, duePkr, code, price.Usd - due);
    }

    public Order CreateOrder(string subject, string planId, int months, string? couponCode, string? method)
    {
        if (method is not null && !Methods.Contains(method)) throw new ProtocolException(ErrorCode.InvalidRequest, "Choose bank, jazzcash, easypaisa, raast or other.");
        lock (_gate)
        {
            if (_orders.Values.Count(o => o.Subject == subject && o.Status is ("requested" or "proof_submitted")) >= MaxOpenOrders)
                throw new ProtocolException(ErrorCode.LimitExceeded, "You already have several open orders. Cancel one or wait for it to be checked.");
            var quote = QuoteLocked(planId, months, couponCode);
            if (quote.CouponCode is { } code) { var c = _coupons[code]; var used = c with { Redeemed = c.Redeemed + 1 }; _coupons[code] = used; Mark("coupon/" + code, JsonSerializer.Serialize(used)); }
            var order = new Order(Guid.NewGuid().ToString("N")[..12], subject, quote.PlanId, months, quote.ListUsd, quote.DueUsd, quote.DuePkr, quote.CouponCode, "requested", _time.GetUtcNow(), method, null, null, null, null, null);
            _orders[order.Id] = order;
            Mark("order/" + order.Id, JsonSerializer.Serialize(order));
            return order;
        }
    }

    public IReadOnlyList<Order> OrdersFor(string subject) { lock (_gate) return [.. _orders.Values.Where(o => o.Subject == subject).OrderByDescending(o => o.CreatedAt)]; }

    public IReadOnlyList<Order> AllOrders(string? status) { lock (_gate) return [.. _orders.Values.Where(o => status is null || o.Status == status).OrderByDescending(o => o.CreatedAt).Take(500)]; }

    public Order? FindOrder(string id) { lock (_gate) return _orders.GetValueOrDefault(id); }

    /// <summary>The customer says they paid and gives the payment reference (a transaction id or the last digits). Text only; no screenshots are stored.</summary>
    public Order SubmitProof(string subject, string orderId, string method, string reference)
    {
        reference = reference.Trim();
        if (!Methods.Contains(method)) throw new ProtocolException(ErrorCode.InvalidRequest, "Choose bank, jazzcash, easypaisa, raast or other.");
        if (reference.Length is < 4 or > 80 || reference.Any(char.IsControl)) throw new ProtocolException(ErrorCode.InvalidRequest, "Enter the payment reference (4 to 80 characters).");
        lock (_gate)
        {
            var order = OwnOpenOrder(subject, orderId);
            var updated = order with { Status = "proof_submitted", Method = method, Reference = reference };
            _orders[orderId] = updated;
            Mark("order/" + orderId, JsonSerializer.Serialize(updated));
            return updated;
        }
    }

    private Order OwnOpenOrder(string subject, string orderId)
    {
        if (!_orders.TryGetValue(orderId, out var order) || order.Subject != subject) throw new ProtocolException(ErrorCode.NotFound, "That order was not found.");
        if (order.Status is not ("requested" or "proof_submitted")) throw new ProtocolException(ErrorCode.Conflict, "That order is already closed.");
        return order;
    }

    public Order CancelOrder(string subject, string orderId)
    {
        lock (_gate) return Close(OwnOpenOrder(subject, orderId), "cancelled", null, "cancelled by the customer");
    }

    /// <summary>The owner confirms the money arrived: closes the order and adds the pass (stacking after any running pass of that plan).</summary>
    public Order VerifyOrder(string orderId, string by)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order)) throw new ProtocolException(ErrorCode.NotFound, "That order was not found.");
            if (order.Status is not ("requested" or "proof_submitted")) throw new ProtocolException(ErrorCode.Conflict, "That order is already closed.");
            var licence = AddPass(order.Subject, order.PlanId, order.Months, "order", "order " + order.Id);
            var done = order with { Status = "verified", LicenceId = licence.Id, DecidedBy = by, DecidedAt = _time.GetUtcNow() };
            _orders[orderId] = done;
            Mark("order/" + orderId, JsonSerializer.Serialize(done));
            return done;
        }
    }

    public Order RejectOrder(string orderId, string by, string reason)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order)) throw new ProtocolException(ErrorCode.NotFound, "That order was not found.");
            if (order.Status is not ("requested" or "proof_submitted")) throw new ProtocolException(ErrorCode.Conflict, "That order is already closed.");
            return Close(order, "rejected", by, reason);
        }
    }

    private Order Close(Order order, string status, string? by, string reason)
    {
        if (order.CouponCode is { } code && _coupons.TryGetValue(code, out var c) && c.Redeemed > 0)
        {
            var back = c with { Redeemed = c.Redeemed - 1 };
            _coupons[code] = back;
            Mark("coupon/" + code, JsonSerializer.Serialize(back));
        }
        var closed = order with { Status = status, DecidedBy = by, DecidedAt = _time.GetUtcNow(), Reason = reason.Length > 200 ? reason[..200] : reason };
        _orders[order.Id] = closed;
        Mark("order/" + order.Id, JsonSerializer.Serialize(closed));
        return closed;
    }

    // ---- organisations -------------------------------------------------------------------------------------------------------------------------

    private const int MaxMembers = 100, MaxInvites = 20, MaxEvents = 200;
    private static readonly TimeSpan InviteLife = TimeSpan.FromDays(7);

    private OrgMember? MembershipLocked(string uid) => _members.Values.FirstOrDefault(m => m.Uid == uid);
    private static ProtocolException Deny(string text) => new(ErrorCode.Forbidden, text);

    public (Organisation Org, OrgMember Me)? OrgOf(string uid)
    {
        lock (_gate) return MembershipLocked(uid) is { } me && _orgs.TryGetValue(me.OrgId, out var org) ? (org, me) : null;
    }

    /// <summary>The billing subject for a person: their organisation when they belong to one whose owner still has an Office pass (or its grace), otherwise themselves.</summary>
    public string SubjectFor(string uid)
    {
        lock (_gate)
        {
            if (MembershipLocked(uid) is { } me && _orgs.TryGetValue(me.OrgId, out var org))
            {
                var e = EntitlementFor(AccountSubject(org.OwnerUid));
                if (e.PlanId == "office" && e.State != EntitlementState.Free) return "org:" + org.Id;
            }
            return AccountSubject(uid);
        }
    }

    /// <summary>Everyone whose PCs and devices count together with this person's: the members of their organisation, or just them.</summary>
    public IReadOnlyList<string> PoolOf(string uid)
    {
        lock (_gate)
        {
            if (SubjectFor(uid) is { } s && s.StartsWith("org:", StringComparison.Ordinal)) return [.. _members.Values.Where(m => m.OrgId == s[4..]).Select(m => m.Uid)];
            return [uid];
        }
    }

    public Organisation CreateOrg(string ownerUid, string name)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 60 || name.Any(char.IsControl)) throw new ProtocolException(ErrorCode.InvalidRequest, "Give the organisation a name of 2 to 60 characters.");
        lock (_gate)
        {
            if (MembershipLocked(ownerUid) is not null) throw new ProtocolException(ErrorCode.Conflict, "You already belong to an organisation.");
            var plan = EntitlementFor(AccountSubject(ownerUid));
            if (plan.PlanId != "office" || plan.State == EntitlementState.Free) throw new ProtocolException(ErrorCode.Forbidden, "Organisations come with the Office plan.");
            var org = new Organisation(Guid.NewGuid().ToString("N")[..12], name, ownerUid, _time.GetUtcNow());
            _orgs[org.Id] = org; Mark("org/" + org.Id, JsonSerializer.Serialize(org));
            AddMember(org.Id, ownerUid, "owner", "Owner");
            Audit(org.Id, ownerUid, "organisation-created", name);
            return org;
        }
    }

    private void AddMember(string orgId, string uid, string role, string label)
    {
        var member = new OrgMember(orgId, uid, role, _time.GetUtcNow(), label);
        _members[orgId + "|" + uid] = member;
        Mark($"orgmember/{orgId}/{uid}", JsonSerializer.Serialize(member));
    }

    private void Audit(string orgId, string by, string action, string detail)
    {
        var evt = new OrgEvent(Guid.NewGuid().ToString("N")[..12], orgId, _time.GetUtcNow(), by, action, detail.Length > 200 ? detail[..200] : detail);
        if (!_orgEvents.TryGetValue(orgId, out var list)) _orgEvents[orgId] = list = [];
        list.Add(evt);
        Mark($"orgevent/{orgId}/{evt.Id}", JsonSerializer.Serialize(evt));
        while (list.Count > MaxEvents) { var old = list[0]; list.RemoveAt(0); Mark($"orgevent/{orgId}/{old.Id}", null); }
    }

    public IReadOnlyList<OrgEvent> OrgAudit(string orgId, string byUid)
    {
        lock (_gate)
        {
            RequireAdmin(orgId, byUid);
            return [.. (_orgEvents.GetValueOrDefault(orgId) ?? []).OrderByDescending(e => e.At).Take(100)];
        }
    }

    private OrgMember RequireAdmin(string orgId, string uid) =>
        _members.TryGetValue(orgId + "|" + uid, out var m) && m.Role is ("owner" or "admin") ? m : throw Deny("Only an organisation owner or admin can do that.");

    public IReadOnlyList<OrgMember> MembersOf(string orgId, string byUid)
    {
        lock (_gate)
        {
            if (!_members.ContainsKey(orgId + "|" + byUid)) throw Deny("You are not in that organisation.");
            return [.. _members.Values.Where(m => m.OrgId == orgId).OrderBy(m => m.JoinedAt)];
        }
    }

    public OrgInvite Invite(string orgId, string byUid)
    {
        lock (_gate)
        {
            RequireAdmin(orgId, byUid);
            var now = _time.GetUtcNow();
            foreach (var old in _invites.Values.Where(i => i.ExpiresAt <= now).Select(i => i.Code).ToList()) { _invites.Remove(old); Mark("orginvite/" + old, null); }
            if (_invites.Values.Count(i => i.OrgId == orgId) >= MaxInvites) throw new ProtocolException(ErrorCode.LimitExceeded, "There are already many unused invitations. Wait for them to expire.");
            if (_members.Values.Count(m => m.OrgId == orgId) >= MaxMembers) throw new ProtocolException(ErrorCode.LimitExceeded, "The organisation has the most members it can have.");
            var invite = new OrgInvite(Secrets.Code(10), orgId, now + InviteLife);
            _invites[invite.Code] = invite; Mark("orginvite/" + invite.Code, JsonSerializer.Serialize(invite));
            Audit(orgId, byUid, "invitation-created", "single-use code, 7 days");
            return invite;
        }
    }

    /// <summary>Joins with a single-use code. A person can be in one organisation at a time.</summary>
    public Organisation Join(string uid, string code, string label)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        label = label.Trim();
        if (label.Length is < 1 or > 40 || label.Any(char.IsControl)) throw new ProtocolException(ErrorCode.InvalidRequest, "Enter the name your organisation knows you by (up to 40 characters).");
        lock (_gate)
        {
            if (MembershipLocked(uid) is not null) throw new ProtocolException(ErrorCode.Conflict, "You already belong to an organisation. Leave it first.");
            if (!_invites.TryGetValue(code, out var invite) || invite.ExpiresAt <= _time.GetUtcNow() || !_orgs.TryGetValue(invite.OrgId, out var org))
                throw new ProtocolException(ErrorCode.InvalidRequest, "That invitation is not valid or has expired.");
            if (_members.Values.Count(m => m.OrgId == org.Id) >= MaxMembers) throw new ProtocolException(ErrorCode.LimitExceeded, "The organisation has the most members it can have.");
            _invites.Remove(code); Mark("orginvite/" + code, null);
            AddMember(org.Id, uid, "member", label);
            Audit(org.Id, uid, "member-joined", label);
            return org;
        }
    }

    public void RemoveMember(string orgId, string byUid, string targetUid)
    {
        lock (_gate)
        {
            var by = RequireAdmin(orgId, byUid);
            if (!_members.TryGetValue(orgId + "|" + targetUid, out var target)) throw new ProtocolException(ErrorCode.NotFound, "That person is not in the organisation.");
            if (target.Role == "owner") throw Deny("The owner cannot be removed.");
            if (target.Role == "admin" && by.Role != "owner") throw Deny("Only the owner can remove an admin.");
            DropMember(target);
            Audit(orgId, byUid, "member-removed", target.Label);
        }
    }

    public void Leave(string uid)
    {
        lock (_gate)
        {
            var me = MembershipLocked(uid) ?? throw new ProtocolException(ErrorCode.NotFound, "You are not in an organisation.");
            if (me.Role == "owner") throw Deny("The owner cannot leave. Delete the organisation instead.");
            DropMember(me);
            Audit(me.OrgId, uid, "member-left", me.Label);
        }
    }

    public void SetRole(string orgId, string byUid, string targetUid, string role)
    {
        lock (_gate)
        {
            var by = RequireAdmin(orgId, byUid);
            if (by.Role != "owner") throw Deny("Only the owner can change roles.");
            if (role is not ("admin" or "member")) throw new ProtocolException(ErrorCode.InvalidRequest, "A role is admin or member.");
            if (!_members.TryGetValue(orgId + "|" + targetUid, out var target) || target.Role == "owner") throw new ProtocolException(ErrorCode.NotFound, "That person is not in the organisation.");
            var changed = target with { Role = role };
            _members[orgId + "|" + targetUid] = changed;
            Mark($"orgmember/{orgId}/{targetUid}", JsonSerializer.Serialize(changed));
            Audit(orgId, byUid, "role-changed", $"{target.Label} is now {role}");
        }
    }

    private void DropMember(OrgMember member)
    {
        _members.Remove(member.OrgId + "|" + member.Uid);
        Mark($"orgmember/{member.OrgId}/{member.Uid}", null);
    }

    /// <summary>Deletes an organisation and everything kept about it (the owner chose to, or the owner's account was deleted).</summary>
    public void DeleteOrg(string orgId, string byUid)
    {
        lock (_gate)
        {
            if (!_orgs.TryGetValue(orgId, out var org) || org.OwnerUid != byUid) throw Deny("Only the owner can delete the organisation.");
            RemoveOrgLocked(orgId);
        }
    }

    private void RemoveOrgLocked(string orgId)
    {
        foreach (var m in _members.Values.Where(m => m.OrgId == orgId).ToList()) DropMember(m);
        foreach (var i in _invites.Values.Where(i => i.OrgId == orgId).Select(i => i.Code).ToList()) { _invites.Remove(i); Mark("orginvite/" + i, null); }
        if (_orgEvents.Remove(orgId, out var events)) foreach (var e in events) Mark($"orgevent/{orgId}/{e.Id}", null);
        if (_orgs.Remove(orgId)) Mark("org/" + orgId, null);
    }

    /// <summary>Records usage for the pool a person belongs to and, always, for the person (so admins can see each member's own counts).</summary>
    public void RecordTransferFor(string uid, long bytes, bool fileCompleted)
    {
        var subject = SubjectFor(uid);
        RecordTransfer(subject, bytes, fileCompleted);
        if (subject != AccountSubject(uid)) RecordTransfer(AccountSubject(uid), bytes, fileCompleted);
    }

    public void RequireTransferFor(string uid, long size) => RequireTransfer(SubjectFor(uid), size);

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

/// <summary>Maps a billing entry key to a Firestore document id (slashes are not allowed in ids) and back.</summary>
public static class BillingIds
{
    public static string Encode(string key) => key.Replace("%", "%25", StringComparison.Ordinal).Replace("~", "%7E", StringComparison.Ordinal).Replace("/", "~", StringComparison.Ordinal);
    public static string Decode(string id) => id.Replace("~", "/", StringComparison.Ordinal).Replace("%7E", "~", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal);
}
