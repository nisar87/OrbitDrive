using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbitDrive.Protocol;

/// <summary>64-bit integers travel as decimal strings so JavaScript never rounds sizes or offsets.</summary>
public sealed class Int64StringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Parse(ref reader);

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));

    internal static long Parse(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("64-bit integers must be encoded as decimal strings.");
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > 20 || (text.Length > 1 && text[0] == '0') || text[0] == '+' ||
            !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
            value.ToString(CultureInfo.InvariantCulture) != text)
            throw new JsonException("Invalid 64-bit integer string.");
        return value;
    }
}

public sealed class NullableInt64StringConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Int64StringConverter.Parse(ref reader);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        MaxDepth = 16,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Unexpected null document.");

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T FromElement<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException("Unexpected null payload.");
}

/// <summary>Typed protocol failure carried across the control channel and HTTP API.</summary>
public sealed class ProtocolException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code { get; } = code;
    public ErrorInfo ToInfo() => new() { Code = Code, Message = Message };
}
