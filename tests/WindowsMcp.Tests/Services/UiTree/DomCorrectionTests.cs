using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.NodeFixtures;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-5 phase 1 (R2): upstream's <c>_dom_correction()</c>, ported one rule at a time. Chromium
/// reports proper UIA control types for page content (a probe on Edge 2026: <c>&lt;a&gt;</c> is a
/// Hyperlink, <c>&lt;p&gt;</c> a Text, <c>&lt;input&gt;</c> an Edit), so there is no role map to
/// test — what is left are the three corrections a PAGE walk needs that a window walk does not:
/// <list type="number">
/// <item>the page document is not a control, so it is never listed as interactive (it still gets
/// its id and still appears in the scrollable list);</item>
/// <item>a Text node that only repeats its interactive parent's Name is that control's LABEL, not
/// page content;</item>
/// <item>a Text node with nothing in its Name contributes nothing.</item>
/// </list>
/// Pure, on hand-built nodes: the live browser is <c>UIAutomationDomSnapshotTests</c>'s job, and
/// none of these rules needs one.
/// </summary>
/// <remarks>
/// The API takes (node, parent index) pairs rather than the traverser's <c>UiWalkEntry</c> because
/// a <c>UiWalkEntry</c> carries a live <c>AutomationElement</c>, which cannot be faked — pinning
/// the pure shape here is what keeps these rules testable headlessly (design request in the report).
/// </remarks>
[Trait("Category", "Unit")]
public class DomCorrectionTests
{
    /// <summary>The page document as Chromium reports it: a Document named for the &lt;title&gt;, valued with the URL.</summary>
    private static UiNode Document(
        string name = "A5 Probe Page",
        string? url = "http://127.0.0.1:9999/a5",
        ScrollInfo? scroll = null,
        string window = "A5 Probe Page - Microsoft Edge")
        => Node(controlType: "Document", name: name, window: window, value: url,
            scroll: scroll ?? new ScrollInfo(0, 0, true, false), depth: 0);

    private static UiNode Text(string name, string window = "A5 Probe Page - Microsoft Edge")
        => Node(controlType: "Text", name: name, window: window, depth: 1);

    // ---- R2.1 the page document is not an interactive element ---------------------------------

    [Fact]
    public void SuppressesInteractive_hides_the_page_document_itself()
        // A Document is "fill" in the desktop classifier (modern Notepad's editor is one). The web
        // page is not something to type into, and listing it would put a full-window click target
        // at the top of every page's element list.
        => DomCorrection.SuppressesInteractive(Document(), parentIndex: -1).Should().BeTrue();

    [Fact]
    public void SuppressesInteractive_leaves_a_document_INSIDE_the_page_alone()
        // Only the walk root is the page. An embedded document (an iframe's RootWebArea, a
        // rich-text editor) is content the model may well want to click into.
        => DomCorrection.SuppressesInteractive(Document(name: "An iframe"), parentIndex: 0).Should().BeFalse();

    [Theory]
    [InlineData("Button")]
    [InlineData("Hyperlink")]
    [InlineData("Edit")]
    [InlineData("ListItem")]
    public void SuppressesInteractive_leaves_every_page_control_alone(string controlType)
        // Parity with upstream's interactive set: A-5 removes the document from it and nothing
        // else - a ListItem inside a <ul> stays clickable.
        => DomCorrection.SuppressesInteractive(Node(controlType: controlType), parentIndex: 0).Should().BeFalse();

    [Fact]
    public void SuppressesInteractive_only_hides_a_DOCUMENT_root()
        // Defensive: the same helper runs over a fallback walk whose root is the window itself.
        => DomCorrection.SuppressesInteractive(Node(controlType: "Button"), parentIndex: -1).Should().BeFalse();

    // ---- R2.2 the page text, in document order ------------------------------------------------

