using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10 / roadmap C6: the three <c>thefuzz</c> scorers, in-repo and package-free, pinned to a
/// table of scores computed from the definitions below rather than to whatever the first
/// implementation happens to return.
/// <para>
/// The definitions, which are the contract:
/// <list type="bullet">
/// <item><c>Ratio(a,b)</c> = <c>round(200 * LCS(a,b) / (|a| + |b|))</c>, away from zero — the
/// indel ratio <c>thefuzz</c> computes via <c>Levenshtein.ratio</c> (substitution cost 2), and
/// the same value <c>SequenceMatcher.ratio</c> gives for strings like these.</item>
/// <item><c>PartialRatio(a,b)</c> = the best <c>Ratio</c> of the shorter string against any
/// window of that same length inside the longer one (equal lengths → <c>Ratio</c>).</item>
/// <item><c>TokenSetRatio(a,b)</c> = <c>thefuzz</c>'s token-set: lower-case, split on runs of
/// non-alphanumeric characters, <c>sect</c> = sorted intersection, <c>c12</c>/<c>c21</c> =
/// <c>sect</c> plus each side's sorted remainder, result =
/// <c>max(Ratio(sect,c12), Ratio(sect,c21), Ratio(c12,c21))</c>. The property that matters:
/// one side's tokens being a subset of the other's scores 100.</item>
/// </list>
/// All three lower-case first, are symmetric, score 100 for two equal strings, 100 for two empty
/// ones, and 0 when exactly one side is empty.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class FuzzyMatchTests
{
    /// <summary>
    /// The table. Every row is (a, b, Ratio, PartialRatio, TokenSetRatio) and every number was
    /// computed from the definitions above, not observed from an implementation. The rows are
    /// chosen for what they discriminate: row 3 is the case B-10 exists for
    /// ("notepad" must find "Untitled - Notepad" on partial/token-set but scores only 56 on the
    /// plain ratio, which is why the threshold is not applied to Ratio); row 4 is the negative
    /// case (nothing about "edge" reaches 70 against a Notepad title); rows 5-8 are the launcher
    /// names B-8 will reuse.
    /// </summary>
    public static TheoryData<string, string, int, int, int> Table => new()
    {
        { "notepad", "notepad", 100, 100, 100 },
        { "Notepad", "notepad", 100, 100, 100 },                                 // case-insensitive
        { "notepad", "Untitled - Notepad", 56, 100, 100 },
        { "edge", "Untitled - Notepad", 27, 50, 30 },                            // every scorer stays under 70
        { "visual studio code", "Visual Studio Code - Insiders", 77, 100, 100 },
        { "chrome", "Google Chrome", 63, 100, 100 },
        { "calc", "Calculator", 57, 100, 57 },                                   // one token each: no intersection
        { "explorer", "File Explorer", 76, 100, 100 },
        { "code", "Visual Studio Code - Insiders", 24, 100, 100 },               // short needle, long haystack
        { "windows-mcp", "Windows MCP", 91, 91, 100 },                           // punctuation is a token separator
        { "notepad", "Document1 - Word", 26, 29, 29 },
        { "abc", "abd", 67, 67, 67 },
        { "a", "b", 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Ratio_scores_the_table(string a, string b, int ratio, int partial, int tokenSet)
    {
        _ = partial;
        _ = tokenSet;

        FuzzyMatch.Ratio(a, b).Should().Be(ratio);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void PartialRatio_scores_the_table(string a, string b, int ratio, int partial, int tokenSet)
    {
        _ = ratio;
        _ = tokenSet;

        FuzzyMatch.PartialRatio(a, b).Should().Be(partial);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void TokenSetRatio_scores_the_table(string a, string b, int ratio, int partial, int tokenSet)
    {
        _ = ratio;
        _ = partial;

        FuzzyMatch.TokenSetRatio(a, b).Should().Be(tokenSet);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_scorer_is_symmetric(string a, string b, int ratio, int partial, int tokenSet)
    {
        // The matcher compares a request against a title in one order only; a scorer that is not
        // symmetric would make the same pair score differently in B-8's app catalog.
        FuzzyMatch.Ratio(b, a).Should().Be(ratio);
        FuzzyMatch.PartialRatio(b, a).Should().Be(partial);
        FuzzyMatch.TokenSetRatio(b, a).Should().Be(tokenSet);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("a")]
    [InlineData("Untitled - Notepad")]
    public void Every_scorer_gives_a_string_full_marks_against_itself(string s)
    {
        FuzzyMatch.Ratio(s, s).Should().Be(100);
        FuzzyMatch.PartialRatio(s, s).Should().Be(100);
        FuzzyMatch.TokenSetRatio(s, s).Should().Be(100);
    }

    [Fact]
    public void Two_empty_strings_score_100_and_one_empty_string_scores_0()
    {
        // Defined, not accidental: nothing versus nothing is a perfect match, and a caller who
        // sends an empty title must not sail past a >= 70 threshold on every window on the desktop.
        FuzzyMatch.Ratio("", "").Should().Be(100);
        FuzzyMatch.PartialRatio("", "").Should().Be(100);
        FuzzyMatch.TokenSetRatio("", "").Should().Be(100);

        FuzzyMatch.Ratio("", "notepad").Should().Be(0);
        FuzzyMatch.PartialRatio("", "notepad").Should().Be(0);
        FuzzyMatch.TokenSetRatio("", "notepad").Should().Be(0);
        FuzzyMatch.Ratio("notepad", "").Should().Be(0);
        FuzzyMatch.PartialRatio("notepad", "").Should().Be(0);
        FuzzyMatch.TokenSetRatio("notepad", "").Should().Be(0);
    }

    [Theory]
    // A title with only punctuation left after tokenising has no tokens at all.
    [InlineData("---", "notepad", 0)]
    // Whitespace-only is the same case, and must not be treated as a token.
    [InlineData("   ", "notepad", 0)]
    public void TokenSetRatio_treats_a_string_with_no_tokens_as_empty(string a, string b, int expected)
    {
        FuzzyMatch.TokenSetRatio(a, b).Should().Be(expected);
    }

    [Fact]
    public void TokenSetRatio_is_100_when_one_sides_tokens_are_a_subset_of_the_others()
    {
        // The property the >= 70 threshold rests on, stated directly: word order and extra words
        // on the window's side cost nothing.
        FuzzyMatch.TokenSetRatio("studio code visual", "Visual Studio Code - Insiders").Should().Be(100);
        FuzzyMatch.TokenSetRatio("notepad untitled", "Untitled - Notepad").Should().Be(100);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_score_is_inside_0_to_100(string a, string b, int ratio, int partial, int tokenSet)
    {
        _ = ratio;
        _ = partial;
        _ = tokenSet;

        FuzzyMatch.Ratio(a, b).Should().BeInRange(0, 100);
        FuzzyMatch.PartialRatio(a, b).Should().BeInRange(0, 100);
        FuzzyMatch.TokenSetRatio(a, b).Should().BeInRange(0, 100);
    }

    [Theory]
    // (needle, haystack, Ratio, PartialRatio, TokenSetRatio) for a one-character side.
    [InlineData("a", "abc", 50, 100, 50)]
    [InlineData("a", "notepad", 25, 100, 25)]
    [InlineData("a", "b", 0, 0, 0)]
    public void A_one_character_string_scores_100_on_partial_ratio_against_anything_containing_it(
        string a, string b, int ratio, int partial, int tokenSet)
    {
        // thefuzz's partial_ratio semantics, kept deliberately: the shorter string is compared
        // against every window of its own length in the longer one, so a single character that
        // appears anywhere scores 100. It is the reason WindowMatcher tries exact and substring
        // first and only then fuzzes - see WindowMatcherTests' one-character window test, which
        // pins what that means for a desktop.
        FuzzyMatch.Ratio(a, b).Should().Be(ratio);
        FuzzyMatch.PartialRatio(a, b).Should().Be(partial);
        FuzzyMatch.TokenSetRatio(a, b).Should().Be(tokenSet);
    }

    [Fact]
    public void Unicode_in_a_title_is_scored_not_stripped()
    {
        // Window titles carry anything: an accented letter inside a token, an em dash between
        // tokens. Both are UTF-8 literals, as elsewhere in this suite (WindowServiceTests'
        // private-use range check is the precedent).
        const string cafe = "café";
        const string title = "café — Notepad";

        FuzzyMatch.Ratio(cafe, cafe).Should().Be(100);
        FuzzyMatch.PartialRatio(cafe, title).Should().Be(100,
            "the accented character is part of the needle, not a character the scorer drops");
        FuzzyMatch.TokenSetRatio(cafe, "CAFÉ — Notepad").Should().Be(100,
            "tokenising splits on the em dash and lower-casing is culture-invariant, so the "
            + "request's single token is a subset of the title's two");
    }

    // ---- Abbreviations: "vs code" is Visual Studio Code, not QR Code Scanner -------------------
    //
    // The rule (fix/app-catalog-matching, round 2 - the strict one). Tokens as above (lower-case,
    // split on every non-letter/digit run) but kept as an ORDERED LIST, repeats and all:
    //   * ANCHORED, NO SKIPPING: request token 0 is read at candidate token 0, and each next request
    //     token at the very next unread candidate token. No candidate token is ever skipped.
    //   * A request token of 2+ characters reads as a WORD PREFIX (the candidate token starts with
    //     it; consumes 1) or as INITIALS (its k characters are the first characters of the next k
    //     candidate tokens, k >= 2; consumes k). A 1-character token never reads.
    //   * If EVERY request token read as initials, the reading must end exactly at the last
    //     candidate token. Once any token read as a word prefix, trailing tokens may stay unread.
    //   * AbbreviationUnread = the fewest trailing tokens left unread over every reading, or -1
    //     when there is none; AbbreviationCovers = AbbreviationUnread >= 0. A side with no tokens
    //     never abbreviates anything.
    // Round 1 let tokens be skipped and initials stop short of the end. On a real catalog that
    // read "mps" as VLC [media player skinned], "uy" as [Uninstall YubiKey] Manager and "16" as
    // Python [1]3 ([6]4-bit): refusals became confident launches of the wrong app.

    /// <summary>
    /// (request, candidate, unread). Every value follows from the rule above and was checked
    /// against an independent reference walk before the implementation existed. The rows marked
    /// "round 1" were true under the loose rule and are refusals now.
    /// </summary>
    public static TheoryData<string, string, int> UnreadTable => new()
    {
        // reads, nothing left over
        { "vs code", "Visual Studio Code", 0 },          // initials "visual studio", then the word "code"
        { "vsc", "Visual Studio Code", 0 },               // initials over all three tokens
        { "wt", "Windows Terminal", 0 },
        { "ssms", "SQL Server Management Studio", 0 },
        { "sm", "Server Manager", 0 },
        { "7z", "7-Zip", 0 },                             // digits are token characters; '-' separates
        { "vs 2026", "Visual Studio 2026", 0 },
        { "mt", "Movies & TV", 0 },                       // '&' is a separator, not a token
        { "vs", "Visual Studio", 0 },
        { "vi st co", "Visual Studio Code", 0 },          // three word prefixes at exactly 2 characters
        { "vs-code", "Visual Studio Code", 0 },           // punctuation separates the request's tokens too
        { "term", "Tiny Easy Remote Manager", 0 },

        // a word prefix lets trailing tokens stay unread - and the count is reported
        { "vs code", "Visual Studio Code Insiders", 1 },
        { "visual studio", "Visual Studio Code", 1 },
        { "vis", "Visual Studio Code", 2 },

        // initials alone must reach the last token
        { "vsc", "Visual Studio Code Insiders", -1 },
        { "vs", "Visual Studio Code", -1 },               // round 1: true
        { "sm", "SQL Server Management Studio", -1 },     // and 's','s' is not 's','m' anyway
        { "7z", "7-Zip File Manager", -1 },               // round 1: true
        { "7z", "7-Zip Help", -1 },
        { "uy", "Uninstall YubiKey Manager", -1 },
        { "uu", "Uninstall UltiMaker Cura", -1 },
        { "dcp", "Developer Command Prompt for VS", -1 },
        { "td", "Click to Do", -1 },
        { "mt", "Microsoft To Do", -1 },
        { "ms edge", "Microsoft Edge", -1 },              // "ms" is not the prefix "mi"; as initials "me" it leaves "edge" nothing

        // anchored: nothing before the first match is skipped ...
        { "mps", "VLC media player skinned", -1 },
        { "16", "Python 3.13 (64-bit)", -1 },
        { "10", "my.ini (MariaDB 13.0 (x64))", -1 },
        { "52", "Blender 5.2.3.0", -1 },
        { "td", "Microsoft To Do", -1 },
        { "vs 2026", "Open Sample ASP.NET Core Application in Visual Studio 2026 - Telerik UI for ASP.NET Core", -1 },
        { "vs code", "Microsoft Visual Studio Code", -1 }, // round 1: true (skipped "microsoft")
        { "code", "Visual Studio Code", -1 },             // round 1: true (skipped two tokens)
        { "term", "Windows Terminal", -1 },

        // ... and nothing between two matches
        { "vis code", "Visual Studio Code", -1 },         // round 1: true (skipped "studio")
        { "vc", "Visual Studio Code", -1 },
        { "aab bx", "Ax Aab Bx", -1 },                    // round 1: true (skipped "ax")

        // other refusals, unchanged from round 1
        { "vs code", "QR Code Scanner", -1 },
        { "v code", "Visual Studio Code", -1 },           // a 1-character token never reads
        { "v", "Visual", -1 },
        { "code vs", "Visual Studio Code", -1 },          // order matters
        { "vsco", "Visual Studio Code", -1 },             // initials do not run on into a prefix
        { "code code", "Visual Studio Code", -1 },        // no candidate token is read twice
        { "visual studio code", "Visual Studio", -1 },    // every request token must read
        { "vsc", "Visual Studio", -1 },                   // initials past the last token: -1, not an index error
        { "wt", "WordPad", -1 },                          // one word has no two-word initials
        { "aa bb cc", "Aa Bb", -1 },                      // more request words than name words: each needs at least one
    };

    [Theory]
    [MemberData(nameof(UnreadTable))]
    public void AbbreviationUnread_reads_the_table(string request, string candidate, int unread)
    {
        FuzzyMatch.AbbreviationUnread(request, candidate).Should().Be(unread,
            $"'{request}' over '{candidate}' leaves {unread} token(s) unread (-1: it does not abbreviate it)");
    }

    [Theory]
    [MemberData(nameof(UnreadTable))]
    public void AbbreviationCovers_is_true_exactly_when_AbbreviationUnread_is_not_negative(
        string request, string candidate, int unread)
    {
        FuzzyMatch.AbbreviationCovers(request, candidate).Should().Be(unread >= 0,
            $"AbbreviationCovers is AbbreviationUnread >= 0, and '{request}' over '{candidate}' is {unread}");
    }

    [Theory]
    [InlineData("VS CODE", "Visual Studio Code")]
    [InlineData("vs code", "VISUAL STUDIO CODE")]
    [InlineData("Vs Code", "visual studio code")]
    public void AbbreviationCovers_ignores_case_on_both_sides(string request, string candidate)
    {
        FuzzyMatch.AbbreviationUnread(request, candidate).Should().Be(0);
        FuzzyMatch.AbbreviationCovers(request, candidate).Should().BeTrue();
    }

    [Theory]
    // "db" reads as the word "db" or as the initials of "DB Browser". Only the word reading leaves
    // "browser" for the second token: an implementation that tries initials first and never
    // retries says -1.
    [InlineData("db browser", "DB Browser for SQLite", 2)]
    // The mirror: "ab" reads as a prefix of "Abc" or as the initials of "Abc Bxx". Only the
    // initials reading puts "cd" at "Cd": prefix-first with no retry says -1.
    [InlineData("ab cd", "Abc Bxx Cd", 0)]
    public void AbbreviationUnread_retries_the_other_reading_when_the_first_one_dead_ends(
        string request, string candidate, int unread)
    {
        FuzzyMatch.AbbreviationUnread(request, candidate).Should().Be(unread);
        FuzzyMatch.AbbreviationCovers(request, candidate).Should().BeTrue();
    }

    [Theory]
    // Both readings succeed. Prefix-first finds "abz" + "bcd" and leaves one "bcd" unread (1);
    // initials-first finds "abz bcd" + "bcd" and leaves none (0). The fewest wins.
    [InlineData("ab bc", "Abz Bcd Bcd", 0)]
    // And the other way round: initials-first finds "ab b" + "bbc" and leaves "cx" (1);
    // prefix-first finds "ab" + the initials of "b bbc cx" and leaves none (0).
    [InlineData("ab bbc", "Ab B Bbc Cx", 0)]
    public void AbbreviationUnread_reports_the_reading_with_the_fewest_unread_not_the_first_found(
        string request, string candidate, int unread)
    {
        FuzzyMatch.AbbreviationUnread(request, candidate).Should().Be(unread,
            "every reading is tried and the best one counts; stopping at the first success depends on which rule is tried first");
    }

    [Theory]
    // Ten 2-character request tokens, the last unreadable, against 30 tokens every one of which
    // the other nine read both as a word prefix and (in pairs) as initials. Round 1's walk with no
    // memo took 59.6 s on this one call. AppCatalog.Match makes the call once per catalog entry.
    [InlineData(9, 30)]
    // The strict rule only branches between "prefix" and "initials" at each token, so the first
    // shape is cheap even without a memo. Forty tokens against eighty is 2^39 readings walked
    // naively, and 40 x 81 (request index, candidate index) states memoised.
    [InlineData(39, 80)]
    public async Task AbbreviationUnread_stays_fast_on_a_pathological_request_and_name(int readable, int nameTokens)
    {
        var request = string.Join(' ', Enumerable.Repeat("aa", readable)) + " zz";
        var candidate = string.Join(' ', Enumerable.Repeat("aa", nameTokens));

        var call = Task.Run(() => (Unread: FuzzyMatch.AbbreviationUnread(request, candidate),
                                   Covers: FuzzyMatch.AbbreviationCovers(request, candidate)));
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().BeSameAs(call,
            "a launch(\"...\") must not spend seconds deciding one catalog entry; the budget is 5 s and a memoised walk takes microseconds");
        var (unread, covers) = await call;
        unread.Should().Be(-1, "'zz' reads nothing in the name");
        covers.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "Visual Studio Code")]
    [InlineData("   ", "Visual Studio Code")]
    [InlineData("---", "Visual Studio Code")]
    [InlineData("vs", "")]
    [InlineData("vs", "---")]
    [InlineData("", "")]
    public void AbbreviationCovers_is_false_when_either_side_has_no_tokens(string request, string candidate)
    {
        // "Every request token reads" is vacuously true of zero tokens. Taken literally, a request
        // of "---" would abbreviate every app in the catalog at 95 and launch() would open the
        // shortest name on the machine. Nothing abbreviates nothing.
        FuzzyMatch.AbbreviationUnread(request, candidate).Should().Be(-1);
        FuzzyMatch.AbbreviationCovers(request, candidate).Should().BeFalse();
    }
}
