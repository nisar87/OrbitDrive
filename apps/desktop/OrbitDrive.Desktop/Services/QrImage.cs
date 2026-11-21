using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbitDrive.Desktop;

/// <summary>Draws a link as a QR code bitmap with the 4-module quiet zone scanners need.</summary>
internal static class QrImage
{
    public static BitmapSource Render(string text)
    {
        var qr = OrbitDrive.Core.QrCode.Encode(text);
        const int quiet = 4;
        var side = qr.Size + quiet * 2;
        var pixels = new byte[side * side];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                if (qr[x, y]) pixels[(y + quiet) * side + x + quiet] = 0;
        var image = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
        image.Freeze();
        return image;
    }
}
