using System.Diagnostics;
using System.IO;
using System.Windows;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>Owner console entry point: one console per Windows session, attached to the per-user agent.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var stateIndex = Array.IndexOf(args, "--state");
        var stateDirectory = stateIndex >= 0 && stateIndex + 1 < args.Length
            ? Path.GetFullPath(args[stateIndex + 1])
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitDrive", "agent");

        using var single = new Mutex(true, "Local\\OrbitDrive.Console." + LocalIpc.PipeName(stateDirectory), out var first);
        if (!first) return 3; // a console for this user and state is already open

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(stateDirectory);
        window.Show();
        return app.Run();
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
