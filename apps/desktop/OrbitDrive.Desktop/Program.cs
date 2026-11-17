using System.Diagnostics;
using System.IO;
using System.Windows;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>Owner console entry point: one console per Windows session, attached to the per-user agent.</summary>
public static class Program
{
    public static string DefaultStateDirectory { get; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitDrive", "agent");

    /// <summary>"System", "Light" or "Dark" - what the console was asked to follow (shown in Diagnostics).</summary>
    public static string ThemeChoice { get; private set; } = "System";

    [STAThread]
    public static int Main(string[] args)
    {
        var stateIndex = Array.IndexOf(args, "--state");
        var stateDirectory = stateIndex >= 0 && stateIndex + 1 < args.Length ? Path.GetFullPath(args[stateIndex + 1]) : DefaultStateDirectory;
        var background = args.Contains("--background");   // started by Windows at sign-in: stay in the notification area
        var themeIndex = Array.IndexOf(args, "--theme");
        var theme = themeIndex >= 0 && themeIndex + 1 < args.Length ? args[themeIndex + 1].ToLowerInvariant() : "system";

        var instance = LocalIpc.PipeName(stateDirectory);
        using var single = new Mutex(true, "Local\\OrbitDrive.Console." + instance, out var first);
        var showName = "Local\\OrbitDrive.Show." + instance;
        if (!first)
        {
            // Already running for this user (perhaps hidden in the notification area): ask it to show its window.
            if (!background && EventWaitHandle.TryOpenExisting(showName, out var existing)) { using (existing) existing.Set(); }
            return 3;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // Fluent theme switching is marked experimental in this SDK; it follows Windows light/dark/high-contrast.
        app.ThemeMode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
        ThemeChoice = theme switch { "light" => "Light", "dark" => "Dark", _ => "System" };

        var window = new MainWindow(stateDirectory);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
        var registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => window.Dispatcher.InvokeAsync(window.Reopen), null, Timeout.Infinite, false);
        if (!background) window.Show();
        var exit = app.Run();
        registration.Unregister(null);
        return exit;
    }

    /// <summary>Starts the agent as the current user: never elevated, no shell, arguments passed as a list.</summary>
    public static bool StartAgent(string stateDirectory)
    {
        var exe = Path.Join(AppContext.BaseDirectory, "orbitdrive-agent.exe");
        if (!File.Exists(exe)) return false;
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(stateDirectory);
        using var process = Process.Start(start);
        return process is not null;
    }
}
