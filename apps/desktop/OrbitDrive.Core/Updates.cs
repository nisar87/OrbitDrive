using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbitDrive.Core;

public enum UpdateRejection
{
    TooLarge, Malformed, UnknownKey, BadSignature, UnsupportedFormat, WrongChannel, Expired, NotYetValid, ValidityTooLong,
    NotNewer, RolledBack, IncompatibleProtocol, BadFile, BadUrl, UntrustedHost, NoFileForThisPc, SizeMismatch, HashMismatch, DrainFailed, NotStaged, Interrupted,
}

public sealed class UpdateRejectedException(UpdateRejection reason, string message) : Exception(message)
{
    public UpdateRejection Reason { get; } = reason;
}

public sealed class UpdateFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class UpdateManifest
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("minProtocol")] public int MinProtocol { get; set; }
    [JsonPropertyName("maxProtocol")] public int MaxProtocol { get; set; }
    [JsonPropertyName("published")] public long Published { get; set; }
    [JsonPropertyName("expires")] public long Expires { get; set; }
    [JsonPropertyName("files")] public List<UpdateFile> Files { get; set; } = [];
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
}

/// <summary>A manifest whose signature, freshness, version, compatibility and files all passed. Only this type may be given to <see cref="UpdateStager"/>.</summary>
public sealed record VerifiedUpdate(Version Version, UpdateFile File, string Notes);

/// <summary>What the person who signs releases must publish: the manifest bytes and signatures by one or more keys the app was built to trust.</summary>
public sealed class SignedEnvelope
{
    [JsonPropertyName("manifest")] public string Manifest { get; set; } = "";
    [JsonPropertyName("signatures")] public List<EnvelopeSignature> Signatures { get; set; } = [];
}
public sealed class EnvelopeSignature
{
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";
    [JsonPropertyName("sig")] public string Sig { get; set; } = "";
}

/// <summary>
/// Decides whether an update description may be believed. Nothing from the network is trusted until an ECDSA P-256 signature by a key built into
/// the app verifies over the exact manifest bytes. Then: the manifest must be current (not expired, not from the future, valid for at most
/// 90 days), for this channel and PC architecture, strictly newer than what runs and than the newest version ever accepted here (no rollback),
/// compatible with the protocol this build speaks, and every download must be https from a listed host with a safe name, size and hash.
/// The caller never gets to choose a URL: it can only follow the one inside a verified manifest.
/// </summary>
public sealed class UpdateVerifier(IReadOnlyDictionary<string, byte[]> trustedKeysSpki, IReadOnlyCollection<string> allowedHosts, string channel, string architecture, int protocolVersion, TimeProvider time)
{
    public const int MaxEnvelopeBytes = 64 * 1024;
    public const long MaxFileBytes = 2L << 30;
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(90);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    private static readonly string[] Extensions = [".msix", ".msixbundle", ".msi", ".exe", ".appinstaller", ".zip"];   // ".zip" = the portable build, applied by PortableUpdateLauncher

    private static UpdateRejectedException Reject(UpdateRejection reason, string message) => new(reason, message);

    public VerifiedUpdate Verify(ReadOnlySpan<byte> envelopeJson, Version running, Version? highestEverAccepted = null)
    {
        if (envelopeJson.Length is 0 or > MaxEnvelopeBytes) throw Reject(UpdateRejection.TooLarge, "The update information is not a plausible size.");
        SignedEnvelope envelope;
        byte[] manifestBytes;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeJson) ?? throw new JsonException();
            manifestBytes = Convert.FromBase64String(envelope.Manifest.Replace('-', '+').Replace('_', '/').PadRight((envelope.Manifest.Length + 3) / 4 * 4, '='));
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { throw Reject(UpdateRejection.Malformed, "The update information could not be read."); }
        if (envelope.Signatures.Count is 0 or > 8) throw Reject(UpdateRejection.BadSignature, "The update is not signed.");

