using System.Reflection;
using System.Text.Json;

namespace OrbitDrive.Core;

public enum UpdateState { NotSetUp, UpToDate, Available, Unavailable, Rejected }

/// <summary>What the owner is told after asking for updates. Message is plain language; it never contains addresses, keys or file contents.</summary>
public sealed record UpdateCheck(UpdateState State, string Message, VerifiedUpdate? Update = null);

/// <summary>The public keys this build trusts for updates (an embedded list: key id to base64 SubjectPublicKeyInfo). Public keys only; empty until a release key exists.</summary>
public static class UpdateTrust
{
    public static IReadOnlyDictionary<string, byte[]> Load()
    {
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("update-keys.json");
        if (stream is null) return new Dictionary<string, byte[]>();
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        return raw.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
    }

    /// <summary>The running build's version (the file's version, e.g. 0.1.12 from "0.1.12+abcdef").</summary>
    public static Version RunningVersion(Assembly? assembly = null)
    {
        var text = (assembly ?? typeof(UpdateTrust).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return ParseLabel(text);
    }

    /// <summary>"0.1.12+abcdef" gives 0.1.12; anything unreadable gives 0.0.0 (so a mislabelled build is offered every real update).</summary>
    public static Version ParseLabel(string text)
    {
        var plus = text.IndexOf('+');
        return Version.TryParse(plus >= 0 ? text[..plus] : text, out var v) ? v : new Version(0, 0, 0);
    }
}

/// <summary>
/// Asks the release feed whether a newer version exists and downloads it. Everything fetched is untrusted until <see cref="UpdateVerifier"/> accepts it;
/// the download URL can only come from a verified manifest, and the file is checked for size and SHA-256 while it arrives.
/// </summary>
public sealed class UpdateClient(HttpClient http, UpdateVerifier verifier, UpdateStager stager, Uri feed, bool hasTrustedKeys)
{
    public async Task<UpdateCheck> CheckAsync(Version running, CancellationToken cancellation)
    {
        if (!hasTrustedKeys) return new UpdateCheck(UpdateState.NotSetUp, "Updates are not set up in this build yet (it contains no update key).");
        byte[] bytes;
        try
        {
            using var response = await http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new UpdateCheck(UpdateState.UpToDate, "No update has been published yet.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > UpdateVerifier.MaxEnvelopeBytes) return new UpdateCheck(UpdateState.Rejected, "The update information is not a plausible size.");
            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellation)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > UpdateVerifier.MaxEnvelopeBytes) return new UpdateCheck(UpdateState.Rejected, "The update information is not a plausible size.");
            }
            bytes = buffer.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellation.IsCancellationRequested) throw;
            return new UpdateCheck(UpdateState.Unavailable, "Could not reach the update server. Check this PC's internet connection and try again.");
        }
        try { var update = verifier.Verify(bytes, running, stager.HighestAccepted); return new UpdateCheck(UpdateState.Available, $"Version {update.Version} is available.", update); }
        catch (UpdateRejectedException e) when (e.Reason is UpdateRejection.NotNewer) { return new UpdateCheck(UpdateState.UpToDate, "OrbitDrive is up to date."); }
        catch (UpdateRejectedException e) when (e.Reason is UpdateRejection.Expired or UpdateRejection.RolledBack) { return new UpdateCheck(UpdateState.UpToDate, "OrbitDrive is up to date (no current update is published)."); }
        catch (UpdateRejectedException e) { return new UpdateCheck(UpdateState.Rejected, "The update was refused for safety: " + e.Message); }
    }

    /// <summary>Downloads the verified update into the staging folder. Throws <see cref="UpdateRejectedException"/> (size, checksum or interruption); nothing is installed.</summary>
    public async Task<string> DownloadAsync(VerifiedUpdate update, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.GetAsync(update.File.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellation);
            return await stager.StageAsync(update, body, cancellation);
        }
        catch (HttpRequestException) { throw new UpdateRejectedException(UpdateRejection.Interrupted, "The download failed. Nothing was installed."); }
    }
}
