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
    private sealed record Config(List<SharedRoot> Roots, string? Gateway = null, string? WebOrigin = null);
    private readonly string _configPath;
    private volatile IReadOnlyList<SharedRoot> _roots = [];

    public ECDsa Identity { get; }
    public string PcId { get; }
    public string PublicKeySpki { get; }
    public SealedTokens Tokens { get; }
    public IReadOnlyList<SharedRoot> Roots => _roots;
    public string? Gateway { get; private set; }
    public string? WebOrigin { get; private set; }
    public string Directory_ { get; }

    public AgentState(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory_ = Path.GetFullPath(directory);
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
        ReloadRoots();
    }

    public void ReloadRoots()
    {
        if (!File.Exists(_configPath)) { _roots = []; return; }
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath));
        _roots = config?.Roots ?? [];
        Gateway = config?.Gateway;
        WebOrigin = config?.WebOrigin;
    }

    private void Save(List<SharedRoot> roots)
    {
        File.WriteAllText(_configPath + ".tmp", JsonSerializer.Serialize(new Config(roots, Gateway, WebOrigin), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_configPath + ".tmp", _configPath, overwrite: true);
        _roots = roots;
    }

    /// <summary>Only ever called from the local command line / owner console — never from a remote instruction.</summary>
    public SharedRoot AddRoot(string name, string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Folder not found: {full}");
        if (name.Length is 0 or > 64) throw new ArgumentException("Root name must be 1-64 characters.");
        var root = new SharedRoot(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(9)), name, full);
        Save([.. _roots, root]);
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

    public bool RemoveRoot(string rootId)
    {
        var remaining = _roots.Where(r => r.RootId != rootId).ToList();
        if (remaining.Count == _roots.Count) return false;
        Save(remaining);
        return true;
    }
}
