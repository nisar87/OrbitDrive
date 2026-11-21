using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace OrbitDrive.Desktop.Views;

/// <summary>Home. All behaviour is in <see cref="ViewModels.HomePageViewModel"/>; this is only the view.</summary>
public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();
}

/// <summary>Shows an element while a flag is false (for example the step number until the step is done).</summary>
public sealed class InvertedBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
