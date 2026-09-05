using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-1's pure core: which top-level windows the model is allowed to see, and what to call them.
/// No Win32 — the enumerator fills <see cref="WindowProbe"/>s, this class judges them, so every
/// filter rule is provable on hand-written probes with no desktop attached (roadmap C10).
/// </summary>
internal static class WindowFilter
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;

    /// <summary>
    /// Shell chrome upstream drops too: the taskbars, the desktop host ("Program Manager"), the
    /// wallpaper worker windows, and the input-method windows. Exact class names, ordinal.
    /// </summary>
    private static readonly HashSet<string> ShellChromeClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "IME", "MSCTFIME UI",
    };

    /// <summary>Process names (without extension) whose windows host web content. A-5 walks their DOM.</summary>
    internal static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
    };

    /// <summary>True when a probed window belongs in the inventory.</summary>
    internal static bool Keep(WindowProbe p, bool includeMinimized, bool includeHidden)
    {
        if (!p.IsVisible) return false;
        // A tool window has no taskbar button and is not something the user "has open" — unless
        // WS_EX_APPWINDOW forces one, which is how some apps make a tool window a real window.
        if ((p.ExStyle & WS_EX_TOOLWINDOW) != 0 && (p.ExStyle & WS_EX_APPWINDOW) == 0) return false;
        if (p.IsCloaked) return false;                       // UWP ghosts, other virtual desktops
        if (p.Bounds.Width <= 0 || p.Bounds.Height <= 0) return false;
        if (ShellChromeClasses.Contains(p.ClassName)) return false;
        if (!includeHidden && UiText.Sanitize(p.Title).Length == 0) return false;
        if (!includeMinimized && p.IsMinimized) return false;
        return true;
    }

    /// <summary>
    /// The inventory entry flagged active — the foreground window as the list sees it, so its
    /// ZOrder is real — or null when nothing is flagged (the foreground window was filtered out).
    /// Its own method because "first window" and "active window" coincide on a quiet desktop,
    /// and only a hand-built list can tell the two apart.
    /// </summary>
    internal static WindowInfo? ActiveOf(IReadOnlyList<WindowInfo> windows)
        => windows.FirstOrDefault(w => w.IsActive);

    /// <summary>Minimized wins over Maximized: a minimized window keeps WS_MAXIMIZE.</summary>
    internal static WindowState StateOf(WindowProbe p)
        => p.IsMinimized ? WindowState.Minimized
         : p.IsMaximized ? WindowState.Maximized
         : WindowState.Normal;

    /// <summary>Process name (with or without ".exe", case-insensitive) is a known browser. A-5 reuses this set.</summary>
    internal static bool IsBrowser(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return BrowserProcesses.Contains(name);
    }

    /// <summary>
    /// Filters <paramref name="zOrder"/> (EnumWindows order, index 0 = topmost) and projects the
    /// survivors onto <see cref="WindowInfo"/>, renumbering <c>ZOrder</c> over the kept windows.
    /// The title is sanitised here (A-13); the monitor is the one under the window's centre, so a
    /// window straddling a seam is reported once, and a minimized window parked off-screen is -1.
    /// </summary>
    internal static WindowInfo[] Build(
        IReadOnlyList<WindowProbe> zOrder,
        long foregroundHwnd,
        IReadOnlyList<MonitorInfo> monitors,
        bool includeMinimized,
        bool includeHidden)
    {
        var result = new List<WindowInfo>(zOrder.Count);
        foreach (var p in zOrder)
        {
            if (!Keep(p, includeMinimized, includeHidden)) continue;
            var b = p.Bounds;
            result.Add(new WindowInfo(
                Title: UiText.Sanitize(p.Title),
                Hwnd: p.Hwnd,
                Pid: p.Pid,
                ProcessName: p.ProcessName,
                State: StateOf(p),
                Bounds: b,
                ZOrder: result.Count,
                IsActive: p.Hwnd == foregroundHwnd,
                IsBrowser: IsBrowser(p.ProcessName),
                MonitorIndex: CursorMath.MonitorIndexOf(b.X + b.Width / 2, b.Y + b.Height / 2, monitors)));
        }
        return result.ToArray();
    }
}
