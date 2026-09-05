using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IWindowService
{
    Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default);
    Task<bool> SwitchToAsync(string title, CancellationToken ct = default);
    Task<int> LaunchAsync(string appName, CancellationToken ct = default);
    Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default);

    /// <summary>
    /// A-1: every user-visible top-level window, topmost first (EnumWindows order = z-order).
    /// </summary>
    Task<WindowInfo[]> ListAsync(bool includeMinimized = true, bool includeHidden = false, CancellationToken ct = default);

    /// <summary>
    /// A-1: the foreground window, or null when there is none or it does not survive the filter
    /// (the desktop, a tool window).
    /// </summary>
    Task<WindowInfo?> GetActiveAsync(CancellationToken ct = default);
}
