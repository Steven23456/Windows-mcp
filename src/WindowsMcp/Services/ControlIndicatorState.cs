using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// S-11's pure core: the refcount of driving calls in flight, the fade, the dismissal and the
/// pending takeover. Thread-safe, no timers - visibility is evaluated against the clock on read,
/// so <see cref="ControlIndicator"/>'s pointer-follow tick is also the fade. The fade runs on the
/// monotonic clock (a wall-clock step back must not hold the indicator and the hotkey up); only the
/// takeover's <see cref="TakeoverRecord.At"/>, which the agent reads, is wall-clock time.
/// </summary>
internal sealed class ControlIndicatorState
{
    private readonly object _gate = new();
    private readonly TimeSpan _linger;
    private readonly TimeProvider _clock;
    private int _active;
    private long? _fadeStart;   // monotonic timestamp of the last End
    private bool _dismissed;
    private TakeoverRecord? _pending;

    internal ControlIndicatorState(TimeSpan linger, TimeProvider? clock = null)
    {
        _linger = linger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>refcount++, clears the dismissal and any fade deadline.</summary>
    internal void Begin()
    {
        lock (_gate)
        {
            _active++;
            _dismissed = false;
            _fadeStart = null;
        }
    }

    /// <summary>
    /// refcount--, clamped at 0; the last end starts the fade (visible for the linger from now). An
    /// end with nothing in flight changes nothing, so a stray end can never show a hidden indicator.
    /// </summary>
    internal void End()
    {
        lock (_gate)
        {
            if (_active == 0) return;
            if (--_active == 0) _fadeStart = _clock.GetTimestamp();
        }
    }

    /// <summary>!dismissed &amp;&amp; (Active &gt; 0 || less than the linger has elapsed since the last End).</summary>
    internal bool ShouldShow
    {
        get { lock (_gate) return Shows(); }
    }

    internal int Active
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>
    /// When <see cref="ShouldShow"/>: dismiss (even with calls in flight - they run on, unseen),
    /// record {At = now, Hotkey} as the pending takeover (the latest wins) and return it; otherwise
    /// null and nothing recorded.
    /// </summary>
    internal TakeoverRecord? Takeover(string hotkeyDisplay)
    {
        lock (_gate)
        {
            if (!Shows()) return null;
            _dismissed = true;
            _fadeStart = null;
            return _pending = new TakeoverRecord(_clock.GetUtcNow(), hotkeyDisplay);
        }
    }

    /// <summary>Returns the pending record once, then null.</summary>
    internal TakeoverRecord? Consume()
    {
        lock (_gate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    private bool Shows() =>
        !_dismissed && (_active > 0 || (_fadeStart is { } start && _clock.GetElapsedTime(start) < _linger));
}
