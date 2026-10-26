using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// S-11's Win32 side: a lazily started STA thread owning two <see cref="LayeredWindow"/>s - a
/// <see cref="ControlIndicatorArt.CursorRing"/> that follows the pointer and a
/// <see cref="ControlIndicatorArt.Banner"/> at the top of the pointer's monitor. One tick (16 ms
/// while shown, 100 ms otherwise) asks <see cref="ControlIndicatorState"/> whether to show, so it is
/// both the pointer follower and the fade. The takeover combination is registered against the
/// banner only while shown; its <c>WM_HOTKEY</c> is taken off this thread's own queue (the window
/// class is shared by every instance, so its procedure cannot know which instance it serves).
/// </summary>
/// <remarks>
/// A courtesy, never a failure: nothing here throws into a tool call, a show waits at most 50 ms,
/// and with no interactive window station every member is a silent no-op. Nothing is ever refused
/// or blocked - the indicator only informs.
/// </remarks>
public sealed class ControlIndicator : IControlIndicator, IDisposable
{
    /// <summary>The id the takeover combination is registered under; <c>WM_HOTKEY</c>'s wParam.</summary>
    internal const int HotkeyId = 0x5311;

    private const string ClassName = "WindowsMcpControlIndicator";
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ShownTickMs = 16, IdleTickMs = 100;
    private static readonly TimeSpan ShowWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Before Windows 10 2004 <c>WDA_EXCLUDEFROMCAPTURE</c> "succeeds" but behaves as
    /// <c>WDA_MONITOR</c>: the windows become black boxes in every capture. Below this build no
    /// affinity is applied and <see cref="SuspendForCapture"/> hides the windows instead.
    /// </summary>
    private const int ExcludeFromCaptureMinBuild = 19041;

    private readonly ControlIndicatorOptions _options;
    private readonly ILogger _log;
    private readonly ControlIndicatorState _state;
    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _work = new();
    private volatile Thread? _thread;
    private volatile bool _disposed;
    private volatile bool _shown;
    private volatile bool _hotkeyRegistered;
    private volatile bool _captureExcluded;
    private long _ringHandle, _bannerHandle;
    private int _suspended;   // captures in progress; read by the overlay thread before it shows anything
    private int _takenWarned;   // "the combination could not be registered" is logged once per instance

    // Thread-affine state: only the overlay thread touches these.
    private HWND _ring, _banner;
    private bool _unavailable;   // no window station: stop trying
    private System.Drawing.Point _cursor;
    private uint _ringDpi;
    private HMONITOR _bannerMonitor;
    private ScreenRegion _ringRect = new(0, 0, 1, 1), _bannerRect = new(0, 0, 1, 1);

    public ControlIndicator(ControlIndicatorOptions options, ILogger<ControlIndicator>? log = null, TimeProvider? clock = null)
    {
        _options = options;
        _log = log ?? (ILogger)NullLogger<ControlIndicator>.Instance;
        _state = new ControlIndicatorState(options.Linger, clock);
    }

    /// <summary>R8 seam: false skips <c>SetWindowDisplayAffinity</c>, so a capture can see the windows.</summary>
    internal bool ExcludeFromCapture { get; init; } = true;

    /// <summary>Seam: the Windows build the display-affinity decision is made for (see <see cref="ExcludeFromCaptureMinBuild"/>).</summary>
    internal int OsBuild { get; init; } = Environment.OSVersion.Version.Build;

    /// <summary>Seam: a fault or a stall injected into a show, on the overlay thread, after the hotkey registration and before painting.</summary>
    internal Action? OnShowForTest { get; init; }

    /// <summary>The ring window's handle; 0 until created.</summary>
    internal long RingHandle => Interlocked.Read(ref _ringHandle);

    /// <summary>The banner window's handle; 0 until created.</summary>
    internal long BannerHandle => Interlocked.Read(ref _bannerHandle);

    internal bool IsHotkeyRegistered => _hotkeyRegistered;

    public bool IsVisible => _shown;

    public bool IsCaptureExcluded => _captureExcluded;

    public void BeginAction()
    {
        if (!_options.Enabled || _disposed) return;
        _state.Begin();
        try { Run(Tick, ShowWait); }   // show now rather than on the next tick, but never hold the call up for long
        catch { /* a thread that will not start leaves the tick to the next call; never the tool's problem */ }
    }

    public void EndAction()
    {
        if (!_options.Enabled || _disposed) return;
        _state.End();   // the tick fades it once the linger has passed
    }

