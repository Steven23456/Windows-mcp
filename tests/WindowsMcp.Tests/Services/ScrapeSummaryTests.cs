using FluentAssertions;
using ModelContextProtocol.Protocol;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// MCP9005: the SDK marks the Sampling feature obsolete as of specification version 2026-07-28
// (SEP-2577). C-5 summarises only through client-side sampling; the suppression matches the one
// on src/WindowsMcp/Services/ScrapeSummary.cs and is the reason it is narrow.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): the sampling request, built purely so the prompt can be asserted without a
/// server, a client or a model. The prompt is the whole quality of the summary — it is what stops
/// the model returning the cookie banner and the navigation menu — so it is pinned by the
/// instructions the design note names, not by a length check.
/// </summary>
[Trait("Category", "Unit")]
public class ScrapeSummaryTests
{
    [Fact]
    public void SystemPrompt_tells_the_model_what_to_throw_away()
    {
        var prompt = ScrapeSummary.SystemPrompt(null, false);

        prompt.Should().ContainEquivalentOf("navigation");
        prompt.Should().ContainEquivalentOf("cookie");
        prompt.Should().ContainEquivalentOf("boilerplate");
        prompt.Should().ContainEquivalentOf("advertis", "advertisements are page furniture, not content");
    }

    [Fact]
    public void SystemPrompt_tells_the_model_what_to_keep_untouched()
    {
        var prompt = ScrapeSummary.SystemPrompt(null, false);

        var saysKeepThemAsWritten =
            prompt.Contains("verbatim", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("exactly", StringComparison.OrdinalIgnoreCase);

        saysKeepThemAsWritten.Should().BeTrue(
            "names, numbers, dates and prices are the reason a summary is worth reading, and the "
            + "prompt has to say they are copied rather than paraphrased");
    }

    [Fact]
    public void SystemPrompt_without_a_query_asks_for_a_faithful_summary_of_the_page()
    {
        var prompt = ScrapeSummary.SystemPrompt(null, false);

        prompt.Should().ContainEquivalentOf("summar", "with no query the job is to summarise the page");
        prompt.Should().NotContainEquivalentOf("question",
            "there is no question to answer; a prompt that invents one invites a hallucinated answer");
    }

    [Fact]
    public void SystemPrompt_with_a_query_carries_it_and_allows_for_no_answer()
    {
        var prompt = ScrapeSummary.SystemPrompt("what is the price", false);

        prompt.Should().Contain("what is the price", "the query is what the summary is focused on");
        prompt.Should().ContainEquivalentOf("does not answer",
            "a page that does not answer the query must be reported as such, not guessed at");
    }

    [Fact]
    public void Request_carries_the_prompt_the_content_and_the_budget()
    {
        var request = ScrapeSummary.Request("the page text", "what is the price", false);

        request.SystemPrompt.Should().Be(ScrapeSummary.SystemPrompt("what is the price", false),
            "the request's prompt is the pure one, so the two cannot drift");
        request.Messages.Should().ContainSingle().Which.Role.Should().Be(Role.User);
        request.Messages[0].Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("the page text", "the page goes over as text, whole and unedited");
        request.MaxTokens.Should().Be(ScrapeSummary.MaxTokens);
    }

    [Fact]
    public void Request_without_a_query_still_builds_a_prompt()
    {
        var request = ScrapeSummary.Request("the page text", null, false);

        request.SystemPrompt.Should().NotBeNullOrWhiteSpace();
        request.SystemPrompt.Should().Be(ScrapeSummary.SystemPrompt(null, false));
    }

    // ---- F13: the model is told when it is only holding the beginning of the page ---------------

    /// <summary>
    /// F13: <c>max_chars</c> (or an element budget that cut the walk short) hands the model the
    /// FRONT of a page and nothing else. A model that is not told reads the fragment as the whole
    /// document and answers "the page does not mention it" about text it was never shown — the
    /// worst possible failure, because it is indistinguishable from a real answer.
    /// </summary>
    /// <remarks>
    /// The two ideas are matched against a small set of spellings rather than one exact sentence
    /// (the <c>saysKeepThemAsWritten</c> precedent above): the wording is the implementer's, the
    /// two claims are not.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("what is the price")]
    public void SystemPrompt_of_a_truncated_page_says_the_text_is_only_the_beginning(string? query)
    {
        var prompt = ScrapeSummary.SystemPrompt(query, true);

        var saysItIsOnlyTheStart =
            prompt.Contains("beginning", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("first part", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("start of", StringComparison.OrdinalIgnoreCase);
        saysItIsOnlyTheStart.Should().BeTrue(
            "the model has to know the text stops before the page does; the prompt was: " + prompt);

        var warnsAgainstConcludingTheRestIsMissing =
            prompt.Contains("do not claim", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("not conclude", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("does not contain", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("beyond", StringComparison.OrdinalIgnoreCase);
        warnsAgainstConcludingTheRestIsMissing.Should().BeTrue(
            "saying 'the page does not say' about text past the cut is a wrong answer that reads "
            + "like a right one; the prompt was: " + prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("what is the price")]
    public void SystemPrompt_of_a_whole_page_does_not_mention_a_cut(string? query)
    {
        var prompt = ScrapeSummary.SystemPrompt(query, false);

        prompt.Should().NotBe(ScrapeSummary.SystemPrompt(query, true),
            "a whole page and a cut one are different jobs, and the prompt is the only place the "
            + "difference can be stated");
        prompt.Should().NotContainEquivalentOf("beginning",
            "nothing was cut, so warning the model about a missing remainder would invite hedging "
            + "about a page it holds in full");
        prompt.Should().NotContainEquivalentOf("truncat");
    }

    [Fact]
    public void SystemPrompt_of_a_truncated_page_still_carries_the_query_and_the_rules()
    {
        var prompt = ScrapeSummary.SystemPrompt("what is the price", true);

        prompt.Should().Contain("what is the price", "the cut does not change what was asked");
        prompt.Should().ContainEquivalentOf("navigation", "nor which page furniture to throw away");
    }

    [Fact]
    public void Request_of_a_truncated_page_uses_the_truncated_prompt()
    {
        var request = ScrapeSummary.Request("the page text", "what is the price", true);

        request.SystemPrompt.Should().Be(ScrapeSummary.SystemPrompt("what is the price", true),
            "the request's prompt is the pure one, so the two cannot drift");
        request.SystemPrompt.Should().NotBe(ScrapeSummary.SystemPrompt("what is the price", false));
        request.Messages[0].Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("the page text", "the content is still sent as it was capped");
    }

    [Fact]
    public void MaxTokens_is_a_sane_output_budget()
    {
        ScrapeSummary.MaxTokens.Should().BeInRange(256, 4096,
            "too small truncates the summary mid-sentence; too large bills the client for nothing");
        ScrapeSummary.MaxTokens.Should().Be(2048,
            "the design note names the number, and the client is billed for it - a change here is "
            + "a decision to re-take, not a tuning knob to nudge");
    }
}
#pragma warning restore MCP9005
