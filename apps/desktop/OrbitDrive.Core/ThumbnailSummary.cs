namespace OrbitDrive.Core;

/// <summary>Plain-language status line for the picture-preview setting on the PC. Only counts and sizes; never names or paths.</summary>
public static class ThumbnailSummary
{
    public static string Describe(bool enabled, bool available, long files, long bytes)
    {
        if (!available) return "Picture previews are not available on this PC. Files can still be downloaded.";
        var cache = files <= 0 ? "No previews are stored on this PC." : $"{files.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {(files == 1 ? "preview" : "previews")} stored on this PC ({ActivityPresenter.FormatBytes(bytes)}).";
        return (enabled ? "Paired devices can see small previews of pictures in your shared folders. " : "Previews are off: paired devices see file icons only. ") + cache;
    }
}
