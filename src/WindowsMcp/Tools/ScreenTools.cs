using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly IScreenshotService _screenshot;
    private readonly IOcrService _ocr;
    private readonly IWindowService _windows;
    private readonly ScreenshotOptions _options;

    /// <param name="windows">
    /// Source of the monitor inventory (A-8) — the same order <c>multi_monitor</c> reports, which
    /// is what the <c>display</c> indices refer to.
    /// </param>
    /// <param name="options">
    /// The process-level <c>--screenshot-scale</c> (A-9); null means no process-level scaling,
    /// so tests and other hosts can construct the tool without it.
    /// </param>
    public ScreenTools(IScreenshotService screenshot, IOcrService ocr, IWindowService windows, ScreenshotOptions? options = null)
    {
        _screenshot = screenshot;
        _ocr = ocr;
        _windows = windows;
        _options = options ?? ScreenshotOptions.Default;
    }

    private const string RegionDescription =
        "Region as 'x,y,w,h' in virtual-desktop pixels (the same space click/drag/scroll use; a monitor left of or above the primary has negative coordinates). Must lie inside the virtual screen — it is rejected, not clipped. Wins over 'display'. Default: none";
    private const string DisplayDescription =
        "Which monitor(s) to capture: 'all', or comma-separated zero-based indices in multi_monitor order (e.g. '1' or '0,2'); the union of several is captured. Default: the primary display. 'region' wins over this, but an invalid value still errors";

    /// <summary>
    /// A-8: resolves the rect to capture from <paramref name="region"/> / <paramref name="display"/>
    /// against the live monitor inventory. <c>region</c> wins (validated against the virtual
    /// screen); else the union of the selected displays; neither means the primary display
    /// (roadmap C3). One resolver for both tools, so <c>screenshot</c> and <c>ocr</c> cannot drift
    /// in what they accept. <c>display</c> is parsed even when <c>region</c> wins: a bad value is
    /// a bad call, not something to ignore quietly.
    /// </summary>
    /// <returns>The rect, the inventory it was resolved against, and the selected indices (null
    /// unless <paramref name="display"/> picked the rect).</returns>
    private async Task<(ScreenRegion Region, MonitorInfo[] Monitors, int[]? Selected)> ResolveRegionAsync(
        string? region, string? display, CancellationToken ct = default)
    {
        var monitors = await _windows.EnumerateMonitorsAsync(ct);
        var selected = RegionMath.ParseDisplays(display, monitors.Length);
        var parsed = RegionMath.ParseRegion(region);

        if (parsed is not null)
        {
            RegionMath.Validate(parsed, RegionMath.VirtualScreen(monitors));
            return (parsed, monitors, null);
        }
        if (selected is not null)
            return (RegionMath.Union(selected.Select(i => monitors[i]).ToArray()), monitors, selected);

        var primary = RegionMath.Primary(monitors);
        return (new ScreenRegion(primary.X, primary.Y, primary.Width, primary.Height), monitors, null);
    }

    /// <summary>
    /// The one sentence that tells the model how to turn an image pixel into a virtual-desktop
    /// pixel; null when they are already the same thing (origin 0,0, scale 1). Scale-only keeps
    /// A-9's wording; an off-origin capture (a second monitor, a region) needs the offset too.
    /// </summary>
    internal static string? CoordinateNote(ScreenRegion region, double coordinateScale)
    {
        var s = coordinateScale.ToString(CultureInfo.InvariantCulture);
        if (region.X == 0 && region.Y == 0)
        {
            return coordinateScale == 1.0
                ? null
                : $"multiply image pixel coordinates by {s} before passing them to click/drag/scroll";
        }
        return $"virtual-desktop x = {region.X} + imageX × {s}, y = {region.Y} + imageY × {s} — use these for click/drag/scroll";
    }

    [McpServerTool, Description("Capture a screenshot and return it as MCP image content the model can see directly (parity A-7/A-8/A-9). Result content: a text block with one JSON object of metadata {width, height, originalWidth, originalHeight, format, coordinateSpace:'virtual-desktop', region (the rect actually captured, in virtual-desktop pixels), displays (every monitor: index, x, y, width, height, isPrimary), selectedDisplays? (when 'display' picked the rect), path? (file output), coordinateScale? and note? (present whenever image pixels are not virtual-desktop pixels 1:1: multiply image pixel coordinates by coordinateScale and add the region origin — the note spells it out; do this before calling click/drag/scroll)} followed, for inline output, by an image block. Default: the primary display, downscaled to fit max_width x max_height (1920x1080).")]
    public async Task<CallToolResult> Screenshot(
        [Description(RegionDescription)] string? region = null,
        [Description(DisplayDescription)] string? display = null,
        [Description("Image format: png | jpeg | auto (default: jpeg for inline output, png for file output)")] string format = "auto",
        [Description("Output mode: inline (default) returns the image as an MCP image content block; file saves to %TEMP%\\WindowsMcp and returns only the path in the metadata; base64 is a deprecated alias of inline")] string output = "inline",
        [Description("Downscale so the image is at most this wide, in pixels; 0 = no limit (default 1920)")] int max_width = 1920,
        [Description("Downscale so the image is at most this tall, in pixels; 0 = no limit (default 1080)")] int max_height = 1080,
        [Description("Extra shrink factor applied on top of the max_width/max_height fit, in (0, 1] (default 1.0); the server's --screenshot-scale multiplies it further")] double scale = 1.0,
        [Description("JPEG encoder quality, 1-100 (default 90); ignored for png")] int quality = 90)
    {
        // Validate every argument before touching the screen: a bad call must not cost a capture.
        bool toFile = ParseOutput(output);
        var fmt = ResolveFormat(format, toFile);
        if (max_width < 0)
            throw new ArgumentException($"max_width must be 0 (no limit) or positive, got {max_width}");
        if (max_height < 0)
            throw new ArgumentException($"max_height must be 0 (no limit) or positive, got {max_height}");
        if (!(scale > 0 && scale <= 1))
            throw new ArgumentException($"scale must be in (0, 1], got {scale.ToString(CultureInfo.InvariantCulture)}");
        if (quality is < 1 or > 100)
            throw new ArgumentException($"quality must be 1-100, got {quality}");
        var (r, monitors, selected) = await ResolveRegionAsync(region, display);

        // The process-level --screenshot-scale applies on top of the call's own scale.
        var result = await _screenshot.CaptureAsync(r,
            new CaptureOptions(fmt, max_width, max_height, scale * _options.Scale, quality));

        // Report what was ENCODED, not what was asked for — the image block must never lie
        // about the bytes it carries.
        bool isJpeg = result.Format == ImageFormat.Jpeg;
        var meta = new Dictionary<string, object?>
        {
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["originalWidth"] = result.OriginalWidth,
            ["originalHeight"] = result.OriginalHeight,
            ["format"] = isJpeg ? "jpeg" : "png",
            ["coordinateSpace"] = "virtual-desktop",
            // Always: image (0,0) is this rect's origin, which is not (0,0) on a second monitor.
            ["region"] = new { x = r.X, y = r.Y, width = r.Width, height = r.Height },
            ["displays"] = monitors.Select(m => new { index = m.Index, x = m.X, y = m.Y, width = m.Width, height = m.Height, isPrimary = m.IsPrimary }).ToArray(),
        };
        if (selected is not null)
            meta["selectedDisplays"] = selected;
        if (result.CoordinateScale != 1.0)
            meta["coordinateScale"] = result.CoordinateScale;
        // Absent when nothing needs translating: the model only sees the instruction when it applies.
        if (CoordinateNote(r, result.CoordinateScale) is { } note)
            meta["note"] = note;

        if (toFile)
        {
            var dir = Path.Combine(Path.GetTempPath(), "WindowsMcp");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{(isJpeg ? "jpg" : "png")}");
            await File.WriteAllBytesAsync(filePath, result.Bytes);
            meta["path"] = filePath;
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(meta) }],
            };
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(meta) },
                ImageContentBlock.FromBytes(result.Bytes, isJpeg ? "image/jpeg" : "image/png"),
            ],
        };
    }

    /// <summary>True for file output, false for inline; "base64" is the pre-A-7 alias of inline.</summary>
    private static bool ParseOutput(string output) => output.ToLowerInvariant() switch
    {
        "inline" or "base64" => false,
        "file" => true,
        _ => throw new ArgumentException($"Unknown output '{output}'; expected inline|file|base64"),
    };

    private static ImageFormat ResolveFormat(string format, bool toFile) => format.ToLowerInvariant() switch
    {
        "png" => ImageFormat.Png,
        "jpeg" => ImageFormat.Jpeg,
        // Inline goes to the model's context, where a JPEG is a fraction of the PNG's tokens;
        // a file on disk keeps the lossless default it always had.
        "auto" => toFile ? ImageFormat.Png : ImageFormat.Jpeg,
        _ => throw new ArgumentException($"Unknown format '{format}'; expected png|jpeg|auto"),
    };

    [McpServerTool, Description("Run OCR on the screen and return the extracted text. Default: the primary display at full resolution; 'region' (virtual-desktop pixels) or 'display' narrow it exactly as for screenshot.")]
    public async Task<string> Ocr(
        [Description(RegionDescription)] string? region = null,
        [Description(DisplayDescription)] string? display = null)
    {
        var (r, _, _) = await ResolveRegionAsync(region, display);
        return await _ocr.ExtractTextAsync(r);
    }
}
