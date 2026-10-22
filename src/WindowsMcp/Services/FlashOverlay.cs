using Windows.Win32;
using Windows.Win32.Foundation;
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

    // Thread-affine state: only the overlay thread touches it.
    private HWND _hwnd;

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
                try { action(); }
                catch { /* silent no-op by contract */ }
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

    private bool EnsureWindow()
    {
        if (!_hwnd.IsNull) return true;
        _hwnd = LayeredWindow.Create(ClassName);
        return !_hwnd.IsNull;
    }

    private bool ShowOnThread(ScreenRegion window)
    {
        if (!EnsureWindow()) return false;

        using var glow = FlashGlow.Render(window.Width, window.Height);
        if (!LayeredWindow.Paint(_hwnd, glow, window)) return false;
        LayeredWindow.ShowTopmost(_hwnd, window);
        return true;
    }

    private void HideOnThread()
    {
        if (!_hwnd.IsNull) PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }
}
