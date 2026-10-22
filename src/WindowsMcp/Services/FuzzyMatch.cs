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

    /// <summary>True when <paramref name="request"/> abbreviates <paramref name="candidate"/> (<see cref="AbbreviationUnread"/> is not -1).</summary>
    internal static bool AbbreviationCovers(string request, string candidate)
        => AbbreviationUnread(request, candidate) >= 0;

    /// <summary>
    /// -1 when <paramref name="request"/> does not abbreviate <paramref name="candidate"/>, else the
    /// fewest candidate tokens left unread at the end. Anchored at the first candidate token, no
    /// token skipped; each request token (2+ chars) reads as a word prefix (1 token) or as the
    /// initials of the next k &gt;= 2 tokens; an all-initials reading must end at the last token.
    /// Case-insensitive; see <c>FuzzyMatchTests</c>. So <c>vs code</c> reads "Visual Studio Code"
    /// to the end and <c>wt</c> "Windows Terminal", which the scorers rate no higher than "QR Code
    /// Scanner" or "WordPad", while <c>mps</c> cannot start at "VLC media player skinned"'s second
    /// word and <c>uy</c> cannot stop short of "Uninstall YubiKey Manager"'s last. A side with no
    /// tokens reads nothing, so a request of only punctuation never matches every name.
    /// </summary>
    internal static int AbbreviationUnread(string request, string candidate)
    {
        var req = TokenList(request);
        var cand = TokenList(candidate);
        // Every request token reads at least one candidate token, so more of them cannot fit.
        if (req.Count == 0 || req.Count > cand.Count) return -1;
        var memo = new int?[req.Count, cand.Count + 1, 2];
        int end = End(0, 0, false);
        return end < 0 ? -1 : cand.Count - end;

        // The furthest candidate token a reading of request tokens r onwards can end at, starting
        // at candidate token p, or -1; word says an earlier token was read as a word prefix. Both
        // readings of each token are tried ("db browser" over "DB Browser for SQLite" must take
        // "db" whole, "ab cd" over "Abc Bxx Cd" as initials), and each (r, p, word) is worked out
        // once, so a crafted name cannot make the search exponential.
        int End(int r, int p, bool word)
        {
            if (r == req.Count) return word || p == cand.Count ? p : -1;
            if (memo[r, p, word ? 1 : 0] is { } known) return known;
            var token = req[r];
            int best = -1;
            if (token.Length >= 2 && p < cand.Count)
            {
                if (cand[p].StartsWith(token, StringComparison.Ordinal))
                    best = End(r + 1, p + 1, true);
                if (p + token.Length <= cand.Count && IsInitials(token, p))
                    best = Math.Max(best, End(r + 1, p + token.Length, word));
            }
            memo[r, p, word ? 1 : 0] = best;
            return best;
        }

        bool IsInitials(string token, int start)
        {
            for (int i = 0; i < token.Length; i++)
                if (cand[start + i][0] != token[i]) return false;
            return true;
        }
    }

    /// <summary>Lower-cased tokens split on every run of non-letter/digit characters, as a set.</summary>
    private static HashSet<string> Tokens(string s) => new(TokenList(s), StringComparer.Ordinal);

    /// <summary>The same tokens in order, repeats kept - what an abbreviation is read against.</summary>
    private static List<string> TokenList(string s)
    {
        var tokens = new List<string>();
        var lower = s.ToLowerInvariant();
        int start = -1;
        for (int i = 0; i <= lower.Length; i++)
        {
            bool inToken = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (inToken && start < 0) start = i;
            else if (!inToken && start >= 0)
            {
                tokens.Add(lower[start..i]);
                start = -1;
            }
        }
        return tokens;
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
