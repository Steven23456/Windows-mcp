namespace WindowsMcp.Abstractions.Models;

public record ScreenRegion(int X, int Y, int Width, int Height);
public enum ImageFormat { Png, Jpeg }

/// <summary>
/// Per-call capture settings (A-9). <see cref="MaxWidth"/>/<see cref="MaxHeight"/> of zero or
/// less mean "no limit"; <see cref="Scale"/> multiplies on top of the fit-to-cap factor and must
/// be in (0, 1]; <see cref="Quality"/> is the JPEG encoder quality, 1-100. <see cref="Cursor"/>
/// is the pointer position to draw at when <see cref="IncludeCursor"/> is set — the caller's own
/// read, so the metadata and the painted mark agree; null means the service reads it live.
/// </summary>
public record CaptureOptions(
    ImageFormat Format = ImageFormat.Png,
    int MaxWidth = 1920,
    int MaxHeight = 1080,
    double Scale = 1.0,
    int Quality = 90,
    bool IncludeCursor = false,
    CursorPosition? Cursor = null);

/// <summary>
/// The encoded image plus the geometry the caller needs to map image pixels back to the virtual
/// desktop: <see cref="Width"/>/<see cref="Height"/> are the encoded (possibly downscaled) size,
/// <see cref="OriginalWidth"/>/<see cref="OriginalHeight"/> what was captured, and
/// <see cref="CoordinateScale"/> = OriginalWidth / Width (1.0 when nothing was scaled).
/// </summary>
public record ScreenshotResult(
    byte[] Bytes,
    int Width,
    int Height,
    ImageFormat Format,
    int OriginalWidth,
    int OriginalHeight,
    double CoordinateScale,
    string? CursorDrawn = null);

/// <summary>
/// Process-level screenshot options (roadmap C7): the <c>WINDOWSMCP_SCREENSHOT_SCALE</c> /
/// <c>--screenshot-scale</c> value, applied on top of a call's own <c>scale</c> argument.
/// </summary>
public record ScreenshotOptions(double Scale)
{
    /// <summary>No process-level scaling — what an unconfigured server and the tool's own default use.</summary>
    public static ScreenshotOptions Default { get; } = new(1.0);
}
