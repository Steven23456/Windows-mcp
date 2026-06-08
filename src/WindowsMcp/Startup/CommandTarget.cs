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
    /// executable name is resolved like the shell does — System32, then each PATH directory
    /// (this matters for tasks that store a bare program name, e.g. "powershell.exe", which
    /// lives in System32\WindowsPowerShell\v1.0, not System32 itself).
    /// </summary>
    public static bool Exists(string? command) => ResolveFullPath(command) is not null;

    /// <summary>
    /// Resolve a command to the absolute path of its executable on disk, or null if it cannot be
    /// located. Rooted paths are returned when they exist; a bare name is searched against the
    /// system directory and PATH. Use this (not <see cref="ResolveExe"/>) before a signature check —
    /// the Authenticode inspector needs a real, locatable path, so a bare "explorer.exe" must be
    /// resolved to "C:\Windows\explorer.exe" first.
    /// </summary>
    public static string? ResolveFullPath(string? command)
    {
        string? exe = ResolveExe(command);
        if (string.IsNullOrEmpty(exe)) return null;

        if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;

        string withExe = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
        foreach (var dir in SearchDirectories())
        {
            try
            {
                string p1 = Path.Combine(dir, exe);
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(dir, withExe);
                if (File.Exists(p2)) return p2;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        yield return Environment.SystemDirectory;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) yield break;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;
    }
}
