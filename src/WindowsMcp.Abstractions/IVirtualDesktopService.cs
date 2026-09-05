using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// A-12 (phase 1): read-only view of the Windows virtual desktops — the list and their names
/// from the registry, the per-window queries from the documented <c>IVirtualDesktopManager</c>.
/// Nothing here creates, removes, renames or switches a desktop (that is phase 2, the
/// undocumented internal interface, and is explicitly not planned).
/// </summary>
public interface IVirtualDesktopService
{
    /// <summary>
    /// Every virtual desktop in registry order. An empty array when the registry does not
    /// list any — an absent key is a normal outcome on some builds, never an exception.
    /// </summary>
    Task<VirtualDesktopInfo[]> ListAsync(CancellationToken ct = default);

    /// <summary>The <c>IsCurrent</c> entry of <see cref="ListAsync"/>, or null when there is none.</summary>
    Task<VirtualDesktopInfo?> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// The desktop GUID of a window (lower-case dashed, no braces), or null when the window has
    /// no desktop (hwnd 0, or the manager reports GUID_NULL) or the COM object is unavailable.
    /// </summary>
    Task<string?> GetWindowDesktopIdAsync(long hwnd, CancellationToken ct = default);

    /// <summary>
    /// Whether a window is on the desktop the user is looking at, or null when that cannot be
    /// determined (hwnd 0, or the COM object is unavailable; the manager itself answers true for a handle that is not a window).
    /// </summary>
    Task<bool?> IsWindowOnCurrentDesktopAsync(long hwnd, CancellationToken ct = default);
}
