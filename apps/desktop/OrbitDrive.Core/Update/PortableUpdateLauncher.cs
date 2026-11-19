using System.Diagnostics;
using System.Reflection;

namespace OrbitDrive.Core;

/// <summary>The outcome the apply script left for the next start of the app.</summary>
public sealed record ApplyResult(bool Ok, string Version, string Message);

/// <summary>
/// Applies a verified portable build: starts Windows PowerShell on the embedded script (never one found on disk), as the current user, without a shell
/// string, a window or elevation. The script waits for OrbitDrive to quit, re-checks the zip's hash, swaps the folder and starts the new version.
/// </summary>
public sealed class PortableUpdateLauncher(string installDir, string stateDirectory, string expectedSha256, Version version, string resultFile, bool relaunch = true) : IInstallerLauncher
{
    public static string Script()
    {
        using var stream = typeof(PortableUpdateLauncher).Assembly.GetManifestResourceStream("apply-update.ps1") ?? throw new InvalidOperationException("The update script is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Everything the script is given, as a list (never joined into a command line by us).</summary>
    public IReadOnlyList<string> Arguments(string scriptPath, string zipPath)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath,
            "-InstallDir", installDir, "-Zip", zipPath, "-Sha256", expectedSha256, "-Version", version.ToString(), "-ResultFile", resultFile, "-StateDir", stateDirectory };
        if (relaunch) args.Add("-Relaunch");
        return args;
    }

    public void Launch(string verifiedPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Portable updates are applied on Windows.");
        var folder = Path.Join(Path.GetTempPath(), "orbitdrive-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Join(folder, "apply-update.ps1");
        File.WriteAllText(script, Script(), new System.Text.UTF8Encoding(true));    // BOM so Windows PowerShell 5.1 reads it as UTF-8
        var powershell = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var argument in Arguments(script, verifiedPath)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the updater.");
    }

    public static ApplyResult? ReadResult(string resultFile)
    {
        try
        {
            if (!File.Exists(resultFile)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(resultFile));
            var root = doc.RootElement;
            return new ApplyResult(root.GetProperty("ok").GetBoolean(), root.GetProperty("version").GetString() ?? "", root.GetProperty("message").GetString() ?? "");
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}