        var verified = false;
        foreach (var signature in envelope.Signatures)
        {
            if (!trustedKeysSpki.TryGetValue(signature.KeyId, out var spki)) continue;
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(spki, out _);
                var sig = Convert.FromBase64String(signature.Sig.Replace('-', '+').Replace('_', '/').PadRight((signature.Sig.Length + 3) / 4 * 4, '='));
                if (key.VerifyData(manifestBytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { verified = true; break; }
            }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { /* a bad signature is the same as none */ }
        }
        if (!verified)
            throw Reject(envelope.Signatures.Any(s => trustedKeysSpki.ContainsKey(s.KeyId)) ? UpdateRejection.BadSignature : UpdateRejection.UnknownKey, "The update is not signed by a key this app trusts.");

        UpdateManifest manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes) ?? throw new JsonException(); }
        catch (JsonException) { throw Reject(UpdateRejection.Malformed, "The update information could not be read."); }
        if (manifest.Format != 1) throw Reject(UpdateRejection.UnsupportedFormat, "This update information is from a different version of OrbitDrive.");
        if (manifest.Channel != channel) throw Reject(UpdateRejection.WrongChannel, "This update is for a different release channel.");

        var now = time.GetUtcNow();
        var published = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(manifest.Published, 0, 253402300799));
        var expires = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(manifest.Expires, 0, 253402300799));
        if (published > now + Skew) throw Reject(UpdateRejection.NotYetValid, "The update information is dated in the future.");
        if (expires <= now) throw Reject(UpdateRejection.Expired, "The update information has expired.");
        if (expires - published > MaxValidity || expires <= published) throw Reject(UpdateRejection.ValidityTooLong, "The update information is valid for an unreasonable time.");

        if (!Version.TryParse(manifest.Version, out var version)) throw Reject(UpdateRejection.Malformed, "The update has no usable version.");
        if (version <= running) throw Reject(UpdateRejection.NotNewer, "This update is not newer than the version already installed.");
        if (highestEverAccepted is not null && version < highestEverAccepted) throw Reject(UpdateRejection.RolledBack, "This update is older than one already seen. Older releases are never installed.");
        if (protocolVersion < manifest.MinProtocol || protocolVersion > manifest.MaxProtocol && manifest.MaxProtocol != 0)
            throw Reject(UpdateRejection.IncompatibleProtocol, "This update does not work with the service this PC connects to.");

        var file = manifest.Files.FirstOrDefault(f => string.Equals(f.Arch, architecture, StringComparison.OrdinalIgnoreCase)) ?? throw Reject(UpdateRejection.NoFileForThisPc, "There is no download for this PC's processor.");
        CheckFile(file);
        return new VerifiedUpdate(version, file, manifest.Notes.Length > 1000 ? manifest.Notes[..1000] : manifest.Notes);
    }

    private void CheckFile(UpdateFile file)
    {
        var name = file.Name;
        if (name.Length is 0 or > 100 || name is "." or ".." || name.Any(c => c is '/' or '\\' or ':' or '\0' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c)) ||
            name.EndsWith('.') || name.EndsWith(' ') || !Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            throw Reject(UpdateRejection.BadFile, "The update file has an unsafe name.");
        if (file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit)) throw Reject(UpdateRejection.BadFile, "The update file has no valid checksum.");
        if (file.Size is <= 0 or > MaxFileBytes) throw Reject(UpdateRejection.BadFile, "The update file has an implausible size.");
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length > 0 || !url.IsDefaultPort || url.Fragment.Length > 0 || url.Query.Length > 0)
            throw Reject(UpdateRejection.BadUrl, "The update download address is not acceptable.");
        if (!allowedHosts.Contains(url.IdnHost, StringComparer.OrdinalIgnoreCase)) throw Reject(UpdateRejection.UntrustedHost, "The update would be downloaded from a host this app does not trust.");
    }
}

/// <summary>Whatever starts the installer. It is given only a path inside the staging folder, only after the file was hashed again.</summary>
public interface IInstallerLauncher { void Launch(string verifiedPath); }

/// <summary>
/// Stores a verified update on disk safely and survives being interrupted at any moment. The file is written under a temporary name and hashed as
/// it arrives; only a file of exactly the signed size and hash is ever given its real name. A journal records "staged" and "applying"; on the next
/// start an interrupted apply is reported and the staged file is checked again rather than trusted. The newest version ever accepted is remembered
/// so an old signed manifest cannot be replayed. Nothing here deletes anything outside its own folder.
/// </summary>
public sealed class UpdateStager(string folder)
{
    private sealed class Journal
    {
        [JsonPropertyName("phase")] public string Phase { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("highest")] public string Highest { get; set; } = "";
    }

