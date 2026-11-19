using OrbitDrive.Core;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class ThumbnailSummaryTests
{
    [Fact] public void Unavailable_is_said_plainly_whatever_else_is_true() =>
        Assert.Equal("Picture previews are not available on this PC. Files can still be downloaded.", ThumbnailSummary.Describe(true, false, 5, 1024));

    [Fact] public void Off_with_nothing_stored() =>
        Assert.Equal("Previews are off: paired devices see file icons only. No previews are stored on this PC.", ThumbnailSummary.Describe(false, true, 0, 0));

    [Fact] public void On_with_one_preview_uses_the_singular_and_a_readable_size() =>
        Assert.Equal("Paired devices can see small previews of pictures in your shared folders. 1 preview stored on this PC (2 KB).", ThumbnailSummary.Describe(true, true, 1, 2048));

    [Fact] public void Many_previews_use_a_thousands_separator() =>
        Assert.Equal("Previews are off: paired devices see file icons only. 12,345 previews stored on this PC (5 MB).", ThumbnailSummary.Describe(false, true, 12345, 5L * 1024 * 1024));

    [Fact] public void Output_never_contains_a_path_or_name_even_for_odd_numbers() =>
        Assert.DoesNotContain("\\", ThumbnailSummary.Describe(true, true, -3, -1));
}
