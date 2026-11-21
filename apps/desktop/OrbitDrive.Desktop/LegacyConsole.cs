using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrbitDrive.Desktop;

/// <summary>
/// TRANSITIONAL. Only the small code-built UI helpers that the software-update panel still uses. Every page is now XAML + a view model; when the update
/// panel is rebuilt the same way, this file goes too.
/// </summary>
internal static class LegacyConsole
{
    // Secondary text is the primary colour at reduced opacity (the theme's own secondary brush measured about 3.2:1 on cards), full strength in high contrast.
    internal const string Muted = "muted";
    internal static void Mute(TextBlock block) => block.Opacity = SystemParameters.HighContrast ? 1.0 : 0.74;
    internal const string Danger = "SystemFillColorCriticalBrush";

    internal static TextBlock MakeText(string id, string text, double size, FontWeight weight, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (brushKey == Muted) Mute(block);
        else if (brushKey is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    internal static Button MakeButton(string id, string text, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34 };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    /// <summary>A rounded surface that groups related controls, optionally under a heading.</summary>
    internal static Border Card(string? heading, params UIElement[] children)
    {
        var panel = new StackPanel();
        if (heading is not null) panel.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var child in children) panel.Children.Add(child);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    internal static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

}
