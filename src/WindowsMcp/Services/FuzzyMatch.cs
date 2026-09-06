namespace WindowsMcp.Services;

/// <summary>
/// B-10 / roadmap C6: the three <c>thefuzz</c> scorers upstream matches window and app names
/// with, in-repo and package-free. Every score is an int 0-100, case-insensitive and symmetric;
/// two empty strings score 100, one empty string against a non-empty one scores 0.
/// <para>
/// The exact definitions the tests pin (see <c>FuzzyMatchTests</c>):
/// <c>Ratio</c> = <c>round(200 * LCS(a,b) / (|a| + |b|))</c> with away-from-zero rounding — the
/// indel/Levenshtein ratio <c>thefuzz</c> computes when python-Levenshtein is installed;
/// <c>PartialRatio</c> = the best <c>Ratio</c> of the shorter string against any window of the
/// same length in the longer one; <c>TokenSetRatio</c> = <c>thefuzz</c>'s token-set semantics —
/// lower-case, split on every non-alphanumeric run, then
/// <c>max(Ratio(sect, sect+diff12), Ratio(sect, sect+diff21), Ratio(sect+diff12, sect+diff21))</c>.
/// </para>
/// </summary>
internal static class FuzzyMatch
{
    /// <summary><c>round(200 * LCS / (|a| + |b|))</c>, away from zero; 100 for two empty strings.</summary>
    internal static int Ratio(string a, string b)
        => RatioLower(a.ToLowerInvariant(), b.ToLowerInvariant());

    /// <summary>The best <see cref="Ratio"/> of the shorter string against every same-length window of the longer.</summary>
    internal static int PartialRatio(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length
            ? (a.ToLowerInvariant(), b.ToLowerInvariant())
            : (b.ToLowerInvariant(), a.ToLowerInvariant());
        if (shorter.Length == 0) return longer.Length == 0 ? 100 : 0;
        if (shorter.Length == longer.Length) return RatioLower(shorter, longer);

        int best = 0;
        for (int i = 0; i + shorter.Length <= longer.Length && best < 100; i++)
            best = Math.Max(best, RatioLower(shorter, longer.Substring(i, shorter.Length)));
        return best;
    }

    /// <summary>
    /// thefuzz's token-set ratio: the shared tokens against each side's shared-plus-own tokens,
    /// best of three. One side's tokens being a subset of the other's scores 100.
    /// </summary>
    internal static int TokenSetRatio(string a, string b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 && tb.Count == 0) return 100;
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var sect = string.Join(' ', ta.Intersect(tb, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var diff12 = string.Join(' ', ta.Except(tb, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var diff21 = string.Join(' ', tb.Except(ta, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var c12 = (sect + " " + diff12).Trim();
        var c21 = (sect + " " + diff21).Trim();

        return Math.Max(RatioLower(sect, c12), Math.Max(RatioLower(sect, c21), RatioLower(c12, c21)));
    }

    /// <summary>Lower-cased tokens split on every run of non-letter/digit characters, as a set.</summary>
    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var lower = s.ToLowerInvariant();
        int start = -1;
        for (int i = 0; i <= lower.Length; i++)
        {
            bool inToken = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (inToken && start < 0) start = i;
            else if (!inToken && start >= 0)
            {
                set.Add(lower[start..i]);
                start = -1;
            }
        }
        return set;
    }

    private static int RatioLower(string a, string b)
    {
        int total = a.Length + b.Length;
        if (total == 0) return 100;
        if (a.Length == 0 || b.Length == 0) return 0;
        return (int)Math.Round(200.0 * Lcs(a, b) / total, MidpointRounding.AwayFromZero);
    }

    /// <summary>Longest common subsequence length, two rows of DP.</summary>
    private static int Lcs(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return prev[b.Length];
    }
}
