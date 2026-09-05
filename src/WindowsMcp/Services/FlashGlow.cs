using SkiaSharp;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-14's glow, as pixels: an orange band around the captured area that fades outward, with the
/// captured area itself fully transparent so the picture underneath is untouched. Pure SkiaSharp;
/// <see cref="FlashOverlay"/> hands the bitmap to <c>UpdateLayeredWindow</c>.
/// </summary>
internal static class FlashGlow
{
    /// <summary>Width of the band, in pixels, on every side of the captured rect.</summary>
    internal const int Margin = 10;

    private static readonly SKColor Orange = new(0xFF, 0x8C, 0x00);

    /// <summary>Where the overlay window goes: the captured rect inflated by the band on every side.</summary>
    internal static ScreenRegion WindowRect(ScreenRegion captured)
        => new(captured.X - Margin, captured.Y - Margin, captured.Width + 2 * Margin, captured.Height + 2 * Margin);

    /// <summary>
    /// The overlay bitmap for a window of <paramref name="width"/>×<paramref name="height"/>:
    /// premultiplied BGRA (what <c>UpdateLayeredWindow</c> takes), transparent inside the inner
    /// rect, and <see cref="Margin"/> concentric 1 px frames whose alpha falls from opaque at the
    /// picture's edge to faint at the outside.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No room for an inner pixel (a side below 2·Margin+1).</exception>
    internal static SKBitmap Render(int width, int height)
    {
        const int min = 2 * Margin + 1;
        if (width < min) throw new ArgumentOutOfRangeException(nameof(width), width, $"The glow needs at least {min} px per side.");
        if (height < min) throw new ArgumentOutOfRangeException(nameof(height), height, $"The glow needs at least {min} px per side.");

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
        // Frame i = 0 is the outermost ring; i = Margin - 1 hugs the picture and is opaque.
        for (int i = 0; i < Margin; i++)
        {
            byte alpha = (byte)Math.Round(40 + (255 - 40) * (i / (double)(Margin - 1)));
            paint.Color = Orange.WithAlpha(alpha);
            canvas.DrawRect(SKRect.Create(i + 0.5f, i + 0.5f, width - 2 * i - 1, height - 2 * i - 1), paint);
        }
        return bmp;
    }
}
