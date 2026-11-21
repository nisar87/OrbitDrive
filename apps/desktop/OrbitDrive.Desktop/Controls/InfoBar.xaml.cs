using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OrbitDrive.Desktop.Controls;

public enum InfoSeverity { Info, Success, Warning, Error }

/// <summary>A message with an optional single action, for things the owner should notice (an update, a paused share, a problem). Announced politely to screen readers.</summary>
public partial class InfoBar : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(InfoSeverity), typeof(InfoBar), new PropertyMetadata(InfoSeverity.Info, OnChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(InfoBar), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(InfoBar));
    public static readonly DependencyProperty ActionAutomationIdProperty = DependencyProperty.Register(nameof(ActionAutomationId), typeof(string), typeof(InfoBar), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(InfoBar), new PropertyMetadata(""));

    public InfoSeverity Severity { get => (InfoSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public string ActionAutomationId { get => (string)GetValue(ActionAutomationIdProperty); set => SetValue(ActionAutomationIdProperty, value); }
    /// <summary>A Segoe Fluent Icons code point; defaults follow the severity.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public InfoBar()
    {
        InitializeComponent();
        UpdateParts();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((InfoBar)d).UpdateParts();

    // View concern only: hide the title or the action button when there is none, and pick the default glyph for the severity.
    private void UpdateParts()
    {
        if (TitleText is null || ActionButton is null) return;
        TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        Glyph = Severity switch { InfoSeverity.Success => "", InfoSeverity.Warning => "", InfoSeverity.Error => "", _ => "" };
        System.Windows.Automation.AutomationProperties.SetName(this, string.IsNullOrEmpty(Title) ? Message : string.IsNullOrEmpty(Message) ? Title : Title + ". " + Message);
    }
}
