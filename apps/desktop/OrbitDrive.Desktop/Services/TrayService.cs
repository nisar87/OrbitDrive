namespace OrbitDrive.Desktop;

/// <summary>The notification-area icon and its menu. Windows Forms is referenced only for this; its types stay out of the rest of the app.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon = new();
    private readonly System.Windows.Forms.ContextMenuStrip _menu = new();
    private readonly Action _open;
    private Action? _balloonClicked;

    public TrayService(Action open, Action togglePause, Action quit, Action showFlyout)
    {
        _open = open;
        _icon.Icon = AppIcon.Tray;
        _icon.Text = "MyOrbitDrive";
        _icon.Visible = true;
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) showFlyout(); };   // right-click keeps the menu
        _menu.Items.Add("Open MyOrbitDrive", null, (_, _) => open());
        _menu.Items.Add("Pause / resume remote access", null, (_, _) => togglePause());
        _menu.Items.Add("Quit MyOrbitDrive (stops remote access)", null, (_, _) => quit());
        _icon.ContextMenuStrip = _menu;
        _icon.BalloonTipClicked += (_, _) => (_balloonClicked ?? _open)();
    }

    /// <summary>The fallback notification when a toast can't be shown. Clicking it runs <paramref name="clicked"/> (or just opens the window).</summary>
    public void ShowBalloon(string title, string body, Action? clicked = null)
    {
        _balloonClicked = clicked;
        _icon.ShowBalloonTip(8000, title, body, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
