using System.Buffers.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OrbitDrive.Core;

namespace OrbitDrive.Agent;

/// <summary>Protects local secrets with DPAPI (CurrentUser) on Windows; owner-only file permissions elsewhere (development hosts).</summary>
internal static class LocalProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private const int UiForbidden = 0x1;

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { pinned.Free(); }
    }

    public static void Write(string path, byte[] secret)
    {
        var data = OperatingSystem.IsWindows() ? Dpapi(secret, protect: true) : secret;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public static byte[] Read(string path)
    {
        var data = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows() ? Dpapi(data, protect: false) : data;
    }
}

/// <summary>Per-user agent state: device identity key, token-sealing key and the owner's approved roots.</summary>
internal sealed class AgentState
{
    private sealed record Config(List<SharedRoot> Roots, string? Gateway = null, string? WebOrigin = null, int MaxUploadKBps = 0, int MaxConcurrentTransfers = 4, bool SendToShortcuts = false, bool ThumbnailsEnabled = true, string? PcName = null);
    private readonly string _configPath;
    private volatile IReadOnlyList<SharedRoot> _roots = [];

    public ECDsa Identity { get; }
    public string PcId { get; }
    public string PublicKeySpki { get; }
    public SealedTokens Tokens { get; }
    public IReadOnlyList<SharedRoot> Roots => _roots;
    public string? Gateway { get; private set; }
    public string? WebOrigin { get; private set; }
    /// <summary>Upload speed limit shared by all transfers, in kilobytes per second; 0 = unlimited.</summary>
    public int MaxUploadKBps { get; private set; }
    /// <summary>How many files this PC sends at the same time (1-16).</summary>
    public int MaxConcurrentTransfers { get; private set; } = 4;
    /// <summary>Owner choice: keep one classic Explorer "Send to" shortcut per paired receiver (the fallback beside the modern menu).</summary>
    public bool SendToShortcuts { get; private set; }
    /// <summary>Owner switch for generating thumbnails (D7). Previews of the original file are separate and always allowed to the paired browser.</summary>
    public bool ThumbnailsEnabled { get; private set; } = true;
    /// <summary>The name the owner gave this PC (shown on paired devices), or null to use the Windows computer name.</summary>
    public string? PcName { get; private set; }
    public ThumbnailService Thumbnails { get; private set; } = null!;
    /// <summary>The one filesystem policy object every part of the agent uses, with staged archives (D8) attached.</summary>
    public FileAccessService Files { get; private set; } = null!;
    public ArchiveService Archives { get; private set; } = null!;
    public SearchService Search { get; private set; } = null!;
    public UploadService Uploads { get; private set; } = null!;
    private readonly object _mutate = new();
    public string Directory_ { get; }

    /// <summary>The owner-visible history of what the app did (no file names, paths or contents).</summary>
    public ActivityLog Activity { get; }

    public AgentState(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory_ = Path.GetFullPath(directory);
        Activity = new ActivityLog(Path.Join(Directory_, "activity"), new ActivityLogOptions { MaxFileBytes = long.TryParse(Environment.GetEnvironmentVariable("ORBIT_ACTIVITY_MAX_BYTES"), out var limit) && limit >= 512 ? limit : 256 * 1024 });   // the override exists so rotation can be tested quickly
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _configPath = Path.Join(directory, "config.json");
        var identityPath = Path.Join(directory, "identity.bin");
        var tokenPath = Path.Join(directory, "token-key.bin");

        Identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(identityPath)) Identity.ImportPkcs8PrivateKey(LocalProtection.Read(identityPath), out _);
        else LocalProtection.Write(identityPath, Identity.ExportPkcs8PrivateKey());
        var spki = Identity.ExportSubjectPublicKeyInfo();
        PublicKeySpki = Base64Url.EncodeToString(spki);
        PcId = Base64Url.EncodeToString(SHA256.HashData(spki));

