using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Services;

public sealed class ScreenshotService : IScreenshotService
{
    /// <summary>
    /// capture → <see cref="ScaleMath.Fit"/> → <see cref="Downscale"/> (only when the size
    /// changes) → <see cref="Encode"/> (A-9). The GDI buffer stays locked for the whole chain:
    /// the Skia bitmap is a zero-copy view of it, so both the resize and the encode must finish
    /// before <c>UnlockBits</c>.
    /// </summary>
    public Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, CaptureOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var o = options ?? new CaptureOptions();

        int screenW = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        int screenH = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        var r = region ?? new ScreenRegion(0, 0, screenW, screenH);

        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));

        ct.ThrowIfCancellationRequested();

        // Zero-copy: wrap the locked GDI pixel buffer in an SKBitmap via
        // InstallPixels (stride-aware; avoids assumption that Stride == Width*4).
        var bd = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBmp = new SKBitmap();
            if (!skBmp.InstallPixels(info, bd.Scan0, bd.Stride))
                throw new InvalidOperationException("SKBitmap.InstallPixels failed to wrap GDI bitmap memory.");

            ct.ThrowIfCancellationRequested();

            var (width, height, coordinateScale) = ScaleMath.Fit(bmp.Width, bmp.Height, o.MaxWidth, o.MaxHeight, o.Scale);

            byte[] bytes;
            if (width != bmp.Width || height != bmp.Height)
            {
                using var scaled = Downscale(skBmp, width, height);
                bytes = Encode(scaled, o.Format, o.Quality);
            }
            else
            {
                bytes = Encode(skBmp, o.Format, o.Quality);
            }

            return Task.FromResult(new ScreenshotResult(
                bytes, width, height, o.Format, bmp.Width, bmp.Height, coordinateScale));
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    /// <summary>
    /// Resizes <paramref name="src"/> to exactly <paramref name="width"/>x<paramref name="height"/>
    /// with a Mitchell cubic filter (the closest Skia has to upstream's LANCZOS: sharp enough
    /// that 1 px UI text survives a 2× reduction, without ringing). Returns a NEW bitmap; the
    /// caller owns and disposes it, and <paramref name="src"/> is untouched.
    /// </summary>
    internal static SKBitmap Downscale(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(new SKImageInfo(width, height, src.ColorType, src.AlphaType));
        // Cheap invariant, not an observable failure mode: Skia throws earlier on a degenerate
        // source and Fit guarantees >= 1 px, so ScalePixels has not been seen to return false.
        if (!src.ScalePixels(dst, new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            dst.Dispose();
            throw new InvalidOperationException($"SKBitmap.ScalePixels to {width}x{height} failed.");
        }
        return dst;
    }

    /// <summary>Encodes <paramref name="bmp"/>; <paramref name="quality"/> is 1-100 (used by JPEG, validated always).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> outside 1-100.</exception>
    internal static byte[] Encode(SKBitmap bmp, ImageFormat format, int quality)
    {
        if (quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Quality must be 1-100.");

        var skFormat = format == ImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        using var img = SKImage.FromBitmap(bmp);
        using var encoded = img.Encode(skFormat, quality)
            ?? throw new InvalidOperationException($"Skia returned no data encoding {bmp.Width}x{bmp.Height} as {format}.");
        return encoded.ToArray();
    }
}
