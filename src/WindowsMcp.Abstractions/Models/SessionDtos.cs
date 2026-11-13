namespace WindowsMcp.Abstractions.Models;

/// <summary>E-8: what <c>ISessionService.GetAsync</c> reports (docs/design/E-8-session-state.md).</summary>
/// <param name="Elevated">The shared elevation check startup_report uses.</param>
/// <param name="IntegrityLevel">Untrusted | Low | Medium | MediumPlus | High | System | Protected | Unknown.</param>
/// <param name="UserName">DOMAIN\user of the server process; "" when it cannot be read.</param>
/// <param name="SessionId">The server process's terminal-services session; -1 when unknown.</param>
/// <param name="Interactive">The process has an interactive window station.</param>
/// <param name="ConnectState">Active | Connected | Disconnected | ... (WTS_CONNECTSTATE_CLASS without the WTS prefix) | Unknown.</param>
/// <param name="Locked">True / false, or null when Windows does not say.</param>
/// <param name="InputDesktop">
/// The name of the desktop receiving input ("Default" is the user's); null when it was not read
/// (see <paramref name="InputDesktopAccess"/> for why).
/// </param>
/// <param name="InputDesktopAccess">
/// Opened (the name was read) | Denied (OpenInputDesktop refused with access denied: a secure
/// desktop) | Unknown (any other failure). Unknown alone raises no note.
/// </param>
/// <param name="InputAvailable">Input and UI reads reach the user's desktop.</param>
/// <param name="Note">One sentence when <paramref name="InputAvailable"/> is false, else null.</param>
public record SessionState(
    bool Elevated,
    string IntegrityLevel,
    string UserName,
    int SessionId,
    bool Interactive,
    string ConnectState,
    bool? Locked,
    string? InputDesktop,
    string InputDesktopAccess,
    // The Win32 error OpenInputDesktop failed with; null when it opened or was not attempted.
    int? InputDesktopError,
    bool InputAvailable,
    string? Note);
