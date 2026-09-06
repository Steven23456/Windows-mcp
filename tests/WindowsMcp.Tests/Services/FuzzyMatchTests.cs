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
}
