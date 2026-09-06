namespace WindowsMcp.Services;

/// <summary>
/// C-2: the pure guard behind <c>registry_delete</c>'s key branch — the roots whose recursive
/// loss breaks the user's profile or the OS, plus the hive root itself. Returns the reason a
/// key delete is refused, or null when the path is fair game. Deliberately short: it guards
/// against the catastrophic roots, not every unwise delete; <c>confirm</c> and the client's
/// <c>destructiveHint</c> do the rest. Value deletes under these keys are not guarded.
/// </summary>
internal static class RegistryGuard
{
    /// <summary>Normalised (see <see cref="Normalise"/>), compared ordinal-ignore-case.</summary>
    internal static readonly string[] ProtectedRoots =
    [
        @"Software",
        @"Software\Classes",
        @"Software\Microsoft",
        @"Software\Microsoft\Windows",
        @"Software\Microsoft\Windows\CurrentVersion",
        @"Software\Microsoft\Windows NT",
        @"Software\Microsoft\Windows NT\CurrentVersion",
        @"Software\Policies",
        @"Software\WOW6432Node",
        @"System",
        @"SYSTEM\CurrentControlSet",
        @"SAM",
        @"SECURITY",
        @"Environment",
        @"Control Panel",
        @"Volatile Environment",
    ];

    internal static string? Refusal(string path)
    {
        var normalised = Normalise(path);
        if (normalised.Length == 0)
            return "Refusing to delete the hive root: 'path' is empty. Name the key to delete.";

        foreach (var root in ProtectedRoots)
        {
            if (normalised.Equals(root, StringComparison.OrdinalIgnoreCase))
                return $"Refusing to delete '{path}': '{root}' is a root the user's profile or Windows " +
                       "itself depends on. Delete a key beneath it instead.";
        }

        return null;
    }

    /// <summary>
    /// Trim, forward slashes to backslashes, doubled separators collapsed, leading and trailing
    /// separators dropped — so every spelling of a root compares equal to the list's.
    /// </summary>
    internal static string Normalise(string path)
    {
        var s = (path ?? string.Empty).Trim().Replace('/', '\\');
        while (s.Contains(@"\\", StringComparison.Ordinal))
            s = s.Replace(@"\\", @"\", StringComparison.Ordinal);
        return s.Trim('\\').Trim();
    }
}
