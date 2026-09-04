using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class OcrService : IOcrService
{
    private readonly IScreenshotService _screenshot;

    public OcrService(IScreenshotService screenshot) => _screenshot = screenshot;

    public async Task<string> ExtractTextAsync(ScreenRegion? region = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // OCR reads pixels, so it always gets the full-resolution capture (MaxWidth/MaxHeight 0 = no cap).
        var shot = await _screenshot.CaptureAsync(region, new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0), ct);

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(shot.Bytes.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var bitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR language pack installed");
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text;
    }
}
