using SkiaSharp;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-11's drawn-cursor fallback: a two-tone ring that reads on any background, used when the real
/// cursor image cannot be composited (hidden cursor, DrawIconEx failure). Pure SkiaSharp, so the
/// geometry and the pixels are unit-tested on a synthetic bitmap.
/// </summary>
internal static class CursorOverlay
{
    /// <summary>The cursor in bitmap coordinates (cursor minus the captured rect's origin), or null when it is outside the rect.</summary>
    internal static (int X, int Y)? RingPoint(CursorPosition cursor, ScreenRegion captured)
    {
        int x = cursor.X - captured.X, y = cursor.Y - captured.Y;
        if (x < 0 || y < 0 || x >= captured.Width || y >= captured.Height) return null;
        return (x, y);
    }

    /// <summary>
    /// Outer white stroke (radius 12, 3 px) and inner black stroke (radius 8, 2 px), anti-aliased,
    /// clipped by the canvas at the bitmap edge; the centre and the gap between the strokes are
    /// left untouched so the pointer's exact pixel is still visible.
    /// </summary>
    internal static void DrawRing(SKBitmap bmp, int x, int y)
    {
        using var canvas = new SKCanvas(bmp);
        using var outer = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.White, IsAntialias = true };
        using var inner = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Black, IsAntialias = true };
        // +0.5: centre the ring on the pixel's centre, not its top-left corner, so the strokes land
        // on whole pixels (radius 12 covers pixel 12, radius 10 stays clean) instead of straddling.
        canvas.DrawCircle(x + 0.5f, y + 0.5f, 12, outer);
        canvas.DrawCircle(x + 0.5f, y + 0.5f, 8, inner);
    }
}
