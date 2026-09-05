using SkiaSharp;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-6's drawing core: coloured boxes with label chips around the snapshot's elements, and an
/// optional reference grid, painted onto the (already downscaled) capture. Pure SkiaSharp — a
/// bitmap in, pixels out — so every rule is unit-tested on a synthetic bitmap. Geometry is in
/// virtual-desktop pixels on the way in and image pixels on the way out.
/// </summary>
internal static class Annotator
{
    private const int StrokeWidth = 2;
    private const float ChipTextSize = 11f;
    private const int ChipPadding = 3;
    private const int ChipHeight = 14;

    /// <summary>Twelve distinct opaque colours, cycling; a box's colour is its index in the list, so it stays tied to its label.</summary>
    private static readonly SKColor[] Palette =
    [
        new(0xE6, 0x19, 0x4B), new(0x3C, 0xB4, 0x4B), new(0x43, 0x63, 0xD8), new(0xF5, 0x82, 0x31),
        new(0x91, 0x1E, 0xB4), new(0x46, 0xF0, 0xF0), new(0xF0, 0x32, 0xE6), new(0xBF, 0xEF, 0x45),
        new(0xFA, 0xBE, 0xD4), new(0x00, 0x80, 0x80), new(0x9A, 0x63, 0x24), new(0xFF, 0xE1, 0x19),
    ];

    internal static SKColor ColorFor(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), index, "Palette index must not be negative.");
        return Palette[index % Palette.Length];
    }

    /// <summary>
    /// Virtual-desktop bounds → image pixels: subtract the captured rect's origin, divide by the
    /// coordinate scale, round half away from zero (banker's rounding puts a box half a pixel
    /// off), widen a box that rounds to nothing to 1 px so a tiny element stays visible, then clip
    /// to the image. Null when nothing of it is in the picture.
    /// </summary>
    internal static SKRectI? ToImage(Bounds b, ScreenRegion captured, double coordinateScale, int imageW, int imageH)
    {
        double s = coordinateScale <= 0 ? 1 : coordinateScale;
        int l = Round((b.X - captured.X) / s), t = Round((b.Y - captured.Y) / s);
        int r = Round((b.X + b.Width - captured.X) / s), bt = Round((b.Y + b.Height - captured.Y) / s);
        if (r <= l) r = l + 1;
        if (bt <= t) bt = t + 1;

        l = Math.Max(0, l); t = Math.Max(0, t);
        r = Math.Min(imageW, r); bt = Math.Min(imageH, bt);
        if (r <= l || bt <= t) return null;
        return new SKRectI(l, t, r, bt);
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The label chip sits just above the box's top-left; with no room above it sits inside the box
    /// at its top-left; and it never leaves the image on any side.
    /// </summary>
    internal static SKRectI ChipRect(SKRectI box, int chipW, int chipH, int imageW, int imageH)
    {
        int left = box.Left;
        int top = box.Top - chipH;
        if (top < 0) top = box.Top;
        if (left + chipW > imageW) left = imageW - chipW;
        if (top + chipH > imageH) top = imageH - chipH;
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        return new SKRectI(left, top, left + chipW, top + chipH);
    }

    /// <summary>Black text on a light chip, white on a dark one — by luminance, not hue (yellow is light, navy is dark).</summary>
    internal static bool UseDarkText(SKColor background)
        => (0.2126 * background.Red + 0.7152 * background.Green + 0.0722 * background.Blue) / 255.0 > 0.5;

    /// <summary>
    /// Grid first (so boxes paint over it), then each box in list order: a 2 px stroke in
    /// <see cref="ColorFor"/>(index) and a filled chip carrying the label. A box outside the picture
    /// is skipped but keeps its palette index, so a colour always means the same label. Returns
    /// how many boxes were drawn.
    /// </summary>
    internal static int Draw(SKBitmap bmp, IReadOnlyList<AnnotationBox> boxes, ScreenRegion captured, double coordinateScale, GridSpec? grid)
    {
        using var canvas = new SKCanvas(bmp);
        int w = bmp.Width, h = bmp.Height;

        if (grid is { } g)
            DrawGrid(canvas, w, h, g, captured, coordinateScale);

        using var font = new SKFont(SKTypeface.Default, ChipTextSize);
        int drawn = 0;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (ToImage(boxes[i].Bounds, captured, coordinateScale, w, h) is not { } rect) continue;
            var colour = ColorFor(i);

            using (var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = StrokeWidth, Color = colour, IsAntialias = false })
                canvas.DrawRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height), stroke);

            var label = boxes[i].Label;
            int chipW = (int)Math.Ceiling(font.MeasureText(label)) + 2 * ChipPadding;
            var chip = ChipRect(rect, chipW, ChipHeight, w, h);
            using (var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = colour, IsAntialias = false })
                canvas.DrawRect(SKRect.Create(chip.Left, chip.Top, chip.Width, chip.Height), fill);
            using (var text = new SKPaint { Color = UseDarkText(colour) ? SKColors.Black : SKColors.White, IsAntialias = true })
                canvas.DrawText(label, chip.Left + ChipPadding, chip.Bottom - ChipPadding, SKTextAlign.Left, font, text);

            drawn++;
        }
        return drawn;
    }

    /// <summary>
    /// Semi-transparent grey lines at every interior division, each captioned with the
    /// virtual-desktop coordinate it sits on (the number the model passes to click), not the image pixel.
    /// </summary>
    private static void DrawGrid(SKCanvas canvas, int w, int h, GridSpec grid, ScreenRegion captured, double scale)
    {
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0x30, 0x30, 0x30, 0xA0), IsAntialias = false };
        using var font = new SKFont(SKTypeface.Default, 10f);
        using var text = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20, 0xFF), IsAntialias = true };
        using var halo = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xB0), Style = SKPaintStyle.Fill, IsAntialias = false };
        double s = scale <= 0 ? 1 : scale;

        for (int k = 1; k < grid.Columns; k++)
        {
            int x = (int)Math.Round((double)w * k / grid.Columns, MidpointRounding.AwayFromZero);
            canvas.DrawLine(x + 0.5f, 0, x + 0.5f, h, line);
            Caption(canvas, $"{Round(captured.X + x * s)}", x + 2, 11, font, text, halo);
        }
        for (int k = 1; k < grid.Rows; k++)
        {
            int y = (int)Math.Round((double)h * k / grid.Rows, MidpointRounding.AwayFromZero);
            canvas.DrawLine(0, y + 0.5f, w, y + 0.5f, line);
            Caption(canvas, $"{Round(captured.Y + y * s)}", 2, y - 2, font, text, halo);
        }
    }

    private static void Caption(SKCanvas canvas, string label, float x, float baseline, SKFont font, SKPaint text, SKPaint halo)
    {
        float width = font.MeasureText(label);
        canvas.DrawRect(SKRect.Create(x - 1, baseline - 9, width + 2, 11), halo);
        canvas.DrawText(label, x, baseline, SKTextAlign.Left, font, text);
    }
}
