using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

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

    [McpServerTool, Description("Inspect or act on top-level windows (parity A-1). actions: list (every user-visible window, z-order topmost first: ZOrder 0 = frontmost), active (the foreground window, or {\"found\":false}), desktops (the virtual desktops: {\"current\":{Id,Name,Index,IsCurrent}|null,\"all\":[...]}; Index is the registry's order, Name is the user's name or 'Desktop N'), minimize|maximize|restore|close (need 'title'). list/active/desktops ignore 'title'. Fields: Title (sanitised), Hwnd (the window handle; no tool takes it yet — target windows by Title), Pid/ProcessName, State (Normal|Minimized|Maximized), Bounds in virtual-desktop pixels, IsActive (the foreground window), IsBrowser (chrome/msedge/firefox/brave/opera/vivaldi), MonitorIndex into multi_monitor's list (-1 = on no monitor, e.g. minimized), DesktopId (the virtual desktop the window is on, lower-case GUID; null when unknown).")]
    public async Task<string> Window(
        [Description("list | active | desktops | minimize | maximize | restore | close")] string action,
        [Description("Window title to act on (exact match); required for minimize/maximize/restore/close, ignored by list/active")] string? title = null,
        [Description("list: include minimized windows (default true)")] bool include_minimized = true,
        [Description("list: include windows with no title (default false)")] bool include_hidden = false)
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
                if (string.IsNullOrWhiteSpace(title))
                    throw new ArgumentException($"'{action}' needs a title; only list and active work without one");
                return JsonSerializer.Serialize(await _window.ExecuteAsync(action, title));
            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|active|desktops|minimize|maximize|restore|close");
        }
    }

    [McpServerTool, Description("Switch focus to a window identified by its title using SetForegroundWindow.")]
    public async Task<string> SwitchToWindow(
        [Description("Window title to bring to foreground")] string title)
    {
        bool ok = await _window.SwitchToAsync(title);
        return ok ? $"switched to '{title}'" : $"window '{title}' not found";
    }

    [McpServerTool, Description("Launch an application by name or path. Uses ShellExecute so Start Menu shortcuts and PATH are resolved.")]
    public async Task<string> Launch(
        [Description("Application name or executable path to launch")] string app_name)
    {
        int pid = await _window.LaunchAsync(app_name);
        return $"launched (pid={pid})";
    }

    [McpServerTool, Description("Set keyboard focus to a window identified by title (alias for switch_to_window).")]
    public async Task<string> Focus(
        [Description("Window title to focus")] string title)
    {
        bool ok = await _window.SwitchToAsync(title);
        return ok ? $"focused '{title}'" : $"window '{title}' not found";
    }

    [McpServerTool, Description("Enumerate all connected monitors and return geometry information.")]
    public async Task<string> MultiMonitor()
    {
        var monitors = await _window.EnumerateMonitorsAsync();
        return JsonSerializer.Serialize(monitors);
    }
}