    [Fact]
    public void PageText_collects_the_text_nodes_in_walk_order()
    {
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Text("Probe heading"), 0),
            (Text("First paragraph of body text."), 0),
            (Text("inline span text"), 0),
        ];

        DomCorrection.PageText(entries).Should().Equal(
            "Probe heading", "First paragraph of body text.", "inline span text");
    }

    [Fact]
    public void PageText_reads_nothing_but_text_nodes()
    {
        // The controls are already in the Interactive list with their ids; repeating their names
        // as "page text" would double the tokens and tell the model nothing new.
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Button", name: "Press me"), 0),
            (Node(controlType: "Hyperlink", name: "A link to one"), 0),
            (Node(controlType: "Edit", name: "Search", value: "prefilled"), 0),
            (Text("Probe heading"), 0),
        ];

        DomCorrection.PageText(entries).Should().Equal("Probe heading");
    }

    [Fact]
    public void PageText_drops_a_text_node_that_only_repeats_its_interactive_parents_name()
    {
        // Some Chromium builds expose a link's own label as a Text child of the Hyperlink. It is
        // the control's name, already reported on the interactive row - as page text it is noise.
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Hyperlink", name: "A link to one", value: "http://127.0.0.1:9999/one"), 0),
            (Text("A link to one"), 1),
            (Node(controlType: "Button", name: "Press me"), 0),
            (Text("Press me"), 3),
        ];

        DomCorrection.PageText(entries).Should().BeEmpty();
    }

    [Fact]
    public void PageText_keeps_a_text_node_that_says_something_its_interactive_parent_does_not()
    {
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Button", name: "Press me"), 0),
            (Text("and then wait"), 1),
        ];

        DomCorrection.PageText(entries).Should().Equal("and then wait");
    }

    [Fact]
    public void PageText_keeps_a_text_node_that_repeats_a_NON_interactive_parents_name()
    {
        // A Group/Pane often carries its own heading as its Name; the heading is still the page's
        // only copy of that sentence, and dropping it would lose real content.
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Group", name: "Probe heading"), 0),
            (Text("Probe heading"), 1),
        ];

        DomCorrection.PageText(entries).Should().Equal("Probe heading");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void PageText_drops_a_text_node_with_nothing_to_say(string name)
    {
        IReadOnlyList<(UiNode, int)> entries = [(Document(), -1), (Text(name), 0), (Text("Probe heading"), 0)];

        DomCorrection.PageText(entries).Should().Equal("Probe heading");
    }

    [Fact]
    public void PageText_matches_its_interactive_parents_name_exactly_case_included()
    {
        // AMBIGUITY resolved (flagged in the report): correction 2 drops a REPEAT, and the
        // implementation compares Ordinal. So a Text node that differs from its parent's Name only
        // in case is kept - the page really is showing different characters, and losing a line of
        // content is worse than repeating one. Change the comparison and this row must change too.
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Button", name: "Press me"), 0),
            (Text("PRESS ME"), 1),
        ];

        DomCorrection.PageText(entries).Should().Equal("PRESS ME");
    }

    [Fact]
    public void PageText_keeps_two_identical_lines_that_are_both_page_content()
    {
        // Correction 2 is about a node and its PARENT, not a global dedupe: a page that really
        // says "Item one" twice says it twice.
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Text("Item one"), 0),
            (Text("Item one"), 0),
        ];

        DomCorrection.PageText(entries).Should().Equal("Item one", "Item one");
    }

    [Fact]
    public void PageText_keeps_a_text_node_that_has_no_parent_at_all()
        // The other half of the parent lookup, and the last uncovered branch in the file: a node
        // at the walk root has no parent, so it cannot be a repeat of one - it is content.
        => DomCorrection.PageText([(Text("Probe heading"), -1)]).Should().Equal("Probe heading");

    [Fact]
    public void PageText_keeps_a_text_node_whose_parent_index_is_out_of_range()
        // Defensive, and the last uncovered branch in the file: a parent index the entries list
        // cannot resolve is treated as "no parent" - the text is kept - rather than throwing an
        // IndexOutOfRange that would take the whole snapshot call down with it.
        => DomCorrection.PageText([(Document(), -1), (Text("Probe heading"), 99)])
            .Should().Equal("Probe heading");

    [Fact]
    public void PageText_of_a_page_with_no_text_is_empty_not_null()
        => DomCorrection.PageText([(Document(), -1)]).Should().NotBeNull().And.BeEmpty();

    // ---- R2.3 the page itself -----------------------------------------------------------------

    [Fact]
    public void PageFor_reads_the_title_url_scroll_and_window_off_the_document()
    {
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(scroll: new ScrollInfo(12, 0, true, false)), -1),
            (Text("Probe heading"), 0),
            (Text("First paragraph of body text."), 0),
        ];

        var page = DomCorrection.PageFor("el_7", entries);

        page.Window.Should().Be("A5 Probe Page - Microsoft Edge", "the page belongs to the browser WINDOW");
        page.DocumentId.Should().Be("el_7", "the id the walk issued to the document is what scroll takes");
        page.Title.Should().Be("A5 Probe Page", "the document's Name is the page <title>");
        page.Url.Should().Be("http://127.0.0.1:9999/a5", "the document's ValuePattern value is the URL");
        page.Scroll.Should().Be(new ScrollInfo(12, 0, true, false));
        page.Text.Should().Equal("Probe heading", "First paragraph of body text.");
        page.Note.Should().BeNull("a page that was found has nothing to explain");
    }

    [Fact]
    public void PageFor_leaves_scroll_null_when_the_document_has_no_scroll_pattern()
    {
        var page = DomCorrection.PageFor("el_7", [(Node(controlType: "Document", name: "Short page", value: "http://x/", depth: 0), -1)]);

        page.Scroll.Should().BeNull("a page shorter than its window exposes no scroll pattern");
        page.Title.Should().Be("Short page");
    }

    [Fact]
    public void PageFor_applies_the_text_corrections()
    {
        IReadOnlyList<(UiNode, int)> entries =
        [
            (Document(), -1),
            (Node(controlType: "Button", name: "Press me"), 0),
            (Text("Press me"), 1),
            (Text(""), 0),
            (Text("Probe heading"), 0),
        ];

        // ContainSingle, not Equal("Probe heading", because): FluentAssertions' Equal takes
        // params string[], so a because-string there becomes a second EXPECTED element.
        DomCorrection.PageFor("el_7", entries).Text.Should().ContainSingle(
            "PageFor is PageText's only caller in the service - the rules must not be bypassed")
            .Which.Should().Be("Probe heading");
    }

    [Fact]
    public void PageFor_of_a_document_with_no_children_is_a_page_with_no_text()
        // The walk-root-only case: the budget stopped at entry 0, or the page really is empty.
        // The page is still reported (title, URL, scroll) and its text is empty, never null.
        => DomCorrection.PageFor("el_7", [(Document(), -1)]).Text.Should().NotBeNull().And.BeEmpty();

    [Fact]
    public void PageFor_of_a_document_with_no_children_still_suppresses_the_document_row()
        // …and the one entry it does have is the page, which correction 1 keeps out of the
        // interactive list: a one-entry page must not degrade into "a full-window click target".
        => DomCorrection.SuppressesInteractive(Document(), parentIndex: -1).Should().BeTrue();

    [Fact]
    public void PageFor_without_a_document_is_a_programming_error_not_an_empty_page()
    {
        Action act = () => DomCorrection.PageFor("el_7", []);

        act.Should().Throw<ArgumentException>().WithMessage("*entries*");
    }

    [Fact]
    public void NoPage_says_both_that_there_was_no_page_and_what_was_walked_instead()
    {
        var page = DomCorrection.NoPage("Some Browser Window");

        page.Window.Should().Be("Some Browser Window");
        page.DocumentId.Should().BeNull();
        page.Title.Should().BeNull();
        page.Url.Should().BeNull();
        page.Scroll.Should().BeNull();
        page.Text.Should().BeEmpty();
        page.Note.Should().NotBeNullOrWhiteSpace();
        page.Note.Should().ContainEquivalentOf("no page", "the model must be told the DOM walk found nothing");
        page.Note.Should().ContainEquivalentOf("whole window",
            "and that the elements it DID get are the whole window, chrome included");
    }

    [Fact]
    public void NoPage_note_is_the_one_sentence_the_renderer_prints()
        // One wording, one source: the renderer prints Note verbatim, so a reworded note moves
        // both without a second literal to keep in step.
        => DomCorrection.NoPage("Some Browser Window").Note.Should().Be(DomCorrection.NoPageNote);
}
