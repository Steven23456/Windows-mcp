using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WindowService : IWindowService
{
    private const uint WM_CLOSE = 0x0010;
    private const uint MONITORINFOF_PRIMARY = 1u;

    public Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required for window action", nameof(title));

        HWND hwnd = PInvoke.FindWindow(null, title);
        bool found = hwnd != HWND.Null;

        if (found)
        {
            switch (action.ToLowerInvariant())
            {
                case "minimize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
                    break;
                case "maximize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                    break;
                case "restore":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
                    break;
                case "close":
                    // CloseWindow() actually minimizes; PostMessage WM_CLOSE performs a real close.
                    PInvoke.PostMessage(hwnd, WM_CLOSE, default, default);
                    break;
                default:
                    throw new ArgumentException($"Unknown action '{action}'; expected minimize|maximize|restore|close");
            }
        }

        return Task.FromResult(new WindowAction(action, title, found));
    }

    /// <summary>
    /// A-1: every top-level window in z-order (topmost first), filtered by <see cref="WindowFilter"/>.
    /// The enumerator only gathers facts (<see cref="Probe"/>); every judgement is in the pure
    /// filter so it can be tested without a desktop.
    /// </summary>
    public async Task<WindowInfo[]> ListAsync(bool includeMinimized = true, bool includeHidden = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var probes = new List<WindowProbe>();
        var processNames = new Dictionary<uint, string>();   // many windows per process; one lookup each
        PInvoke.EnumWindows((hwnd, _) =>
        {
            probes.Add(Probe(hwnd, processNames));
            return true;
        }, default);

        var foreground = PInvoke.GetForegroundWindow();
        var monitors = await EnumerateMonitorsAsync(ct);
        return WindowFilter.Build(probes, HwndValue(foreground), monitors, includeMinimized, includeHidden);
    }

    /// <summary>
    /// The foreground window as the inventory sees it — so its <c>ZOrder</c> is real, not a
    /// lie — or null when there is none or it is filtered out (the desktop, a cloaked window).
    /// </summary>
    public async Task<WindowInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        return WindowFilter.ActiveOf(await ListAsync(ct: ct));
    }

    /// <summary>HWND.Value is a raw pointer; this is the one place it is turned into a number.</summary>
    private static unsafe long HwndValue(HWND h) => (long)h.Value;

    /// <summary>The raw Win32 facts about one window. Every read is guarded: a window can die mid-enumeration.</summary>
    private static unsafe WindowProbe Probe(HWND hwnd, Dictionary<uint, string> processNames)
    {
        bool visible = PInvoke.IsWindowVisible(hwnd);
        uint exStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        uint cloakedFlags = 0;
        bool cloaked = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloakedFlags, sizeof(uint)).Succeeded
            && cloakedFlags != 0;

        var bounds = new Bounds(0, 0, 0, 0);
        if (PInvoke.GetWindowRect(hwnd, out var rc))
            bounds = new Bounds(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);

        string? title = null;
        int len = PInvoke.GetWindowTextLength(hwnd);
        if (len > 0)
        {
            Span<char> buf = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
            int n = PInvoke.GetWindowText(hwnd, buf);
            title = new string(buf[..Math.Max(0, n)]);
        }
        else
        {
            title = "";
        }

        Span<char> cls = stackalloc char[256];
        int clsLen = PInvoke.GetClassName(hwnd, cls);
        string className = clsLen > 0 ? new string(cls[..clsLen]) : "";

        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (!processNames.TryGetValue(pid, out var processName))
        {
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = ""; }   // gone, or access denied (a protected process)
            processNames[pid] = processName;
        }

        return new WindowProbe(
            Hwnd: HwndValue(hwnd),
            IsVisible: visible,
            ExStyle: exStyle,
            IsCloaked: cloaked,
            Bounds: bounds,
            Title: title,
            ClassName: className,
            IsMinimized: PInvoke.IsIconic(hwnd),
            IsMaximized: PInvoke.IsZoomed(hwnd),
            Pid: (int)pid,
            ProcessName: processName);
    }

    public Task<bool> SwitchToAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        HWND hwnd = PInvoke.FindWindow(null, title);
        if (hwnd == HWND.Null)
            return Task.FromResult(false);

        bool ok = PInvoke.SetForegroundWindow(hwnd);
        return Task.FromResult(ok);
    }

    public Task<int> LaunchAsync(string appName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = appName,
            UseShellExecute = true
        };

        // Dispose our wrapper handle; the launched app keeps running independently.
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch '{appName}'");

        return Task.FromResult(process.Id);
    }

    public unsafe Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var monitors = new List<HMONITOR>();

        PInvoke.EnumDisplayMonitors(default, null,
            (hMonitor, hdcMonitor, lprcMonitor, lParam) =>
            {
                monitors.Add(hMonitor);
                return true;
            },
            default);

        var results = new List<MonitorInfo>();
        foreach (var handle in monitors)
        {
            var info = new MONITORINFO
            {
                cbSize = (uint)sizeof(MONITORINFO)
            };

            if (PInvoke.GetMonitorInfo(handle, ref info))
            {
                var rc = info.rcMonitor;
                bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

                // Index = position in the returned array, not the enumeration counter: a failed
                // GetMonitorInfo must not leave a gap, because screenshot/ocr 'display' selects
                // by position and reports Index — the two numberings have to be one numbering.
                int position = results.Count;
                results.Add(new MonitorInfo(
                    position,
                    $"Monitor{position}",
                    rc.left,
                    rc.top,
                    rc.right - rc.left,
                    rc.bottom - rc.top,
                    isPrimary));
            }
        }

        return Task.FromResult(results.ToArray());
    }
}
