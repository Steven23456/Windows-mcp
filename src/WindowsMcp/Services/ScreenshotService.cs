using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Services;

public sealed class ScreenshotService : IScreenshotService
{
    /// <summary>
    /// capture → cursor overlay (A-11, on the full-resolution bitmap) → <see cref="ScaleMath.Fit"/>
    /// → <see cref="Downscale"/> (only when the size changes) → <see cref="Encode"/> (A-9). The GDI
    /// buffer stays locked for the resize and encode: the Skia bitmap is a zero-copy view of it,
    /// so both must finish before <c>UnlockBits</c>. The cursor goes on before the lock because
    /// the icon path draws through the bitmap's HDC.
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

        // The caller's own read wins (the tool reports that same point in the metadata, so the
        // picture and the numbers cannot disagree); a live read is the fallback for direct callers.
        string? cursorDrawn = null;
        if (o.IncludeCursor)
        {
            var at = o.Cursor;
            if (at is null && PInvoke.GetCursorPos(out var live)) at = new CursorPosition(live.X, live.Y);
            if (at is not null) cursorDrawn = DrawCursor(bmp, r, at, TryDrawCursorIcon);
        }

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
                bytes, width, height, o.Format, bmp.Width, bmp.Height, coordinateScale, cursorDrawn));
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    /// <summary>
    /// Puts the cursor onto <paramref name="bmp"/> (a capture of <paramref name="captured"/>):
    /// the real cursor image when <paramref name="tryIcon"/> manages it, otherwise
    /// <see cref="CursorOverlay.DrawRing"/> at the same point. Returns "icon", "ring", or null
    /// when the cursor is outside the captured rect (nothing drawn). The icon step is a parameter
    /// so the fallback is unit-testable without a live cursor.
    /// </summary>
    internal static string? DrawCursor(Bitmap bmp, ScreenRegion captured, CursorPosition cursor, Func<Bitmap, int, int, bool> tryIcon)
    {
        if (CursorOverlay.RingPoint(cursor, captured) is not { } p) return null;
        if (tryIcon(bmp, p.X, p.Y)) return "icon";
        DrawRing(bmp, p.X, p.Y);
        return "ring";
    }

    /// <summary>
    /// Composites the live cursor image at (<paramref name="x"/>, <paramref name="y"/>) — the
    /// hotspot, not the image's top-left — through the bitmap's HDC. False when the cursor is
    /// hidden, has no handle, or DrawIconEx refuses, so the caller can fall back to the ring.
    /// </summary>
    internal static unsafe bool TryDrawCursorIcon(Bitmap bmp, int x, int y)
    {
        var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(ref ci)) return false;
        if ((ci.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || ci.hCursor.IsNull) return false;

        var hIcon = new HICON(ci.hCursor.Value);
        int hotX = 0, hotY = 0;
        ICONINFO ii;
        if (PInvoke.GetIconInfo(hIcon, &ii))
        {
            hotX = (int)ii.xHotspot;
            hotY = (int)ii.yHotspot;
            // GetIconInfo hands back copies of the mask/colour bitmaps that the caller owns.
            if (!ii.hbmMask.IsNull) PInvoke.DeleteObject(ii.hbmMask);
            if (!ii.hbmColor.IsNull) PInvoke.DeleteObject(ii.hbmColor);
        }

        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            return PInvoke.DrawIconEx(new HDC(hdc), x - hotX, y - hotY, hIcon, 0, 0, 0, HBRUSH.Null, DI_FLAGS.DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    /// <summary>The ring fallback on a GDI bitmap: a read-write lock wrapped in a Skia view, drawn, unlocked.</summary>
    private static void DrawRing(Bitmap bmp, int x, int y)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var sk = new SKBitmap();
            if (sk.InstallPixels(info, bd.Scan0, bd.Stride))
                CursorOverlay.DrawRing(sk, x, y);
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
