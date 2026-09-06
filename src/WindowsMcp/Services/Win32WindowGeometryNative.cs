using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-9: the user32 behind <see cref="IWindowGeometryNative"/> — the only place a window is
/// moved. <see cref="WindowGeometry.Apply"/> decides what to send; this only sends it and reads
/// the rect back in virtual-desktop pixels, the same numbers A-1's inventory reports.
/// </summary>
internal sealed class Win32WindowGeometryNative : IWindowGeometryNative
{
    internal static Win32WindowGeometryNative Instance { get; } = new();

    private static unsafe HWND Handle(long hwnd) => new((void*)(nint)hwnd);

    public bool IsIconic(long hwnd) => PInvoke.IsIconic(Handle(hwnd));

    public bool IsZoomed(long hwnd) => PInvoke.IsZoomed(Handle(hwnd));

    public bool Restore(long hwnd) => PInvoke.ShowWindow(Handle(hwnd), SHOW_WINDOW_CMD.SW_RESTORE);

    public bool SetWindowPos(long hwnd, int x, int y, int width, int height, uint flags)
        => PInvoke.SetWindowPos(Handle(hwnd), HWND.Null, x, y, width, height, (SET_WINDOW_POS_FLAGS)flags);

    public Bounds GetRect(long hwnd)
    {
        if (!PInvoke.GetWindowRect(Handle(hwnd), out var rc))
            throw new KeyNotFoundException($"Window {hwnd} (0x{hwnd:X}) no longer exists.");
        return new Bounds(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
    }
}
