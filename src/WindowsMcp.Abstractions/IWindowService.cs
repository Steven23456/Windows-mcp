using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IWindowService
{
    /// <summary>
    /// B-10: act on one window. The target is resolved through the same matcher
    /// <see cref="BringToFrontAsync"/> uses — an explicit <paramref name="hwnd"/> wins, otherwise
    /// <paramref name="title"/> is matched exact → substring → fuzzy, so
    /// <c>window(action:"close", title:"notepad")</c> closes "Untitled - Notepad". Neither given
    /// is an <see cref="ArgumentException"/>; no match is a <see cref="KeyNotFoundException"/>
    /// listing the open titles.
    /// </summary>
    Task<WindowAction> ExecuteAsync(string action, string? title, long? hwnd = null, CancellationToken ct = default);

    /// <summary>
    /// B-10: bring a window to the foreground and report what actually happened (roadmap C11) —
    /// which window matched and how, whether it had to be restored, which step of the ladder
    /// worked, and whether <c>GetForegroundWindow</c> agrees afterwards.
    /// </summary>
    Task<ForegroundResult> BringToFrontAsync(string? title, long? hwnd, CancellationToken ct = default);
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
