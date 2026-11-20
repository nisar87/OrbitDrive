using System.Text;
using OrbitDrive.Core;
using OrbitDrive.Core.Ipc;
using OrbitDrive.Protocol;
using SharpFuzz;

// Fuzz targets for the PC-side parsers that read bytes or text from outside the trust boundary.
//   dotnet run -c Release -- <target> < input        one input from stdin (a crash is a non-zero exit)
//   dotnet run -c Release -- <target> --afl          under afl-fuzz after `sharpfuzz` instrumented OrbitDrive.Core.dll
//   dotnet run -c Release -- smoke [iterations] [seed]   seeded random inputs plus the corpus, in-process, no AFL needed
// A target passes when it returns normally. Expected, documented refusals are caught; ANY other exception, hang or broken invariant is a finding.
var targets = new Dictionary<string, Action<byte[]>>
{
    ["sniff"] = Sniff,
    ["probe"] = Probe,
    ["segment"] = Segment,
    ["label"] = Label,
    ["line"] = Line,
};

if (args.Length >= 1 && args[0] == "smoke") return Smoke(args, targets);
if (args.Length == 0 || !targets.TryGetValue(args[0], out var target))
{
    Console.Error.WriteLine("usage: OrbitDrive.Fuzz <" + string.Join('|', targets.Keys) + "> [--afl]  |  smoke [iterations] [seed]");
    return 2;
}
if (args.Length > 1 && args[1] == "--afl") Fuzzer.OutOfProcess.Run(stream => target(ReadAll(stream)));
else target(ReadAll(Console.OpenStandardInput()));
return 0;

static byte[] ReadAll(Stream stream) { using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
static void Broken(string message) => throw new InvalidOperationException("Invariant broken: " + message);

// First byte picks a file name extension; the rest is the head of the file.
static void Sniff(byte[] data)
{
    string[] names = ["a.txt", "a.png", "a.pdf", "a.mp4", "a", "a.mp3", "a.svg", "a.html", "a.jpg", "a.bmp", "a.ogv", "a.weba", "a.md"];
    var name = names[data.Length == 0 ? 0 : data[0] % names.Length];
    ReadOnlySpan<byte> head = data.Length == 0 ? ReadOnlySpan<byte>.Empty : data.AsSpan(1);
    var result = ContentSniffer.Classify(head, name);
    if (string.IsNullOrEmpty(result.ContentType)) Broken("empty content type");
    if (result.Kind == PreviewKind.None && result.Inlineable) Broken("inlineable with no kind");
    if (result.Inlineable && (result.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase) || result.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)))
        Broken("a scriptable type would be served inline");
    ContentSniffer.LooksLikeText(head);
}

static void Probe(byte[] data)
{
    string[] types = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/svg+xml", "text/plain"];
    var type = types[data.Length == 0 ? 0 : data[0] % types.Length];
    ReadOnlySpan<byte> body = data.Length == 0 ? ReadOnlySpan<byte>.Empty : data.AsSpan(1);
    // TryReadSize reports what the header says, however absurd; Require is the gate every caller goes through, so the limits are asserted on it.
    var ok = ImageProbe.TryReadSize(body, type, out var width, out var height);
    if (!ok && (width != 0 || height != 0)) Broken("size set although refused");
    if (ok && (width < 0 || height < 0)) Broken($"negative size {width}x{height}");
    try
    {
        var (w, h) = ImageProbe.Require(body, type);
        if (w <= 0 || h <= 0 || w > ImageProbe.MaxDimension || h > ImageProbe.MaxDimension || (long)w * h > ImageProbe.MaxPixels)
            Broken($"Require accepted size {w}x{h}");
    }
    catch (ProtocolException e) when (e.Code is ErrorCode.InvalidRequest or ErrorCode.LimitExceeded) { /* documented refusals */ }
}

static void Segment(byte[] data)
{
    var text = Encoding.UTF8.GetString(data);
    if (!PathPolicy.IsSafeSegment(text)) return;
    // Accepted names must be harmless to join under a root on Windows and Unix, whatever the rule's own code says.
    if (text.Length is 0 or > 255 || text is "." or "..") Broken("accepted '.', '..' or an out-of-range length");
    foreach (var c in text)
        if (c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\u007f') Broken("accepted a separator or control character");
    if (text[^1] is '.' or ' ') Broken("accepted a trailing dot or space");
}

static void Label(byte[] data)
{
    var version = UpdateTrust.ParseLabel(Encoding.UTF8.GetString(data));
    if (version.Major < 0 || version.Minor < 0) Broken("negative version");
}

static void Line(byte[] data)
{
    using var stream = new MemoryStream(data);
    try { LocalIpc.ReadLineAsync(stream, CancellationToken.None).GetAwaiter().GetResult(); }
    catch (Exception e) when (e is IOException or InvalidDataException) { /* documented refusals: truncated or oversized message */ }
}

static int Smoke(string[] args, Dictionary<string, Action<byte[]>> targets)
{
    var iterations = args.Length > 1 ? int.Parse(args[1]) : 20_000;
    var seed = args.Length > 2 ? int.Parse(args[2]) : 20261120;
    var random = new Random(seed);
    var seeds = new List<byte[]>();
    var corpus = Path.Combine(AppContext.BaseDirectory, "corpus");
    if (Directory.Exists(corpus)) foreach (var file in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)) seeds.Add(File.ReadAllBytes(file));
    seeds.Add([]);
    var runs = 0;
    foreach (var (name, run) in targets)
    {
        for (var i = 0; i < iterations; i++)
        {
            var input = Mutate(random, seeds[random.Next(seeds.Count)]);
            try { run(input); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"FINDING in '{name}' (seed {seed}, iteration {i}, {input.Length} bytes): {e.GetType().Name}: {e.Message}");
                Console.Error.WriteLine("input (base64): " + Convert.ToBase64String(input));
                return 1;
            }
            runs++;
        }
    }
    Console.WriteLine($"smoke ok: {runs} inputs across {targets.Count} targets, seed {seed}, {seeds.Count} corpus seeds");
    return 0;
}

static byte[] Mutate(Random random, byte[] seed)
{
    var data = seed.ToList();
    for (var n = random.Next(1, 6); n > 0; n--)
    {
        switch (random.Next(5))
        {
            case 0 when data.Count > 0: data[random.Next(data.Count)] = (byte)random.Next(256); break;
            case 1: data.Insert(random.Next(data.Count + 1), (byte)random.Next(256)); break;
            case 2 when data.Count > 0: data.RemoveAt(random.Next(data.Count)); break;
            case 3: data.AddRange(Enumerable.Range(0, random.Next(1, 40)).Select(_ => (byte)random.Next(256))); break;
            case 4 when data.Count > 1: data.RemoveRange(random.Next(data.Count), 1); break;
        }
    }
    return data.Count > 70_000 ? [.. data.Take(70_000)] : [.. data];
}
