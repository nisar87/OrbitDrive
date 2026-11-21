using System.Windows;
using System.Windows.Input;

namespace OrbitDrive.Desktop.Views;

/// <summary>The small window that opens from the notification-area icon. It closes when it loses focus or on Escape, and sits above the taskbar corner.</summary>
public partial class TrayFlyout : Window
{
    private bool _closing;

    public TrayFlyout(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Bottom - ActualHeight - 12;
            Activate();
        };
        Deactivated += (_, _) => CloseOnce();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseOnce(); e.Handled = true; } };
    }

    /// <summary>Any button closes the flyout after its command has run.</summary>
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.OriginalSource is DependencyObject source && FindButton(source)) Dispatcher.InvokeAsync(CloseOnce);
    }

    private static bool FindButton(DependencyObject node)
    {
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Button) return true;
        return false;
    }

    private void CloseOnce() { if (_closing) return; _closing = true; Close(); }
}
