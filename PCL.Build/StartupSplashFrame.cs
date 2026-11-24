using System.IO.Compression;
using PCL.Views.Shell;
using SkiaSharp;

namespace PCL.Build;

internal static class StartupSplashFrame
{
    public static void Write(string output)
    {
        const int scale = 2;
        using var bitmap = new SKBitmap(StartupSplashArtwork.Width * scale, StartupSplashArtwork.Height * scale,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var artwork = new StartupSplashArtwork();
        canvas.Scale(scale);
        artwork.Draw(canvas, 0, animate: false);

        using var file = File.Create(output);
        using var compressed = new GZipStream(file, CompressionLevel.SmallestSize);
        compressed.Write(bitmap.GetPixelSpan());
    }
}
