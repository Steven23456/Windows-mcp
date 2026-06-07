namespace WindowsMcp.Startup;

/// <summary>
/// Best-effort extraction of the executable from a command line (Run value, task action, or
/// service binary path) and a test for whether that executable exists on disk — the
/// HiJackThis "(file missing)" signal.
/// </summary>
public static class CommandTarget
{
    /// <summary>
    /// Resolve the executable path from a command string. Handles a quoted executable, an
    /// unquoted path that contains spaces (by cutting at the first ".exe"), and a bare first
    /// token. Environment variables are expanded.
    /// </summary>
    public static string? ResolveExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string cmd = Environment.ExpandEnvironmentVariables(command.Trim());

        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            string quoted = end > 1 ? cmd[1..end] : cmd[1..];
            return quoted.Length == 0 ? null : quoted;
        }

        // Unquoted: a path ending at the first ".exe" survives spaces in the directory name.
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0) return cmd[..(exe + 4)];

        int space = cmd.IndexOf(' ');
        return space < 0 ? cmd : cmd[..space];
    }

    /// <summary>
    /// True when the command's executable exists. Rooted paths are checked directly; a bare
    /// executable name is resolved against the system directory (best effort).
    /// </summary>
    public static bool Exists(string? command)
    {
        string? exe = ResolveExe(command);
        if (string.IsNullOrEmpty(exe)) return false;

        if (Path.IsPathRooted(exe)) return File.Exists(exe);

        return File.Exists(Path.Combine(Environment.SystemDirectory, exe));
    }
}
