using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// E-8: the server process's session as Windows reports it - elevation, integrity, user, session,
/// lock state and which desktop has the input. It only reports: it never elevates, unlocks or
/// switches desktop. See docs/design/E-8-session-state.md.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Reads every fact independently; a read that fails yields that field's unknown value.
    /// Never throws, except <see cref="OperationCanceledException"/> for a cancelled token.
    /// </summary>
    Task<SessionState> GetAsync(CancellationToken ct = default);
}
