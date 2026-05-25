using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IUIAutomationService : IDisposable
{
    Task<ElementTree> GetStateAsync(CancellationToken ct = default);
    Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any, CancellationToken ct = default);
    Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct = default);
    Task<string> GetTextAsync(string elementId, CancellationToken ct = default);
    Task<bool> AssertElementAsync(string elementId, string state, CancellationToken ct = default);
    Task InteractAsync(string elementId, string action, string? value, CancellationToken ct = default);
    Task<TableData> GetTableAsync(string elementId, CancellationToken ct = default);
    Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs, CancellationToken ct = default);
    Task FocusAsync(string elementId, CancellationToken ct = default);
}
