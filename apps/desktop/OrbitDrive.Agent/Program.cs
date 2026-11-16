using System.Net;
using System.Text.Json;
using OrbitDrive.Agent;
using OrbitDrive.Protocol;

// OrbitDrive agent (per-user, never elevated). Headless host for the worker until the WPF owner console (D1) drives it.
// Machine-readable events are written to stdout as lines starting with "@@ ".
static void Emit(string name, object? data)
{
    var line = JsonSerializer.Serialize(new { @event = name, data });
    lock (Console.Out) { Console.Out.WriteLine("@@ " + line); Console.Out.Flush(); }
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Usage()
{
    Console.Error.WriteLine("""
        orbitdrive-agent run --gateway <https-url> [--state <dir>] [--name <pc-name>] [--web-origin <https-url>]
        orbitdrive-agent roots add <name> <folder> [--state <dir>]
        orbitdrive-agent roots list [--state <dir>]
        orbitdrive-agent roots remove <rootId> [--state <dir>]

        While running, type: pair | approve | deny | pause | resume | reload | quit
        """);
    return 2;
}

if (args.Length == 0) return Usage();
var stateDirectory = Option(args, "--state") ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitDrive", "agent");
var state = new AgentState(stateDirectory);

if (args[0] == "roots" && args.Length >= 2)
{
    switch (args[1])
    {
        case "add" when args.Length >= 4:
            var added = state.AddRoot(args[2], args[3]);
            Console.WriteLine($"Shared read-only: {added.Name} ({added.RootId}) -> {added.Path}");
            return 0;
        case "list":
            foreach (var root in state.Roots) Console.WriteLine($"{root.RootId}\t{root.Name}\t{root.Path}");
            return 0;
        case "remove" when args.Length >= 3:
            return state.RemoveRoot(args[2]) ? 0 : 1;
        default: return Usage();
    }
}
if (args[0] != "run") return Usage();

if (!Uri.TryCreate(Option(args, "--gateway"), UriKind.Absolute, out var gateway)) return Usage();
var loopback = gateway.IsLoopback || (IPAddress.TryParse(gateway.Host, out var address) && IPAddress.IsLoopback(address));
if (gateway.Scheme != "https" && !(gateway.Scheme == "http" && loopback))
{
    Console.Error.WriteLine("The gateway must use HTTPS. Plain HTTP is accepted only for a loopback development gateway.");
    return 2;
}
var webOrigin = Option(args, "--web-origin") ?? gateway.GetLeftPart(UriPartial.Authority);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
using var client = new ControlClient(gateway, state, Option(args, "--name") ?? Environment.MachineName, Emit);
var running = client.RunAsync(shutdown.Token);

// Local owner commands. Approval can only come from this local console, never from the network.
while (!shutdown.IsCancellationRequested && await Console.In.ReadLineAsync(shutdown.Token).AsTask().ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null) is { } line)
{
    try
    {
        switch (line.Trim().ToLowerInvariant())
        {
            case "pair":
                var pairing = await client.CreatePairingAsync(shutdown.Token);
                // The secret travels in the URL fragment so it is never sent to a web server, proxy log or Referer header.
                Emit("pairing", new { url = $"{webOrigin}/pair#id={pairing.PairingId}&s={pairing.Secret}", code = $"{pairing.PairingId}-{pairing.Secret}", expiresAtUnixMs = pairing.ExpiresAtUnixMs.ToString() });
                break;
            case "approve": Emit("pairing_result", new { state = (await client.ConfirmPairingAsync(true, shutdown.Token)).ToString().ToLowerInvariant() }); break;
            case "deny": Emit("pairing_result", new { state = (await client.ConfirmPairingAsync(false, shutdown.Token)).ToString().ToLowerInvariant() }); break;
            case "pause": client.Paused = true; Emit("paused", null); break;
            case "resume": client.Paused = false; Emit("resumed", null); break;
            case "reload": state.ReloadRoots(); Emit("roots_reloaded", new { count = state.Roots.Count }); break;
            case "quit": shutdown.Cancel(); break;
            case "": break;
            default: Emit("error", new { message = "Unknown command." }); break;
        }
    }
    catch (ProtocolException e) { Emit("error", new { code = e.Code.ToString(), message = e.Message }); }
    catch (TimeoutException) { Emit("error", new { message = "The service did not answer in time." }); }
}
if (!shutdown.IsCancellationRequested) await Task.Delay(Timeout.Infinite, shutdown.Token).ContinueWith(_ => { }); // stdin closed: keep serving until signalled
await running;
return 0;
