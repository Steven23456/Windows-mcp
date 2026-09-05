namespace WindowsMcp.Abstractions.Models;

public record ScreenRegion(int X, int Y, int Width, int Height);
public enum ImageFormat { Png, Jpeg }

/// <summary>
/// Per-call capture settings (A-9). <see cref="MaxWidth"/>/<see cref="MaxHeight"/> of zero or
/// less mean "no limit"; <see cref="Scale"/> multiplies on top of the fit-to-cap factor and must
/// be in (0, 1]; <see cref="Quality"/> is the JPEG encoder quality, 1-100. <see cref="Cursor"/>
/// is the pointer position to draw at when <see cref="IncludeCursor"/> is set — the caller's own
/// read, so the metadata and the painted mark agree; null means the service reads it live.
/// <see cref="Annotations"/> and <see cref="Grid"/> are A-6: what to draw on top of the finished
/// picture, both null (nothing drawn, bytes unchanged) unless the caller asks.
/// </summary>
public record CaptureOptions(
    ImageFormat Format = ImageFormat.Png,
    int MaxWidth = 1920,
    int MaxHeight = 1080,
    double Scale = 1.0,
    int Quality = 90,
    bool IncludeCursor = false,
    CursorPosition? Cursor = null,
    IReadOnlyList<AnnotationBox>? Annotations = null,
    GridSpec? Grid = null);

/// <summary>
/// A-6: one labelled box to draw on a capture. <paramref name="Bounds"/> is in virtual-desktop
/// pixels (roadmap C1) — the same space the snapshot reports element bounds in — and is mapped
/// onto image pixels by the annotator; <paramref name="Label"/> is the snapshot's element id, so
/// label N in the picture is row N in the text of the same call.
/// </summary>
public record AnnotationBox(string Label, Bounds Bounds);

/// <summary>
/// A-6: the coordinate grid to overlay. A column/row count of zero or less means "no lines on
/// that axis"; <c>Columns</c> of 4 draws the three interior vertical lines.
/// </summary>
public record GridSpec(int Columns, int Rows);

/// <summary>
/// The encoded image plus the geometry the caller needs to map image pixels back to the virtual
/// desktop: <see cref="Width"/>/<see cref="Height"/> are the encoded (possibly downscaled) size,
/// <see cref="OriginalWidth"/>/<see cref="OriginalHeight"/> what was captured, and
/// <see cref="CoordinateScale"/> = OriginalWidth / Width (1.0 when nothing was scaled).
/// <see cref="AnnotationsDrawn"/> is A-6: how many of the requested boxes actually landed on the
/// image (a box off the captured rect is not drawn and not counted).
/// </summary>
public record ScreenshotResult(
    byte[] Bytes,
    int Width,
    int Height,
    ImageFormat Format,
    int OriginalWidth,
    int OriginalHeight,
    double CoordinateScale,
    string? CursorDrawn = null,
    int AnnotationsDrawn = 0);

/// <summary>
/// Process-level screenshot options (roadmap C7): the <c>WINDOWSMCP_SCREENSHOT_SCALE</c> /
/// <c>--screenshot-scale</c> value, applied on top of a call's own <c>scale</c> argument.
/// </summary>
public record ScreenshotOptions(double Scale)
{
    /// <summary>No process-level scaling — what an unconfigured server and the tool's own default use.</summary>
    public static ScreenshotOptions Default { get; } = new(1.0);
}
