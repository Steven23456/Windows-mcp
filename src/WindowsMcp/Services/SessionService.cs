using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// E-8: the session probe (docs/design/E-8-session-state.md). Reports; never elevates, unlocks or
/// switches desktop. Each fact is read independently: a read that fails yields that field's
/// "unknown" value, and the call never throws (cancellation aside).
/// </summary>
public sealed class SessionService : ISessionService
{
    /// <summary>WTS_CONNECTSTATE_CLASS, by value, without the WTS prefix.</summary>
    private static readonly string[] ConnectStates =
        ["Active", "Connected", "ConnectQuery", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init"];

    /// <summary>ERROR_ACCESS_DENIED: the one OpenInputDesktop failure that means a secure desktop has the input.</summary>
    private const int AccessDenied = 5;

    private readonly ISessionProbe _probe;

    /// <summary>The server's constructor: the real Win32 probe.</summary>
    public SessionService() : this(new Win32SessionProbe())
    {
    }

    /// <summary>The seam: every raw read goes through <paramref name="probe"/>.</summary>
    internal SessionService(ISessionProbe probe)
    {
        _probe = probe;
    }

    public Task<SessionState> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        bool elevated = Read(_probe.IsElevated, false);
        string integrity = Read(() => IntegrityLevels.Name(_probe.IntegrityRid()), "Unknown");
        string user = Read(_probe.UserName, "") ?? "";
        int sessionId = Read(_probe.SessionId, -1);
        // A failed read is not evidence of a service host: unknown must not raise the note.
        bool interactive = Read(_probe.UserInteractive, true);
        string connect = Read(() => _probe.ConnectState() is var raw and >= 0 && raw < ConnectStates.Length ? ConnectStates[raw] : "Unknown", "Unknown");
        bool? locked = Read<bool?>(() => _probe.SessionFlags() switch { 0 => true, 1 => false, _ => null }, null);
        // Three answers, not two: the name; a refusal with access denied (what a secure desktop
        // looks like to a process that is not SYSTEM); or anything else - another error, or a
        // read that threw - which says nothing and must not raise a note.
        string? desktop = null;
        string access = "Unknown";
        int? desktopError = null;
        try
        {
            var (name, error) = _probe.InputDesktop();
            if (name is not null)
            {
                desktop = name;
                access = "Opened";
            }
            else
            {
                desktopError = error;
                if (error == AccessDenied) access = "Denied";
            }
        }
        catch
        {
            // Unknown, with no error to report.
        }

        var (available, note) = SessionNotes.Evaluate(locked, connect, desktop, access, interactive);
        return Task.FromResult(new SessionState(elevated, integrity, user, sessionId, interactive, connect, locked, desktop, access, desktopError, available, note));
    }

    private static T Read<T>(Func<T> read, T unknown)
    {
        try { return read(); }
        catch { return unknown; }
    }
}
