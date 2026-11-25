using OrbitDrive.Gateway;

namespace OrbitDrive.Gateway.Tests;

public class PlansTests
{
    private static readonly DateTimeOffset T0 = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<Plan> Plans = PlanCatalog.Defaults();

    [Fact]
    public void Nothing_held_means_the_published_free_plan()
    {
        var e = Entitlements.Resolve([], Plans, T0);
        Assert.Equal(EntitlementState.Free, e.State);
        Assert.Equal(new PlanLimits(1, 1, 5, 100 * PlanCatalog.MiB, 300 * PlanCatalog.MiB), e.Limits);
        Assert.Null(e.EndsAt);
    }

    [Fact]
    public void The_published_paid_limits_are_what_the_catalogue_holds()
    {
        var plus = Plans.Single(p => p.Id == "plus");
        Assert.Equal(new PlanLimits(3, 5, 50, PlanCatalog.GiB, 20 * PlanCatalog.GiB), plus.Limits);
        Assert.Equal([1000, 2500, 8000], plus.Prices.Select(p => PlanCatalog.PkrFor(p, PlanCatalog.DefaultRate)));
        var office = Plans.Single(p => p.Id == "office");
        Assert.Null(office.Limits.Devices);
        Assert.Null(office.Limits.FilesPerDay);
        Assert.Equal(10, office.Limits.Pcs);
        Assert.Equal([3000, 7500, 24000], office.Prices.Select(p => PlanCatalog.PkrFor(p, PlanCatalog.DefaultRate)));
    }

    [Fact]
    public void An_active_pass_applies_then_grace_keeps_it_for_three_days_then_free_returns()
    {
        var pass = new Licence("l1", "acct:a", "plus", T0, T0.AddMonths(1), "manual");
        Assert.Equal(EntitlementState.Active, Entitlements.Resolve([pass], Plans, T0.AddDays(10)).State);
        var grace = Entitlements.Resolve([pass], Plans, T0.AddMonths(1).AddDays(2));
        Assert.Equal(EntitlementState.Grace, grace.State);
        Assert.Equal("plus", grace.PlanId);
        var after = Entitlements.Resolve([pass], Plans, T0.AddMonths(1).AddDays(3));
        Assert.Equal(EntitlementState.Free, after.State);
        Assert.Equal("free", after.PlanId);
    }

    [Fact]
    public void A_pass_that_has_not_started_yet_gives_nothing()
    {
        var future = new Licence("l1", "acct:a", "plus", T0.AddDays(5), T0.AddDays(35), "manual");
        Assert.Equal(EntitlementState.Free, Entitlements.Resolve([future], Plans, T0).State);
    }

    [Fact]
    public void The_higher_plan_wins_and_ties_go_to_the_later_end()
    {
        var plus = new Licence("p", "acct:a", "plus", T0, T0.AddMonths(12), "manual");
        var office = new Licence("o", "acct:a", "office", T0, T0.AddMonths(1), "manual");
        Assert.Equal("office", Entitlements.Resolve([plus, office], Plans, T0.AddDays(1)).PlanId);
        Assert.Equal("plus", Entitlements.Resolve([plus, office], Plans, T0.AddMonths(1).AddDays(5)).PlanId);
    }

    [Fact]
    public void Corrupt_or_unknown_licences_are_ignored_not_trusted()
    {
        var unknown = new Licence("x", "acct:a", "platinum", T0, T0.AddMonths(1), "manual");
        var backwards = new Licence("y", "acct:a", "office", T0.AddMonths(1), T0, "manual");
        Assert.Equal(EntitlementState.Free, Entitlements.Resolve([unknown, backwards], Plans, T0.AddDays(1)).State);
    }

    [Fact]
    public void Buying_again_stacks_after_the_running_pass_and_never_loses_days()
    {
        var first = Entitlements.Extend([], "acct:a", "plus", 1, T0, "manual", "l1");
        Assert.Equal(T0, first.StartsAt);
        var second = Entitlements.Extend([first], "acct:a", "plus", 3, T0.AddDays(10), "manual", "l2");
        Assert.Equal(first.EndsAt, second.StartsAt);
        Assert.Equal(first.EndsAt.AddMonths(3), second.EndsAt);
        var other = Entitlements.Extend([first], "acct:b", "plus", 1, T0.AddDays(10), "manual", "l3");   // another account is independent
        Assert.Equal(T0.AddDays(10), other.StartsAt);
        var afterEnd = Entitlements.Extend([first], "acct:a", "plus", 1, first.EndsAt.AddDays(40), "manual", "l4");
        Assert.Equal(first.EndsAt.AddDays(40), afterEnd.StartsAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(37)]
    public void Silly_pass_lengths_are_refused(int months) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Entitlements.Extend([], "acct:a", "plus", months, T0, "manual", "l"));

    [Fact]
    public void Rupees_follow_the_rate_rounded_to_fifty_and_a_hand_set_price_wins()
    {
        var rate = new ExchangeRate(283m, T0);
        Assert.Equal(1000, PlanCatalog.PkrFor(3.50m, rate));            // 990.5 -> 1000
        Assert.Equal(8200, PlanCatalog.PkrFor(29m, rate));              // 8207 -> 8200
        Assert.Equal(1000, PlanCatalog.PkrFor(new PlanPrice(1, 3.50m, 1000), new ExchangeRate(500m, T0)));
        Assert.Equal(1750, PlanCatalog.PkrFor(new PlanPrice(1, 3.50m), new ExchangeRate(500m, T0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanCatalog.PkrFor(1m, new ExchangeRate(0m, T0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanCatalog.PkrFor(-1m, rate));
    }

    [Fact]
    public void Dollar_text_is_stable_whatever_the_machine_culture()
    {
        Assert.Equal("3.50", PlanCatalog.UsdText(3.5m));
        Assert.Equal("29", PlanCatalog.UsdText(29m));
    }

    [Fact]
    public void Days_left_rounds_up_and_is_zero_after_the_end()
    {
        var pass = new Licence("l", "acct:a", "plus", T0, T0.AddDays(30), "manual");
        var e = Entitlements.Resolve([pass], Plans, T0.AddDays(29).AddHours(1));
        Assert.Equal(1, Entitlements.DaysLeft(e, T0.AddDays(29).AddHours(1)));
        var grace = Entitlements.Resolve([pass], Plans, T0.AddDays(31));
        Assert.Equal(0, Entitlements.DaysLeft(grace, T0.AddDays(31)));
    }
}
