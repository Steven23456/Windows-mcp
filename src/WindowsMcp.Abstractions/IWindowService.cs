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


    /// <summary>
    /// B-8: launch by Start Menu name. A path or an executable name that exists is started
    /// outright (<c>Strategy: "path"</c>, no catalog consulted); anything else is resolved through
    /// <see cref="IAppCatalogService"/> and activated by AUMID (packaged) or by ShellExecute on
    /// its <c>.lnk</c> (shortcut). With <paramref name="waitForWindow"/> the window inventory is
    /// polled up to <paramref name="timeoutMs"/> (1..60000) for a window of the launched process,
    /// or a new window whose title matches the resolved name; a timeout is reported as
    /// <c>WindowDetected:false</c>, not thrown.
    /// </summary>
    Task<LaunchResult> LaunchAsync(string appName, bool waitForWindow, int timeoutMs, CancellationToken ct = default);

    /// <summary>
    /// B-9: move and/or resize one window. The target is resolved through the same matcher as
    /// <see cref="BringToFrontAsync"/> (hwnd wins, else title exact → substring → fuzzy); both
    /// null targets the foreground window. At least one of
    /// <paramref name="x"/>/<paramref name="y"/>/<paramref name="width"/>/<paramref name="height"/>
    /// is required; a minimised or maximised target is refused naming its state unless
    /// <paramref name="restoreFirst"/>. The bounds in the result are re-read from the window.
    /// </summary>
    Task<WindowBoundsResult> SetBoundsAsync(
        string? title, long? hwnd, int? x, int? y, int? width, int? height, bool restoreFirst,
        CancellationToken ct = default);

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
