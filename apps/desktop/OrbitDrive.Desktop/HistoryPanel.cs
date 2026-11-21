using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OrbitDrive.Core;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// The History page's stored history: what OrbitDrive did on this PC (connections, pairings, files sent and received, problems), newest first,
/// kept by the agent across restarts. The agent only ever records fixed event kinds, counts and sizes (never file names, folders or people),
/// so this page cannot show them either. The owner can copy it as text or clear it; nothing leaves the PC.
/// </summary>
internal sealed class HistoryPanel
{
    private const int Shown = 200;
    private readonly ListBox _list = new() { MinHeight = 200, MaxHeight = 420 };
    private readonly TextBlock _empty = LegacyConsole.MakeText("HistoryEmpty", "Nothing has been recorded yet.", 13, FontWeights.Normal, LegacyConsole.Muted);
    private readonly TextBlock _error = LegacyConsole.MakeText("HistoryError", "", 13, FontWeights.Normal, LegacyConsole.Danger);
    private readonly Func<TextBlock, Func<LocalIpcClient, Task>, Task> _run;
    private IReadOnlyList<ActivityEntry> _entries = [];
    private string _shown = "";

    public Border Card { get; }

    public HistoryPanel(Func<TextBlock, Func<LocalIpcClient, Task>, Task> run)
    {
        _run = run;
        AutomationProperties.SetAutomationId(_list, "HistoryList");
        AutomationProperties.SetName(_list, "Activity history");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var copy = LegacyConsole.MakeButton("CopyHistoryButton", "Copy as text");
        var clear = LegacyConsole.MakeButton("ClearHistoryButton", "Clear history");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(ActivityPresenter.ToText(_entries)); _error.Text = ""; }
            catch (System.Runtime.InteropServices.ExternalException) { _error.Text = "Windows would not let OrbitDrive use the clipboard just now."; }
        };
        clear.Click += (_, _) =>
        {
            var ok = MessageBox.Show("Delete the saved history on this PC? This cannot be undone. Your shared folders, paired devices and settings are not affected.",
                "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok == MessageBoxResult.OK) _ = _run(_error, c => c.SendAsync("clear_activity"));
        };
        Card = LegacyConsole.Card("History on this PC", _empty, _list, LegacyConsole.Row(copy, clear), _error,
            LegacyConsole.MakeText("HistoryHint", "Only what happened is kept: times, kinds of events, counts and sizes. File names, folder names and people are never saved. The newest 200 are shown.", 13, FontWeights.Normal, LegacyConsole.Muted));
        Show([]);
    }

    /// <summary>Called with a live connection whenever the History page is on screen. Redraws only when something changed.</summary>
    public async Task RefreshAsync(LocalIpcClient client)
    {
        try
        {
            if (await client.SendAsync("activity", new JsonObject { ["count"] = Shown }) is not JsonObject reply || reply["entries"] is not JsonArray array) return;
            var entries = new List<ActivityEntry>();
            foreach (var node in array)
            {
                if (node is not JsonObject o || !DateTimeOffset.TryParse(o["time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                long? bytes = long.TryParse(o["bytes"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
                entries.Add(new ActivityEntry(time, o["kind"]?.ToString() ?? "", o["detail"]?.ToString() ?? "", bytes));
            }
            var signature = array.ToJsonString();
            if (signature == _shown) return;
            _shown = signature;
            Show(entries);
        }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
    }

    private void Show(IReadOnlyList<ActivityEntry> entries)
    {
        _entries = entries;
        _list.Items.Clear();
        foreach (var entry in entries)
        {
            var row = ActivityPresenter.Present(entry);
            var title = new TextBlock { Text = row.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var when = new TextBlock { Text = row.Detail.Length == 0 ? row.When : $"{row.When} · {row.Detail}", FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            if (row.IsProblem) title.SetResourceReference(TextBlock.ForegroundProperty, LegacyConsole.Danger);
            LegacyConsole.Mute(when);
            var lines = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            lines.Children.Add(title); lines.Children.Add(when);
            var item = new ListBoxItem { Content = lines };
            AutomationProperties.SetName(item, $"{row.When}  {row.Title}{(row.Detail.Length == 0 ? "" : ", " + row.Detail)}{(row.IsProblem ? ", a problem" : "")}");
            _list.Items.Add(item);
        }
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
