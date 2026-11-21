using System.Text;
using OrbitDrive.Gateway;
using Xunit;

namespace OrbitDrive.Gateway.Tests;

/// <summary>
/// Seeded, replayable fuzzing of the range parser (set ORBIT_FUZZ_SEED to replay a failure). Whatever a client sends, the parser must not throw and must
/// never return a range that reaches outside the file.
/// </summary>
public class RangeParserFuzzTests
{
    private static readonly long[] Sizes = [0, 1, 2, 999, 1000, 1L << 31, (1L << 40) + 7, long.MaxValue];
    private static readonly string[] Pieces = ["bytes=", "bytes", "=", "-", ",", " ", "0", "1", "9", "99999999999999999999", "9223372036854775807", "9223372036854775808", "-1", "+1", "٣", "０", "\u0000", "\n", "a", "bytes=0-", "bytes=-1"];

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("ORBIT_FUZZ_SEED"), out var seed) ? seed : 20261122;

    private static string Mutate(Random random)
    {
        var text = new StringBuilder();
        var count = random.Next(0, 9);
        for (var i = 0; i < count; i++) text.Append(Pieces[random.Next(Pieces.Length)]);
        if (random.Next(4) == 0) text.Append(random.Next(0, 70_000));
        return text.ToString();
    }

    [Fact]
    public void Random_headers_never_throw_and_never_reach_outside_the_file()
    {
        var random = new Random(Seed);
        for (var round = 0; round < 40_000; round++)
        {
            var header = random.Next(10) == 0 ? null : Mutate(random);
            var size = Sizes[random.Next(Sizes.Length)];
            ByteRange? result = null;
            var thrown = Record.Exception(() => result = RangeParser.Parse(header, size));
            Assert.True(thrown is null, $"seed {Seed}, header {Describe(header)}, size {size}: {thrown}");
            if (result is not { } range) continue;
            Assert.True(range.Start >= 0 && range.Length >= 0, $"seed {Seed}, header {Describe(header)}, size {size}: negative range {range}");
            Assert.True(range.Start <= size && range.Length <= size - range.Start, $"seed {Seed}, header {Describe(header)}, size {size}: {range} reaches outside the file");
            if (range.Partial) Assert.True(range.Length >= 1 && range.Start < size, $"seed {Seed}, header {Describe(header)}, size {size}: empty partial range {range}");
            else Assert.True(range.Start == 0 && range.Length == size, $"seed {Seed}, header {Describe(header)}, size {size}: a full response must be the whole file");
        }
    }

    [Fact]
    public void A_valid_range_always_gives_exactly_those_bytes()
    {
        var random = new Random(Seed);
        for (var round = 0; round < 20_000; round++)
        {
            var size = random.NextInt64(1, 1L << 40);
            var start = random.NextInt64(0, size);
            var end = random.NextInt64(start, size);
            var range = RangeParser.Parse($"bytes={start}-{end}", size);
            Assert.Equal(new ByteRange(start, end - start + 1, true), range);
        }
    }

    private static string Describe(string? header) => header is null ? "(none)" : "[" + string.Join(' ', header.Select(c => ((int)c).ToString("x"))) + "]";
}
