using OrbitDrive.Gateway;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway.Tests;

public class MetricsTests
{
    [Fact]
    public void A_daily_cap_blocks_new_work_once_used_and_recovers_at_midnight_UTC()
    {
        var time = new FakeTime { Now = new DateTimeOffset(2026, 11, 17, 23, 59, 0, TimeSpan.Zero) };
        var metrics = new Metrics(new GatewayOptions { MaxRelayBytesPerOwnerPerDay = 1000 }, time);
        metrics.RequireRelayAllowance("a");
        metrics.Downloaded("a", 1000, completed: true);
        var refused = Assert.Throws<ProtocolException>(() => metrics.RequireRelayAllowance("a"));
        Assert.Equal(ErrorCode.LimitExceeded, refused.Code);
        metrics.RequireRelayAllowance("b");                                  // another account is unaffected
        time.Now = time.Now.AddMinutes(2);                                   // 00:01 UTC the next day
        metrics.RequireRelayAllowance("a");
        Assert.Equal(0, metrics.RelayBytesToday("a"));
        Assert.Equal(1, metrics.QuotaRefusals);
    }

    [Fact]
    public void No_cap_means_no_refusals_and_counters_still_count()
    {
        var metrics = new Metrics(new GatewayOptions(), new FakeTime());
        metrics.Downloaded("a", long.MaxValue / 4, completed: false);
        metrics.RequireRelayAllowance("a");
        metrics.PieceRelayed("a", 10, stored: true);
        metrics.PieceRelayed("a", 10, stored: false);
        Assert.Equal(1, metrics.DownloadsFailed);
        Assert.Equal(0, metrics.DownloadsCompleted);
        Assert.Equal(10, metrics.Up);
        Assert.Equal(1, metrics.PiecesRelayed);
        Assert.Equal(1, metrics.PiecesFailed);
    }

    [Fact]
    public void Forgetting_an_account_clears_its_day_count()
    {
        var metrics = new Metrics(new GatewayOptions { MaxRelayBytesPerOwnerPerDay = 10 }, new FakeTime());
        metrics.Downloaded("a", 50, true);
        metrics.Forget("a");
        metrics.RequireRelayAllowance("a");
    }
}

public class AdminLogTests
{
    [Theory]
    [InlineData("", "1.0.0", false)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.10.0", false)]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("1.2.0", "1.2.0+abc123", false)]
    [InlineData("1.2.0", "1.1.0-beta", true)]
    [InlineData("1.2.0", "garbage", true)]
    [InlineData("1.2.0", "", true)]
    public void Versions_are_compared_as_numbers_and_unreadable_ones_are_too_old(string minimum, string reported, bool tooOld)
    {
        var log = new AdminLog(new GatewayOptions { MinAgentVersion = minimum }, new FakeTime());
        Assert.Equal(tooOld, log.IsBelowMinimum(reported));
    }

    [Fact]
    public void Settings_and_the_trail_survive_a_restart_and_a_bad_version_is_refused()
    {
        var dir = Path.Join(Path.GetTempPath(), "orbit-adminlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GatewayOptions { DataDirectory = dir };
            var log = new AdminLog(options, new FakeTime());
            log.SetMinAgentVersion("2.0.1");
            log.Record("op", "suspend", "user", new string('x', 500));
            Assert.Throws<ProtocolException>(() => log.SetMinAgentVersion("not a version"));
            var reopened = new AdminLog(options, new FakeTime());
            Assert.Equal("2.0.1", reopened.MinAgentVersion);
            var entry = Assert.Single(reopened.Recent(10));
            Assert.Equal(("op", "suspend", "user"), (entry.Operator, entry.Action, entry.Target));
            Assert.Equal(200, entry.Detail.Length);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Options_reject_nonsense_operator_settings()
    {
        var env = new FakeEnvironment("Development");
        GatewayOptions Valid() => new() { DevIdentity = new DevIdentityOptions { Enabled = true, Secret = new string('s', 40) } };
        Valid().Validate(env);
        foreach (var bad in new Action<GatewayOptions>[]
        {
            o => o.OperatorUids = [""], o => o.OperatorUids = ["a b"], o => o.OperatorMaxAuthAgeSeconds = 5, o => o.OperatorMaxAuthAgeSeconds = 99999,
            o => o.MaxRelayBytesPerOwnerPerDay = -1, o => o.MinAgentVersion = "x.y",
        })
        {
            var options = Valid(); bad(options);
            Assert.Throws<InvalidOperationException>(() => options.Validate(env));
        }
    }
}
