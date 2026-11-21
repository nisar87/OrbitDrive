using System.Windows;
using System.Windows.Controls;

namespace OrbitDrive.Desktop.Controls;

/// <summary>How a status reads at a glance. Always shown with a glyph and a word as well as a colour.</summary>
public enum StatusKind { Neutral, Good, Warning, Critical }

/// <summary>A compact status: glyph + word on a tinted pill (for example "Online", "Read-only", "Needs attention").</summary>
public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusPill), new PropertyMetadata(StatusKind.Neutral));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(""));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatusPill), new PropertyMetadata(""));

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>A Segoe Fluent Icons code point.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public StatusPill() => InitializeComponent();
}
