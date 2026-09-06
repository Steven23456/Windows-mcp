using System.Text.Json.Serialization;

namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// The outcome of <c>window(action: minimize|maximize|restore|close)</c>.
/// <paramref name="Title"/> is the title of the window that was actually acted on (B-10: the
/// matcher's pick, which need not equal the string the caller sent);
/// <paramref name="MatchStrategy"/>/<paramref name="Score"/>/<paramref name="Hwnd"/> say how it
/// was found. The three trailing fields default so pre-B-10 constructions still compile.
/// </summary>
public record WindowAction(
    string Action,
    string? Title,
    bool Success,
    string? MatchStrategy = null,
    int Score = 0,
    long Hwnd = 0);

/// <summary>
/// One monitor. The first seven fields are A-8's and are unchanged; B-12 appends the detail
/// <c>multi_monitor</c> reports and nothing else consumes:
/// <paramref name="WorkArea"/> is <c>GetMonitorInfo.rcWork</c> (the desktop minus the taskbar and
/// any appbars) in virtual-desktop pixels, <paramref name="Orientation"/> is the display rotation
/// in degrees (0|90|180|270), <paramref name="EffectiveDpi"/> is
/// <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c> and <paramref name="Scale"/> is
/// <c>EffectiveDpi / 96.0</c>. The four are trailing and defaulted so every existing
/// construction and A-8's region maths are untouched.
/// </summary>
public record MonitorInfo(
    int Index,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    Bounds? WorkArea = null,
    int Orientation = 0,
    int EffectiveDpi = 96,
    double Scale = 1.0);

/// <summary>
/// B-10: the outcome of bringing a window to the foreground.
/// <paramref name="Window"/> is the inventory entry that was targeted;
/// <paramref name="MatchStrategy"/>/<paramref name="Score"/> are the matcher's verdict
/// (<c>exact|substring|fuzzy|hwnd</c>);
/// <paramref name="Restored"/> is true when the window was minimised and SW_RESTORE was sent;
/// <paramref name="Strategy"/> names the step of the ladder that actually worked
/// (<c>SetForegroundWindow|AttachThreadInput|AltNudge</c>), null when none did; and
/// <paramref name="Success"/> is re-read from <c>GetForegroundWindow</c> after the attempt
/// (roadmap C11) — never assumed from a return code.
/// </summary>
public record ForegroundResult(
    WindowInfo Window,
    string MatchStrategy,
    int Score,
    bool Restored,
    string? Strategy,
    bool Success);

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
/// <paramref name="DesktopId"/> is the virtual desktop the window is on (lower-case GUID, A-12), null when Windows does not say.
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
