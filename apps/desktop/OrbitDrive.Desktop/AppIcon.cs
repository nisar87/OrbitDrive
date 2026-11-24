using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitDrive.Desktop;

/// <summary>The MyOrbitDrive mark ("Folder Orbit": a folder with an orbit ring passing behind and in front), drawn in code for the window, sidebar and tray. Keep in step with docs/branding/tools/logos.py (mark_F).</summary>
internal static class AppIcon
{
    public static ImageSource Image { get; } = Render(64);

    public static System.Drawing.Icon Tray { get; } = CreateTrayIcon();

    private const string FolderPath = "M20.5 24.6a3.2 3.2 0 0 1 3.2-3.2h6.6l3.6 4H40.3a3.2 3.2 0 0 1 3.2 3.2V39a3.2 3.2 0 0 1-3.2 3.2H23.7a3.2 3.2 0 0 1-3.2-3.2Z";

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static BitmapSource Render(int size)
    {
        var navy = Frozen(0x14, 0x2C, 0x43);
        var pearl = Frozen(0xF7, 0xF8, 0xF5);
        var mint = Frozen(0xA4, 0xE2, 0xCE);
        var ring = new Pen(pearl, 5.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        ring.Freeze();
        var back = Geometry.Parse("M7 32A25 13 0 0 1 57 32");
        var front = Geometry.Parse("M7 32A25 13 0 0 0 57 32");
        var folder = Geometry.Parse(FolderPath);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
            context.DrawRoundedRectangle(navy, null, new Rect(0, 0, 64, 64), 14, 14);
            context.PushTransform(new RotateTransform(-28, 32, 32));
            context.DrawGeometry(null, ring, back);
            context.Pop();
            context.DrawGeometry(mint, null, folder);
            context.PushTransform(new RotateTransform(-28, 32, 32));
            context.DrawGeometry(null, ring, front);
            context.Pop();
            context.Pop();
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
