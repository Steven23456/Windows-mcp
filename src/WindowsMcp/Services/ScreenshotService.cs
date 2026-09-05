using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Services;

public sealed class ScreenshotService : IScreenshotService, IDisposable
{
    private readonly ILogger _log;

    /// <param name="log">Optional so tests can construct the service directly; stage timings are logged here when profiling is on (A-14).</param>
    /// <param name="options">
    /// A-10: the process-level capture backend (<c>--screenshot-backend</c>) a call that asks for
    /// <c>auto</c> resolves to; null means <see cref="ScreenshotOptions.Default"/> (auto).
    /// </param>
    public ScreenshotService(ILogger<ScreenshotService>? log = null, ScreenshotOptions? options = null)
    {
        _log = log ?? (ILogger)NullLogger<ScreenshotService>.Instance;
        _options = options ?? ScreenshotOptions.Default;
    }

    private readonly ScreenshotOptions _options;
    private readonly object _wgcGate = new();
    private WgcCaptureBackend? _wgc;

    /// <summary>
    /// Test seam: when set, "wgc" frames come from here instead of the real compositor (null = the
    /// compositor refused), so the auto→gdi fallback and the pipeline on a Skia frame are provable
    /// without a desktop.
    /// </summary>
    internal Func<ScreenRegion, SKBitmap?>? WgcFrameSource { get; set; }

    private static readonly string[] Backends = ["auto", "gdi", "wgc"];

    /// <summary>
    /// A-10: the backend that will produce the frame. <paramref name="requested"/> of "auto" means
    /// <paramref name="processDefault"/>; the answer is lower-case, and "auto" only when both are.
    /// </summary>
    /// <exception cref="ArgumentException">Either value is not auto|gdi|wgc.</exception>
    internal static string ResolveBackend(string requested, string processDefault)
    {
        var req = Normalise(requested, nameof(requested));
        var def = Normalise(processDefault, nameof(processDefault));
        return req == "auto" ? def : req;

        static string Normalise(string? value, string name)
        {
            if (value is null) throw new ArgumentNullException(name, "Backend must be auto|gdi|wgc.");
            var lower = value.ToLowerInvariant();   // no Trim: a padded value is a wrong value, like every option
            if (Array.IndexOf(Backends, lower) < 0)
                throw new ArgumentException($"Unknown backend '{value}'; expected auto|gdi|wgc.", name);
            return lower;
        }
    }

    /// <summary>A-10: releases the D3D device the WGC backend holds; the DI container disposes the singleton.</summary>
    public void Dispose()
    {
        lock (_wgcGate)
        {
            _wgc?.Dispose();
            _wgc = null;
        }
    }

    /// <summary>
    /// frame (gdi or wgc, A-10) → cursor overlay (A-11, on the full-resolution frame) →
    /// <see cref="ScaleMath.Fit"/> → <see cref="Downscale"/> (only when the size changes) →
    /// <see cref="Encode"/> (A-9). Both sources hand over a writable Skia bitmap; the cursor's icon
    /// path draws through a GDI view over that same memory.
    /// </summary>
    public Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, CaptureOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var o = options ?? new CaptureOptions();
        // Decided before anything is allocated: a bad backend is a bad argument, not a wasted capture.
        var backend = ResolveBackend(o.Backend, _options.Backend);
        var sw = o.Profile ? Stopwatch.StartNew() : null;
        long captureMs = 0, cursorMs = 0, resizeMs = 0;

        int screenW = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        int screenH = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        var r = region ?? new ScreenRegion(0, 0, screenW, screenH);

