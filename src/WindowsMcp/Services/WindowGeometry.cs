using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-9: the user32 calls a move/resize makes, behind a seam so the flag composition, the
/// minimised/maximised refusal, <c>restore_first</c> and the re-read of the outcome are
/// unit-testable with no desktop — the same shape <see cref="IForegroundNative"/> gave B-10.
/// </summary>
internal interface IWindowGeometryNative
{
    /// <summary><c>IsIconic</c>: the window is minimised.</summary>
    bool IsIconic(long hwnd);

    /// <summary><c>IsZoomed</c>: the window is maximised.</summary>
    bool IsZoomed(long hwnd);

    /// <summary><c>ShowWindow(SW_RESTORE)</c>.</summary>
    bool Restore(long hwnd);

    /// <summary><c>SetWindowPos(hwnd, null, x, y, cx, cy, flags)</c>; the return is not trusted.</summary>
    bool SetWindowPos(long hwnd, int x, int y, int width, int height, uint flags);

    /// <summary><c>GetWindowRect</c> in virtual-desktop pixels — the only source of the outcome.</summary>
    Bounds GetRect(long hwnd);
}

/// <summary>
/// B-9: move/resize applied to a window the matcher already chose, with the decisions kept out of
/// user32 so they can be tested. <c>SWP_NOZORDER|SWP_NOACTIVATE</c> is always set — a move must
/// not raise or focus the window — plus <c>SWP_NOMOVE</c> when no position was asked for and
/// <c>SWP_NOSIZE</c> when no size was.
/// </summary>
internal static class WindowGeometry
{
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>At least one of the four; a size must be positive. Runs before any window is touched.</summary>
    internal static void Validate(int? x, int? y, int? width, int? height)
    {
        if (x is null && y is null && width is null && height is null)
            throw new ArgumentException("Give something to change: x and/or y to move, width and/or height to resize.");
        if (width is <= 0) throw new ArgumentException($"width must be positive, got {width}", nameof(width));
        if (height is <= 0) throw new ArgumentException($"height must be positive, got {height}", nameof(height));
    }

    /// <summary>
    /// Validates, asks the window (not the inventory) whether it is minimised or maximised —
    /// refused naming the state unless <paramref name="restoreFirst"/> sends SW_RESTORE — then
    /// reads the rect, sends one <c>SetWindowPos</c> that never raises or activates the window
    /// (a half-given pair is filled from the current rect; a wholly absent pair becomes
    /// SWP_NOMOVE/SWP_NOSIZE), and reads the rect again: <c>After</c> is the outcome, whatever
    /// user32 returned.
    /// </summary>
    internal static WindowBoundsResult Apply(
        WindowMatch match, int? x, int? y, int? width, int? height, bool restoreFirst, IWindowGeometryNative native)
    {
        Validate(x, y, width, height);
        long hwnd = match.Window.Hwnd;

        string? state = native.IsIconic(hwnd) ? nameof(WindowState.Minimized)
            : native.IsZoomed(hwnd) ? nameof(WindowState.Maximized)
            : null;
        bool restored = false;
        if (state is not null)
        {
            if (!restoreFirst)
                throw new InvalidOperationException(
                    $"Window '{match.Window.Title}' is {state}; moving or resizing it would be undone by Windows. Pass restore_first:true to restore it first.");
            native.Restore(hwnd);
            restored = true;
        }

        var before = native.GetRect(hwnd);
        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        if (x is null && y is null) flags |= SWP_NOMOVE;
        if (width is null && height is null) flags |= SWP_NOSIZE;
        native.SetWindowPos(hwnd, x ?? before.X, y ?? before.Y, width ?? before.Width, height ?? before.Height, flags);
        var after = native.GetRect(hwnd);

        return new WindowBoundsResult(match.Window, before, after, match.Strategy, match.Score, restored);
    }
}
