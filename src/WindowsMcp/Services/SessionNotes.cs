namespace WindowsMcp.Services;

/// <summary>
/// E-8: whether input and UI reads reach the user's desktop, and the one-line note that says why
/// not. Pure: the ordered rules of docs/design/E-8-session-state.md, first match wins.
/// </summary>
internal static class SessionNotes
{
    private const string Stop = " Stop and tell the user.";

    /// <param name="inputDesktopAccess">
    /// Opened (the name was read) | Denied (Windows refused to open the input desktop: what a
    /// secure desktop looks like to a process that is not SYSTEM) | Unknown (it could not be
    /// read for any other reason, which is not evidence of anything).
    /// </param>
    internal static (bool InputAvailable, string? Note) Evaluate(
        bool? locked, string connectState, string? inputDesktop, string inputDesktopAccess, bool interactive)
    {
        // First: a server in session 0 (a service, a scheduled task, sshd) also reports
        // "Disconnected", and telling it to reconnect would be advice that can never help.
        if (!interactive)
            return (false, "The server has no interactive desktop (it was started as a service or a scheduled task without one): UI tools cannot see or drive a desktop.");

        if (locked == true)
            return (false, "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it." + Stop);

        if (string.Equals(connectState, "Disconnected", StringComparison.Ordinal))
            return (false, "The session is disconnected (no display or remote-desktop client is attached): nothing can be seen or driven until the user reconnects." + Stop);

        bool denied = string.Equals(inputDesktopAccess, "Denied", StringComparison.Ordinal);
        bool opened = string.Equals(inputDesktopAccess, "Opened", StringComparison.Ordinal);
        if (denied || (opened && string.Equals(inputDesktop, "Winlogon", StringComparison.OrdinalIgnoreCase)))
            return (false, "A secure desktop has the input (a UAC prompt, or the sign-in or lock screen): the user's desktop cannot be seen or driven until the user answers it." + Stop);
        if (opened && inputDesktop is not null && !string.Equals(inputDesktop, "Default", StringComparison.OrdinalIgnoreCase))
        {
            var name = OneLine(inputDesktop);
            var which = name.Length > 0 ? $"The '{name}' desktop" : "An unnamed desktop";
            return (false, $"{which} has the input, not the user's desktop: the user's desktop cannot be seen or driven until the user returns to it." + Stop);
        }

        // An unknown connect state, lock state or input desktop alone is not a reason to cry wolf.
        return (true, null);
    }

    /// <summary>
    /// The note is printed as one line of a snapshot, and any process can create a desktop with a
    /// name of its choosing: every control character and line separator becomes a space, so a
    /// name cannot add a line.
    /// </summary>
    private static string OneLine(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || chars[i] is '\u0085' or '\u2028' or '\u2029') chars[i] = ' ';
        }
        var line = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 64 ? line : line[..64] + "...";
    }
}
