using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrbitDrive.Desktop.Views;

/// <summary>
/// Lets the mouse wheel keep scrolling the page while the pointer is over a list. A list inside a scrolling page otherwise swallows the wheel (even when the
/// list has nothing to scroll), so the rest of the page could not be reached with the wheel. A list that does have its own scrollbar keeps the wheel until it
/// reaches its top or bottom, then hands it on. Set <c>local:ScrollForwarding.Enabled="True"</c> on the list.
/// </summary>
public static class ScrollForwarding
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollForwarding), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject list) return;
        var inner = FindScrollViewer(list);
        if (inner is not null && inner.ScrollableHeight > 0)
        {
            var atTop = inner.VerticalOffset <= 0 && e.Delta > 0;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight && e.Delta < 0;
            if (!atTop && !atBottom) return;   // the list still has room to scroll in that direction
        }
        if (VisualTreeHelper.GetParent(list) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found) return found;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }
}
