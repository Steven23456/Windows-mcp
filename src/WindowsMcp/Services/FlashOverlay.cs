using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-14: the glow a person at the machine sees around what the agent just captured. A layered,
/// click-through, top-most, non-activating tool window on its own STA thread, painted with
/// <see cref="FlashGlow"/> through <c>UpdateLayeredWindow</c>, hidden again after the duration
/// (or by the next capture, which hides it first so it is never in a picture). Every Win32
/// failure — no interactive window station, a class that will not register — is a silent no-op
/// with <see cref="IsVisible"/> false: the glow is a courtesy, never a reason a screenshot fails.
/// </summary>
public sealed class FlashOverlay : IFlashOverlay, IDisposable
{
    private const string ClassName = "WindowsMcpFlash";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private Thread? _thread;
    private Timer? _hideTimer;
    private volatile bool _visible;
    private volatile bool _disposed;

    // Thread-affine state: only the overlay thread touches these.
    private HWND _hwnd;
    private WNDPROC? _wndProc;   // kept alive: Win32 holds a raw pointer to it
    private bool _classRegistered;

    public bool IsVisible => _visible;

    public void Show(ScreenRegion rect, TimeSpan duration)
    {
        if (_disposed) return;
        var window = FlashGlow.WindowRect(rect);
        bool ok = false;
        Run(() => ok = ShowOnThread(window));
        _visible = ok;
        if (!ok) return;

        lock (_gate)
        {
            _hideTimer?.Dispose();
            _hideTimer = new Timer(_ => Hide(), null, duration, Timeout.InfiniteTimeSpan);
        }
    }

    public void Hide()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _hideTimer?.Dispose();
            _hideTimer = null;
        }
        if (_thread is null) { _visible = false; return; }
        Run(HideOnThread);
        _visible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _hideTimer?.Dispose(); _hideTimer = null; }
        _visible = false;
        if (_thread is { } t)
        {
            try
            {
                _work.Add(() =>
                {
                    HideOnThread();
                    if (!_hwnd.IsNull) { PInvoke.DestroyWindow(_hwnd); _hwnd = default; }
                    // The class stays registered for the process's lifetime: a later instance reuses it.
                });
                _work.CompleteAdding();
                t.Join(CallTimeout);
            }
            catch { /* tearing down: nothing left to report to */ }
        }
        _work.Dispose();
    }

    /// <summary>Runs <paramref name="action"/> on the overlay thread and waits for it (bounded). Exceptions are swallowed.</summary>
    private void Run(Action action)
    {
        EnsureThread();
        using var done = new ManualResetEventSlim(false);
        try
        {
            _work.Add(() =>
            {
                try { action(); } catch { /* silent no-op by contract */ }
                finally { done.Set(); }
            });
            done.Wait(CallTimeout);
        }
        catch { /* queue completed (disposing): no-op */ }
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        lock (_gate)
        {
            if (_thread is not null) return;
            var t = new Thread(Loop) { IsBackground = true, Name = "WindowsMcp-Flash" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            t.Start();
        }
    }

    /// <summary>The overlay thread: queued actions plus a non-blocking message pump, so the window stays responsive.</summary>
    private void Loop()
    {
        try
        {
            while (!_work.IsCompleted)
            {
                if (_work.TryTake(out var action, 20))
                {
                    try { action(); } catch { /* per-action failures are the action's problem */ }
                }
                Pump();
            }
        }
        catch { /* queue disposed under us: done */ }
    }

    private static unsafe void Pump()
    {
        MSG msg;
        while (PInvoke.PeekMessage(&msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    private static HINSTANCE GetInstance() => new(PInvoke.GetModuleHandle((string?)null).DangerousGetHandle());

    private unsafe bool EnsureWindow()
    {
        if (!_hwnd.IsNull) return true;

        var instance = GetInstance();
        if (!_classRegistered)
        {
            _wndProc ??= (hwnd, msg, wParam, lParam) => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),   // managed struct (delegate field): no sizeof
                    lpfnWndProc = _wndProc,
                    hInstance = instance,
                    lpszClassName = name,
                };
                // 0 = failed; an already-registered class (a previous instance in this process) is fine.
                if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                    return false;
            }
            _classRegistered = true;
        }

        var hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            ClassName, "", WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, null, null, null);
        if (hwnd.IsNull) return false;
        _hwnd = hwnd;
        return true;
    }

    private unsafe bool ShowOnThread(ScreenRegion window)
    {
        if (!EnsureWindow()) return false;

        using var glow = FlashGlow.Render(window.Width, window.Height);

        // A top-down 32bpp DIB the size of the window, filled from the premultiplied Skia pixels.
        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull) return false;
        var memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = window.Width;
            bmi.bmiHeader.biHeight = -window.Height;   // top-down, like Skia's rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;   // BI_RGB

            // The safe handle deletes the DIB when this scope ends — after UpdateLayeredWindow has copied it.
            using var dibHandle = PInvoke.CreateDIBSection(memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, out void* bits, null, 0);
            if (dibHandle.IsInvalid || bits is null) return false;
            var dib = new HBITMAP(dibHandle.DangerousGetHandle());
            Buffer.MemoryCopy((void*)glow.GetPixels(), bits, (long)window.Width * window.Height * 4, (long)window.Width * window.Height * 4);

            var old = PInvoke.SelectObject(memDc, dib);
            try
            {
                var dst = new System.Drawing.Point(window.X, window.Y);
                var size = new SIZE { cx = window.Width, cy = window.Height };
                var src = new System.Drawing.Point(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = (byte)PInvoke.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
                };
                if (!PInvoke.UpdateLayeredWindow(_hwnd, HDC.Null, dst, size, memDc, src, new COLORREF(0), blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
                    return false;
            }
            finally
            {
                PInvoke.SelectObject(memDc, old);
            }

            PInvoke.SetWindowPos(_hwnd, new HWND((void*)-1) /* HWND_TOPMOST */, window.X, window.Y, window.Width, window.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            return true;
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    private void HideOnThread()
    {
        if (!_hwnd.IsNull) PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }
}
