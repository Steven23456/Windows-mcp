using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// S-11: the "AI is in control" indicator - a cursor ring and a hotkey banner shown while a
/// driving tool runs, and the user's deliberate takeover by the key combination. Information,
/// never control: nothing here refuses, delays or cancels a tool call, and no physical input is
/// blocked. See docs/design/S-11-control-indicator.md.
/// </summary>
/// <remarks>
/// Every member is a silent no-op when the indicator is switched off, disposed, or there is no
/// interactive window station (the <see cref="IFlashOverlay"/> rule). Nothing here ever throws.
/// </remarks>
public interface IControlIndicator
{
    /// <summary>A driving tool is starting: show the indicator (waiting at most ~50 ms) and keep it up until the matching <see cref="EndAction"/>.</summary>
    void BeginAction();

    /// <summary>A driving tool has finished; the last one to finish starts the fade.</summary>
    void EndAction();

    /// <summary>True while the ring and banner are up (logically - a capture in progress may hold the windows down).</summary>
    bool IsVisible { get; }

    /// <summary>True when display affinity keeps both windows out of every capture, so <see cref="SuspendForCapture"/> has nothing to do.</summary>
    bool IsCaptureExcluded { get; }

    /// <summary>
    /// Brackets a screen grab: unless <see cref="IsCaptureExcluded"/>, the windows are down when this
    /// returns and stay down (even if a show starts meanwhile) until the scope is disposed.
    /// </summary>
    IDisposable SuspendForCapture();

    /// <summary>The takeover the AI has not been told about yet, once; null when there is none.</summary>
    TakeoverRecord? ConsumeTakeover();
}
