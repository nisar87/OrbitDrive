using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OrbitDrive.Core;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// Settings > picture previews: an owner switch (on by default, as in the agent) and a button that deletes every stored preview. The agent keeps the
/// previews in a private cache on this PC; nothing here shows names or paths, only how many previews exist and how much space they use.
/// </summary>
internal sealed class ThumbnailPanel
{
    private readonly CheckBox _enabled = new() { Content = "Show small previews of pictures to my paired devices", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _status = MainWindow.MakeText("ThumbnailStatus", "", 13, FontWeights.Normal, MainWindow.Muted);
    private readonly TextBlock _error = MainWindow.MakeText("ThumbnailError", "", 13, FontWeights.Normal, MainWindow.Danger);
    private readonly Button _clear = MainWindow.MakeButton("ClearThumbnailsButton", "Delete stored previews");
    private readonly Func<TextBlock, Func<LocalIpcClient, Task>, Task> _run;
    private bool _applying;
    private string _shown = "";

    public Border Card { get; }

    public ThumbnailPanel(Func<TextBlock, Func<LocalIpcClient, Task>, Task> run)
    {
        _run = run;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_enabled, "ThumbnailsToggle");
        // Checked/Unchecked (not Click) so keyboard, mouse and assistive technology take the same path.
        void Changed(object sender, RoutedEventArgs e)
        {
            if (_applying) return;
            var on = _enabled.IsChecked == true;
            _shown = "";                                         // always redraw from what the agent says it stored
            _ = _run(_error, async c => Apply(await c.SendAsync("set_thumbnails", new JsonObject { ["enabled"] = on }) as JsonObject));
        }
        _enabled.Checked += Changed; _enabled.Unchecked += Changed;
        _clear.Click += (_, _) => _ = _run(_error, async c => Apply(await c.SendAsync("clear_thumbnails") as JsonObject));
        Card = MainWindow.Card("Picture previews", _enabled, _status, _clear, _error);
    }

    /// <summary>Called with a live connection while Settings is on screen.</summary>
    public async Task RefreshAsync(LocalIpcClient client)
    {
        try { Apply(await client.SendAsync("thumbnails") as JsonObject); }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
    }

    private void Apply(JsonObject? status)
    {
        if (status is null) return;
        var enabled = status["enabled"]?.GetValue<bool>() ?? false;
        // "available" in the agent means enabled AND a helper exists, so it is false while the owner has switched previews off. The switch must stay usable
        // then, so what matters here is only whether the helper that makes previews exists on this PC.
        var available = status["hasHelper"]?.GetValue<bool>() ?? false;
        var files = status["files"]?.GetValue<long>() ?? 0;
        var bytes = status["bytes"]?.GetValue<long>() ?? 0;
        var signature = $"{enabled}|{available}|{files}|{bytes}";
        if (signature == _shown) return;
        _shown = signature;
        _applying = true;
        try
        {
            _enabled.IsChecked = enabled;
            _enabled.IsEnabled = available;
            _clear.IsEnabled = available && files > 0;
            _status.Text = ThumbnailSummary.Describe(enabled, available, files, bytes);
        }
        finally { _applying = false; }
    }
}
