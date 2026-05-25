using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IOcrService
{
    Task<string> ExtractTextAsync(ScreenRegion? region = null, CancellationToken ct = default);
}
