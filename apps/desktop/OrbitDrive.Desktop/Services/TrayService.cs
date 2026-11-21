namespace OrbitDrive.Desktop;

/// <summary>The notification-area icon and its menu. Windows Forms is referenced only for this; its types stay out of the rest of the app.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon = new();
    private readonly System.Windows.Forms.ContextMenuStrip _menu = new();

    public TrayService(Action open, Action togglePause, Action quit)
    {
        _icon.Icon = AppIcon.Tray;
        _icon.Text = "OrbitDrive";
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => open();
        _menu.Items.Add("Open OrbitDrive", null, (_, _) => open());
        _menu.Items.Add("Pause / resume remote access", null, (_, _) => togglePause());
        _menu.Items.Add("Quit OrbitDrive (stops remote access)", null, (_, _) => quit());
        _icon.ContextMenuStrip = _menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
