using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IScreenshotService
{
    /// <summary>
    /// Captures <paramref name="region"/> (null = the primary display) and encodes it per
    /// <paramref name="options"/> (null = <see cref="CaptureOptions"/> defaults).
    /// </summary>
    Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, CaptureOptions? options = null, CancellationToken ct = default);
}
