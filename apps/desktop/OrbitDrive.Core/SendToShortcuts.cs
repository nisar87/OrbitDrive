using System.Runtime.Versioning;

namespace OrbitDrive.Core;

/// <summary>
/// The classic Explorer "Send to" fallback: one shortcut per paired receiver in the user's SendTo folder. Explorer appends the selected
/// file paths to the shortcut's arguments; the target is OrbitDrive's own command, which hands them to the running agent as data.
/// This is the legacy menu ("Show more options" > Send to), NOT the Windows 11 modern menu, and it is evidence for nothing more.
/// </summary>
public static class SendToShortcuts
{
    private const string InvalidChars = "\\/:*?\"<>|";   // Windows rules on every platform, so the result is the same wherever it is computed
    public const string Prefix = "OrbitDrive - ";

    public static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    /// <summary>A file-system-safe, readable shortcut name for a receiver name the owner (or a stranger's browser) chose.</summary>
    public static string SafeName(string receiverName)
    {
        var cleaned = new string([.. receiverName.Where(c => c >= ' ' && !InvalidChars.Contains(c))]).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "Phone";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    public sealed record Receiver(string Id, string Name);

    /// <summary>Makes the folder hold exactly one shortcut per receiver (and removes ours for receivers that are gone). Returns the paths.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Sync(string folder, IEnumerable<Receiver> receivers, string executable, string leadingArguments)
    {
        Directory.CreateDirectory(folder);
        var wanted = new Dictionary<string, Receiver>(StringComparer.OrdinalIgnoreCase);
        foreach (var receiver in receivers)
        {
            if (!receiver.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;      // ids are opaque tokens; anything else is not ours
            var path = Path.Join(folder, $"{Prefix}{SafeName(receiver.Name)}.lnk");
            var n = 2;
            while (wanted.ContainsKey(path)) path = Path.Join(folder, $"{Prefix}{SafeName(receiver.Name)} ({n++}).lnk");
            wanted[path] = receiver;
        }
        foreach (var existing in Directory.EnumerateFiles(folder, Prefix + "*.lnk"))
            if (!wanted.ContainsKey(existing)) File.Delete(existing);
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("Shortcuts need Windows.")) ?? throw new PlatformNotSupportedException();
        try
        {
            foreach (var (path, receiver) in wanted)
            {
                dynamic link = ((dynamic)shell).CreateShortcut(path);
                try
                {
                    link.TargetPath = executable;
                    link.Arguments = $"{leadingArguments} send_files {receiver.Id}".Trim();
                    link.WindowStyle = 7;                                       // minimised: the command has no window of its own
                    link.Description = $"Send the selected files to {receiver.Name} through OrbitDrive";
                    link.IconLocation = executable + ",0";
                    link.Save();
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        return [.. wanted.Keys];
    }

    public static void RemoveAll(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var existing in Directory.EnumerateFiles(folder, Prefix + "*.lnk")) File.Delete(existing);
    }
}
