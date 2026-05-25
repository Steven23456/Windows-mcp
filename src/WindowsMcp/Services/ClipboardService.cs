using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class ClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await TextCopy.ClipboardService.GetTextAsync();
    }

    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await TextCopy.ClipboardService.SetTextAsync(text);
    }
}
