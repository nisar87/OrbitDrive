using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbitDrive.Core;

// D11 diagnostics. Three rules shape everything here:
//   1. Nothing private is recorded by default: no file names, folder paths, file contents, tokens, e-mail addresses or links with secrets.
//      Entries are a kind (from a short fixed vocabulary), opaque ids, counts, byte totals and outcome codes.
//   2. Every free-text value is cleaned on the way in (Redactor) even if a caller forgets to, and again when a support bundle is built.
//   3. Disk use is bounded (rotation), and nothing leaves the PC: a support bundle is a local file the owner previews first and chooses to share.

/// <summary>Replaces anything that could identify a person, a file or a secret with a neutral marker.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\bhttps?://\S+")] private static partial Regex Url();
    [GeneratedRegex(@"(?i)\bBearer\s+(?!<secret>)\S+")] private static partial Regex BearerToken();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")] private static partial Regex Email();
    [GeneratedRegex(@"\\\\[^\s""'<>|]+")] private static partial Regex UncPath();
    [GeneratedRegex(@"(?i)\b[A-Z]:[\\/][^\s""'<>|]*")] private static partial Regex WindowsPath();
    [GeneratedRegex(@"(?<![\w.:/-])~?/(?:[^\s/""'<>|]+/)+[^\s""'<>|]*")] private static partial Regex UnixPath();
    // A name with an extension that contains at least one letter ("report.pdf", "a.b.txt"), but not a version such as 1.2.0.
    [GeneratedRegex(@"[^\s\\/:*?""<>|,;()\[\]{}]+\.(?=[A-Za-z0-9]{1,8}(?![\w]))(?=[0-9]*[A-Za-z])[A-Za-z0-9]{1,8}(?![\w.])")] private static partial Regex FileName();
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{32,}(?![A-Za-z0-9_-])")] private static partial Regex LongToken();

    private static readonly (Regex Pattern, string Marker)[] Rules =
    [
        (Url(), "<link>"), (BearerToken(), "Bearer <secret>"), (Email(), "<email>"), (UncPath(), "<path>"), (WindowsPath(), "<path>"),
        (UnixPath(), "<path>"), (FileName(), "<file>"), (LongToken(), "<secret>"),
    ];

    /// <summary>The text with every private-looking part replaced; control characters removed.</summary>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new string(value.Where((c) => !char.IsControl(c) || c == ' ').ToArray());
        foreach (var (pattern, marker) in Rules) text = pattern.Replace(text, marker);
        return text;
    }

    /// <summary>True when the text still contains something that looks like a path, file name, address, link or secret.</summary>
    public static bool LooksPrivate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var (pattern, _) in Rules) if (pattern.IsMatch(value)) return true;
        return false;
    }
}

/// <summary>One line of the owner-visible activity history. Detail is already cleaned; it never holds a path or a file name.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, string Kind, string Detail, long? Bytes);

public sealed class ActivityLogOptions
{
    public long MaxFileBytes { get; init; } = 256 * 1024;
    public int MaxFiles { get; init; } = 5;
    public int MaxDetailChars { get; init; } = 160;
}

/// <summary>
/// A small append-only history of what the app did (connected, a browser was paired, a download finished, ...). Rotates by size,
/// keeps a fixed number of files, never throws into the caller, and cleans every entry.
/// </summary>
public sealed partial class ActivityLog
{
    private readonly string _directory;
    private readonly ActivityLogOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // a local text file for the owner to read, not HTML

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")] private static partial Regex KindPattern();

    public ActivityLog(string directory, ActivityLogOptions? options = null, TimeProvider? clock = null)
    {
        _directory = directory; _options = options ?? new ActivityLogOptions(); _clock = clock ?? TimeProvider.System;
        if (_options.MaxFiles < 1 || _options.MaxFileBytes < 512) throw new ArgumentException("Activity log limits are too small to be useful.");
    }

    private string PathOf(int index) => Path.Join(_directory, index == 0 ? "activity.jsonl" : $"activity.{index}.jsonl");

