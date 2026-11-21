using OrbitDrive.Core;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class ConsoleNoticesTests
{
    private static NoticeTracker Tracker(Func<DateTimeOffset>? clock = null, int gapSeconds = 30) =>
        new(new NoticeThrottle(TimeSpan.FromSeconds(gapSeconds), clock ?? (() => DateTimeOffset.UnixEpoch)));

    private static readonly (string, string)[] None = [];

    [Fact]
    public void A_pairing_notice_can_only_deny_or_review_never_approve()
    {
        var notice = ConsoleNotices.PairingRequest("Pixel 9");
        Assert.Equal([NoticeAction.Deny, NoticeAction.Review], notice.Actions);
        Assert.Equal(["Deny", "Review"], Enum.GetNames<NoticeAction>());   // there is no Approve value to misuse
    }

    [Fact]
    public void The_pairing_notice_tells_the_owner_to_check_the_code_in_the_console_and_shows_none()
    {
        var notice = ConsoleNotices.PairingRequest("Pixel 9");
        Assert.Contains("check the code", notice.Body);
        Assert.Contains("Pixel 9", notice.Body);
        Assert.DoesNotContain("@", notice.Title + notice.Body);
    }

    [Theory]
    [InlineData(null, "A device")]
    [InlineData("", "A device")]
    [InlineData("   ", "A device")]
    [InlineData("Pixel\r\n9\u0007", "Pixel9")]
    public void Browser_names_from_another_machine_are_cleaned(string? raw, string shown) => Assert.Equal(shown, ConsoleNotices.CleanName(raw));

    [Fact]
    public void A_very_long_browser_name_is_cut()
    {
        var shown = ConsoleNotices.CleanName(new string('x', 500));
        Assert.Equal(ConsoleNotices.MaxNameLength, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Theory]
    [InlineData(1, "A file was sent")]
    [InlineData(3, "3 files were sent")]
    public void Sent_notices_have_no_buttons_and_no_names(int count, string title)
    {
        var notice = ConsoleNotices.FilesSent(count);
        Assert.Equal(title, notice.Title);
        Assert.Empty(notice.Actions);
        Assert.Contains("can't see whether it was saved", notice.Body);
    }

    [Fact]
    public void The_throttle_allows_one_per_gap()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new NoticeThrottle(TimeSpan.FromSeconds(30), () => now);
        Assert.True(throttle.Allow());
        now = now.AddSeconds(10); Assert.False(throttle.Allow());
        now = now.AddSeconds(25); Assert.True(throttle.Allow());
    }

    [Fact]
    public void Starting_the_console_never_notifies_about_what_was_already_happening()
    {
        var tracker = Tracker();
        Assert.Empty(tracker.Observe("123456", "Pixel 9", [("a;1", "sending")]));
        Assert.DoesNotContain(tracker.Observe("123456", "Pixel 9", [("a;1", "sent_to_relay")]), n => n.Kind == NoticeKind.PairingRequest);   // same request, already known
    }

    [Fact]
    public void A_new_request_notifies_once_and_a_later_request_notifies_again()
    {
        var tracker = Tracker();
        Assert.Empty(tracker.Observe(null, null, None));
        var first = Assert.Single(tracker.Observe("111111", "Pixel 9", None));
        Assert.Equal(NoticeKind.PairingRequest, first.Kind);
        Assert.Empty(tracker.Observe("111111", "Pixel 9", None));
        Assert.Empty(tracker.Observe(null, null, None));
        Assert.Single(tracker.Observe("222222", "iPhone", None));
        Assert.Equal("222222", tracker.PendingApprovalCode);
    }

    [Fact]
    public void A_transfer_that_finishes_notifies_once_and_one_that_was_already_done_does_not()
    {
        var tracker = Tracker();
        Assert.Empty(tracker.Observe(null, null, [("old;1", "sent_to_relay"), ("new;2", "sending")]));
        var sent = Assert.Single(tracker.Observe(null, null, [("old;1", "sent_to_relay"), ("new;2", "sent_to_relay")]));
        Assert.Equal(NoticeKind.FilesSent, sent.Kind);
        Assert.Empty(tracker.Observe(null, null, [("old;1", "sent_to_relay"), ("new;2", "sent_to_relay")]));
    }

    [Fact]
    public void Cancelled_transfers_do_not_notify_as_sent()
    {
        var tracker = Tracker();
        tracker.Observe(null, null, [("a;1", "sending")]);
        Assert.Empty(tracker.Observe(null, null, [("a;1", "cancelled")]));
    }

    [Fact]
    public void A_burst_of_files_gives_one_batched_notice_after_the_gap()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = Tracker(() => now);
        tracker.Observe(null, null, [("a;1", "sending"), ("b;2", "sending"), ("c;3", "sending")]);
        Assert.Single(tracker.Observe(null, null, [("a;1", "sent_to_relay"), ("b;2", "sending"), ("c;3", "sending")]));
        now = now.AddSeconds(5);
        Assert.Empty(tracker.Observe(null, null, [("a;1", "sent_to_relay"), ("b;2", "sent_to_relay"), ("c;3", "sent_to_relay")]));   // held back by the gap
        now = now.AddSeconds(40);
        var batch = Assert.Single(tracker.Observe(null, null, [("a;1", "sent_to_relay"), ("b;2", "sent_to_relay"), ("c;3", "sent_to_relay")]));
        Assert.Equal("2 files were sent", batch.Title);
    }
}
