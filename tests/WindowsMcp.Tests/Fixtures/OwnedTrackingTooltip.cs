using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// PR #43 Z5: a real comctl32 tooltip, shown with no input and no activation, in the real-world
/// shape: OWNED by an application window. A tracking tooltip (<c>TTF_TRACK</c>) is positioned and
/// shown by messages (<c>TTM_TRACKPOSITION</c>, <c>TTM_TRACKACTIVATE</c>) instead of by hovering, and a
/// tooltip never takes activation. The host is a STATIC window with a button, shown without
/// activation at the bottom of the z-order; the tooltip window is created with the host as its
/// owner (hWndParent of a WS_POPUP window), so Win32's owner relation returns it for the host.
/// Built on its own thread with its own message pump, like <see cref="OwnedWindowFamily"/>.
/// </summary>
internal sealed class OwnedTrackingTooltip : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint TTS_ALWAYSTIP = 0x01;
    private const uint TTS_NOPREFIX = 0x02;
    private const uint TTF_TRACK = 0x0020;
    private const uint TTF_ABSOLUTE = 0x0080;
    private const uint WM_USER = 0x0400;
    private const uint TTM_TRACKACTIVATE = WM_USER + 17;
    private const uint TTM_TRACKPOSITION = WM_USER + 18;
    private const uint TTM_ADDTOOLW = WM_USER + 50;
    private const uint ICC_WIN95_CLASSES = 0x000000FF;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>TTTOOLINFOW up to lParam (the V2 size every comctl32 since 4.70 accepts).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TOOLINFO
    {
        public uint cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public UIntPtr uId;
        public RECT rect;
        public IntPtr hinst;
        public string lpszText;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, ref TOOLINFO lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ManualResetEventSlim _ready = new(false);

    public string Token { get; } = "wmcp-tip-" + Guid.NewGuid().ToString("N");
    public string HostTitle => Token + " host";
    public string HostButton => Token + " host button";
    public string TipText => Token + " saves the document";

    public IntPtr Host { get; private set; }
    public IntPtr Tooltip { get; private set; }

    /// <summary>Whether TTM_ADDTOOL accepted the tool (it answers TRUE).</summary>
    public bool ToolAdded { get; private set; }

    public bool TooltipVisible => Tooltip != IntPtr.Zero && IsWindowVisible(Tooltip);

    public OwnedTrackingTooltip()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-tracking-tooltip" };
        thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"{nameof(OwnedTrackingTooltip)}: the window thread did not create its windows within 10 s.");
    }

    private void Pump()
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_WIN95_CLASSES };
        InitCommonControlsEx(ref icc);

        Host = CreateWindowExW(0, "STATIC", HostTitle, WS_OVERLAPPEDWINDOW, 160, 160, 420, 220, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CreateWindowExW(0, "BUTTON", HostButton, WS_CHILD | WS_VISIBLE, 10, 10, 300, 30, Host, new IntPtr(301), IntPtr.Zero, IntPtr.Zero);
        ShowWindow(Host, SW_SHOWNOACTIVATE);
        SetWindowPos(Host, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Owned by the host: hWndParent of a WS_POPUP window is its owner.
        Tooltip = CreateWindowExW(WS_EX_TOPMOST, "tooltips_class32", null, WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP,
            0, 0, 0, 0, Host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Tooltip != IntPtr.Zero)
        {
            var ti = new TOOLINFO
            {
                cbSize = (uint)Marshal.SizeOf<TOOLINFO>(),
                uFlags = TTF_TRACK | TTF_ABSOLUTE,
                hwnd = Host,
                uId = new UIntPtr(1),
                lpszText = TipText,
            };
            ToolAdded = SendMessageW(Tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref ti) != IntPtr.Zero;
            // Below the host's button, inside the host's rectangle.
            SendMessageW(Tooltip, TTM_TRACKPOSITION, IntPtr.Zero, new IntPtr((260 << 16) | 200));
            SendMessageW(Tooltip, TTM_TRACKACTIVATE, new IntPtr(1), ref ti);
        }
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            if (!IsWindow(Host)) break;
        }
    }

    public void Dispose()
    {
        if (Host != IntPtr.Zero && IsWindow(Host))
        {
            PostMessageW(Host, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);   // destroys the owned tooltip with it
            for (int i = 0; i < 40 && IsWindow(Host); i++) Thread.Sleep(50);
        }
        _ready.Dispose();
    }
}
