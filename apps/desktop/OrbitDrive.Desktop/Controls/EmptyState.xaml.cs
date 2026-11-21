using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OrbitDrive.Desktop.Controls;

/// <summary>An empty list's explanation and its single next step.</summary>
public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(EmptyState));
    public static readonly DependencyProperty ActionAutomationIdProperty = DependencyProperty.Register(nameof(ActionAutomationId), typeof(string), typeof(EmptyState), new PropertyMetadata(""));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public string ActionAutomationId { get => (string)GetValue(ActionAutomationIdProperty); set => SetValue(ActionAutomationIdProperty, value); }

    public EmptyState()
    {
        InitializeComponent();
        UpdateParts();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EmptyState)d).UpdateParts();

    private void UpdateParts()
    {
        if (ActionButton is null) return;
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
