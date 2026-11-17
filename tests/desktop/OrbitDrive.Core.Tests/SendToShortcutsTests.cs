using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

/// <summary>The classic Explorer Send to fallback. Shortcut creation needs Windows (WScript.Shell); the naming rules run everywhere.</summary>
public sealed class SendToShortcutsTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "orbit-sendto-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    [Theory]
    [InlineData("Pixel 8", "Pixel 8")]
    [InlineData("Ali's \"phone\" <home>", "Ali's phone home")]
    [InlineData("a/b\\c:d*e?f|g", "abcdefg")]
    [InlineData("   ", "Phone")]
    [InlineData("name.", "name")]
    public void Receiver_names_become_safe_file_names(string name, string expected) => Assert.Equal(expected, SendToShortcuts.SafeName(name));

    [Fact]
    public void Very_long_names_are_shortened() => Assert.Equal(60, SendToShortcuts.SafeName(new string('x', 500)).Length);

    [Fact]
    public void Shortcuts_point_at_the_command_with_the_receiver_and_stale_ones_are_removed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Join(Environment.SystemDirectory, "cmd.exe");
        var made = SendToShortcuts.Sync(_folder, [new("abc-123", "Phone"), new("def_456", "Phone"), new("bad id; calc", "Evil")], exe, "ctl");
        Assert.Equal(2, made.Count);                                           // the id that is not an opaque token is not ours
        Assert.Equal(2, Directory.GetFiles(_folder, "OrbitDrive - *.lnk").Length);
        File.WriteAllText(Path.Join(_folder, "Somebody else's shortcut.lnk"), "not ours");

        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var args = new List<string>();
        foreach (var path in made)
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            Assert.Equal(exe, (string)link.TargetPath);
            args.Add((string)link.Arguments);
        }
        Assert.Contains("ctl send_files abc-123", args);
        Assert.Contains("ctl send_files def_456", args);

        SendToShortcuts.Sync(_folder, [new("abc-123", "Phone")], exe, "ctl");     // one receiver left
        Assert.Single(Directory.GetFiles(_folder, "OrbitDrive - *.lnk"));
        Assert.True(File.Exists(Path.Join(_folder, "Somebody else's shortcut.lnk")));   // never touches what is not ours
        SendToShortcuts.RemoveAll(_folder);
        Assert.Empty(Directory.GetFiles(_folder, "OrbitDrive - *.lnk"));
        Assert.True(File.Exists(Path.Join(_folder, "Somebody else's shortcut.lnk")));
    }
}
