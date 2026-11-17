using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// Optional "start when I sign in": a per-user Run entry (HKCU, no elevation, nothing machine-wide) that launches the
/// console hidden in the notification area. Off unless the owner turns it on; turning it off removes the entry.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsDefault(string stateDirectory) => string.Equals(stateDirectory, Program.DefaultStateDirectory, StringComparison.OrdinalIgnoreCase);
    private static string ValueName(string stateDirectory) => IsDefault(stateDirectory) ? "OrbitDrive" : "OrbitDrive-" + LocalIpc.PipeName(stateDirectory);
    private static string Command(string stateDirectory) =>
        $"\"{Environment.ProcessPath}\" --background" + (IsDefault(stateDirectory) ? "" : $" --state \"{stateDirectory}\"");

    /// <summary>True only when the entry exists and points at this installation (a stale entry from a moved copy reads as off).</summary>
    public static bool IsEnabled(string stateDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName(stateDirectory)) is string value && string.Equals(value, Command(stateDirectory), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(string stateDirectory, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName(stateDirectory), Command(stateDirectory), RegistryValueKind.String);
        else key.DeleteValue(ValueName(stateDirectory), throwOnMissingValue: false);
    }
}

/// <summary>Console-side preferences kept next to the agent state (no secrets).</summary>
internal sealed class ConsoleSettings
{
    private readonly string _file;

    public bool PauseWhenLocked { get; private set; }

    public ConsoleSettings(string stateDirectory)
    {
        _file = Path.Join(stateDirectory, "console.json");
        try
        {
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject saved)
                PauseWhenLocked = saved["pauseWhenLocked"]?.GetValue<bool>() ?? false;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { /* unreadable: defaults */ }
    }

    public void SetPauseWhenLocked(bool value)
    {
        PauseWhenLocked = value;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, new JsonObject { ["pauseWhenLocked"] = value }.ToJsonString());
    }
}