    private string JournalPath => Path.Join(folder, "journal.json");
    private Journal Read()
    {
        try { return File.Exists(JournalPath) ? JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath)) ?? new Journal() : new Journal(); }
        catch (Exception e) when (e is JsonException or IOException) { return new Journal(); }        // unreadable: trust nothing in it
    }
    private void Write(Journal journal)
    {
        Directory.CreateDirectory(folder);
        var temp = JournalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal));
        File.Move(temp, JournalPath, overwrite: true);
    }

    /// <summary>The newest version this PC ever accepted a manifest for (for rollback protection), or null.</summary>
    public Version? HighestAccepted => Version.TryParse(Read().Highest, out var v) ? v : null;

    public string StagedPath(UpdateFile file) => Path.Join(folder, file.Name);

    /// <summary>Receives the file, checks size and SHA-256 while it arrives, and only then gives it its real name. Any failure leaves nothing behind.</summary>
    public async Task<string> StageAsync(VerifiedUpdate update, Stream source, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        var final = StagedPath(update.File);
        var partial = final + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += read;
                    if (total > update.File.Size) throw new UpdateRejectedException(UpdateRejection.SizeMismatch, "The download is larger than the signed size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
                await output.FlushAsync(cancellation);
            }
            if (total != update.File.Size) throw new UpdateRejectedException(UpdateRejection.SizeMismatch, "The download is shorter than the signed size.");
            if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(update.File.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRejectedException(UpdateRejection.HashMismatch, "The download does not match its signed checksum.");
            File.Move(partial, final, overwrite: true);
            var journal = Read();
            journal.Phase = "staged"; journal.Version = update.Version.ToString(); journal.Name = update.File.Name; journal.Sha256 = update.File.Sha256.ToLowerInvariant(); journal.Size = update.File.Size;
            if (!Version.TryParse(journal.Highest, out var highest) || update.Version > highest) journal.Highest = update.Version.ToString();
            Write(journal);
            return final;
        }
        catch (Exception e) when (e is not UpdateRejectedException and (IOException or OperationCanceledException or HttpRequestException or UnauthorizedAccessException))
        {
            TryDelete(partial);
            throw new UpdateRejectedException(UpdateRejection.Interrupted, "The download was interrupted. Nothing was installed.");
        }
        catch { TryDelete(partial); throw; }
    }

    /// <summary>
    /// Hands the staged file to the installer, but only after active work was drained and the file was hashed once more (what is on disk now is
    /// what was signed). The journal says "applying" first, so a crash in the middle is noticed at the next start.
    /// </summary>
    public async Task ApplyAsync(Func<CancellationToken, Task> drainActiveWork, IInstallerLauncher launcher, TimeSpan drainTimeout, CancellationToken cancellation)
    {
        var journal = Read();
        if (journal.Phase != "staged" || journal.Name.Length == 0) throw new UpdateRejectedException(UpdateRejection.NotStaged, "There is no verified update waiting.");
        var path = Path.Join(folder, journal.Name);
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            throw new UpdateRejectedException(UpdateRejection.NotStaged, "The staged update is missing.");
        try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(drainTimeout); await drainActiveWork(timeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException) { throw new UpdateRejectedException(UpdateRejection.DrainFailed, "Running transfers could not be paused, so nothing was changed."); }
        bool intact;
        await using (var stream = File.OpenRead(path))
            intact = stream.Length == journal.Size && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation)).Equals(journal.Sha256, StringComparison.Ordinal);
        if (!intact)
        {
            // Deleted only after the stream is closed: Windows refuses to delete a file that is still open.
            TryDelete(path);
            journal.Phase = ""; Write(journal);
            throw new UpdateRejectedException(UpdateRejection.HashMismatch, "The staged update changed on disk and was removed.");
        }
        journal.Phase = "applying"; Write(journal);
        launcher.Launch(path);
    }

    public enum RecoveryOutcome { Nothing, ApplyInterrupted, StagedReady, StagedDamaged }

    /// <summary>Run at start. Removes partial downloads and reports what the last run left behind; the running version is never replaced here.</summary>
    public RecoveryOutcome Recover(Version running)
    {
        if (!Directory.Exists(folder)) return RecoveryOutcome.Nothing;
        foreach (var partial in Directory.EnumerateFiles(folder, "*.part")) TryDelete(partial);
        var journal = Read();
        if (journal.Phase is "" or null) return RecoveryOutcome.Nothing;
        if (Version.TryParse(journal.Version, out var staged) && staged <= running)
        {
            // The update was installed (we are running it or newer): its file is no longer needed.
            TryDelete(Path.Join(folder, journal.Name)); journal.Phase = ""; Write(journal);
            return RecoveryOutcome.Nothing;
        }
        var path = Path.Join(folder, journal.Name);
        var intact = File.Exists(path) && new FileInfo(path).Length == journal.Size && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == journal.Sha256;
        var wasApplying = journal.Phase == "applying";
        if (!intact) { TryDelete(path); journal.Phase = ""; Write(journal); return RecoveryOutcome.StagedDamaged; }
        journal.Phase = "staged"; Write(journal);                  // the old version is still what runs; the update can be tried again
        return wasApplying ? RecoveryOutcome.ApplyInterrupted : RecoveryOutcome.StagedReady;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
