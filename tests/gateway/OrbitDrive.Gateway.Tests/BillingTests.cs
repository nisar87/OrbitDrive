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