    /// <remarks>
    /// The count goes up on the caller's thread BEFORE anything is looked at, so a show that starts
    /// mid-grab finds it and stays down; then, if the overlay thread exists, the windows are taken
    /// down synchronously (whatever <see cref="IsVisible"/> says - a hide may still be in flight).
    /// With no thread yet nothing can be on screen, so no screenshot ever starts the thread.
    /// </remarks>
    public IDisposable SuspendForCapture()
    {
        if (!_options.Enabled || _disposed || _captureExcluded) return NoScope.Instance;
        Interlocked.Increment(ref _suspended);
        if (_thread is not null) Run(ApplyVisibility, CallTimeout);   // down before the grab
        return new CaptureScope(this);
    }

    public TakeoverRecord? ConsumeTakeover() => _options.Enabled && !_disposed ? _state.Consume() : null;

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        _shown = false;
        if (thread is not null)
        {
            try
            {
                _work.Add(Teardown);
                _work.CompleteAdding();
                thread.Join(CallTimeout);
            }
            catch { /* tearing down: nothing left to report to */ }
        }
        _work.Dispose();
    }

    // ---- the overlay thread ----------------------------------------------------------------------

    /// <summary>Runs <paramref name="action"/> on the overlay thread and waits up to <paramref name="wait"/>. Never throws.</summary>
    private void Run(Action action, TimeSpan wait)
    {
        if (!EnsureThread()) return;
        var done = new TaskCompletionSource();
        if (!Post(() => { try { action(); } finally { done.TrySetResult(); } })) return;
        try { done.Task.Wait(wait); } catch { /* the action's failure is a silent no-op by contract */ }
    }

    /// <summary>Queues <paramref name="action"/> without waiting; false once the indicator is disposed.</summary>
    private bool Post(Action action)
    {
        try
        {
            _work.Add(action);
            return true;
        }
        catch { return false; }   // completed or disposed: shutting down
    }

    private bool EnsureThread()
    {
        if (_thread is not null) return true;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_thread is not null) return true;
            var t = new Thread(Loop) { IsBackground = true, Name = "WindowsMcp-ControlIndicator" };
            t.SetApartmentState(ApartmentState.STA);
            _thread = t;
            t.Start();
            return true;
        }
    }

    private void Loop()
    {
        try
        {
            while (!_work.IsCompleted)
            {
                if (_work.TryTake(out var action, _shown ? ShownTickMs : IdleTickMs))
                {
                    try { action(); } catch { /* per-action failures are the action's problem */ }
                }
                try { Pump(); } catch { /* a failed takeover must not end the thread */ }
                try { Tick(); } catch { /* a failed paint must not end the thread */ }
            }
        }
        catch { /* queue disposed under us: done */ }
    }

    private unsafe void Pump()
    {
        MSG msg;
        while (PInvoke.PeekMessage(&msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
        {
            if (msg.message == WmHotkey && msg.hwnd == _banner && !_banner.IsNull && (int)msg.wParam.Value == HotkeyId)
            {
                OnHotkey();
                continue;
            }
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    private void Tick()
    {
        if (_disposed) return;
        var want = _state.ShouldShow;
        if (want && !_shown) Show();
        else if (!want && (_shown || _hotkeyRegistered)) Hide();   // also a show that faulted after registering
        if (_shown) Follow(force: false);
    }

    private void OnHotkey()
    {
        if (_state.Takeover(_options.Hotkey.Display) is not null) Hide();
    }

    private void Show()
    {
        if (!EnsureWindows()) return;

        // A show that faulted after registering is retried by the next tick: keep that registration
        // rather than fail against it and mistake our own hold for someone else's.
        if (!_hotkeyRegistered)
        {
            _hotkeyRegistered = PInvoke.RegisterHotKey(_banner, HotkeyId,
                (HOT_KEY_MODIFIERS)(_options.Hotkey.Modifiers | ModNoRepeat), _options.Hotkey.Key);
            if (!_hotkeyRegistered && Interlocked.Exchange(ref _takenWarned, 1) == 0)
                _log.LogWarning(
                    "The control indicator's takeover combination {Hotkey} could not be registered: another application " +
                    "is using it, or Windows has it reserved (as it does Ctrl+Alt+Del and Win+L). The banner will not " +
                    "offer it; choose another with --control-hotkey.", _options.Hotkey.Display);
        }
        OnShowForTest?.Invoke();

        Follow(force: true);   // paint both at the pointer before they appear
        _shown = true;
        ApplyVisibility();
    }

    private void Hide()
    {
        if (_hotkeyRegistered)
        {
            PInvoke.UnregisterHotKey(_banner, HotkeyId);
            _hotkeyRegistered = false;
        }
        _shown = false;
        ApplyVisibility();
    }

    /// <summary>Up while shown and no capture is in progress; down otherwise.</summary>
    private void ApplyVisibility()
    {
        if (_ring.IsNull || _banner.IsNull) return;
        if (_shown && Volatile.Read(ref _suspended) == 0)
        {
            LayeredWindow.ShowTopmost(_ring, _ringRect);
            LayeredWindow.ShowTopmost(_banner, _bannerRect);
        }
        else
        {
            PInvoke.ShowWindow(_ring, SHOW_WINDOW_CMD.SW_HIDE);
            PInvoke.ShowWindow(_banner, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>
    /// Keeps the ring on the pointer (repainted only when the monitor's DPI changes) and the banner
    /// on the pointer's monitor (repainted only when that monitor changes). <paramref name="force"/>
    /// repaints both - a new show may carry a different banner text.
    /// </summary>
    private unsafe void Follow(bool force)
    {
        if (!PInvoke.GetCursorPos(out var at))
        {
            if (!force) return;   // a secure desktop or a locked session: stay where we were
            at = _cursor;
        }
        if (!force && at == _cursor) return;
        _cursor = at;

        var monitor = PInvoke.MonitorFromPoint(at, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0 ? dpiX : 96u;
        var scale = dpi / 96.0;

        if (force || dpi != _ringDpi)
        {
            using var ring = ControlIndicatorArt.CursorRing(scale);
            _ringRect = ControlIndicatorArt.RingRect(at.X, at.Y, ring.Width);
            LayeredWindow.Paint(_ring, ring, _ringRect);
            _ringDpi = dpi;
        }
        else
        {
            _ringRect = ControlIndicatorArt.RingRect(at.X, at.Y, _ringRect.Width);
            LayeredWindow.Move(_ring, _ringRect.X, _ringRect.Y);
        }

        if (force || monitor != _bannerMonitor)
        {
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            var work = PInvoke.GetMonitorInfo(monitor, &info)
                ? new ScreenRegion(info.rcWork.left, info.rcWork.top, info.rcWork.right - info.rcWork.left, info.rcWork.bottom - info.rcWork.top)
                : new ScreenRegion(0, 0, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN), PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN));
            var text = ControlIndicatorArt.BannerText(_hotkeyRegistered ? _options.Hotkey.Display : null);
            using var banner = ControlIndicatorArt.Banner(text, scale);
            _bannerRect = ControlIndicatorArt.BannerRect(work, banner.Width, banner.Height, scale);
            LayeredWindow.Paint(_banner, banner, _bannerRect);
            _bannerMonitor = monitor;
        }
    }

    private unsafe bool EnsureWindows()
    {
        if (!_ring.IsNull && !_banner.IsNull) return true;
        if (_unavailable) return false;

        _ring = LayeredWindow.Create(ClassName);
        _banner = LayeredWindow.Create(ClassName);
        if (_ring.IsNull || _banner.IsNull)
        {
            DestroyWindows();
            _unavailable = true;
            return false;
        }

        // Kept out of every capture (Windows 10 2004+); on an older build, or when the call is
        // refused, SuspendForCapture hides the windows around each grab instead.
        _captureExcluded = ExcludeFromCapture && OsBuild >= ExcludeFromCaptureMinBuild
            && PInvoke.SetWindowDisplayAffinity(_ring, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE)
            && PInvoke.SetWindowDisplayAffinity(_banner, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);

        Interlocked.Exchange(ref _ringHandle, (nint)_ring.Value);
        Interlocked.Exchange(ref _bannerHandle, (nint)_banner.Value);
        return true;
    }

    private void Teardown()
    {
        if (_hotkeyRegistered)
        {
            PInvoke.UnregisterHotKey(_banner, HotkeyId);
            _hotkeyRegistered = false;
        }
        _shown = false;
        DestroyWindows();
    }

    private void DestroyWindows()
    {
        if (!_ring.IsNull) PInvoke.DestroyWindow(_ring);
        if (!_banner.IsNull) PInvoke.DestroyWindow(_banner);
        _ring = _banner = default;
        Interlocked.Exchange(ref _ringHandle, 0);
        Interlocked.Exchange(ref _bannerHandle, 0);
    }

    /// <summary>Closes one capture suspension; the windows come back once no capture holds them down.</summary>
    private sealed class CaptureScope(ControlIndicator owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Interlocked.Decrement(ref owner._suspended);
            if (owner._thread is not null) owner.Post(owner.ApplyVisibility);
        }
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}