        if (!File.Exists(tokenPath)) LocalProtection.Write(tokenPath, RandomNumberGenerator.GetBytes(32));
        Tokens = new SealedTokens(LocalProtection.Read(tokenPath));
        var thumbnailOptions = new ThumbnailOptions();
        var helper = ProcessThumbnailRunner.Locate(AppContext.BaseDirectory);
        Thumbnails = new ThumbnailService(new ThumbnailCache(Path.Join(directory, "thumbnails"), thumbnailOptions), helper is null ? null : new ProcessThumbnailRunner(helper, thumbnailOptions), thumbnailOptions);
        Files = new FileAccessService(() => Roots, Tokens) { Thumbnails = Thumbnails };
        Archives = new ArchiveService(Files, Tokens, () => Roots, new ArchiveOptions { Directory = Path.Join(directory, "archives") });
        Files.Archives = Archives;
        Search = new SearchService(Files, () => Roots);
        Uploads = new UploadService(Files, () => Roots, AddUploadUsed, new UploadOptions());
        ReloadRoots();
    }

    public void ReloadRoots()
    {
        if (!File.Exists(_configPath)) { _roots = []; return; }
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath));
        _roots = config?.Roots ?? [];
        Gateway = config?.Gateway;
        WebOrigin = config?.WebOrigin;
        MaxUploadKBps = Math.Clamp(config?.MaxUploadKBps ?? 0, 0, 10_000_000);
        MaxConcurrentTransfers = Math.Clamp(config?.MaxConcurrentTransfers ?? 4, 1, 16);
        SendToShortcuts = config?.SendToShortcuts ?? false;
        ThumbnailsEnabled = config?.ThumbnailsEnabled ?? true;
        PcName = config?.PcName;
        if (Thumbnails is not null) Thumbnails.Enabled = ThumbnailsEnabled;
        // Folders approved before disk binding existed are bound to the disk they are on now (once, when reachable).
        if (_roots.Any(r => r.VolumeId is null && Directory.Exists(r.Path)))
            Save([.. _roots.Select(r => r.VolumeId is null && NativeFile.GetVolumeIdentity(r.Path) is { } id ? r with { VolumeId = id } : r)]);
    }

    private void Save(List<SharedRoot> roots)
    {
        File.WriteAllText(_configPath + ".tmp", JsonSerializer.Serialize(new Config(roots, Gateway, WebOrigin, MaxUploadKBps, MaxConcurrentTransfers, SendToShortcuts, ThumbnailsEnabled, PcName), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_configPath + ".tmp", _configPath, overwrite: true);
        _roots = roots;
    }

    /// <summary>Only ever called from the local command line / owner console — never from a remote instruction.</summary>
    public SharedRoot AddRoot(string name, string path, bool showHidden = false)
    {
        // A relative path would be resolved against whatever folder the agent happens to run in; the owner must name the folder in full.
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Enter the full path of the folder, for example D:\\Shared\\Photos.");
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Folder not found: {full}");
        if (name.Length is 0 or > 64) throw new ArgumentException("Root name must be 1-64 characters.");
        var root = new SharedRoot(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(9)), name, full, showHidden, NativeFile.GetVolumeIdentity(full));
        lock (_mutate) Save([.. _roots, root]);
        return root;
    }

    /// <summary>Validates and stores the relay address. HTTPS is mandatory except for a loopback development relay.</summary>
    public static Uri ValidateGateway(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/" || uri.UserInfo.Length > 0)
            throw new ArgumentException("Enter the relay address, for example https://relay.example.com");
        var loopback = uri.IsLoopback || (System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && loopback))
            throw new ArgumentException("The relay must use HTTPS. Plain HTTP is accepted only for a loopback development relay.");
        return uri;
    }

    public void SetGateway(string gateway, string? webOrigin)
    {
        Gateway = ValidateGateway(gateway).GetLeftPart(UriPartial.Authority);
        WebOrigin = webOrigin is null ? null : new Uri(webOrigin).GetLeftPart(UriPartial.Authority);
        Save([.. _roots]);
    }

    private static string SameAddress(string address) => address.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// After an update: moves the relay and/or web address to the new shipped default ONLY when the stored one is exactly an address an earlier release shipped
    /// (listed in the release's "supersedes"). An address the owner typed that is not on that list is never changed. Returns whether anything changed.
    /// </summary>
    public bool MigrateSupersededAddresses(ReleaseDefaults.Addresses defaults)
    {
        if (defaults.Superseded is not { Count: > 0 } old) return false;
        var earlier = old.Select(SameAddress).ToHashSet();
        var changed = false;
        if (Gateway is not null && earlier.Contains(SameAddress(Gateway)) && SameAddress(defaults.Gateway) != SameAddress(Gateway))
        {
            Gateway = ValidateGateway(defaults.Gateway).GetLeftPart(UriPartial.Authority);
            changed = true;
        }
        if (WebOrigin is not null && defaults.WebOrigin is not null && earlier.Contains(SameAddress(WebOrigin)) && SameAddress(defaults.WebOrigin) != SameAddress(WebOrigin))
        {
            WebOrigin = new Uri(defaults.WebOrigin).GetLeftPart(UriPartial.Authority);
            changed = true;
        }
        if (changed) Save([.. _roots]);
        return changed;
    }

    /// <summary>Owner-only transfer limits. Out-of-range values are refused rather than silently adjusted.</summary>
    public void SetLimits(int maxUploadKBps, int maxConcurrentTransfers)
    {
        if (maxUploadKBps is < 0 or > 10_000_000) throw new ArgumentException("The speed limit must be 0 (no limit) to 10,000,000 KB/s.");
        if (maxConcurrentTransfers is < 1 or > 16) throw new ArgumentException("Send between 1 and 16 files at the same time.");
        MaxUploadKBps = maxUploadKBps; MaxConcurrentTransfers = maxConcurrentTransfers;
        Save([.. _roots]);
    }

    public void SetSendToShortcuts(bool enabled) { SendToShortcuts = enabled; Save([.. _roots]); }
    public const int MaxPcNameLength = 60;

    /// <summary>Sets the PC's display name. Empty goes back to the Windows computer name. Control characters are refused; the name is only ever text.</summary>
    public string? SetPcName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length > MaxPcNameLength) throw new ArgumentException($"The name can be at most {MaxPcNameLength} characters.");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException("The name can't contain control characters.");
        PcName = trimmed.Length == 0 ? null : trimmed;
        Save([.. _roots]);
        return PcName;
    }

    public void SetThumbnailsEnabled(bool enabled) { ThumbnailsEnabled = enabled; Thumbnails.Enabled = enabled; if (!enabled) Thumbnails.Cache.Clear(); Save([.. _roots]); }

    /// <summary>Owner-only: whether hidden files and folders inside this shared folder are visible to paired devices.</summary>
    public bool SetShowHidden(string rootId, bool showHidden)
    {
        if (_roots.All(r => r.RootId != rootId)) return false;
        lock (_mutate) Save([.. _roots.Select(r => r.RootId == rootId ? r with { ShowHidden = showHidden } : r)]);
        return true;
    }

    /// <summary>
    /// Owner-only: let paired browsers upload into this shared folder, within a total allowance and a per-file limit (0 = the default
    /// per-file limit / no total allowance). Turning it off discards every upload still in progress. Never reachable from a remote request.
    /// </summary>
    public bool SetUploads(string rootId, bool enabled, long quotaBytes = 0, long maxFileBytes = 0, bool resetUsed = false)
    {
        if (quotaBytes is < 0 or > (1L << 50) || maxFileBytes is < 0 or > (1L << 50)) throw new ArgumentException("The limits must be between 0 and 1 PiB.");
        lock (_mutate)
        {
            if (_roots.All(r => r.RootId != rootId)) return false;
            Save([.. _roots.Select(r => r.RootId != rootId ? r : r with
            {
                UploadsEnabled = enabled,
                UploadQuotaBytes = enabled ? quotaBytes : r.UploadQuotaBytes,
                UploadMaxFileBytes = enabled ? maxFileBytes : r.UploadMaxFileBytes,
                UploadUsedBytes = resetUsed ? 0 : r.UploadUsedBytes,
            })]);
        }
        if (!enabled) Uploads.DropForRoot(rootId);
        return true;
    }

    private void AddUploadUsed(string rootId, long bytes)
    {
        lock (_mutate) Save([.. _roots.Select(r => r.RootId == rootId ? r with { UploadUsedBytes = r.UploadUsedBytes + bytes } : r)]);
    }

    public bool RemoveRoot(string rootId)
    {
        List<SharedRoot> remaining;
        lock (_mutate)
        {
            remaining = _roots.Where(r => r.RootId != rootId).ToList();
            if (remaining.Count == _roots.Count) return false;
            Save(remaining);
        }
        Uploads.DropForRoot(rootId);
        Thumbnails.Cache.Clear();      // derived content never outlives a permission change
        Archives.DropForRoot(rootId);  // nor do archives made from the folder, or searches still reading it
        Search.CancelAll();
        return true;
    }
}
