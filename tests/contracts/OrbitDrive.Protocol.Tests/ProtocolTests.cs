using System.Text.Json;
using System.Text.Json.Nodes;
using OrbitDrive.Protocol;

namespace OrbitDrive.Protocol.Tests;

public class ProtocolTests
{
    private static string Fixture(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Join(directory, "tests", "contracts", "fixtures"))) directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("fixtures");
        return File.ReadAllText(Path.Join(directory, "tests", "contracts", "fixtures", name + ".json"));
    }

    private static void RoundTrips<T>(string name)
    {
        var json = Fixture(name);
        var again = ProtocolJson.Serialize(ProtocolJson.Deserialize<T>(json));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(again)), $"{name} did not round-trip: {again}");
    }

    [Fact] public void DirectoryPageFixtureRoundTrips() => RoundTrips<DirectoryPage>("DirectoryPage");
    [Fact] public void EnvelopeFixtureRoundTrips() => RoundTrips<Envelope>("Envelope");
    [Fact] public void ErrorResponseFixtureRoundTrips() => RoundTrips<ErrorResponse>("ErrorResponse");
    [Fact] public void DownloadSessionFixtureRoundTrips() => RoundTrips<DownloadSession>("DownloadSession");

    [Fact]
    public void SizesBeyondJavaScriptSafeIntegersSurviveExactly()
    {
        var page = ProtocolJson.Deserialize<DirectoryPage>(Fixture("DirectoryPage"));
        Assert.Equal(9007199254740993L, page.Entries[0].Size);
        Assert.Equal(-1L, page.Entries[1].ModifiedUnixMs);
        Assert.Contains("\"size\":\"9007199254740993\"", ProtocolJson.Serialize(page));
        var open = ProtocolJson.FromElement<OpenReadRequest>(ProtocolJson.Deserialize<Envelope>(Fixture("Envelope")).Payload!.Value);
        Assert.Equal(long.MaxValue - 1, open.Offset);
    }

    [Theory]
    [InlineData("123")]                       // JSON number: could already have been rounded by a JS peer
    [InlineData("\"+5\"")]
    [InlineData("\"05\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\"1e3\"")]
    [InlineData("\" 7\"")]
    [InlineData("\"9223372036854775808\"")]   // overflow
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Int64RejectsNonCanonicalEncodings(string encoded)
    {
        var json = $"{{\"capability\":\"c\",\"urlPath\":\"/\",\"name\":\"n\",\"size\":{encoded},\"revision\":\"r\",\"expiresAtUnixMs\":\"1\"}}";
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<DownloadSession>(json));
    }

    [Fact]
    public void MissingRequiredFieldsAndUnknownEnumValuesAreRejected()
    {
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<ErrorResponse>("{\"error\":{\"code\":\"not_found\"}}"));
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<ErrorResponse>("{\"error\":{\"code\":\"execute_command\",\"message\":\"x\"}}"));
        Assert.Throws<JsonException>(() => ProtocolJson.Deserialize<Envelope>("{\"v\":1,\"id\":\"a\",\"kind\":\"shell\"}"));
    }

    [Fact]
    public void UnknownFieldsFromNewerPeersAreIgnoredAndOptionalFieldsOmitted()
    {
        var envelope = ProtocolJson.Deserialize<Envelope>("{\"v\":1,\"id\":\"a\",\"kind\":\"evt\",\"futureField\":{\"x\":1}}");
        Assert.Null(envelope.Action);
        Assert.Equal("{\"v\":1,\"id\":\"a\",\"kind\":\"evt\"}", ProtocolJson.Serialize(envelope));
    }

    [Fact]
    public void ActionSetIsClosedAndContainsNoExecutionPrimitive()
    {
        Assert.Equal(1, ProtocolInfo.Version);
        Assert.DoesNotContain(Actions.All, a => a.Contains("exec") || a.Contains("shell") || a.Contains("command") || a.Contains("proxy"));
        Assert.Contains(Actions.OpenRead, Actions.All);
    }
}
