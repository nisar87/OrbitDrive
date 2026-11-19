using OrbitDrive.Core;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class ActivityPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static ActivityEntry E(string kind, string detail = "", long? bytes = null, int daysAgo = 0) => new(Now.AddDays(-daysAgo), kind, detail, bytes);

    [Theory]
    [InlineData("download", "A file was sent to a browser", false)]
    [InlineData("upload_failed", "A receive failed", true)]
    [InlineData("error", "Something went wrong", true)]
    [InlineData("connected", "Connected to the relay", false)]
    public void KnownKindsGetPlainWords(string kind, string title, bool problem)
    {
        var row = ActivityPresenter.Present(E(kind), TimeZoneInfo.Utc, Now);
        Assert.Equal(title, row.Title);
        Assert.Equal(problem, row.IsProblem);
    }

    [Fact]
    public void UnknownKindsAreStillShownNotDropped()
    {
        var row = ActivityPresenter.Present(E("brand_new_kind", "x"), TimeZoneInfo.Utc, Now);
        Assert.Equal("Other activity", row.Title);
        Assert.Equal("x", row.Detail);
        Assert.False(row.IsProblem);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(1L << 30, "1 GB")]
    [InlineData(200L * 1024 * 1024, "200 MB")]
    [InlineData(-1, "")]
    public void SizesAreHumanReadable(long bytes, string expected) => Assert.Equal(expected, ActivityPresenter.FormatBytes(bytes));

    [Fact]
    public void DetailAndSizeAreJoined()
    {
        Assert.Equal("ok · 2 KB", ActivityPresenter.Present(E("download", "ok", 2048), TimeZoneInfo.Utc, Now).Detail);
        Assert.Equal("2 KB", ActivityPresenter.Present(E("download", "", 2048), TimeZoneInfo.Utc, Now).Detail);
    }

    [Fact]
    public void TodayShowsTimeAndOlderDaysShowDate()
    {
        Assert.Equal("12:00:00", ActivityPresenter.FormatTime(Now, TimeZoneInfo.Utc, Now));
        Assert.Equal("17 Nov 12:00", ActivityPresenter.FormatTime(Now.AddDays(-2), TimeZoneInfo.Utc, Now));
    }

    [Fact]
    public void TheDayIsJudgedInTheOwnersTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(5), "t", "t");
        var late = new DateTimeOffset(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);   // 01:00 on the 20th at +05:00
        Assert.Equal("20 Nov 01:00", ActivityPresenter.FormatTime(late, zone, Now));
    }

    [Fact]
    public void TextExportIsOneLinePerEntryAndAddsNothing()
    {
        var text = ActivityPresenter.ToText([E("download", "ok", 2048), E("connected")], TimeZoneInfo.Utc, Now);
        Assert.Equal("12:00:00  A file was sent to a browser (ok · 2 KB)\n12:00:00  Connected to the relay", text);
        Assert.Equal("", ActivityPresenter.ToText([], TimeZoneInfo.Utc, Now));
    }
}
