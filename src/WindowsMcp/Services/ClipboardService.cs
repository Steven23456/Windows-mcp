using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class ClipboardService : IClipboardService
{
    public Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TextCopy.ClipboardService.GetTextAsync();
    }

    public Task SetTextAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TextCopy.ClipboardService.SetTextAsync(text);
    }
}
