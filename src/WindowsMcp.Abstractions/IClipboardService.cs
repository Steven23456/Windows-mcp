namespace WindowsMcp.Abstractions;

public interface IClipboardService
{
    Task<string?> GetTextAsync(CancellationToken ct = default);
    Task SetTextAsync(string text, CancellationToken ct = default);
}
