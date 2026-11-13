using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WinOcr = Windows.Media.Ocr;

namespace WindowsMcp.Services;

public sealed class OcrService : IOcrService
{
    private readonly IScreenshotService _screenshot;

    public OcrService(IScreenshotService screenshot) => _screenshot = screenshot;

    public async Task<string> ExtractTextAsync(ScreenRegion? region = null, CancellationToken ct = default)
        => (await RecognizeCoreAsync(region, ct)).Result.Text;

    /// <summary>
    /// E-7: the same capture and the same engine pass as <see cref="ExtractTextAsync"/>, with
    /// where each line and word is — in virtual-desktop pixels, mapped back through the capture's
    /// origin and scale by <see cref="OcrGeometry"/>.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(ScreenRegion region, CancellationToken ct = default)
    {
        var (result, scale, imageWidth, imageHeight) = await RecognizeCoreAsync(region, ct);
        var lines = result.Lines
            .Select(l => new EngineLine(l.Text, l.Words
                .Select(w => new EngineWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToArray()))
            .ToArray();
        // For tilted text the engine's rectangles are in its deskewed frame: map them back.
        double? angle = result.TextAngle is { } reported && double.IsFinite(reported) ? reported : null;
        var mapped = OcrGeometry.ToLines(lines, region.X, region.Y, scale, angle ?? 0, imageWidth, imageHeight);
        return new OcrResult(result.Text, mapped, region, angle);
    }

    private async Task<(WinOcr.OcrResult Result, double Scale, int ImageWidth, int ImageHeight)> RecognizeCoreAsync(ScreenRegion? region, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // OCR reads pixels, so it always gets the full-resolution capture (MaxWidth/MaxHeight 0 = no cap).
        var shot = await _screenshot.CaptureAsync(region, new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0), ct);

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(shot.Bytes.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var bitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR language pack installed");
        return (await engine.RecognizeAsync(bitmap), shot.CoordinateScale, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
