using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// E-5 round 2: the window shapes the filtered inventory (<c>WindowFilter.Keep</c>) and the UIA parent
/// walk disagree about, all owned by this process and built on one thread with its own message pump,
/// shown without activation - so nothing is clicked, typed or focused and the foreground is not
/// taken. Same pattern as <see cref="OwnedControlsWindow"/> (built-in STATIC parents, BUTTON
/// children).
/// <list type="bullet">
/// <item><see cref="Host"/>: a normal titled window - in the inventory.</item>
/// <item><see cref="Owned"/>: a titled top-level window OWNED by the host (a dialog), beside it.
/// GetAncestor(GA_ROOT) of its button is the owned window, not the host.</item>
/// <item><see cref="Untitled"/>: a normal window with no title - filtered out of the inventory.</item>
/// <item><see cref="Tool"/>: a titled WS_EX_TOOLWINDOW - filtered out of the inventory.</item>
/// <item><see cref="Popup"/>: an untitled, UNOWNED, non-activating WS_POPUP tool window of the
/// same process holding two buttons - the shape of an open menu or a combo dropdown - filtered out
/// of the inventory. Unowned on purpose: UIA lists an OWNED top-level window under its owner (so
/// a walk of the owner already contains it), while an unowned popup is a separate child of the
/// desktop, which is the case R3 exists for.</item>
/// </list>
/// Every name starts with <see cref="Token"/>.
/// </summary>
internal sealed class OwnedWindowFamily : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint BS_PUSHBUTTON = 0x0;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ManualResetEventSlim _ready = new(false);

    public string Token { get; } = "wmcp-fam-" + Guid.NewGuid().ToString("N");

    public string HostTitle => Token + " host";
    public string OwnedTitle => Token + " owned";
    public string ToolTitle => Token + " tool";

    public string HostButton => Token + " host button";
    public string OwnedButton => Token + " owned button";
    public string UntitledButton => Token + " untitled button";
    public string ToolButton => Token + " tool button";
    public string PopupItemA => Token + " popup item A";
    public string PopupItemB => Token + " popup item B";

    // PR #43 P2: a second owner with an owned dialog that reaches OUTSIDE it.
    public string Owner2Title => Token + " owner2";
    public string Owned2Title => Token + " owned2";
    public string Owner2Button => Token + " owner2 button";
    public string Owned2InsideButton => Token + " owned2 inside button";
    public string Owned2OutsideButton => Token + " owned2 outside button";

    public IntPtr Host { get; private set; }
    public IntPtr Owned { get; private set; }
    public IntPtr Untitled { get; private set; }
    public IntPtr Tool { get; private set; }
    public IntPtr Popup { get; private set; }
    public IntPtr Owner2 { get; private set; }
    public IntPtr Owned2 { get; private set; }

    public IntPtr HostButtonHandle { get; private set; }
    public IntPtr OwnedButtonHandle { get; private set; }

    public OwnedWindowFamily()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-window-family" };
        thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"{nameof(OwnedWindowFamily)}: the window thread did not create its windows within 10 s.");
    }

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    private IntPtr[] All => [Popup, Owned, Host, Untitled, Tool, Owned2, Owner2];

    private IntPtr Button(IntPtr parent, string text, int y, int id, int width = 300) =>
        CreateWindowExW(0, "BUTTON", text, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 10, y, width, 30, parent, new IntPtr(id), IntPtr.Zero, IntPtr.Zero);

    private void Pump()
    {
        Host = CreateWindowExW(0, "STATIC", HostTitle, WS_OVERLAPPEDWINDOW, 140, 140, 420, 240, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        HostButtonHandle = Button(Host, HostButton, 10, 201);
        // Owned: hWndParent of a non-child window is its OWNER.
        Owned = CreateWindowExW(0, "STATIC", OwnedTitle, WS_OVERLAPPEDWINDOW, 580, 140, 360, 200, Host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        OwnedButtonHandle = Button(Owned, OwnedButton, 10, 202);
        Untitled = CreateWindowExW(0, "STATIC", "", WS_OVERLAPPEDWINDOW, 140, 420, 360, 160, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Button(Untitled, UntitledButton, 10, 203);
        Tool = CreateWindowExW(WS_EX_TOOLWINDOW, "STATIC", ToolTitle, WS_OVERLAPPEDWINDOW, 580, 420, 360, 160, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Button(Tool, ToolButton, 10, 204);
        // No owner: see the class note.
        Popup = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "STATIC", "", WS_POPUP | WS_BORDER, 200, 230, 320, 90, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Button(Popup, PopupItemA, 5, 205);
        Button(Popup, PopupItemB, 45, 206);
        // Owner2 (x0..x0+300, 140..340) and Owned2 (x0+40..x0+320, 250..470), owned by Owner2. The
        // inside button (client y 5) lies inside both; the outside button (client y 120) lies below
        // Owner2's rectangle, so a walk of Owner2 clips it away. Kept on the primary display.
        int x0 = Math.Max(0, Math.Min(960, GetSystemMetrics(0) - 340));
        Owner2 = CreateWindowExW(0, "STATIC", Owner2Title, WS_OVERLAPPEDWINDOW, x0, 140, 300, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Button(Owner2, Owner2Button, 10, 207, 250);
        Owned2 = CreateWindowExW(0, "STATIC", Owned2Title, WS_OVERLAPPEDWINDOW, x0 + 40, 250, 280, 220, Owner2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Button(Owned2, Owned2InsideButton, 5, 208, 250);
        Button(Owned2, Owned2OutsideButton, 120, 209, 250);

        foreach (var w in new[] { Host, Untitled, Tool, Owner2 })
        {
            ShowWindow(w, SW_SHOWNOACTIVATE);
            SetWindowPos(w, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        ShowWindow(Owned, SW_SHOWNOACTIVATE);
        ShowWindow(Popup, SW_SHOWNOACTIVATE);
        ShowWindow(Owned2, SW_SHOWNOACTIVATE);
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            if (!All.Any(Exists)) break;   // STATIC posts no WM_QUIT when it is closed
        }
    }

    /// <summary>Closes one of the family's windows and waits until it is really gone.</summary>
    public void Close(IntPtr hwnd)
    {
        if (!Exists(hwnd)) return;
        PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40 && Exists(hwnd); i++) Thread.Sleep(50);
    }

    public void Dispose()
    {
        foreach (var w in All) Close(w);
        _ready.Dispose();
    }
}
