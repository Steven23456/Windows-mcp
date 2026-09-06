using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-8: the window wait, extracted from <c>WindowService</c> so the polling and the choice of
/// window are testable without launching anything. Packaged apps and Edge hand their command off
/// to a process that is not the one the activation reported, so a PID match is tried first and a
/// <b>new</b> window whose title matches the resolved app name is the fallback.
/// </summary>
internal static class LaunchWait
{
    /// <summary>Roadmap C7 / B-8: the poll interval the tool's timeout is spent in.</summary>
    internal const int DefaultPollMs = 250;

    /// <summary>
    /// The launched app's window, if the inventory holds it: a window of <paramref name="pid"/>
    /// (any title, frontmost first — the strongest evidence), else, because packaged apps and
    /// browsers hand off to another process, a window that was not open before the launch whose
    /// title matches <paramref name="matchedName"/> exact → substring → fuzzy (70+). Null otherwise.
    /// </summary>
    internal static WindowInfo? Pick(
        IReadOnlyList<WindowInfo> inventory, int pid, string matchedName, IReadOnlyCollection<long> before)
    {
        var byPid = inventory.Where(w => w.Pid == pid).OrderBy(w => w.ZOrder).FirstOrDefault();
        if (byPid is not null) return byPid;

        var fresh = inventory.Where(w => !before.Contains(w.Hwnd) && w.Title.Length > 0).OrderBy(w => w.ZOrder).ToArray();
        var exact = fresh.FirstOrDefault(w => string.Equals(w.Title, matchedName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var substring = fresh.FirstOrDefault(w => w.Title.Contains(matchedName, StringComparison.OrdinalIgnoreCase));
        if (substring is not null) return substring;

        WindowInfo? best = null; int bestScore = -1;
        foreach (var w in fresh)
        {
            int score = Math.Max(FuzzyMatch.PartialRatio(matchedName, w.Title), FuzzyMatch.TokenSetRatio(matchedName, w.Title));
            if (score > bestScore) { best = w; bestScore = score; }
        }
        return bestScore >= WindowMatcher.FuzzyThreshold ? best : null;
    }

    /// <summary>
    /// Polls the inventory — immediately, then every <paramref name="pollMs"/> — until
    /// <see cref="Pick"/> finds the window or <paramref name="timeoutMs"/> is spent. Null on
    /// timeout, never an exception; cancellation throws.
    /// </summary>
    internal static async Task<WindowInfo?> ForWindowAsync(
        Func<CancellationToken, Task<WindowInfo[]>> inventory,
        int pid,
        string matchedName,
        IReadOnlyCollection<long> before,
        int timeoutMs,
        int pollMs = DefaultPollMs,
        CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var found = Pick(await inventory(ct).ConfigureAwait(false), pid, matchedName, before);
            if (found is not null) return found;
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) return null;
            await Task.Delay((int)Math.Min(pollMs, remaining), ct).ConfigureAwait(false);
        }
    }
}
