namespace WindowsMcp.Hosting;

/// <summary>
/// C-6 (roadmap R9): the pure half of the <c>Path</c> repair — merging the host's inherited
/// <c>Path</c> with the registry's machine and user values without removing or reordering
/// anything the host set, and deciding whether a <c>Path</c> can resolve a system command at all.
/// Pure and Windows-free so it is unit-testable; <see cref="EnvironmentRepair"/> is the only caller.
/// </summary>
internal static class PathMerge
{
    /// <summary>
    /// The <c>;</c>-joined merge: <paramref name="host"/>'s entries first, then
    /// <paramref name="machine"/>, then <paramref name="user"/>. Entries are compared
    /// ordinal-ignore-case after trimming whitespace, surrounding double quotes and a trailing
    /// <c>\</c> or <c>/</c>; the first spelling wins and is kept (trimmed and unquoted, otherwise
    /// as written); empty entries are dropped.
    /// </summary>
    internal static string Merge(string? host, string? machine, string? user)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var source in new[] { host, machine, user })
        {
            foreach (var entry in Split(source))
            {
                if (seen.Add(Key(entry))) merged.Add(entry);
            }
        }
        return string.Join(';', merged);
    }

    /// <summary>
    /// Whether <paramref name="path"/> carries the system directory: an entry equal to
    /// <c>&lt;systemRoot&gt;\System32</c> when <paramref name="systemRoot"/> is known, and any
    /// entry whose last segment is <c>System32</c> when it is not.
    /// </summary>
    internal static bool HasSystem32(string? path, string? systemRoot)
    {
        var entries = Split(path);
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            var wanted = Key(systemRoot.Trim().TrimEnd('\\', '/') + @"\System32");
            return entries.Any(e => string.Equals(Key(e), wanted, StringComparison.OrdinalIgnoreCase));
        }
        return entries.Any(e => string.Equals(LastSegment(Key(e)), "System32", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The entries of a <c>;</c>-separated <c>Path</c>: whitespace trimmed, surrounding double
    /// quotes stripped, empty entries dropped, everything else exactly as written.
    /// </summary>
    internal static string[] Split(string? path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var entries = new List<string>();
        foreach (var raw in path.Split(';'))
        {
            var entry = raw.Trim();
            if (entry.Length >= 2 && entry[0] == '"' && entry[^1] == '"')
                entry = entry[1..^1].Trim();
            if (entry.Length > 0) entries.Add(entry);
        }
        return entries.ToArray();
    }

    /// <summary>The comparison form of one entry: a trailing separator is not a different directory.</summary>
    private static string Key(string entry) => entry.TrimEnd('\\', '/');

    private static string LastSegment(string key)
    {
        int i = key.LastIndexOfAny(['\\', '/']);
        return i < 0 ? key : key[(i + 1)..];
    }
}
