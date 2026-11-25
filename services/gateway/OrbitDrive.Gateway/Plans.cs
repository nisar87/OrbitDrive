using System.Globalization;

namespace OrbitDrive.Gateway;

/// <summary>What an account may do. A null value means no limit (the Office plan still has fair-use alerts, see the design notes).</summary>
public sealed record PlanLimits(int? Pcs, int? Devices, int? FilesPerDay, long? BytesPerFile, long? BytesPerDay);

/// <summary>A list price for one pass length. USD is the list price; PKR is derived from the exchange rate unless the owner set it by hand.</summary>
public sealed record PlanPrice(int Months, decimal Usd, int? PkrOverride = null);

public sealed record Plan(string Id, string Name, int Rank, PlanLimits Limits, IReadOnlyList<PlanPrice> Prices, bool Purchasable = true);

/// <summary>PKR per one USD, set by the owner (or refreshed automatically later). Always shown as approximate.</summary>
public sealed record ExchangeRate(decimal PkrPerUsd, DateTimeOffset UpdatedAt);

public static class PlanCatalog
{
    public const string Free = "free", Plus = "plus", Office = "office";
    public const long MiB = 1024 * 1024, GiB = 1024 * MiB;

    public static readonly ExchangeRate DefaultRate = new(280m, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero));

    /// <summary>The launch plans exactly as published on the website (docs/branding/PRICING_DECISIONS.md). PKR figures are the agreed ones.</summary>
    public static IReadOnlyList<Plan> Defaults() =>
    [
        new(Free, "Free", 0, new PlanLimits(1, 1, 5, 100 * MiB, 300 * MiB), [], Purchasable: false),
        new(Plus, "Plus", 1, new PlanLimits(3, 5, 50, 1 * GiB, 20 * GiB),
            [new(1, 3.50m, 1000), new(3, 9m, 2500), new(12, 29m, 8000)]),
        new(Office, "Office", 2, new PlanLimits(10, null, null, 2 * GiB, 100 * GiB),
            [new(1, 11m, 3000), new(3, 27m, 7500), new(12, 86m, 24000)]),
    ];

    /// <summary>Approximate rupees for a dollar price, rounded to the nearest 50 so a quoted amount is a clean number to pay.</summary>
    public static int PkrFor(decimal usd, ExchangeRate rate)
    {
        if (usd < 0 || rate.PkrPerUsd <= 0) throw new ArgumentOutOfRangeException(nameof(usd), "price and rate must be positive");
        return (int)(Math.Round(usd * rate.PkrPerUsd / 50m, MidpointRounding.AwayFromZero) * 50m);
    }

    public static int PkrFor(PlanPrice price, ExchangeRate rate) => price.PkrOverride ?? PkrFor(price.Usd, rate);

    public static string UsdText(decimal usd) => usd.ToString(usd % 1 == 0 ? "0" : "0.00", CultureInfo.InvariantCulture);
}

public enum EntitlementState { Free, Active, Grace }

/// <summary>What an account gets right now. Limits are those of the plan in force; Grace means the pass ended within the grace window.</summary>
public sealed record Entitlement(string PlanId, string PlanName, PlanLimits Limits, EntitlementState State, DateTimeOffset? EndsAt, DateTimeOffset? GraceEndsAt);

/// <summary>A pass an account (or later an organisation) holds. Subject is "acct:{uid}" or "org:{id}". Source is manual, coupon, grant or gateway.</summary>
public sealed record Licence(string Id, string Subject, string PlanId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Source, string? Note = null);

public static class Entitlements
{
    public static readonly TimeSpan Grace = TimeSpan.FromDays(3);

    /// <summary>
    /// The plan in force at <paramref name="now"/>. Of the passes that have started and not yet passed their grace window, the highest-ranked plan wins; ties go
    /// to the one ending later. With none, the account has the Free plan. An unknown plan id in a stored licence is ignored, never trusted.
    /// </summary>
    public static Entitlement Resolve(IEnumerable<Licence> licences, IReadOnlyList<Plan> plans, DateTimeOffset now)
    {
        var free = plans.Single(p => p.Id == PlanCatalog.Free);
        Licence? best = null; Plan? bestPlan = null;
        foreach (var licence in licences)
        {
            var plan = plans.FirstOrDefault(p => p.Id == licence.PlanId);
            if (plan is null || licence.EndsAt <= licence.StartsAt) continue;
            if (now < licence.StartsAt || now >= licence.EndsAt + Grace) continue;
            if (bestPlan is null || plan.Rank > bestPlan.Rank || (plan.Rank == bestPlan.Rank && licence.EndsAt > best!.EndsAt)) { best = licence; bestPlan = plan; }
        }
        if (best is null || bestPlan is null) return new Entitlement(free.Id, free.Name, free.Limits, EntitlementState.Free, null, null);
        var active = now < best.EndsAt;
        return new Entitlement(bestPlan.Id, bestPlan.Name, bestPlan.Limits, active ? EntitlementState.Active : EntitlementState.Grace, best.EndsAt, best.EndsAt + Grace);
    }

    /// <summary>
    /// A new pass for <paramref name="months"/> of <paramref name="planId"/>. Passes of the same plan stack: it starts when the latest one of that plan ends
    /// (or now, if none is running), so buying early never loses days.
    /// </summary>
    public static Licence Extend(IEnumerable<Licence> existing, string subject, string planId, int months, DateTimeOffset now, string source, string id, string? note = null)
    {
        if (months < 1 || months > 36) throw new ArgumentOutOfRangeException(nameof(months));
        var start = now;
        foreach (var licence in existing)
            if (licence.Subject == subject && licence.PlanId == planId && licence.EndsAt > start) start = licence.EndsAt;
        return new Licence(id, subject, planId, start, start.AddMonths(months), source, note);
    }

    /// <summary>Whole days left in the current pass, rounded up, for display. Zero once it has ended.</summary>
    public static int DaysLeft(Entitlement e, DateTimeOffset now) =>
        e.EndsAt is { } end && end > now ? (int)Math.Ceiling((end - now).TotalDays) : 0;
}
