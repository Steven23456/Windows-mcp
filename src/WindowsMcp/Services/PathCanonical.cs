namespace WindowsMcp.Services;

/// <summary>
/// C-1 round 4d: the one spelling of a path that every alias shares. A <c>subst</c> drive, a
/// mapped network drive, a junction or a symlink are second spellings of the same directory that
/// no string comparison sees; the volume's own spelling of the deepest ancestor that exists
/// (<see cref="IFinalPathNative.FinalPathOf"/>, <c>GetFinalPathNameByHandle</c> in production)
/// plus the segments that do not exist yet is what the containment check compares. Pure, so the
/// walk is unit-tested with a fake volume.
/// </summary>
internal static class PathCanonical
{
    /// <summary>
    /// Asks the volume about <paramref name="fullPath"/> itself first, then about each ancestor
    /// up to (and including) the root; the first answer wins and the segments below it are
    /// appended as written. A path the volume cannot speak for at any level is returned as
    /// written. The result never carries a trailing separator (a root keeps its own).
    /// </summary>
    internal static string Canonical(string fullPath, IFinalPathNative native)
    {
        var asWritten = Path.TrimEndingDirectorySeparator(fullPath);
        var tail = new Stack<string>();
        var probe = asWritten;
        while (probe.Length > 0)
        {
            var final = native.FinalPathOf(probe);
            if (final is not null)
            {
                var result = Path.TrimEndingDirectorySeparator(final);
                while (tail.Count > 0) result = Path.Combine(result, tail.Pop());
                return Path.TrimEndingDirectorySeparator(result);
            }
            var parent = Path.GetDirectoryName(probe);
            if (parent is null) break;   // probe was the root: no further up
            tail.Push(Path.GetFileName(probe));
            probe = parent;
        }
        return asWritten;
    }
}
