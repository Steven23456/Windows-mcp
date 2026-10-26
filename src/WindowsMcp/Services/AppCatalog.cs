using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-8 / roadmap C7: the pure half of the app catalog — merging the two sources into one list and
/// resolving a requested name against it. Nothing here touches the Start Menu, the package
/// manager or a clock, so every rule an agent's <c>launch("calc")</c> depends on is unit-testable.
/// <see cref="AppCatalogService"/> owns the sources and the cache; this owns the rules.
/// </summary>
internal static class AppCatalog
{
    /// <summary>The fuzzy floor, shared with <see cref="WindowMatcher"/> (roadmap C6).</summary>
    internal const int FuzzyThreshold = 70;

    /// <summary>
    /// The fuzzy score of a request that abbreviates a name to its last word
    /// (<see cref="FuzzyMatch.AbbreviationUnread"/>); one less per word left unread.
    /// </summary>
    internal const int AbbreviationScore = 95;

    /// <summary>
    /// One list, one entry per name (ordinal, ignoring case): a shortcut beats a packaged entry of
    /// the same name because the <c>.lnk</c> carries the user's intent, and the first of two
    /// shortcuts sharing a name (scan order) wins. Ordered by name.
    /// </summary>
    internal static IReadOnlyList<AppEntry> Merge(IEnumerable<AppEntry> shortcuts, IEnumerable<AppEntry> packaged)
    {
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in shortcuts.Concat(packaged))
            byName.TryAdd(entry.Name, entry);
        return byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Exact name → the shortest name the request is a prefix of → the highest fuzzy score
    /// (<c>max(PartialRatio, TokenSetRatio)</c>, or <see cref="AbbreviationScore"/> less the unread
    /// words when the request abbreviates the name and that is higher; at least
    /// <see cref="FuzzyThreshold"/>, ties to the shortest name). Nothing → a
    /// <see cref="KeyNotFoundException"/> naming the request and the five nearest names with their
    /// scores, so a miss is actionable. A top-score tie that an abbreviation is part of → an
    /// <see cref="ArgumentException"/> naming every tied name: <c>wt</c> fits "Windows Terminal" and
    /// "Windows Tools" equally, and the shorter name is no reason to pick one.
    /// </summary>
    /// <remarks>
    /// <see cref="AbbreviationScore"/> is what makes <c>vs code</c> mean Visual Studio Code: the
    /// scorers alone tie it with "QR Code Scanner" (both share only "code") and the tie goes to the
    /// shorter name. It sits below 100 so a name a scorer matches outright still wins, and loses a
    /// point per unread word so Visual Studio Code (95) beats its Insiders build (94).
    /// </remarks>
    internal static AppMatch Match(IReadOnlyList<AppEntry> catalog, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An app name is required.", nameof(name));

        var exact = catalog.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return new AppMatch(exact, 100, "exact");

        var prefix = catalog
            .Where(e => e.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (prefix is not null) return new AppMatch(prefix, 100, "prefix");

        var scored = catalog
            .Select(e =>
            {
                int scorers = Math.Max(FuzzyMatch.PartialRatio(name, e.Name), FuzzyMatch.TokenSetRatio(name, e.Name));
                int unread = FuzzyMatch.AbbreviationUnread(name, e.Name);
                int abbreviation = unread < 0 ? 0 : AbbreviationScore - unread;
                return (Entry: e, Score: Math.Max(scorers, abbreviation), Abbreviated: abbreviation > scorers);
            })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Entry.Name.Length).ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scored.Length > 0 && scored[0].Score >= FuzzyThreshold)
        {
            var tied = scored.TakeWhile(x => x.Score == scored[0].Score).ToArray();
            if (tied.Length > 1 && tied.Any(x => x.Abbreviated))
                throw new ArgumentException(
                    $"'{name}' could mean more than one app: {string.Join(", ", tied.Select(x => $"'{x.Entry.Name}' ({x.Score})"))}. Use more of the name.");
            return new AppMatch(scored[0].Entry, scored[0].Score, "fuzzy");
        }

        var nearest = string.Join(", ", scored.Take(5).Select(x => $"'{x.Entry.Name}' ({x.Score})"));
        throw new KeyNotFoundException(
            $"No app matching '{name}'. Nearest: {(nearest.Length > 0 ? nearest : "(the catalog is empty)")}. Use the Start Menu name, or a path.");
    }
}
