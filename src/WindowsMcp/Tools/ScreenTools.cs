using System.ComponentModel;
using System.Text.Json;
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

    [McpServerTool, Description("Capture a screenshot of the screen or a region.")]
    public async Task<string> Screenshot(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null,
        [Description("Image format: png or jpeg")] string format = "png")
    {
        var r = ParseRegion(region);
        var fmt = format.ToLowerInvariant() == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        var result = await _screenshot.CaptureAsync(r, fmt);
        return JsonSerializer.Serialize(new
        {
            width = result.Width,
            height = result.Height,
            format = result.Format.ToString().ToLowerInvariant(),
            data_base64 = Convert.ToBase64String(result.Bytes)
        });
    }

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
