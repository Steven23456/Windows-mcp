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

    [McpServerTool, Description("Inspect or act on top-level windows (parity A-1). actions: list (every user-visible window, z-order topmost first: ZOrder 0 = frontmost), active (the foreground window, or {\"found\":false}), desktops (the virtual desktops: {\"current\":{Id,Name,Index,IsCurrent}|null,\"all\":[...]}; Index is the registry's order, Name is the user's name or 'Desktop N'), minimize|maximize|restore|close (need 'title' — matched exact, then substring, then fuzzy — or 'hwnd', which wins; the result carries the matched Title, Hwnd, MatchStrategy and Score; no match lists the open windows). list/active/desktops ignore 'title' and 'hwnd'. Fields: Title (sanitised), Hwnd (the window handle; pass it as 'hwnd' to this tool's acting actions, switch_to_window and focus for a precise target), Pid/ProcessName, State (Normal|Minimized|Maximized), Bounds in virtual-desktop pixels, IsActive (the foreground window), IsBrowser (chrome/msedge/firefox/brave/opera/vivaldi), MonitorIndex into multi_monitor's list (-1 = on no monitor, e.g. minimized), DesktopId (the virtual desktop the window is on, lower-case GUID; null when unknown).")]
    public async Task<string> Window(
        [Description("list | active | desktops | minimize | maximize | restore | close")] string action,
        [Description("Window title to act on, matched exact then substring then fuzzy; minimize/maximize/restore/close need it or hwnd; ignored by list/active/desktops")] string? title = null,
        [Description("list: include minimized windows (default true)")] bool include_minimized = true,
        [Description("list: include windows with no title (default false)")] bool include_hidden = false,
        [Description("Window handle to act on, as reported by action:list; an alternative to 'title'")] long? hwnd = null)
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
            case "minimize" or "maximize" or "restore" or "close":
                // Validated here, before the service, so a bad call never touches a window.
                if (hwnd is null && string.IsNullOrWhiteSpace(title))
                    throw new ArgumentException($"'{action}' needs a title or an hwnd; only list, active and desktops work without one");
                return JsonSerializer.Serialize(await _window.ExecuteAsync(action, title, hwnd));
            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|active|desktops|minimize|maximize|restore|close");
        }
    }

    [McpServerTool, Description("Bring a window to the foreground. Name it by title — matched exact, then substring, then fuzzy (score 70+), so 'notepad' finds 'Untitled - Notepad' — or by hwnd from window list, which wins over title. A minimised window is restored first. Windows refuses a plain SetForegroundWindow to a background process, so the tool climbs a ladder (SetForegroundWindow, AttachThreadInput, an ALT nudge) and re-reads the foreground window after each step. Returns {Window, MatchStrategy (exact|substring|fuzzy|hwnd), Score, Restored, Strategy (the step that worked, null when none did), Success}. No match lists the open windows.")]
    public async Task<string> SwitchToWindow(
        [Description("Window title: exact, substring or fuzzy match, case-insensitive")] string? title = null,
        [Description("Window handle from window list; wins over title")] long? hwnd = null,
        CancellationToken ct = default)
        => JsonSerializer.Serialize(await BringToFrontAsync(title, hwnd, ct));

    [McpServerTool, Description("Launch an application by name or path. Uses ShellExecute so Start Menu shortcuts and PATH are resolved.")]
    public async Task<string> Launch(
        [Description("Application name or executable path to launch")] string app_name)
    {
        int pid = await _window.LaunchAsync(app_name);
        return $"launched (pid={pid})";
    }

    [McpServerTool, Description("Set keyboard focus to a window (alias for switch_to_window): title matched exact, then substring, then fuzzy, or hwnd from window list; restores a minimised window; climbs the same SetForegroundWindow/AttachThreadInput/ALT-nudge ladder and returns the same {Window, MatchStrategy, Score, Restored, Strategy, Success} result.")]
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

    [McpServerTool, Description("Enumerate all connected monitors. Each entry: Index (what screenshot/ocr 'display' selects), DeviceName, X/Y/Width/Height in virtual-desktop pixels, IsPrimary, WorkArea (the monitor minus the taskbar and docked bars), Orientation (0|90|180|270), EffectiveDpi and Scale (EffectiveDpi/96: 1.5 on a 150% display).")]
    public async Task<string> MultiMonitor()
    {
        var monitors = await _window.EnumerateMonitorsAsync();
        return JsonSerializer.Serialize(monitors);
    }
}
