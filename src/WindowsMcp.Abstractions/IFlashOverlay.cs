using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// A-14: the post-capture glow. A layered, click-through, always-on-top window drawn around the
/// rect that was just captured — the only signal a person at the target machine gets that an
/// agent (local or remote) took a picture of their screen, which is why it is on by default under
/// both transports and turned off with one switch (<c>--flash off</c>).
/// </summary>
/// <remarks>
/// Every member is a silent no-op when there is no interactive window station (Task Scheduler,
/// session 0): that is a robustness case, not a policy one. Nothing here ever throws, and
/// <see cref="IsVisible"/> simply stays false.
/// </remarks>
public interface IFlashOverlay
{
    /// <summary>
    /// Shows the glow around <paramref name="rect"/> (virtual-desktop pixels, roadmap C1) and
    /// hides it again after <paramref name="duration"/>. A call while the glow is up replaces it.
    /// </summary>
    void Show(ScreenRegion rect, TimeSpan duration);

    /// <summary>Takes the glow down now. Idempotent — hiding a hidden overlay is not an error.</summary>
    void Hide();

    /// <summary>True while the glow is on screen.</summary>
    bool IsVisible { get; }
}
