using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IOcrService
{
    Task<string> ExtractTextAsync(ScreenRegion? region = null, CancellationToken ct = default);

    /// <summary>
    /// E-7: the text of <paramref name="region"/> with where each line and word is. The capture is
    /// the one <see cref="ExtractTextAsync"/> takes; every bounds is in virtual-desktop pixels.
    /// </summary>
    Task<OcrResult> RecognizeAsync(ScreenRegion region, CancellationToken ct = default);
}