        var (frame, produced) = AcquireFrame(r, backend);
        using (frame)
        {
            ct.ThrowIfCancellationRequested();
            if (sw is not null) captureMs = sw.ElapsedMilliseconds;

            // The caller's own read wins (the tool reports that same point in the metadata, so the
            // picture and the numbers cannot disagree); a live read is the fallback for direct callers.
            string? cursorDrawn = null;
            if (o.IncludeCursor)
            {
                var at = o.Cursor;
                if (at is null && PInvoke.GetCursorPos(out var live)) at = new CursorPosition(live.X, live.Y);
                if (at is not null)
                {
                    // A GDI bitmap over the Skia pixels: DrawIconEx needs an HDC, the ring locks it read-write.
                    using var gdiView = new Bitmap(frame.Width, frame.Height, frame.RowBytes, PixelFormat.Format32bppPArgb, frame.GetPixels());
                    cursorDrawn = DrawCursor(gdiView, r, at, TryDrawCursorIcon);
                }
            }
            if (sw is not null) cursorMs = sw.ElapsedMilliseconds - captureMs;

            ct.ThrowIfCancellationRequested();

            var (width, height, coordinateScale) = ScaleMath.Fit(frame.Width, frame.Height, o.MaxWidth, o.MaxHeight, o.Scale);

            // A-6: annotations go on AFTER the downscale (so 2 px boxes and chips stay legible at the
            // output size), mapped through the same coordinate scale the metadata reports.
            byte[] bytes;
            int drawn;
            long beforeResize = sw?.ElapsedMilliseconds ?? 0;
            if (width != frame.Width || height != frame.Height)
            {
                using var scaled = Downscale(frame, width, height);
                if (sw is not null) resizeMs = sw.ElapsedMilliseconds - beforeResize;
                (bytes, drawn) = EncodeAnnotated(scaled, o.Format, o.Quality, o.Annotations, r, coordinateScale, o.Grid);
            }
            else
            {
                (bytes, drawn) = EncodeAnnotated(frame, o.Format, o.Quality, o.Annotations, r, coordinateScale, o.Grid);
            }

            StageTiming[]? stages = null;
            if (sw is not null)
            {
                long encodeMs = sw.ElapsedMilliseconds - beforeResize - resizeMs;
                stages = [new("capture", captureMs), new("cursor", cursorMs), new("resize", resizeMs), new("encode", encodeMs)];
                _log.LogInformation("screenshot ({Backend}): capture {CaptureMs} ms, cursor {CursorMs} ms, resize {ResizeMs} ms, encode {EncodeMs} ms ({W}x{H} -> {OutW}x{OutH})",
                    produced, captureMs, cursorMs, resizeMs, encodeMs, frame.Width, frame.Height, width, height);
            }

            return Task.FromResult(new ScreenshotResult(
                bytes, width, height, o.Format, frame.Width, frame.Height, coordinateScale, cursorDrawn, drawn, stages, produced));
        }
    }

    /// <summary>
    /// The frame for <paramref name="r"/> as a writable Skia bitmap, and which backend made it.
    /// "wgc" asks the compositor and refuses loudly when it cannot serve; "auto" prefers the
    /// compositor where it is supported and falls back to GDI silently; "gdi" is the classic copy.
    /// </summary>
    private (SKBitmap Frame, string Backend) AcquireFrame(ScreenRegion r, string backend)
    {
        if (backend != "gdi" && (backend == "wgc" || WgcCaptureBackend.IsSupported() || WgcFrameSource is not null))
        {
            var wgc = TryWgc(r);
            if (wgc is not null) return (wgc, "wgc");
            if (backend == "wgc")
                throw new InvalidOperationException(
                    $"backend 'wgc' could not capture {r.X},{r.Y},{r.Width},{r.Height}: Windows.Graphics.Capture is unavailable or refused the rect (session 0, no compositor, no monitor under it). Use backend 'auto' or 'gdi'.");
        }
        return (GdiFrame(r), "gdi");
    }

    private SKBitmap? TryWgc(ScreenRegion r)
    {
        try
        {
            if (WgcFrameSource is not null) return WgcFrameSource(r);
            var monitors = new WindowService().EnumerateMonitorsAsync().GetAwaiter().GetResult();
            WgcCaptureBackend backend;
            lock (_wgcGate) backend = _wgc ??= new WgcCaptureBackend();
            return backend.TryCapture(r, monitors, out var bmp) ? bmp : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "screenshot: wgc capture failed");
            return null;
        }
    }

    /// <summary>The classic screen copy, copied out of the locked GDI buffer into a bitmap the pipeline may write to.</summary>
    private static SKBitmap GdiFrame(ScreenRegion r)
    {
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));

        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var view = new SKBitmap();
            if (!view.InstallPixels(info, bd.Scan0, bd.Stride))
                throw new InvalidOperationException("SKBitmap.InstallPixels failed to wrap GDI bitmap memory.");
            return view.Copy();
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

    /// <summary>
    /// A-6: draws the boxes and the grid onto <paramref name="bmp"/> (already downscaled, so the
    /// mapping uses <paramref name="coordinateScale"/>) and then encodes it, reporting how many
    /// boxes landed. With no boxes and no grid the bytes are exactly <see cref="Encode"/>'s.
    /// The one step of the annotate path that needs no desktop, so the pixels can be checked by
    /// decoding the result on a synthetic bitmap.
    /// </summary>
    internal static (byte[] Bytes, int Drawn) EncodeAnnotated(
        SKBitmap bmp, ImageFormat format, int quality, IReadOnlyList<AnnotationBox>? boxes,
        ScreenRegion captured, double coordinateScale, GridSpec? grid)
    {
        if (quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Quality must be 1-100.");

        bool anyBox = boxes is { Count: > 0 };
        if (!anyBox && grid is null)
            return (Encode(bmp, format, quality), 0);   // nothing to draw: byte-identical to a plain encode

        // Draw on a copy: the caller's bitmap may be the zero-copy view of a read-only GDI lock.
        using var canvasBmp = bmp.Copy();
        int drawn = Annotator.Draw(canvasBmp, boxes ?? Array.Empty<AnnotationBox>(), captured, coordinateScale, grid);
        return (Encode(canvasBmp, format, quality), drawn);
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
