using System.Text.Json;

namespace OrbitDrive.Gateway;

/// <summary>JSON for the billing endpoints: camelCase, unknown members refused (an owner's typo must not silently do nothing).</summary>
public static class BillingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
}

public sealed record PriceView(int Months, decimal Usd, int Pkr, bool PkrApprox);
public sealed record PlanView(string Id, string Name, bool Purchasable, PlanLimits Limits, IReadOnlyList<PriceView> Prices);
/// <summary>What the public website and the sign-in-free price list show. Rupee amounts are approximate unless the owner fixed them.</summary>
public sealed record PlansView(string Currency, decimal PkrPerUsd, DateTimeOffset RateUpdatedAt, bool LimitsEnabled, IReadOnlyList<PlanView> Plans);

public sealed record AccountPlanView(string PlanId, string PlanName, string State, DateTimeOffset? EndsAt, DateTimeOffset? GraceEndsAt, int DaysLeft, PlanLimits Limits,
    int FilesToday, long BytesToday, DateTimeOffset ResetsAt, int Pcs, int Devices, bool LimitsEnabled);

public sealed record QuoteRequest(string PlanId, int Months, string? Coupon);
public sealed record OrderRequest(string PlanId, int Months, string? Coupon, string? Method);
public sealed record ProofRequest(string Method, string Reference);
public sealed record PlansUpdate(IReadOnlyList<Plan> Plans, string Reason);
public sealed record RateUpdate(decimal PkrPerUsd, string Reason);
public sealed record LimitsUpdate(bool Enabled, string Reason);
public sealed record CouponUpdate(string Kind, decimal Value, IReadOnlyList<string>? PlanIds, DateTimeOffset? ExpiresAt, int? MaxRedemptions, bool Active, string? Note, string Reason);
public sealed record PassRequest(string PlanId, int Months, string Reason, string? Source);
public sealed record EndPassRequest(bool SkipGrace, string Reason);
public sealed record DecisionRequest(string Reason);
public sealed record AdminBillingView(PlansView Plans, IReadOnlyList<Plan> Editable, IReadOnlyList<Coupon> Coupons, int OpenOrders);
public sealed record UserBillingView(string Uid, AccountPlanView Plan, IReadOnlyList<Licence> Passes, IReadOnlyList<Order> Orders);

public static class BillingViews
{
    public static PlansView Public(BillingStore billing)
    {
        var rate = billing.Rate;
        return new PlansView("USD", rate.PkrPerUsd, rate.UpdatedAt, billing.LimitsEnabled, [.. billing.Plans.Select(p => new PlanView(p.Id, p.Name, p.Purchasable, p.Limits,
            [.. p.Prices.OrderBy(x => x.Months).Select(x => new PriceView(x.Months, x.Usd, PlanCatalog.PkrFor(x, rate), x.PkrOverride is null))]))]);
    }

    public static AccountPlanView Account(BillingStore billing, string subject, DateTimeOffset now, int pcs, int devices)
    {
        var a = billing.AllowanceFor(subject);
        var e = billing.EntitlementFor(subject);
        return new AccountPlanView(a.PlanId, a.PlanName, a.State.ToString().ToLowerInvariant(), e.EndsAt, e.GraceEndsAt, Entitlements.DaysLeft(e, now), a.Limits, a.FilesToday, a.BytesToday, a.ResetsAt, pcs, devices, billing.LimitsEnabled);
    }
}
