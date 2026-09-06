using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-10: the Win32 calls the foreground ladder makes, behind a seam so the ladder itself — which
/// step is tried, in what order, what is reported — is unit-testable with no desktop and no
/// injected input. The production implementation is the only caller of user32; the tests drive a
/// recording fake.
/// </summary>
internal interface IForegroundNative
{
    /// <summary><c>IsIconic</c>: the window is minimised.</summary>
    bool IsIconic(long hwnd);

    /// <summary><c>ShowWindow(SW_RESTORE)</c>.</summary>
    bool Restore(long hwnd);

    /// <summary><c>SetForegroundWindow</c>. Its return value is a request, not an outcome.</summary>
    bool SetForegroundWindow(long hwnd);

    /// <summary><c>GetForegroundWindow</c> — the only source of truth for success (C11).</summary>
    long GetForegroundWindow();

    /// <summary>
    /// <c>AttachThreadInput(GetCurrentThreadId(), GetWindowThreadProcessId(hwnd), attach)</c>.
    /// False means Windows refused (the usual case: an elevated target), and the ladder skips
    /// straight to the ALT nudge without attempting a detach.
    /// </summary>
    bool AttachThreadInput(long hwnd, bool attach);

    /// <summary><c>BringWindowToTop</c>.</summary>
    bool BringWindowToTop(long hwnd);

    /// <summary><c>keybd_event(VK_MENU)</c> down then up — the documented last resort.</summary>
    void AltNudge();
}

/// <summary>
/// B-10: the bring-to-foreground ladder, extracted from <c>WindowService</c> so it can be tested
/// without a desktop. Sequence: restore if minimised, then <c>SetForegroundWindow</c>, then
/// <c>AttachThreadInput</c> + <c>BringWindowToTop</c> + <c>SetForegroundWindow</c> + detach, then
/// the ALT nudge + <c>SetForegroundWindow</c>. <c>GetForegroundWindow</c> is re-read after every
/// step and is what <see cref="ForegroundResult.Success"/> and
/// <see cref="ForegroundResult.Strategy"/> are built from (roadmap C11).
/// </summary>
internal static class ForegroundLadder
{
    internal static ForegroundResult Bring(WindowMatch match, IForegroundNative native)
    {
        long hwnd = match.Window.Hwnd;

        // A minimised window cannot take the foreground; restore first, whatever happens next.
        bool restored = false;
        if (native.IsIconic(hwnd))
        {
            native.Restore(hwnd);
            restored = true;
        }

        string? strategy = null;

        // Rung 1: the plain request. user32's return value is not consulted — only the re-read is.
        native.SetForegroundWindow(hwnd);
        if (native.GetForegroundWindow() == hwnd) strategy = "SetForegroundWindow";

        // Rung 2: share the target's input queue so Windows treats us as that thread. A refused
        // attach (an elevated target) skips the rung entirely — nothing to detach, nothing to raise.
        if (strategy is null && native.AttachThreadInput(hwnd, attach: true))
        {
            try
            {
                native.BringWindowToTop(hwnd);
                native.SetForegroundWindow(hwnd);
            }
            finally
            {
                native.AttachThreadInput(hwnd, attach: false);
            }
            if (native.GetForegroundWindow() == hwnd) strategy = "AttachThreadInput";
        }

        // Rung 3: the ALT nudge lifts the foreground lock for one more request.
        if (strategy is null)
        {
            native.AltNudge();
            native.SetForegroundWindow(hwnd);
            if (native.GetForegroundWindow() == hwnd) strategy = "AltNudge";
        }

        return new ForegroundResult(match.Window, match.Strategy, match.Score, restored, strategy, strategy is not null);
    }
}
