using System.Text.Json;

namespace OrbitDrive.Core;

/// <summary>
/// Addresses a release build ships with (defaults.json next to the program), so a first start already knows its relay. They apply only while the owner has not
/// set a relay, are validated by the same rules as typed addresses, and never override anything the owner chose.
/// </summary>
public static class ReleaseDefaults
{
    public sealed record Addresses(string Gateway, string? WebOrigin);

    public static Addresses? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 4096) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("gateway", out var gateway) || gateway.ValueKind != JsonValueKind.String) return null;
            var web = root.TryGetProperty("webOrigin", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            return new Addresses(gateway.GetString()!, web);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
