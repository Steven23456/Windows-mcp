using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IScreenshotService
{
    Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, ImageFormat format = ImageFormat.Png, CancellationToken ct = default);
}
