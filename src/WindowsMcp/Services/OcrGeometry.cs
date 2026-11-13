using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// The engine's own view of one recognised word: its text and its rectangle in IMAGE pixels
/// (Windows.Media.Ocr reports doubles). A plain record so the mapping is testable with no WinRT.
/// </summary>
internal sealed record EngineWord(string Text, double X, double Y, double Width, double Height);

/// <summary>One recognised line as the engine reports it: its text and its words, in reading order.</summary>
internal sealed record EngineLine(string Text, IReadOnlyList<EngineWord> Words);

/// <summary>
/// E-7: the pure image-pixel → virtual-desktop mapping behind <c>ocr(format:"json")</c>
/// (docs/design/E-7-capture-targets-ocr-words.md, "Service").
/// </summary>
internal static class OcrGeometry
{
    /// <summary>
    /// One engine rectangle as virtual-desktop bounds. The four EDGES are scaled and rounded
    /// (midpoint away from zero), not the size, so two words that touch in the image still touch
    /// on the desktop. <paramref name="scale"/> is the capture's CoordinateScale: desktop pixels
    /// per image pixel.
    /// </summary>
    /// <param name="angleDegrees">
    /// The engine's TextAngle. For tilted text Windows.Media.Ocr reports its rectangles in the
    /// DESKEWED frame, not in the image's own axes (measured with the real engine: at 15 degrees a
    /// word near the edge of a 1000-pixel frame is reported about 80 pixels from its glyphs). A
    /// non-zero angle rotates the rectangle's corners back by +angle about the image centre —
    /// clockwise on screen, y down, in image pixels, before scale and origin — and the result is
    /// the axis-aligned box of the rotated corners.
    /// </param>
    /// <param name="imageWidth">The captured image's width in pixels; only used with an angle.</param>
    /// <param name="imageHeight">The captured image's height in pixels; only used with an angle.</param>
    internal static Bounds ToVirtualDesktop(double x, double y, double width, double height, int originX, int originY, double scale = 1.0,
        double angleDegrees = 0, int imageWidth = 0, int imageHeight = 0)
    {
        double l = x, t = y, r = x + width, b = y + height;
        // An angle that is not a number is no angle: map the rectangle as it stands.
        if (double.IsFinite(angleDegrees) && angleDegrees != 0)
        {
            // Without the image size the pivot would silently be the image origin.
            if (imageWidth <= 0 || imageHeight <= 0)
                throw new ArgumentException(
                    $"A text angle ({angleDegrees}) needs the size of the image it was read from, got {imageWidth}x{imageHeight}.", nameof(imageWidth));
            double radians = angleDegrees * Math.PI / 180.0, cos = Math.Cos(radians), sin = Math.Sin(radians);
            double cx = imageWidth / 2.0, cy = imageHeight / 2.0;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var (px, py) in new[] { (l, t), (r, t), (r, b), (l, b) })
            {
                double dx = px - cx, dy = py - cy;
                double rx = cx + dx * cos - dy * sin, ry = cy + dx * sin + dy * cos;
                minX = Math.Min(minX, rx);
                maxX = Math.Max(maxX, rx);
                minY = Math.Min(minY, ry);
                maxY = Math.Max(maxY, ry);
            }
            (l, t, r, b) = (minX, minY, maxX, maxY);
        }

        int left = originX + Edge(l * scale);
        int top = originY + Edge(t * scale);
        int right = originX + Edge(r * scale);
        int bottom = originY + Edge(b * scale);
        return new Bounds(left, top, right - left, bottom - top);
    }

    private static int Edge(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The engine's lines as the result's lines: every word mapped, each line's bounds the union
    /// of its words'. A line with no words has no rectangle and is dropped.
    /// </summary>
    internal static OcrLine[] ToLines(IReadOnlyList<EngineLine> lines, int originX, int originY, double scale = 1.0,
        double angleDegrees = 0, int imageWidth = 0, int imageHeight = 0)
    {
        var mapped = new List<OcrLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            var words = new OcrWord[line.Words.Count];
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            for (int i = 0; i < words.Length; i++)
            {
                var w = line.Words[i];
                var b = ToVirtualDesktop(w.X, w.Y, w.Width, w.Height, originX, originY, scale, angleDegrees, imageWidth, imageHeight);
                words[i] = new OcrWord(w.Text, b);
                left = Math.Min(left, b.X);
                top = Math.Min(top, b.Y);
                right = Math.Max(right, b.X + b.Width);
                bottom = Math.Max(bottom, b.Y + b.Height);
            }
            mapped.Add(new OcrLine(line.Text, new Bounds(left, top, right - left, bottom - top), words));
        }
        return mapped.ToArray();
    }
}
