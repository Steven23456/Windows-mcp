using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-10: the window the caller meant, and how it was found. <see cref="Strategy"/> is one of
/// <c>hwnd</c>, <c>exact</c>, <c>substring</c>, <c>fuzzy</c>; <see cref="Score"/> is 100 for the
/// first three and the fuzzy score (70-100) for the last.
/// </summary>
internal sealed record WindowMatch(WindowInfo Window, string Strategy, int Score);

/// <summary>
/// B-10 / roadmap C5: one pure title-to-window resolver for <c>switch_to_window</c>,
/// <c>focus</c> and <c>window(action:…)</c>. Not to be confused with
/// <c>UIAutomationService.MatchWindows</c>, which stays strict on purpose — a snapshot scope
/// must not fuzz.
/// </summary>
internal static class WindowMatcher
{
    /// <summary>The fuzzy floor: below it a title is "not this window", not a weak match.</summary>
    internal const int FuzzyThreshold = 70;

    /// <summary>
    /// The single window <paramref name="title"/>/<paramref name="hwnd"/> names, over A-1's
    /// inventory. An <paramref name="hwnd"/> wins over a title and never fuzzes; a title is
    /// matched exact (ordinal, ignoring case) → substring → fuzzy
    /// (<c>max(PartialRatio, TokenSetRatio) &gt;= 70</c>), and ties inside one strategy go to the
    /// lowest <see cref="WindowInfo.ZOrder"/> — the frontmost. Minimised windows are candidates.
    /// Neither argument is an <see cref="ArgumentException"/>; nothing matched is a
    /// <see cref="KeyNotFoundException"/> naming the open windows.
    /// </summary>
    internal static WindowMatch Match(IReadOnlyList<WindowInfo> inventory, string? title, long? hwnd)
    {
        if (hwnd is { } handle)
        {
            var byHandle = inventory.Where(w => w.Hwnd == handle).OrderBy(w => w.ZOrder).FirstOrDefault();
            if (byHandle is null)
                throw new KeyNotFoundException(
                    $"No top-level window has handle {handle} (0x{handle:X}). Open windows: {OpenWindows(inventory)}");
            return new WindowMatch(byHandle, "hwnd", 100);
        }

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A window must be named: give a title (exact, substring or fuzzy) or an hwnd from window list.");

        var exact = Frontmost(inventory.Where(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return new WindowMatch(exact, "exact", 100);

        var substring = Frontmost(inventory.Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)));
        if (substring is not null) return new WindowMatch(substring, "substring", 100);

        WindowInfo? best = null;
        int bestScore = -1;
        foreach (var w in inventory.OrderBy(w => w.ZOrder))
        {
            if (w.Title.Length == 0) continue;
            int score = Math.Max(FuzzyMatch.PartialRatio(title, w.Title), FuzzyMatch.TokenSetRatio(title, w.Title));
            if (score > bestScore) { best = w; bestScore = score; }   // strict: an earlier (frontmost) tie keeps the win
        }
        if (best is not null && bestScore >= FuzzyThreshold)
            return new WindowMatch(best, "fuzzy", bestScore);

        var nearest = best is null ? "" : $" Nearest: '{best.Title}' scored {bestScore} (below {FuzzyThreshold}).";
        throw new KeyNotFoundException($"No top-level window matching '{title}'. Open windows: {OpenWindows(inventory)}.{nearest}");
    }

    private static WindowInfo? Frontmost(IEnumerable<WindowInfo> candidates)
        => candidates.OrderBy(w => w.ZOrder).FirstOrDefault();

    private static string OpenWindows(IReadOnlyList<WindowInfo> inventory)
    {
        var open = inventory.OrderBy(w => w.ZOrder).Select(w => w.Title).Where(t => t.Length > 0).Distinct().Take(15).ToArray();
        return open.Length > 0 ? string.Join(", ", open.Select(t => $"'{t}'")) : "(none with a title)";
    }
}
