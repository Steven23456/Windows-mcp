using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly IScreenshotService _screenshot;
    private readonly IOcrService _ocr;

    public ScreenTools(IScreenshotService screenshot, IOcrService ocr)
    {
        _screenshot = screenshot;
        _ocr = ocr;
    }

    [McpServerTool, Description("Capture a screenshot and return it as MCP image content the model can see directly (parity A-7). Result content: a text block with one JSON object of metadata {width, height, format, coordinateSpace:'virtual-desktop', region? (only when given), path? (file output)} followed, for inline output, by an image block. Coordinates are virtual-desktop pixels, the same space click/drag/scroll use.")]
    public async Task<CallToolResult> Screenshot(
        [Description("Region as 'x,y,w,h' in virtual-desktop coordinates, or null for the full primary display")] string? region = null,
        [Description("Image format: png | jpeg | auto (default: jpeg for inline output, png for file output)")] string format = "auto",
        [Description("Output mode: inline (default) returns the image as an MCP image content block; file saves to %TEMP%\\WindowsMcp and returns only the path in the metadata; base64 is a deprecated alias of inline")] string output = "inline")
    {
        // Validate every argument before touching the screen: a bad call must not cost a capture.
        bool toFile = ParseOutput(output);
        var fmt = ResolveFormat(format, toFile);
        var r = ParseRegion(region);

        var result = await _screenshot.CaptureAsync(r, fmt);

        // Report what was ENCODED, not what was asked for — the image block must never lie
        // about the bytes it carries.
        bool isJpeg = result.Format == ImageFormat.Jpeg;
        var meta = new Dictionary<string, object?>
        {
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["format"] = isJpeg ? "jpeg" : "png",
            ["coordinateSpace"] = "virtual-desktop",
        };
        if (r is not null)
            meta["region"] = new { x = r.X, y = r.Y, width = r.Width, height = r.Height };

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

    [McpServerTool, Description("Run OCR on the screen or a region and return extracted text.")]
    public async Task<string> Ocr(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null)
    {
        var r = ParseRegion(region);
        return await _ocr.ExtractTextAsync(r);
    }

    private static ScreenRegion? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            throw new ArgumentException($"Invalid region '{region}'; expected 'x,y,w,h'");
        return new ScreenRegion(
            int.Parse(parts[0]), int.Parse(parts[1]),
            int.Parse(parts[2]), int.Parse(parts[3]));
    }
}
