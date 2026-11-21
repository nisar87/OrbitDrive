using System.Diagnostics;
using System.IO;
using System.Windows;
using OrbitDrive.Core;
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
        // Design tokens and shared component styles (Components.xaml merges Tokens.xaml). Added after the Fluent theme so both are in place before any window.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/OrbitDrive;component/Theme/Components.xaml", UriKind.Absolute) });
        ThemeChoice = theme switch { "light" => "Light", "dark" => "Dark", _ => "System" };

        var window = new MainWindow(stateDirectory);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
        var registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => window.Dispatcher.InvokeAsync(window.Reopen), null, Timeout.Infinite, false);
        if (!background) window.Show();
        var exit = app.Run();
        registration.Unregister(null);
        return exit;
    }

    /// <summary>What the last <see cref="StartAgentAsync"/> had to do, in plain words, for the console's activity list (empty when nothing unusual).</summary>
    public static string StartNote { get; private set; } = "";

    /// <summary>
    /// Starts the agent as the current user: never elevated, no shell, arguments passed as a list. Called only after the console could not reach
    /// an agent, so an agent that still holds this state folder's lock is either shutting down or no longer answering. A stopping one is given a few
    /// seconds; one that is still there afterwards, and is this very installation's own program, is ended so a fresh one can start. Without that, one
    /// stuck process (for example left over from before an update) made every later start give up silently.
    /// </summary>
    public static async Task<bool> StartAgentAsync(string stateDirectory)
    {
        StartNote = "";
        var exe = Path.Join(AppContext.BaseDirectory, "orbitdrive-agent.exe");
        if (!File.Exists(exe)) return false;
        if (AgentLock.IsHeld(stateDirectory) && !await AgentLock.WaitUntilFreeAsync(stateDirectory, TimeSpan.FromSeconds(5)))
        {
            var ended = EndOwnUnresponsiveAgents(exe);
            StartNote = ended > 0 ? $"An agent from this installation was not answering and was stopped ({ended}); starting a fresh one." : "Another program is holding the agent's lock and could not be identified as OrbitDrive's own agent.";
            if (ended == 0 || !await AgentLock.WaitUntilFreeAsync(stateDirectory, TimeSpan.FromSeconds(5))) return false;
        }
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(stateDirectory);
        using var process = Process.Start(start);
        return process is not null;
    }

    /// <summary>Ends running copies of exactly this installation's agent program, as this user. Never matches by name alone, never touches another program.</summary>
    private static int EndOwnUnresponsiveAgents(string exe)
    {
        var ended = 0;
        foreach (var process in Process.GetProcessesByName("orbitdrive-agent"))
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill();
                if (process.WaitForExit(5000)) ended++;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* not ours to stop, or already gone */ }
            finally { process.Dispose(); }
        }
        return ended;
    }
}
