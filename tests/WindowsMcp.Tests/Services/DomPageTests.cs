using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-5 (roadmap R8): the pure renderer behind <c>scrape(source:"dom")</c>. The hint is the only
/// thing that tells a model there is more of the page than it was handed, so each threshold gets
/// its own row — a renderer that always says "Reached top" would pass a single happy-path test.
/// </summary>
[Trait("Category", "Unit")]
public class DomPageTests
{
    private const string TopHint = "Reached top of the page; scroll down to see more.";
    private const string BottomHint = "Reached bottom of the page; scroll up to see more.";

    private static ScrollInfo At(double verticalPercent, bool verticallyScrollable = true)
        => new(verticalPercent, 0, verticallyScrollable, false);

    private static SnapshotPage Page(string[] text, ScrollInfo? scroll = null)
        => new("A5 Probe Page", "el_7", "A5 Probe Page", "http://127.0.0.1:9999/a5", scroll, text, null);

    // ---- Hint --------------------------------------------------------------------------------

    [Fact]
    public void Hint_without_a_scroll_pattern_is_null()
        => DomPage.Hint(null).Should().BeNull("no scroll pattern means the page never said it scrolls");

    [Fact]
    public void Hint_for_a_page_that_does_not_scroll_vertically_is_null()
        => DomPage.Hint(At(0, verticallyScrollable: false)).Should().BeNull(
            "the whole page is on screen; there is nothing above or below to point at");

    [Fact]
    public void Hint_at_a_horizontally_scrollable_but_vertically_fixed_page_is_null()
        => DomPage.Hint(new ScrollInfo(0, 40, false, true)).Should().BeNull(
            "only the vertical axis is what 'more of the page' means here");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]      // UIA reports -1 for an axis at rest; it is still the top
    [InlineData(-100)]
    public void Hint_at_the_top_says_to_scroll_down(double percent)
        => DomPage.Hint(At(percent)).Should().Be(TopHint);

    [Theory]
    [InlineData(100)]
    [InlineData(100.5)]
    [InlineData(1000)]
    public void Hint_at_the_bottom_says_to_scroll_up(double percent)
        => DomPage.Hint(At(percent)).Should().Be(BottomHint);

    [Theory]
    [InlineData(42.4, 42)]
    [InlineData(66.6, 67)]
    [InlineData(0.4, 0)]      // rounds to 0 but is NOT the top: the page has already moved
    [InlineData(99.6, 100)]   // rounds to 100 but is NOT the bottom
    [InlineData(50, 50)]
    public void Hint_in_the_middle_reports_the_rounded_percentage(double percent, int rounded)
        => DomPage.Hint(At(percent)).Should()
            .Be($"Scrolled {rounded}% down the page; scroll up or down to see more.");

    // ---- Render ------------------------------------------------------------------------------

    [Fact]
    public void Render_joins_the_lines_with_a_newline()
        => DomPage.Render(Page(["Probe heading", "First paragraph of body text."]))
            .Should().Be("Probe heading\nFirst paragraph of body text.");

    [Fact]
    public void Render_puts_the_hint_after_a_blank_line()
        => DomPage.Render(Page(["Probe heading"], At(0)))
            .Should().Be($"Probe heading\n\n{TopHint}");

    [Fact]
    public void Render_without_a_hint_has_no_trailing_blank_line()
    {
        var rendered = DomPage.Render(Page(["a", "b"]));

        rendered.Should().Be("a\nb");
        rendered.Should().NotEndWith("\n", "a trailing blank line is where the hint would have gone");
    }

    [Fact]
    public void Render_of_an_empty_page_with_a_hint_is_just_the_hint()
        => DomPage.Render(Page([], At(100))).Should().Be(BottomHint,
            "no text means no blank line to separate from");

    [Fact]
    public void Render_of_an_empty_page_without_a_hint_is_empty()
        => DomPage.Render(Page([])).Should().BeEmpty();

    [Fact]
    public void Render_keeps_the_lines_in_document_order_and_does_not_deduplicate_them()
        => DomPage.Render(Page(["Item one", "Item one", "Item two"]))
            .Should().Be("Item one\nItem one\nItem two",
                "the page said it twice; the renderer reports the page, it does not edit it");
}
