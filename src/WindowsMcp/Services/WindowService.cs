using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WindowService : IWindowService
{
    private const uint WM_CLOSE = 0x0010;
    private const uint MONITORINFOF_PRIMARY = 1u;

    /// <summary>
    /// A-12: the optional virtual-desktop service that fills <c>WindowInfo.DesktopId</c>.
    /// Optional so the 39 existing <c>new WindowService()</c> call sites (and any host that has
    /// not registered it) keep working with a null id.
    /// </summary>
    private readonly IVirtualDesktopService? _desktops;

    /// <summary>B-10: the user32 behind the foreground ladder; a fake in the unit tests.</summary>
    private readonly IForegroundNative _native;

    public WindowService(IVirtualDesktopService? desktops = null) : this(desktops, null) { }

    internal WindowService(IVirtualDesktopService? desktops, IForegroundNative? native)
    {
        _desktops = desktops;
        _native = native ?? Win32ForegroundNative.Instance;
    }

    /// <summary>
    /// B-10: the target is resolved through <see cref="WindowMatcher"/> (hwnd, else exact →
    /// substring → fuzzy over the inventory), so a partial title acts on the window it names; the
    /// action is validated before the inventory is read; nothing matched is a
    /// <see cref="KeyNotFoundException"/> naming the open windows.
    /// </summary>
    public async Task<WindowAction> ExecuteAsync(string action, string? title, long? hwnd = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var verb = action.ToLowerInvariant();
        if (verb is not ("minimize" or "maximize" or "restore" or "close"))
            throw new ArgumentException($"Unknown action '{action}'; expected minimize|maximize|restore|close");
        if (hwnd is null && string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A window action needs a title or an hwnd.", nameof(title));

        var match = WindowMatcher.Match(await ListAsync(true, false, ct), title, hwnd);
        var handle = ToHwnd(match.Window.Hwnd);

        switch (verb)
        {
            case "minimize":
                PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_MINIMIZE);
                break;
            case "maximize":
                PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                break;
            case "restore":
                PInvoke.ShowWindow(handle, SHOW_WINDOW_CMD.SW_RESTORE);
                break;
            case "close":
                // CloseWindow() actually minimizes; PostMessage WM_CLOSE performs a real close.
                PInvoke.PostMessage(handle, WM_CLOSE, default, default);
                break;
        }

        return new WindowAction(verb, match.Window.Title, true, match.Strategy, match.Score, match.Window.Hwnd);
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
        var windows = WindowFilter.Build(probes, HwndValue(foreground), monitors, includeMinimized, includeHidden);

        // A-12: which virtual desktop each listed window is on — asked only for the survivors, and
        // never allowed to cost the list itself.
        if (_desktops is not null)
        {
            for (int i = 0; i < windows.Length; i++)
            {
                string? id;
                try { id = await _desktops.GetWindowDesktopIdAsync(windows[i].Hwnd, ct); }
                catch (OperationCanceledException) { throw; }
                catch { id = null; }
                if (id is not null) windows[i] = windows[i] with { DesktopId = id };
            }
        }
        return windows;
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

    private static unsafe HWND ToHwnd(long hwnd) => new((void*)(nint)hwnd);

    /// <summary>
    /// B-10: match (hwnd, else exact → substring → fuzzy), then climb the ladder —
    /// <c>SetForegroundWindow</c>, <c>AttachThreadInput</c>, the ALT nudge — re-reading the
    /// foreground window after each step. The result says which step worked, or that none did.
    /// </summary>
    public async Task<ForegroundResult> BringToFrontAsync(string? title, long? hwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hwnd is null && string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Name the window: a title (exact, substring or fuzzy) or an hwnd from window list.", nameof(title));

        var match = WindowMatcher.Match(await ListAsync(true, false, ct), title, hwnd);
        return ForegroundLadder.Bring(match, _native);
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
            // MONITORINFOEXW: the same header plus the device name EnumDisplaySettings needs.
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);

            if (PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info))
            {
                var rc = info.monitorInfo.rcMonitor;
                var work = info.monitorInfo.rcWork;
                bool isPrimary = (info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0;
                int dpi = EffectiveDpiOf(handle);
                int orientation = OrientationOf(info.szDevice.ToString());

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
                    isPrimary,
                    // B-12: the detail. WorkArea is the monitor rect minus the taskbar/docked bars.
                    WorkArea: new Bounds(work.left, work.top, work.right - work.left, work.bottom - work.top),
                    Orientation: orientation,
                    EffectiveDpi: dpi,
                    Scale: dpi / 96.0));
            }
        }

        return Task.FromResult(results.ToArray());
    }

    /// <summary>B-12: <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>; 96 when Windows will not say.</summary>
    private static int EffectiveDpiOf(HMONITOR handle)
    {
        try
        {
            return PInvoke.GetDpiForMonitor(handle, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0
                ? (int)dpiX
                : 96;
        }
        catch { return 96; }
    }

    /// <summary>B-12: the display's rotation in degrees (0, 90, 180, 270) from its current mode; 0 when unknown.</summary>
    private static unsafe int OrientationOf(string deviceName)
    {
        try
        {
            var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
            if (!PInvoke.EnumDisplaySettings(deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode))
                return 0;
            return (int)mode.Anonymous1.Anonymous2.dmDisplayOrientation * 90;
        }
        catch { return 0; }
    }
}
