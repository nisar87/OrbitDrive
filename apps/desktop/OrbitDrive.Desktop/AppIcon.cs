using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitDrive.Desktop;

/// <summary>The OrbitDrive mark, drawn in code (a planet with an orbiting dot) for the window, sidebar and tray.</summary>
internal static class AppIcon
{
    public static ImageSource Image { get; } = Render(64);

    public static System.Drawing.Icon Tray { get; } = CreateTrayIcon();

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var fill = new LinearGradientBrush(Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x7C, 0x3A, 0xED), 45);
            context.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            var ring = new Pen(Brushes.White, size * 0.06);
            context.DrawEllipse(null, ring, new Point(size / 2.0, size / 2.0), size * 0.30, size * 0.30);
            context.DrawEllipse(Brushes.White, null, new Point(size / 2.0, size / 2.0), size * 0.12, size * 0.12);
            context.DrawEllipse(Brushes.White, null, new Point(size * 0.74, size * 0.30), size * 0.075, size * 0.075);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        var source = Render(32);
        var pixels = new byte[32 * 32 * 4];
        source.CopyPixels(pixels, 32 * 4, 0);
        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, 32, 32), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());   // lives for the whole process
    }
}
