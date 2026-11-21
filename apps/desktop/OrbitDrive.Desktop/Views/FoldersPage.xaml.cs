using System.Windows;
using System.Windows.Controls;
using OrbitDrive.Desktop.ViewModels;

namespace OrbitDrive.Desktop.Views;

/// <summary>Shared folders. Behaviour is in <see cref="FoldersPageViewModel"/>; this only accepts folders dropped from Explorer.</summary>
public partial class FoldersPage : UserControl
{
    public FoldersPage()
    {
        InitializeComponent();
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += OnDrop;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not FoldersPageViewModel page || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths.Where(System.IO.Directory.Exists)) await page.ShareDroppedAsync(path);   // files are ignored: only folders can be shared
    }
}
