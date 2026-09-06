using Windows.Win32;
using Windows.Win32.Foundation;

namespace WindowsMcp.Services;

/// <summary>
/// C-3: the production <see cref="IProcessWindowNative"/>. Only <em>visible</em> top-level
/// windows count — a hidden helper window would swallow the <c>WM_CLOSE</c> and make a windowless
/// process look like one that was asked to close.
/// </summary>
internal sealed class Win32ProcessWindowNative : IProcessWindowNative
{
    internal static Win32ProcessWindowNative Instance { get; } = new();

    private const uint WM_CLOSE = 0x0010;

    public unsafe long[] TopLevelWindowsOf(int pid)
    {
        var handles = new List<long>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            uint owner = 0;
            PInvoke.GetWindowThreadProcessId(hwnd, &owner);
            if (owner == (uint)pid && PInvoke.IsWindowVisible(hwnd))
                handles.Add((long)(nint)hwnd.Value);
            return true;
        }, default);
        return handles.ToArray();
    }

    public bool PostClose(long hwnd) =>
        PInvoke.PostMessage(new HWND((nint)hwnd), WM_CLOSE, default, default);
}
