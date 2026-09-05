using System.Text.Json.Serialization;

namespace WindowsMcp.Abstractions.Models;

public record WindowAction(string Action, string? Title, bool Success);
public record MonitorInfo(int Index, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary);

/// <summary>
/// A-1: how a top-level window is showing. <see cref="Minimized"/> wins over
/// <see cref="Maximized"/> — a minimized window keeps its WS_MAXIMIZE style, so
/// <c>IsIconic</c> has to be asked first.
/// </summary>
// By name on the wire: the model reads "Minimized", not 1 (and upstream reports status names).
[JsonConverter(typeof(JsonStringEnumConverter<WindowState>))]
public enum WindowState { Normal, Minimized, Maximized }

/// <summary>
/// A-1: one user-visible top-level window. <paramref name="Bounds"/> is virtual-desktop pixels
/// (roadmap C1); <paramref name="ZOrder"/> is the position in the filtered list, 0 = topmost;
/// <paramref name="MonitorIndex"/> indexes <c>multi_monitor</c>'s inventory and is -1 when the
/// window's centre is on no monitor (a minimized window parked off-screen);
/// <paramref name="DesktopId"/> is reserved for A-12 and is null until then.
/// </summary>
public record WindowInfo(
    string Title,
    long Hwnd,
    int Pid,
    string ProcessName,
    WindowState State,
    Bounds Bounds,
    int ZOrder,
    bool IsActive,
    bool IsBrowser,
    int MonitorIndex,
    string? DesktopId = null);

/// <summary>
/// A-1: the raw Win32 facts about one top-level window, as read by the enumerator and judged by
/// the pure <c>WindowFilter</c>. Nothing here is sanitised or interpreted —
/// <paramref name="Title"/> is <c>GetWindowText</c> verbatim (null when the read failed).
/// </summary>
public record WindowProbe(
    long Hwnd,
    bool IsVisible,
    uint ExStyle,
    bool IsCloaked,
    Bounds Bounds,
    string? Title,
    string ClassName,
    bool IsMinimized,
    bool IsMaximized,
    int Pid,
    string ProcessName);

/// <summary>
/// A-12 (phase 1): one Windows virtual desktop. <paramref name="Id"/> is the desktop GUID in
/// lower-case dashed form with no braces (the same format <see cref="WindowInfo.DesktopId"/>
/// carries); <paramref name="Index"/> is the zero-based position in the registry's
/// <c>VirtualDesktopIDs</c> list; <paramref name="Name"/> is the user's name for the desktop,
/// or <c>Desktop {Index+1}</c> when none is stored.
/// </summary>
public record VirtualDesktopInfo(string Id, string Name, int Index, bool IsCurrent);
