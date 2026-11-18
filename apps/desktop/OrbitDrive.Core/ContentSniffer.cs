using System.Text;

namespace OrbitDrive.Core;

public enum PreviewKind { None, Image, Pdf, Audio, Video, Text }

/// <summary>What a file really is, judged from its first bytes, never from its name alone.</summary>
public readonly record struct SniffedContent(string ContentType, PreviewKind Kind)
{
    public static readonly SniffedContent Unknown = new("application/octet-stream", PreviewKind.None);
    /// <summary>True when the browser may show this inline (the gateway applies the same list again).</summary>
    public bool Inlineable => Kind != PreviewKind.None;
}

/// <summary>
/// Closed allow-list of preview types. A file whose name says ".png" but whose bytes are HTML is not an image; HTML, SVG,
/// XML-based and script types are never previewed inline (they would be active content), and anything unlisted is a plain download.
/// </summary>
public static class ContentSniffer
{
    public const int HeadBytes = 4096;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];   // (a string literal would be UTF-8 encoded, turning 0x89 into two bytes)
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".toml", ".c", ".h", ".cpp", ".hpp", ".cs",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".sh", ".bat", ".ps1", ".sql", ".css", ".gitignore",
    };

    public static SniffedContent Classify(ReadOnlySpan<byte> head, string name)
    {
        var ext = Path.GetExtension(name);
        if (head.Length >= 8 && head[..8].SequenceEqual(PngSignature)) return new("image/png", PreviewKind.Image);
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return new("image/jpeg", PreviewKind.Image);
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8))) return new("image/gif", PreviewKind.Image);
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
        {
            if (head[8..12].SequenceEqual("WEBP"u8)) return new("image/webp", PreviewKind.Image);
            if (head[8..12].SequenceEqual("WAVE"u8)) return new("audio/wav", PreviewKind.Audio);
        }
        if (head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M' && ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)) return new("image/bmp", PreviewKind.Image);
        if (head.Length >= 5 && IndexOf(head[..Math.Min(head.Length, 1024)], "%PDF-"u8) >= 0) return new("application/pdf", PreviewKind.Pdf);
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8)) return new("audio/flac", PreviewKind.Audio);
        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8))
            return ext.Equals(".ogv", StringComparison.OrdinalIgnoreCase) ? new("video/ogg", PreviewKind.Video) : new("audio/ogg", PreviewKind.Audio);
        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
            return ext.Equals(".weba", StringComparison.OrdinalIgnoreCase) ? new("audio/webm", PreviewKind.Audio) : new("video/webm", PreviewKind.Video);
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
            return head[8..12].SequenceEqual("M4A "u8) ? new("audio/mp4", PreviewKind.Audio) : new("video/mp4", PreviewKind.Video);
        if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8)) return new("audio/mpeg", PreviewKind.Audio);
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return new("audio/mpeg", PreviewKind.Audio);
        if (TextExtensions.Contains(ext) && LooksLikeText(head)) return new("text/plain; charset=utf-8", PreviewKind.Text);
        return SniffedContent.Unknown;
    }

    /// <summary>Valid UTF-8 (a cut-off last character is fine), no NUL and almost no control characters.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return true;
        if (sample.IndexOf((byte)0) >= 0) return false;
        var trimmed = sample;
        for (var cut = 0; cut < 4 && cut < sample.Length; cut++) // allow the sample to end inside a multi-byte character
        {
            if (IsValidUtf8(sample[..(sample.Length - cut)])) { trimmed = sample[..(sample.Length - cut)]; break; }
            if (cut == 3) return false;
        }
        var control = 0;
        foreach (var b in trimmed) if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C and not 0x1B) control++;
        return control * 100 <= trimmed.Length; // ≤ 1 %
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try { new UTF8Encoding(false, true).GetCharCount(bytes); return true; }
        catch (ArgumentException) { return false; }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => haystack.IndexOf(needle);
}
