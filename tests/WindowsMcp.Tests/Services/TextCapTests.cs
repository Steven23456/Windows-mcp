using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-5 (roadmap R8): the one cut both <c>scrape</c> sources share, so <c>max_chars</c> means the
/// same thing whether the text was fetched or walked. The surrogate rule is the reason it exists
/// as a helper at all: a naive <c>text[..maxChars]</c> can end a string with half a character,
/// which serialises as U+FFFD and corrupts the last emoji or CJK extension character on the page.
/// </summary>
[Trait("Category", "Unit")]
public class TextCapTests
{
    /// <summary>U+1F600 GRINNING FACE — two UTF-16 chars, one character.</summary>
    private const string Grin = "\U0001F600";

    [Fact]
    public void Cut_under_the_cap_returns_the_text_unchanged()
    {
        var cut = TextCap.Cut("abc", 10, out var truncated);

        cut.Should().Be("abc");
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Cut_exactly_at_the_cap_is_not_truncated()
    {
        var cut = TextCap.Cut("abcde", 5, out var truncated);

        cut.Should().Be("abcde");
        truncated.Should().BeFalse("nothing was removed at exactly the cap - off by one here loses a character");
    }

    [Fact]
    public void Cut_one_over_the_cap_keeps_the_front_and_says_it_cut()
    {
        var cut = TextCap.Cut("abcdef", 5, out var truncated);

        cut.Should().Be("abcde");
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Cut_of_an_empty_string_is_empty_and_untruncated()
    {
        var cut = TextCap.Cut("", 5, out var truncated);

        cut.Should().BeEmpty();
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Cut_that_would_split_a_surrogate_pair_takes_one_char_less()
    {
        // "ab" + the two halves of one emoji: a cut at 3 lands between them.
        var cut = TextCap.Cut("ab" + Grin, 3, out var truncated);

        cut.Should().Be("ab", "half a character is not a character");
        truncated.Should().BeTrue();
        char.IsHighSurrogate(cut[^1]).Should().BeFalse("the result never ends on an unpaired lead surrogate");
    }

    [Fact]
    public void Cut_that_lands_after_a_whole_surrogate_pair_keeps_it()
    {
        var cut = TextCap.Cut("ab" + Grin + "cd", 4, out var truncated);

        cut.Should().Be("ab" + Grin, "the pair is complete at 4, so it survives whole");
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Cut_of_one_char_on_a_string_that_starts_with_a_surrogate_pair_is_empty()
    {
        var cut = TextCap.Cut(Grin + "abc", 1, out var truncated);

        cut.Should().BeEmpty("there is no whole character that fits in one UTF-16 unit here");
        truncated.Should().BeTrue("the caller still has to know the text was cut, even to nothing");
    }

    /// <summary>
    /// A cap of 0 cannot arrive through <c>scrape</c> — both the tool and the service refuse it
    /// (0 is not "all") — but the helper still has to be total: the surrogate step-back must not
    /// index <c>text[-1]</c> if a future caller passes one.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("\U0001F600abc")]
    public void Cut_of_zero_is_empty_and_never_indexes_before_the_start(string text)
    {
        var act = () => TextCap.Cut(text, 0, out _);

        act.Should().NotThrow();
        TextCap.Cut(text, 0, out var truncated).Should().BeEmpty();
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Cut_keeps_a_combining_sequence_it_was_asked_to_split()
    {
        // Only surrogate pairs are protected. "e" + U+0301 (combining acute) is two UTF-16
        // units to the cap and the cut is allowed to land between them; pinned so the rule
        // cannot quietly widen into grapheme clusters, which would change what max_chars counts.
        var text = "e" + (char)0x0301 + "x";

        var cut = TextCap.Cut(text, 1, out var truncated);
        cut.Should().Be("e");
        truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("line one\r\nline two", 10, "line one\r\n")]
    [InlineData("line one\r\nline two", 9, "line one\r")]
    public void Cut_does_not_treat_CRLF_as_one_unit(string text, int max, string expected)
        => TextCap.Cut(text, max, out _).Should().Be(expected,
            "the cap counts UTF-16 units; only surrogate pairs are indivisible");
}
