using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WindowsMcp.Services;

/// <summary>
/// B-10: the user32 behind <see cref="IForegroundNative"/> — the only place the foreground
/// ladder touches Windows. Every call takes the window handle the matcher chose; the ladder
/// decides the order and reads <see cref="GetForegroundWindow"/> for the truth.
/// </summary>
internal sealed class Win32ForegroundNative : IForegroundNative
{
    internal static Win32ForegroundNative Instance { get; } = new();

    public unsafe bool IsIconic(long hwnd) => PInvoke.IsIconic(new HWND((void*)(nint)hwnd));

    public unsafe bool Restore(long hwnd) => PInvoke.ShowWindow(new HWND((void*)(nint)hwnd), SHOW_WINDOW_CMD.SW_RESTORE);

    public unsafe bool SetForegroundWindow(long hwnd) => PInvoke.SetForegroundWindow(new HWND((void*)(nint)hwnd));

    public unsafe long GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public unsafe bool AttachThreadInput(long hwnd, bool attach)
    {
        uint target = PInvoke.GetWindowThreadProcessId(new HWND((void*)(nint)hwnd), null);
        if (target == 0) return false;
        uint self = PInvoke.GetCurrentThreadId();
        if (target == self) return false;   // attaching a thread to itself is an error, and pointless
        return PInvoke.AttachThreadInput(self, target, attach);
    }

    public unsafe bool BringWindowToTop(long hwnd) => PInvoke.BringWindowToTop(new HWND((void*)(nint)hwnd));

    /// <summary>
    /// The documented last resort: a synthetic ALT press makes the shell treat our process as
    /// having received input, which lifts the foreground lock for the next SetForegroundWindow.
    /// </summary>
    public void AltNudge()
    {
        PInvoke.keybd_event((byte)VIRTUAL_KEY.VK_MENU, 0, 0, 0);
        PInvoke.keybd_event((byte)VIRTUAL_KEY.VK_MENU, 0, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
    }
}
