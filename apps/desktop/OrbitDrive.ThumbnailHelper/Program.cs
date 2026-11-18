using SkiaSharp;

// Protocol: image bytes on standard input; a WebP thumbnail on standard output; exit code 0. Anything else is a failure and
// prints nothing. The helper opens no files, no sockets and starts no processes; the parent also confines it (job object on
// Windows, timeout and output cap everywhere).
const long MaxInput = 64L * 1024 * 1024;
const long MaxPixels = 64_000_000;

try
{
    var edge = 256;
    var maxBytes = 512 * 1024;
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (args[i] == "--edge") edge = Math.Clamp(int.Parse(args[i + 1]), 16, 1024);
        else if (args[i] == "--max-bytes") maxBytes = Math.Clamp(int.Parse(args[i + 1]), 1024, 4 * 1024 * 1024);
    }

    using var input = new MemoryStream();
    using (var stdin = Console.OpenStandardInput())
    {
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stdin.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (input.Length + read > MaxInput) return 3;
            input.Write(buffer, 0, read);
        }
    }
    input.Position = 0;

    using var codec = SKCodec.Create(input);
    if (codec is null) return 4;
    var info = codec.Info;
    if (info.Width <= 0 || info.Height <= 0 || info.Width > 30_000 || info.Height > 30_000 || (long)info.Width * info.Height > MaxPixels) return 5;

    // Ask the decoder for a reduced size when it can (JPEG decodes at 1/2, 1/4, 1/8 cheaply), so a big photo never needs full memory.
    var scale = Math.Min(1f, 2f * edge / Math.Max(info.Width, info.Height));
    var target = codec.GetScaledDimensions(scale);
    var decodeInfo = new SKImageInfo(Math.Max(1, target.Width), Math.Max(1, target.Height), SKColorType.Rgba8888, SKAlphaType.Premul);
    using var bitmap = new SKBitmap(decodeInfo);
    var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return 6;   // a truncated file is shown as far as it decoded

    var turned = codec.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftTop;
    var width = turned ? bitmap.Height : bitmap.Width;
    var height = turned ? bitmap.Width : bitmap.Height;
    var ratio = Math.Min(1.0, (double)edge / Math.Max(width, height));
    var outW = Math.Max(1, (int)Math.Round(width * ratio));
    var outH = Math.Max(1, (int)Math.Round(height * ratio));

    using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale((float)ratio);
    switch (codec.EncodedOrigin)
    {
        case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
        case SKEncodedOrigin.RightTop: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
        case SKEncodedOrigin.LeftBottom: canvas.Translate(0, height); canvas.RotateDegrees(270); break;
    }
    using var image = SKImage.FromBitmap(bitmap);
    canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

    using var snapshot = surface.Snapshot();
    using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, 78);
    if (encoded is null || encoded.Size == 0 || encoded.Size > maxBytes) return 7;
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(encoded.Span);
    stdout.Flush();
    return 0;
}
catch (Exception)
{
    return 1;     // never describe the failure: the bytes came from a hostile file
}
