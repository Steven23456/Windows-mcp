using SkiaSharp;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// S-11's indicator, as pixels and positions: a blue ring around the pointer and a dark pill banner
/// naming the takeover hotkey. Blue so it is never mistaken for A-14's orange capture flash. Pure
/// SkiaSharp (the <see cref="FlashGlow"/> precedent); <see cref="ControlIndicator"/> hands the
/// bitmaps to <c>UpdateLayeredWindow</c>, which takes premultiplied BGRA.
/// </summary>
internal static class ControlIndicatorArt
{
    /// <summary>Side of the ring's square bitmap at 96 DPI.</summary>
    internal const int RingDiameter = 56;

    /// <summary>Height of the banner pill at 96 DPI.</summary>
    internal const int BannerHeight = 32;

    /// <summary>Gap between the top of the work area and the banner at 96 DPI.</summary>
    internal const int BannerTopMargin = 12;

    internal static readonly SKColor RingColor = new(0x3B, 0x82, 0xF6);

    private static readonly SKColor BannerFill = new(0x1E, 0x29, 0x3B, 0xE6);
    private const string Message = "AI is controlling mouse & keyboard";

    /// <summary>
    /// The ring: a soft blue halo, a solid blue stroke and a thin white inner line (so it reads on a
    /// blue window too), all well clear of the centre so the pointer itself is never tinted.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dpiScale"/> is not positive.</exception>
    internal static SKBitmap CursorRing(double dpiScale)
    {
        RequirePositive(dpiScale);
        int side = Scaled(RingDiameter, dpiScale);
        var s = (float)dpiScale;
        float c = side / 2f, r = side * 0.36f;

        var bmp = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };

        paint.Color = RingColor.WithAlpha(70);
        paint.StrokeWidth = 10 * s;
        canvas.DrawCircle(c, c, r, paint);

        paint.Color = RingColor.WithAlpha(235);
        paint.StrokeWidth = 4 * s;
        canvas.DrawCircle(c, c, r, paint);

        paint.Color = SKColors.White.WithAlpha(190);
        paint.StrokeWidth = 1.5f * s;
        canvas.DrawCircle(c, c, r - 3 * s, paint);
        return bmp;
    }

    /// <summary>
    /// The banner: a dark, mostly opaque pill sized to <paramref name="text"/> at the monitor's DPI,
    /// a blue dot on the left that matches the ring, and the text in white.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dpiScale"/> is not positive.</exception>
    internal static SKBitmap Banner(string text, double dpiScale)
    {
        RequirePositive(dpiScale);
        int height = Scaled(BannerHeight, dpiScale);
        var s = (float)dpiScale;

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 14 * s) { Edging = SKFontEdging.Antialias };
        float textWidth = font.MeasureText(text);

        float dot = 8 * s, gap = 8 * s, end = height / 2f;
        int width = (int)Math.Ceiling(end + dot + gap + textWidth + end);

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };

        paint.Color = BannerFill;
        canvas.DrawRoundRect(new SKRect(0, 0, width, height), end, end, paint);

        paint.Color = RingColor;
        canvas.DrawCircle(end + dot / 2, height / 2f, dot / 2, paint);

        paint.Color = SKColors.White;
        font.GetFontMetrics(out var metrics);
        float baseline = height / 2f - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(text, end + dot + gap, baseline, SKTextAlign.Left, font, paint);
        return bmp;
    }

    /// <summary>The banner's words: how to take over, or only the state when the hotkey could not be registered.</summary>
    internal static string BannerText(string? hotkeyDisplay) =>
        hotkeyDisplay is null ? Message : $"{Message} · {hotkeyDisplay} to take over";

    /// <summary>The ring's window, centred on the pointer (virtual-desktop pixels).</summary>
    internal static ScreenRegion RingRect(int cursorX, int cursorY, int side) =>
        new(cursorX - side / 2, cursorY - side / 2, side, side);

    /// <summary>The banner's window: horizontally centred in <paramref name="workArea"/>, just below its top.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dpiScale"/> is not positive.</exception>
    internal static ScreenRegion BannerRect(ScreenRegion workArea, int width, int height, double dpiScale)
    {
        RequirePositive(dpiScale);
        return new(workArea.X + (workArea.Width - width) / 2, workArea.Y + Scaled(BannerTopMargin, dpiScale), width, height);
    }

    private static int Scaled(int px, double scale) => (int)Math.Round(px * scale, MidpointRounding.AwayFromZero);

    private static void RequirePositive(double dpiScale)
    {
        if (!(dpiScale > 0))   // positive test: NaN must fail too
            throw new ArgumentOutOfRangeException(nameof(dpiScale), dpiScale, "A DPI scale must be a positive number.");
    }
}
