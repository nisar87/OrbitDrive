using System.Text.RegularExpressions;
using OrbitDrive.Core.Ipc;
using Xunit;

namespace OrbitDrive.Core.Tests;

/// <summary>
/// The local pipe refuses any command that is not in <see cref="LocalIpc.Commands"/>. A handler added to the agent without adding its name there
/// is silently unreachable (that is how PC rename and key rotation shipped broken, F-007). This test reads the agent's own switch and compares.
/// </summary>
public sealed class IpcCommandListTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "OrbitDrive.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Join([dir!.FullName, .. parts]);
    }

    private static string[] HandlerCases()
    {
        var source = File.ReadAllText(RepoFile("apps", "desktop", "OrbitDrive.Agent", "AgentHost.cs"));
        var start = source.IndexOf("private async Task<JsonNode?> HandleAsync(string command", StringComparison.Ordinal);
        Assert.True(start > 0, "HandleAsync was not found; update this test with the new name");
        var end = source.IndexOf("default: throw new IpcException(\"Unknown command.\")", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of the command switch was not found");
        return [.. Regex.Matches(source[start..end], "case \"([a-z_]+)\":").Select(m => m.Groups[1].Value).Distinct()];
    }

    [Fact]
    public void Every_command_the_agent_handles_is_allowed_through_the_pipe()
    {
        var cases = HandlerCases();
        Assert.True(cases.Length >= 20, $"only {cases.Length} commands were found in the handler");
        var missing = cases.Where(c => !LocalIpc.Commands.Contains(c)).ToArray();
        Assert.True(missing.Length == 0, "handled but refused by the pipe: " + string.Join(", ", missing));
    }

    [Fact]
    public void Rename_and_key_rotation_are_in_the_list() => Assert.True(LocalIpc.Commands.Contains("set_pc_name") && LocalIpc.Commands.Contains("rotate_identity"));

    [Fact]
    public void Every_allowed_command_has_a_handler()
    {
        var cases = HandlerCases();
        var orphans = LocalIpc.Commands.Where(c => !cases.Contains(c)).ToArray();
        Assert.True(orphans.Length == 0, "allowed but not handled: " + string.Join(", ", orphans));
    }
}
