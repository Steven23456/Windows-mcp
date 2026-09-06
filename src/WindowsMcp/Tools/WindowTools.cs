using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class WindowTools
{
    private readonly IWindowService _window;

    private readonly IVirtualDesktopService _desktops;

    public WindowTools(IWindowService window, IVirtualDesktopService desktops)
    {
        _window = window;
        _desktops = desktops;
    }

    [McpServerTool(Title = "Window action", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Inspect or act on top-level windows (parity A-1). actions: list (every user-visible window, z-order topmost first: ZOrder 0 = frontmost), active (the foreground window, or {\"found\":false}), desktops (the virtual desktops: {\"current\":{Id,Name,Index,IsCurrent}|null,\"all\":[...]}; Index is the registry's order, Name is the user's name or 'Desktop N'), minimize|maximize|restore|close (need 'title' — matched exact, then substring, then fuzzy — or 'hwnd', which wins; the result carries the matched Title, Hwnd, MatchStrategy and Score; no match lists the open windows). move|resize|set_bounds (parity B-9: move needs x,y; resize needs width,height; set_bounds needs all four; same title/hwnd targeting, or the foreground window when neither is given; a minimized or maximized window is refused unless restore_first:true; returns {Window, Before, After (the rect it actually ended up with), MatchStrategy, Score, Restored}). list/active/desktops ignore 'title' and 'hwnd'. Fields: Title (sanitised), Hwnd (the window handle; pass it as 'hwnd' to this tool's acting actions, switch_to_window and focus for a precise target), Pid/ProcessName, State (Normal|Minimized|Maximized), Bounds in virtual-desktop pixels, IsActive (the foreground window), IsBrowser (chrome/msedge/firefox/brave/opera/vivaldi), MonitorIndex into multi_monitor's list (-1 = on no monitor, e.g. minimized), DesktopId (the virtual desktop the window is on, lower-case GUID; null when unknown).")]
    public async Task<string> Window(
        [Description("list | active | desktops | minimize | maximize | restore | close | move | resize | set_bounds")] string action,
        [Description("Window title to act on, matched exact then substring then fuzzy; minimize/maximize/restore/close need it or hwnd; move/resize/set_bounds use it too, or act on the foreground window when neither is given; ignored by list/active/desktops")] string? title = null,
        [Description("list: include minimized windows (default true)")] bool include_minimized = true,
        [Description("list: include windows with no title (default false)")] bool include_hidden = false,
        [Description("Window handle to act on, as reported by action:list; an alternative to 'title'")] long? hwnd = null,
        [Description("move|set_bounds: new left edge in virtual-desktop pixels")] int? x = null,
        [Description("move|set_bounds: new top edge in virtual-desktop pixels")] int? y = null,
        [Description("resize|set_bounds: new width in pixels")] int? width = null,
        [Description("resize|set_bounds: new height in pixels")] int? height = null,
        [Description("move|resize|set_bounds: restore a minimized or maximized window first instead of refusing it (default false)")] bool restore_first = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                return JsonSerializer.Serialize(await _window.ListAsync(include_minimized, include_hidden));
            case "active":
                var active = await _window.GetActiveAsync();
                return active is null ? "{\"found\":false}" : JsonSerializer.Serialize(active);
            case "desktops":
                // One read, one truth: 'current' is the flagged entry of the same list.
                var all = await _desktops.ListAsync();
                return JsonSerializer.Serialize(new { current = all.FirstOrDefault(d => d.IsCurrent), all });
            case "move":
                if (x is null || y is null)
                    throw new ArgumentException("'move' needs x and y (the new top-left, in virtual-desktop pixels); use resize for a size or set_bounds for both.");
                return JsonSerializer.Serialize(await _window.SetBoundsAsync(title, hwnd, x, y, null, null, restore_first));
            case "resize":
                if (width is null || height is null)
                    throw new ArgumentException("'resize' needs width and height; use move for a position or set_bounds for both.");
                return JsonSerializer.Serialize(await _window.SetBoundsAsync(title, hwnd, null, null, width, height, restore_first));
            case "set_bounds":
                if (x is null || y is null || width is null || height is null)
                    throw new ArgumentException("'set_bounds' needs all four of x, y, width and height; use move or resize for one pair.");
                return JsonSerializer.Serialize(await _window.SetBoundsAsync(title, hwnd, x, y, width, height, restore_first));
            case "minimize" or "maximize" or "restore" or "close":
                // Validated here, before the service, so a bad call never touches a window.
                if (hwnd is null && string.IsNullOrWhiteSpace(title))
                    throw new ArgumentException($"'{action}' needs a title or an hwnd; only list, active and desktops work without one");
                return JsonSerializer.Serialize(await _window.ExecuteAsync(action, title, hwnd));
            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|active|desktops|minimize|maximize|restore|close|move|resize|set_bounds");
        }
    }

    [McpServerTool(Title = "Switch to window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Bring a window to the foreground. Name it by title — matched exact, then substring, then fuzzy (score 70+), so 'notepad' finds 'Untitled - Notepad' — or by hwnd from window list, which wins over title. A minimised window is restored first. Windows refuses a plain SetForegroundWindow to a background process, so the tool climbs a ladder (SetForegroundWindow, AttachThreadInput, an ALT nudge) and re-reads the foreground window after each step. Returns {Window, MatchStrategy (exact|substring|fuzzy|hwnd), Score, Restored, Strategy (the step that worked, null when none did), Success}. No match lists the open windows.")]
    public async Task<string> SwitchToWindow(
        [Description("Window title: exact, substring or fuzzy match, case-insensitive")] string? title = null,
        [Description("Window handle from window list; wins over title")] long? hwnd = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await BringToFrontAsync(title, hwnd, ct));

    [McpServerTool(Title = "Launch app", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true), Description("Launch an application by its Start Menu name, its AUMID's display name, or a path. A path, or an explicit .exe name found on PATH, is started outright; anything else is resolved against an in-process catalog of Start Menu shortcuts and packaged (Store/MSIX) apps, matched exact, then by prefix, then fuzzy (score 70+), so launch('calc') opens Calculator and launch('vs code') opens Visual Studio Code. With wait_for_window the window inventory is polled up to timeout_ms for a window of the launched process, or a new window whose title matches the app; a timeout is reported as windowDetected:false with the pid, not as an error. Returns {MatchedName, Kind (shortcut|packaged|path), Score, Strategy (path|exact|prefix|fuzzy), Pid, Hwnd, Title, WindowDetected}. A name that matches nothing lists the five nearest apps with their scores.")]
    public async Task<string> Launch(
        [Description("Application name (Start Menu or Store display name), or an executable path")] string app_name,
        [Description("Wait for the app's window to appear and report its Hwnd/Title (default true)")] bool wait_for_window = true,
        [Description("How long to wait for the window, in milliseconds: 1..60000 (default 10000)")] int timeout_ms = 10000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app_name))
            throw new ArgumentException("app_name is required: a Start Menu name, a Store app's display name, or a path.", nameof(app_name));
        if (timeout_ms is < 1 or > 60000)
            throw new ArgumentException($"timeout_ms must be between 1 and 60000, got {timeout_ms}", nameof(timeout_ms));
        return JsonSerializer.Serialize(await _window.LaunchAsync(app_name, wait_for_window, timeout_ms, ct));
    }

    [McpServerTool(Title = "Focus element", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Set keyboard focus to a window (alias for switch_to_window): title matched exact, then substring, then fuzzy, or hwnd from window list; restores a minimised window; climbs the same SetForegroundWindow/AttachThreadInput/ALT-nudge ladder and returns the same {Window, MatchStrategy, Score, Restored, Strategy, Success} result.")]
    public async Task<string> Focus(
        [Description("Window title: exact, substring or fuzzy match, case-insensitive")] string? title = null,
        [Description("Window handle from window list; wins over title")] long? hwnd = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await BringToFrontAsync(title, hwnd, ct));

    private Task<ForegroundResult> BringToFrontAsync(string? title, long? hwnd, CancellationToken ct)
    {
        // Validated here so a call that names nothing never reads the inventory.
        if (hwnd is null && string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Give a title (exact, substring or fuzzy) or an hwnd from window list.");
        return _window.BringToFrontAsync(title, hwnd, ct);
    }

    [McpServerTool(Title = "List monitors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Enumerate all connected monitors. Each entry: Index (what screenshot/ocr 'display' selects), DeviceName, X/Y/Width/Height in virtual-desktop pixels, IsPrimary, WorkArea (the monitor minus the taskbar and docked bars), Orientation (0|90|180|270), EffectiveDpi and Scale (EffectiveDpi/96: 1.5 on a 150% display).")]
    public async Task<string> MultiMonitor()
    {
        var monitors = await _window.EnumerateMonitorsAsync();
        return JsonSerializer.Serialize(monitors);
    }
}
