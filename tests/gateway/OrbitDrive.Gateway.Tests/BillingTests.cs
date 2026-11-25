using OrbitDrive.Gateway;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway.Tests;

public class BillingTests
{
    private static BillingStore Store(IBillingPersistence p, FakeTime time) => new(p, time);
    private static ProtocolException Refusal(Action act) => Assert.Throws<ProtocolException>(act);

    [Fact]
    public void The_usage_day_turns_over_at_midnight_in_Pakistan_not_UTC()
    {
        var time = new FakeTime { Now = new DateTimeOffset(2026, 12, 1, 18, 59, 0, TimeSpan.Zero) };   // 23:59 in Pakistan
        var billing = Store(new MemoryBillingPersistence(), time);
        billing.RecordTransfer("acct:a", 100, fileCompleted: true);
        Assert.Equal((1, 100L), billing.UsageToday("acct:a"));
        time.Now = time.Now.AddMinutes(2);                                                          // 00:01 in Pakistan, still 19:01 UTC
        Assert.Equal((0, 0L), billing.UsageToday("acct:a"));
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 19, 0, 0, TimeSpan.Zero), BillingStore.NextReset(time.Now));
    }

    [Fact]
    public void Free_blocks_the_sixth_file_a_file_over_100_MB_and_data_over_300_MB()
    {
        var time = new FakeTime();
        var billing = Store(new MemoryBillingPersistence(), time);
        billing.RequireTransfer("acct:a", 100 * PlanCatalog.MiB);                                    // exactly the limit is allowed
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("acct:a", 100 * PlanCatalog.MiB + 1)).Code);
        for (var i = 0; i < 5; i++) { billing.RequireTransfer("acct:a", 1); billing.RecordTransfer("acct:a", 1, fileCompleted: true); }
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("acct:a", 1)).Code);
        billing.RequireTransfer("acct:b", 1);                                                         // another account is unaffected
        billing.ResetToday("acct:a");
        billing.RecordTransfer("acct:a", 300 * PlanCatalog.MiB, fileCompleted: false);
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("acct:a", 1)).Code);
    }

    [Fact]
    public void A_failed_or_cancelled_transfer_does_not_use_up_a_file()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        for (var i = 0; i < 20; i++) billing.RecordTransfer("acct:a", 1000, fileCompleted: false);
        Assert.Equal(0, billing.UsageToday("acct:a").Files);
        billing.RequireTransfer("acct:a", 1);
    }

    [Fact]
    public void A_paid_pass_lifts_the_limits_and_expiry_brings_them_back_after_grace()
    {
        var time = new FakeTime();
        var billing = Store(new MemoryBillingPersistence(), time);
        billing.AddPass("acct:a", "plus", 1, "manual");
        billing.RequireTransfer("acct:a", 900 * PlanCatalog.MiB);
        for (var i = 0; i < 20; i++) billing.RecordTransfer("acct:a", 10, fileCompleted: true);
        billing.RequireTransfer("acct:a", 1);                                                         // 20 of 50 files used
        time.Now = time.Now.AddMonths(1).AddDays(2);
        Assert.Equal(EntitlementState.Grace, billing.EntitlementFor("acct:a").State);
        time.Now = time.Now.AddDays(2);
        Assert.Equal("free", billing.EntitlementFor("acct:a").PlanId);
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("acct:a", 900 * PlanCatalog.MiB)).Code);
    }

    [Fact]
    public void Office_has_no_file_count_limit_but_still_a_per_file_and_fair_use_data_limit()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        billing.AddPass("org:o", "office", 1, "manual");
        for (var i = 0; i < 500; i++) billing.RecordTransfer("org:o", 1, fileCompleted: true);
        billing.RequireTransfer("org:o", 2 * PlanCatalog.GiB);
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("org:o", 2 * PlanCatalog.GiB + 1)).Code);
        billing.RecordTransfer("org:o", 100 * PlanCatalog.GiB, fileCompleted: false);
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransfer("org:o", 1)).Code);
    }

    [Fact]
    public void Passes_counts_and_prices_survive_a_restart()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var first = Store(persistence, time);
        first.AddPass("acct:a", "plus", 3, "manual", "paid by bank transfer");
        first.RecordTransfer("acct:a", 5000, fileCompleted: true);
        first.SetRate(300m);
        first.Flush();
        var second = Store(persistence, time);
        Assert.Equal("plus", second.EntitlementFor("acct:a").PlanId);
        Assert.Equal((1, 5000L), second.UsageToday("acct:a"));
        Assert.Equal(300m, second.Rate.PkrPerUsd);
        Assert.Equal("paid by bank transfer", second.LicencesFor("acct:a").Single().Note);
    }

    [Fact]
    public void Only_changed_entries_are_written_and_a_failed_write_is_retried()
    {
        var inner = new MemoryBillingPersistence();
        var flaky = new FlakyPersistence(inner) { FailNext = true };
        var billing = Store(flaky, new FakeTime());
        billing.RecordTransfer("acct:a", 10, true);
        billing.Flush();                                                                              // fails, state stays in memory
        Assert.Equal((1, 10L), billing.UsageToday("acct:a"));
        Assert.Empty(inner.LoadAll());
        billing.Flush();                                                                              // retried
        Assert.Single(inner.LoadAll());
        var writes = inner.Writes;
        billing.Flush();                                                                              // nothing changed: nothing written
        Assert.Equal(writes, inner.Writes);
    }

    [Fact]
    public void Unreadable_stored_entries_are_skipped_without_losing_the_rest()
    {
        var persistence = new MemoryBillingPersistence();
        persistence.Write([new("licence/bad", "{not json"), new("config/rate", "{\"PkrPerUsd\":-5,\"UpdatedAt\":\"2026-01-01T00:00:00+00:00\"}"), new("usage/acct:a/1", "{\"Files\":2,\"Bytes\":9}")]);
        var billing = Store(persistence, new FakeTime());
        Assert.Equal(PlanCatalog.DefaultRate.PkrPerUsd, billing.Rate.PkrPerUsd);                      // a nonsense stored rate is not trusted
        Assert.Empty(billing.LicencesFor("acct:a"));
    }

    [Fact]
    public void Owner_edits_to_plans_and_the_rate_are_validated()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetRate(0)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetRate(1_000_000)).Code);
        var plans = PlanCatalog.Defaults().ToList();
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPlans([.. plans.Where(p => p.Id != "free")])).Code);          // Free must exist
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPlans([.. plans.Select(p => p.Id == "free" ? p with { Purchasable = true } : p)])).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPlans([.. plans, plans[1]])).Code);                            // duplicate id
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPlans([.. plans.Select(p => p.Id == "plus" ? p with { Prices = [new PlanPrice(1, -1m)] } : p)])).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPlans([.. plans, new Plan("bad id!", "x", 3, plans[0].Limits, [])])).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.AddPass("acct:a", "free", 1, "manual")).Code);                     // Free is never a pass
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.AddPass("acct:a", "nope", 1, "manual")).Code);
        billing.SetRate(290m);
        Assert.Equal(290m, billing.Rate.PkrPerUsd);
    }

    [Fact]
    public void Ending_a_pass_and_deleting_an_account_remove_what_they_should()
    {
        var time = new FakeTime();
        var billing = Store(new MemoryBillingPersistence(), time);
        var pass = billing.AddPass("acct:a", "plus", 1, "manual");
        Assert.True(billing.EndPass(pass.Id, skipGrace: false));
        Assert.Equal(EntitlementState.Grace, billing.EntitlementFor("acct:a").State);
        Assert.True(billing.EndPass(pass.Id, skipGrace: true));
        Assert.Equal("free", billing.EntitlementFor("acct:a").PlanId);
        Assert.False(billing.EndPass("missing", false));
        billing.RecordTransfer("acct:a", 5, true);
        billing.Forget("acct:a");
        Assert.Empty(billing.LicencesFor("acct:a"));
        Assert.Equal((0, 0L), billing.UsageToday("acct:a"));
    }

    [Fact]
    public void Usage_older_than_forty_days_is_dropped_on_flush()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var billing = Store(persistence, time);
        billing.RecordTransfer("acct:a", 5, true);
        billing.Flush();
        Assert.Single(persistence.LoadAll());
        time.Now = time.Now.AddDays(41);
        billing.Flush();
        Assert.Empty(persistence.LoadAll());
    }

    [Fact]
    public void The_file_backend_round_trips_and_keeps_the_file_private()
    {
        var dir = Path.Join(Path.GetTempPath(), "orbit-billing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTime();
            var first = Store(new FileBillingPersistence(dir), time);
            first.AddPass("acct:a", "office", 12, "grant");
            first.Flush();
            var second = Store(new FileBillingPersistence(dir), time);
            Assert.Equal("office", second.EntitlementFor("acct:a").PlanId);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Join(dir, "billing.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_download_counts_as_one_file_only_when_every_byte_reached_the_browser_and_only_once()
    {
        var session = new DownloadSessionRecord
        {
            OwnerUid = "a", BrowserDeviceId = "d", PcId = "p", Name = "n", Size = 100, Revision = "r", ReadToken = "t", CreatedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        };
        session.StreamStarted();
        session.StreamEnded(0, 40);
        Assert.False(session.TryCountAsFile());                                   // only part of it arrived
        session.StreamStarted();
        session.StreamEnded(40, 60);
        Assert.True(session.TryCountAsFile());                                    // the rest arrived on a resumed request
        Assert.False(session.TryCountAsFile());                                   // never counted twice
    }

    [Theory]
    [InlineData("licence/abc")]
    [InlineData("usage/acct:u1/20424")]
    [InlineData("config/plans")]
    [InlineData("weird~name/with%percent")]
    public void Firestore_document_ids_round_trip_and_never_contain_a_slash(string key)
    {
        var id = BillingIds.Encode(key);
        Assert.False(id.Contains('/'));
        Assert.Equal(key, BillingIds.Decode(id));
    }

    [Fact]
    public void A_quote_shows_dollars_and_approximate_rupees_and_applies_a_coupon()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        var plain = billing.QuoteFor("plus", 1, null);
        Assert.Equal((3.50m, 3.50m, 1000, 1000), (plain.ListUsd, plain.DueUsd, plain.ListPkr, plain.DuePkr));
        billing.SaveCoupon("save20", "percent", 20, ["plus"], null, 10, true, null);
        var cut = billing.QuoteFor("plus", 12, "save20");                                              // codes are not case sensitive
        Assert.Equal((29m, 23.20m, 8000, 6400), (cut.ListUsd, cut.DueUsd, cut.ListPkr, cut.DuePkr));
        billing.SaveCoupon("TWOOFF", "fixed", 2m, [], null, null, true, null);
        Assert.Equal(1.5m, billing.QuoteFor("plus", 1, "TWOOFF").DueUsd);
        Assert.Equal(0m, billing.QuoteFor("plus", 1, "TWOOFF").DueUsd - 1.5m);
        billing.SaveCoupon("BIG", "fixed", 500m, [], null, null, true, null);
        Assert.Equal(0m, billing.QuoteFor("plus", 1, "BIG").DueUsd);                                    // never negative
    }

    [Fact]
    public void Coupons_are_refused_when_expired_used_up_switched_off_or_for_another_plan()
    {
        var time = new FakeTime();
        var billing = Store(new MemoryBillingPersistence(), time);
        billing.SaveCoupon("SOON", "percent", 10, [], time.Now.AddDays(1), null, true, null);
        billing.SaveCoupon("ONCE", "percent", 10, [], null, 1, true, null);
        billing.SaveCoupon("OFFICEONLY", "percent", 10, ["office"], null, null, true, null);
        billing.SaveCoupon("DEAD", "percent", 10, [], null, null, false, null);
        billing.QuoteFor("plus", 1, "SOON");
        time.Now = time.Now.AddDays(2);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "SOON")).Code);
        billing.CreateOrder("acct:a", "plus", 1, "ONCE", null);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "ONCE")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "OFFICEONLY")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "DEAD")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "NOSUCH")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.QuoteFor("plus", 1, "x")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SaveCoupon("BAD", "percent", 101, [], null, null, true, null)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SaveCoupon("BAD", "weird", 5, [], null, null, true, null)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SaveCoupon("BAD", "percent", 5, ["free"], null, null, true, null)).Code);
    }

    [Fact]
    public void An_order_is_priced_once_verified_by_the_owner_and_then_gives_the_pass()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        var order = billing.CreateOrder("acct:a", "plus", 3, null, "jazzcash");
        Assert.Equal("requested", order.Status);
        Assert.Equal(2500, order.DuePkr);
        billing.SetRate(400m);                                                                          // a later rate change never alters an existing order
        Assert.Equal(2500, billing.FindOrder(order.Id)!.DuePkr);
        Assert.Equal("free", billing.EntitlementFor("acct:a").PlanId);                                  // asking is not paying
        var proof = billing.SubmitProof("acct:a", order.Id, "jazzcash", "TXN 8841 2210");
        Assert.Equal("proof_submitted", proof.Status);
        var done = billing.VerifyOrder(order.Id, "operator1");
        Assert.Equal("verified", done.Status);
        Assert.Equal("plus", billing.EntitlementFor("acct:a").PlanId);
        Assert.Equal(done.LicenceId, billing.LicencesFor("acct:a").Single().Id);
        Assert.Equal(ErrorCode.Conflict, Refusal(() => billing.VerifyOrder(order.Id, "operator1")).Code);   // never verified twice
        Assert.Equal(ErrorCode.Conflict, Refusal(() => billing.RejectOrder(order.Id, "operator1", "late")).Code);
    }

    [Fact]
    public void Customers_can_only_touch_their_own_open_orders_and_a_rejection_frees_the_coupon()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        billing.SaveCoupon("ONCE", "percent", 10, [], null, 1, true, null);
        var order = billing.CreateOrder("acct:a", "plus", 1, "ONCE", null);
        Assert.Equal(ErrorCode.NotFound, Refusal(() => billing.SubmitProof("acct:b", order.Id, "bank", "12345678")).Code);
        Assert.Equal(ErrorCode.NotFound, Refusal(() => billing.CancelOrder("acct:b", order.Id)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SubmitProof("acct:a", order.Id, "cheque", "12345678")).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SubmitProof("acct:a", order.Id, "bank", "123")).Code);
        billing.RejectOrder(order.Id, "operator1", "no such payment");
        Assert.Equal(0, billing.Coupons().Single().Redeemed);
        Assert.Equal("free", billing.EntitlementFor("acct:a").PlanId);
        Assert.Equal(ErrorCode.Conflict, Refusal(() => billing.CancelOrder("acct:a", order.Id)).Code);
        billing.CreateOrder("acct:a", "plus", 1, "ONCE", null);                                         // the code can be used again
    }

    [Fact]
    public void Only_a_few_orders_may_be_open_at_once_and_unsold_plans_cannot_be_ordered()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        for (var i = 0; i < 5; i++) billing.CreateOrder("acct:a", "plus", 1, null, null);
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.CreateOrder("acct:a", "plus", 1, null, null)).Code);
        billing.CreateOrder("acct:b", "plus", 1, null, null);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.CreateOrder("acct:b", "free", 1, null, null)).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.CreateOrder("acct:b", "plus", 2, null, null)).Code);     // only offered lengths
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.CreateOrder("acct:b", "plus", 1, null, "cash")).Code);
    }

    [Fact]
    public void Orders_and_coupons_survive_a_restart_and_leave_with_the_account()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var first = Store(persistence, time);
        first.SaveCoupon("KEEP", "percent", 15, [], null, 5, true, "launch");
        var order = first.CreateOrder("acct:a", "office", 1, "KEEP", "bank");
        first.Flush();
        var second = Store(persistence, time);
        Assert.Equal(order.DuePkr, second.OrdersFor("acct:a").Single().DuePkr);
        Assert.Equal(1, second.Coupons().Single().Redeemed);
        second.Forget("acct:a");
        Assert.Empty(second.OrdersFor("acct:a"));
    }

    [Fact]
    public void The_public_price_list_shows_dollars_with_approximate_rupees_and_follows_the_rate()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        var view = BillingViews.Public(billing);
        Assert.Equal("USD", view.Currency);
        Assert.False(view.LimitsEnabled);
        var plus = view.Plans.Single(p => p.Id == "plus");
        Assert.Equal([1, 3, 12], plus.Prices.Select(x => x.Months));
        Assert.Equal([3.50m, 9m, 29m], plus.Prices.Select(x => x.Usd));
        Assert.Equal([1000, 2500, 8000], plus.Prices.Select(x => x.Pkr));
        Assert.True(plus.Prices.All(x => !x.PkrApprox));                                               // fixed by the owner
        Assert.Empty(view.Plans.Single(p => p.Id == "free").Prices);
        var open = PlanCatalog.Defaults().Select(p => p.Id == "plus" ? p with { Prices = [new PlanPrice(1, 4m)] } : p).ToList();
        billing.SetPlans(open);
        billing.SetRate(300m);
        var again = BillingViews.Public(billing).Plans.Single(p => p.Id == "plus").Prices.Single();
        Assert.Equal((1200, true), (again.Pkr, again.PkrApprox));                                       // derived from the rate, flagged approximate
    }

    [Fact]
    public void Plan_edits_round_trip_through_the_billing_json_and_unknown_fields_are_refused()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PlansUpdate(PlanCatalog.Defaults(), "test"), BillingJson.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<PlansUpdate>(json, BillingJson.Options)!;
        Assert.Equal(3, back.Plans.Count);
        Assert.Equal(PlanCatalog.Defaults()[1].Limits, back.Plans[1].Limits);
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<RateUpdate>("{\"pkrPerUsd\":280,\"reason\":\"x\",\"extra\":1}", BillingJson.Options));
    }

    [Fact]
    public void The_limits_switch_is_kept_across_a_restart_and_overrides_the_start_up_setting()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var first = new BillingStore(persistence, time, null, null, limitsEnabled: false);
        Assert.False(first.LimitsEnabled);
        first.SetLimitsEnabled(true);
        first.Flush();
        Assert.True(new BillingStore(persistence, time, null, null, limitsEnabled: false).LimitsEnabled);
    }

    [Fact]
    public void Payment_instructions_are_kept_and_unusual_characters_refused()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var billing = Store(persistence, time);
        billing.SetPaymentInstructions("  Bank Al Habib, title X\nIBAN ...  ");
        billing.Flush();
        Assert.Equal("Bank Al Habib, title X\nIBAN ...", Store(persistence, time).PaymentInstructions);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPaymentInstructions(new string('x', 1501))).Code);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.SetPaymentInstructions("bell\a")).Code);
    }

    private static BillingStore OfficeOwner(IBillingPersistence p, FakeTime time, out Organisation org)
    {
        var billing = Store(p, time);
        billing.AddPass("acct:boss", "office", 1, "manual");
        org = billing.CreateOrg("boss", "Acme Traders");
        return billing;
    }

    [Fact]
    public void Only_an_Office_account_can_create_an_organisation_and_only_one()
    {
        var billing = Store(new MemoryBillingPersistence(), new FakeTime());
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.CreateOrg("free-user", "Acme")).Code);
        billing.AddPass("acct:plusser", "plus", 1, "manual");
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.CreateOrg("plusser", "Acme")).Code);
        billing.AddPass("acct:boss", "office", 1, "manual");
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.CreateOrg("boss", "x")).Code);
        billing.CreateOrg("boss", "Acme Traders");
        Assert.Equal(ErrorCode.Conflict, Refusal(() => billing.CreateOrg("boss", "Second")).Code);
    }

    [Fact]
    public void Members_join_with_a_single_use_code_and_share_the_organisations_office_plan()
    {
        var time = new FakeTime();
        var billing = OfficeOwner(new MemoryBillingPersistence(), time, out var org);
        Assert.Equal("acct:ali", billing.SubjectFor("ali"));                                            // not a member yet: their own (free) account
        var invite = billing.Invite(org.Id, "boss");
        billing.Join("ali", invite.Code.ToLowerInvariant(), "Ali, accounts");
        Assert.Equal("org:" + org.Id, billing.SubjectFor("ali"));
        Assert.Equal("office", billing.EntitlementFor(billing.SubjectFor("ali")).PlanId);
        Assert.Equal(["boss", "ali"], billing.PoolOf("ali").OrderByDescending(x => x == "boss").ToArray());
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => billing.Join("bo", invite.Code, "Bo")).Code);   // codes work once
        Assert.Equal(ErrorCode.Conflict, Refusal(() => billing.Join("ali", billing.Invite(org.Id, "boss").Code, "Ali again")).Code);
    }

    [Fact]
    public void Usage_is_pooled_for_the_organisation_and_each_member_keeps_their_own_count()
    {
        var billing = OfficeOwner(new MemoryBillingPersistence(), new FakeTime(), out var org);
        billing.Join("ali", billing.Invite(org.Id, "boss").Code, "Ali");
        billing.Join("sana", billing.Invite(org.Id, "boss").Code, "Sana");
        billing.RecordTransferFor("ali", 1000, true);
        billing.RecordTransferFor("sana", 500, true);
        Assert.Equal((2, 1500L), billing.UsageToday("org:" + org.Id));
        Assert.Equal((1, 1000L), billing.UsageToday("acct:ali"));
        Assert.Equal((1, 500L), billing.UsageToday("acct:sana"));
        billing.RequireTransferFor("ali", 2 * PlanCatalog.GiB);                                         // Office limits apply to members
        Assert.Equal(ErrorCode.LimitExceeded, Refusal(() => billing.RequireTransferFor("ali", 2 * PlanCatalog.GiB + 1)).Code);
    }

    [Fact]
    public void Admins_manage_members_but_members_and_outsiders_cannot_and_the_owner_stays()
    {
        var billing = OfficeOwner(new MemoryBillingPersistence(), new FakeTime(), out var org);
        billing.Join("ali", billing.Invite(org.Id, "boss").Code, "Ali");
        billing.Join("sana", billing.Invite(org.Id, "boss").Code, "Sana");
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.Invite(org.Id, "ali")).Code);
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.Invite(org.Id, "stranger")).Code);
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.MembersOf(org.Id, "stranger")).Code);
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.RemoveMember(org.Id, "ali", "sana")).Code);
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.SetRole(org.Id, "ali", "sana", "admin")).Code);
        billing.SetRole(org.Id, "boss", "ali", "admin");
        billing.RemoveMember(org.Id, "ali", "sana");                                                    // an admin can remove a member
        Assert.Equal("acct:sana", billing.SubjectFor("sana"));
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.RemoveMember(org.Id, "ali", "boss")).Code);   // never the owner
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.Leave("boss")).Code);
        billing.Leave("ali");
        Assert.Equal(["boss"], billing.MembersOf(org.Id, "boss").Select(m => m.Uid));
        var actions = billing.OrgAudit(org.Id, "boss").Select(e => e.Action).ToList();
        Assert.True(actions.Contains("member-removed") && actions.Contains("role-changed") && actions.Contains("member-left"));
        Assert.Equal(ErrorCode.Forbidden, Refusal(() => billing.OrgAudit(org.Id, "ali")).Code);
    }

    [Fact]
    public void When_the_owners_office_pass_ends_members_fall_back_to_their_own_free_accounts()
    {
        var time = new FakeTime();
        var billing = OfficeOwner(new MemoryBillingPersistence(), time, out var org);
        billing.Join("ali", billing.Invite(org.Id, "boss").Code, "Ali");
        time.Now = time.Now.AddMonths(1).AddDays(2);                                                    // inside the grace days: still Office
        Assert.Equal("org:" + org.Id, billing.SubjectFor("ali"));
        time.Now = time.Now.AddDays(2);
        Assert.Equal("acct:ali", billing.SubjectFor("ali"));
        Assert.Equal("free", billing.EntitlementFor(billing.SubjectFor("ali")).PlanId);
        Assert.Equal(["ali"], billing.PoolOf("ali"));
    }

    [Fact]
    public void Invitations_expire_and_organisations_survive_a_restart_and_vanish_with_their_owner()
    {
        var persistence = new MemoryBillingPersistence();
        var time = new FakeTime();
        var first = OfficeOwner(persistence, time, out var org);
        var stale = first.Invite(org.Id, "boss");
        first.Join("ali", first.Invite(org.Id, "boss").Code, "Ali");
        first.Flush();
        var second = Store(persistence, time);
        Assert.Equal("org:" + org.Id, second.SubjectFor("ali"));
        Assert.Equal(2, second.MembersOf(org.Id, "boss").Count);
        time.Now = time.Now.AddDays(8);
        Assert.Equal(ErrorCode.InvalidRequest, Refusal(() => second.Join("bo", stale.Code, "Bo")).Code);
        second.Forget("acct:boss");
        Assert.Null(second.OrgOf("ali"));
        Assert.Equal("acct:ali", second.SubjectFor("ali"));
        second.Flush();
        Assert.Empty(persistence.LoadAll().Where(e => e.Key.StartsWith("org", StringComparison.Ordinal)));
    }

    private sealed class FlakyPersistence(MemoryBillingPersistence inner) : IBillingPersistence
    {
        public bool FailNext { get; set; }
        public IReadOnlyList<KeyValuePair<string, string>> LoadAll() => inner.LoadAll();
        public void Write(IReadOnlyList<KeyValuePair<string, string?>> changes)
        {
            if (FailNext) { FailNext = false; throw new IOException("disk"); }
            inner.Write(changes);
        }
    }
}
