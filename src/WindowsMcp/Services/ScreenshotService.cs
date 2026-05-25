using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ScreenshotService : IScreenshotService
{
    public Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, Abstractions.Models.ImageFormat format = Abstractions.Models.ImageFormat.Png, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int screenW = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        int screenH = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        var r = region ?? new ScreenRegion(0, 0, screenW, screenH);

        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));

        ct.ThrowIfCancellationRequested();

        // Lock bitmap bits, copy into SKBitmap, encode via SkiaSharp.
        var data = new byte[bmp.Width * bmp.Height * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                              ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, data, 0, data.Length);
        bmp.UnlockBits(bd);

        using var skBmp = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, skBmp.GetPixels(), data.Length);
        using var img = SKImage.FromBitmap(skBmp);

        var skFormat = format == Abstractions.Models.ImageFormat.Jpeg
            ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
        using var encoded = img.Encode(skFormat, 90);
        var bytes = encoded.ToArray();

        return Task.FromResult(new ScreenshotResult(bytes, r.Width, r.Height, format));
    }
}