    /// <summary>Adds an entry. A kind that is not a short lower-case word is recorded as "unknown"; the detail is cleaned and shortened.</summary>
    public void Record(string kind, string detail = "", long? bytes = null)
    {
        try
        {
            var cleanKind = KindPattern().IsMatch(kind ?? "") ? kind! : "unknown";
            var cleanDetail = Redactor.Text(detail);
            if (cleanDetail.Length > _options.MaxDetailChars) cleanDetail = cleanDetail[.._options.MaxDetailChars] + "…";
            var line = JsonSerializer.Serialize(new ActivityEntry(_clock.GetUtcNow(), cleanKind, cleanDetail, bytes is < 0 ? null : bytes), Json) + "\n";
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var current = new FileInfo(PathOf(0));
                if (current.Exists && current.Length + line.Length > _options.MaxFileBytes) Rotate();
                File.AppendAllText(PathOf(0), line, Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* diagnostics must never break the app */ }
    }

    private void Rotate()
    {
        var last = _options.MaxFiles - 1;
        if (File.Exists(PathOf(last)) && last > 0) File.Delete(PathOf(last));
        for (var i = last - 1; i >= 1; i--) if (File.Exists(PathOf(i))) File.Move(PathOf(i), PathOf(i + 1), overwrite: true);
        if (last == 0) File.Delete(PathOf(0));
        else File.Move(PathOf(0), PathOf(1), overwrite: true);
    }

    /// <summary>The newest entries first. Damaged lines are skipped, never fatal.</summary>
    public IReadOnlyList<ActivityEntry> Recent(int count)
    {
        count = Math.Clamp(count, 1, 2000);
        var found = new List<ActivityEntry>();
        lock (_gate)
        {
            for (var i = 0; i < _options.MaxFiles && found.Count < count; i++)
            {
                if (!File.Exists(PathOf(i))) continue;
                string[] lines;
                try { lines = File.ReadAllLines(PathOf(i), Encoding.UTF8); } catch (IOException) { continue; }
                for (var j = lines.Length - 1; j >= 0 && found.Count < count; j--)
                {
                    try { if (JsonSerializer.Deserialize<ActivityEntry>(lines[j], Json) is { } entry && KindPattern().IsMatch(entry.Kind ?? "")) found.Add(entry with { Detail = Redactor.Text(entry.Detail) }); }
                    catch (JsonException) { /* a half-written or damaged line */ }
                }
            }
        }
        return found;
    }

    /// <summary>How much disk the history uses right now.</summary>
    public (int Files, long Bytes) Footprint()
    {
        lock (_gate)
        {
            var files = 0; long bytes = 0;
            for (var i = 0; i < _options.MaxFiles; i++) if (File.Exists(PathOf(i))) { files++; bytes += new FileInfo(PathOf(i)).Length; }
            return (files, bytes);
        }
    }

    /// <summary>Deletes the whole history (the owner's choice).</summary>
    public void Clear()
    {
        lock (_gate) { for (var i = 0; i < _options.MaxFiles; i++) try { File.Delete(PathOf(i)); } catch (IOException) { } }
    }
}

/// <summary>One file that would go into a support bundle, with a short preview the owner can read first.</summary>
public sealed record BundleItem(string Name, string Description, long Bytes, string Preview);

/// <summary>
/// A support bundle is a local ZIP the owner creates on purpose. It is previewed before it exists, needs an explicit yes, holds only
/// the cleaned activity history and a few non-identifying facts about the app, and is refused outright if anything in it still looks private.
/// </summary>
public sealed class SupportBundle(ActivityLog activity, Func<IReadOnlyDictionary<string, string>> facts, string directory, TimeProvider? clock = null)
{
    public const int KeepBundles = 5;
    private static readonly JsonSerializerOptions BundleJson = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private (string Name, string Description, string Content)[] Build()
    {
        var history = string.Join("\n", activity.Recent(500).Reverse().Select((e) => JsonSerializer.Serialize(e, BundleJson)));
        var info = string.Join("\n", facts().OrderBy((pair) => pair.Key, StringComparer.Ordinal).Select((pair) => $"{pair.Key}: {pair.Value}"));
        const string readme = "This file was made on this PC because the owner asked for it. It was not sent anywhere. It contains the recent activity history " +
            "(what happened and when, never file names, folder paths or file contents) and a few facts about the app. Share it only if you choose to.";
        var items = new[]
        {
            ("activity.jsonl", "Recent activity: what the app did and when (no file names, folders or contents)", history),
            ("about.txt", "App version, system type and counts (no names, paths or accounts)", info),
            ("README.txt", "What this file is", readme),
        };
        foreach (var (name, _, content) in items)
            if (Redactor.LooksPrivate(content)) throw new InvalidOperationException($"{name} would contain something that looks private, so no support bundle was made.");
        return items;
    }

    /// <summary>What a bundle would contain right now. Nothing is written.</summary>
    public IReadOnlyList<BundleItem> Preview() =>
        [.. Build().Select((item) => new BundleItem(item.Name, item.Description, Encoding.UTF8.GetByteCount(item.Content),
            item.Content.Length > 1500 ? item.Content[..1500] + "\n…" : item.Content))];

    /// <summary>Writes the bundle into the support folder. Without a clear yes, nothing is written.</summary>
    public string Create(bool consent)
    {
        if (!consent) throw new ArgumentException("A support bundle is only made when the owner says yes.");
        var items = Build();
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, $"orbitdrive-support-{_clock.GetUtcNow():yyyyMMdd-HHmmss-fff}.zip");
        var temporary = path + ".part";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            foreach (var (name, _, content) in items)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        File.Move(temporary, path, overwrite: false);
        foreach (var old in Directory.EnumerateFiles(directory, "orbitdrive-support-*.zip").OrderByDescending((f) => f, StringComparer.Ordinal).Skip(KeepBundles))
            try { File.Delete(old); } catch (IOException) { }
        return path;
    }
}
