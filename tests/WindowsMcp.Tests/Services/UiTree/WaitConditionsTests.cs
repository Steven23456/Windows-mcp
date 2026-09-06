using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// B-6 (R6-R28): the pure half of <c>wait_for</c>. One poll's evidence in, a verdict and the
/// sentence the agent reads out — no UIA, no clock, no desktop, so every condition (including the
/// ones a live desktop only produces by luck: a disabled control, a page whose text is below the
/// fold) is pinned here. The loop that gathers the evidence is <c>WaitForServiceTests</c>; the
/// wiring to a real desktop is <c>UIAutomationToolsWaitForDesktopTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class WaitConditionsTests
{
    private static ElementInfo Match(
        string id = "el_1", string name = "Save", string controlType = "Button",
        bool enabled = true, string? value = null)
        => new(id, name, controlType, enabled, IsOffscreen: false,
               Bounds: new Bounds(10, 20, 100, 30), Value: value, IsChecked: null, IsSelected: null);

    private static WaitEvidence Found(params ElementInfo[] matches) => new(Matches: matches);

    private static WaitEvidence Screen(
        SnapshotElement[]? interactive = null, SnapshotScrollable[]? scrollable = null, SnapshotPage[]? pages = null)
        => new(Snapshot: Result(interactive: interactive, scrollable: scrollable, pages: pages));

    private static WaitEvidence Desktop(params WindowInfo[] windows) => new(Windows: windows);

    // ---- element_exists: today's wait_for, given a name ---------------------------------------

    [Fact]
    public void Element_exists_is_satisfied_by_any_match_and_names_it()
    {
        var hit = Match();

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.ElementExists, "Save", Found(hit));

        satisfied.Should().BeTrue();
        detail.Should().Be("found 'Save' (el_1)", "the agent's next call is by id, so the id is part of the answer");
        element.Should().BeSameAs(hit);
    }

    [Fact]
    public void Element_exists_returns_the_first_match_when_several_hit()
    {
        var first = Match("el_1", "Save");
        var second = Match("el_2", "Save As");

        var (satisfied, _, element) = WaitConditions.Evaluate(WaitCondition.ElementExists, "Save", Found(first, second));

        satisfied.Should().BeTrue();
        element.Should().BeSameAs(first, "find returns matches in walk order; the first is the answer");
    }

    [Fact]
    public void Element_exists_is_not_satisfied_by_an_empty_result_and_says_what_was_wanted()
    {
        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.ElementExists, "Save", Found());

        satisfied.Should().BeFalse();
        detail.Should().Be("no element matching 'Save'");
        element.Should().BeNull();
    }

    [Fact]
    public void Element_exists_treats_evidence_it_never_got_as_no_match()
    {
        // A poll that gathered nothing must not throw: the loop has to be able to report "not yet"
        // rather than crash on its own bookkeeping.
        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ElementExists, "Save", new WaitEvidence());

        satisfied.Should().BeFalse();
        detail.Should().Be("no element matching 'Save'");
    }

    // ---- element_enabled ---------------------------------------------------------------------

    [Fact]
    public void Element_enabled_is_satisfied_only_by_an_enabled_match()
    {
        var hit = Match(enabled: true);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.ElementEnabled, "Save", Found(hit));

        satisfied.Should().BeTrue();
        detail.Should().Contain("'Save'").And.Contain("el_1").And.Contain("enabled");
        element.Should().BeSameAs(hit);
    }

    [Fact]
    public void Element_enabled_reports_a_match_that_is_still_disabled()
    {
        // The distinction the condition exists for: "the button is not there yet" and "the button
        // is there and greyed out" are different states, and the agent acts differently on each.
        var (satisfied, detail, element) = WaitConditions.Evaluate(
            WaitCondition.ElementEnabled, "Save", Found(Match(enabled: false)));

        satisfied.Should().BeFalse();
        detail.Should().Contain("'Save'").And.Contain("el_1").And.Contain("disabled");
        detail.Should().NotContain("no element matching", "it WAS found - only disabled");
        element.Should().BeNull("the element is reported only when the condition is satisfied");
    }

    [Fact]
    public void Element_enabled_skips_a_disabled_match_for_an_enabled_one()
    {
        var disabled = Match("el_1", "Save", enabled: false);
        var enabled = Match("el_2", "Save", enabled: true);

        var (satisfied, _, element) = WaitConditions.Evaluate(
            WaitCondition.ElementEnabled, "Save", Found(disabled, enabled));

        satisfied.Should().BeTrue("one enabled match is what was asked for");
        element.Should().BeSameAs(enabled);
    }

    [Fact]
    public void Element_enabled_with_no_match_reports_the_same_absence_as_element_exists()
    {
        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ElementEnabled, "Save", Found());

        satisfied.Should().BeFalse();
        detail.Should().Be("no element matching 'Save'");
    }

    // ---- focused_element ---------------------------------------------------------------------

    [Fact]
    public void Focused_element_is_satisfied_when_the_focused_control_name_contains_the_text()
    {
        var evidence = Screen(interactive:
        [
            Element(id: "el_3", name: "Search", focused: false),
            Element(id: "el_4", name: "Password box", controlType: "Edit", focused: true, value: "secret"),
        ]);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.FocusedElement, "password", evidence);

        satisfied.Should().BeTrue("the match is case-insensitive, like every other name match here");
        detail.Should().Contain("Password box");
        element.Should().NotBeNull();
        element!.ElementId.Should().Be("el_4", "the projection carries the id the agent clicks with");
        element.Name.Should().Be("Password box");
        element.ControlType.Should().Be("Edit");
        element.Bounds.Should().Be(new Bounds(600, 380, 24, 16), "the snapshot element's rectangle, unchanged");
        element.Value.Should().Be("secret");
        element.IsEnabled.Should().BeTrue("a control that holds the focus is reachable");
        element.IsOffscreen.Should().BeFalse("the snapshot's interactive list is on-screen elements");
    }

    [Fact]
    public void Focused_element_is_not_satisfied_when_another_control_holds_the_focus()
    {
        var evidence = Screen(interactive:
        [
            Element(id: "el_3", name: "Cancel", focused: true),
            Element(id: "el_4", name: "Save", focused: false),
        ]);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.FocusedElement, "Save", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Contain("Cancel").And.Contain("Save",
            "both halves matter: where the focus IS and where it was wanted");
        element.Should().BeNull();
    }

    [Fact]
    public void Focused_element_says_when_nothing_is_focused_at_all()
    {
        var evidence = Screen(interactive: [Element(id: "el_3", name: "Save", focused: false)]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.FocusedElement, "Save", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Contain("focus", "'nothing is focused' is a different diagnosis from 'the wrong thing is'");
        detail.Should().NotContain("wanted", "there is no other element to name");
    }

    [Fact]
    public void Focused_element_without_a_snapshot_is_not_satisfied_and_does_not_throw()
    {
        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.FocusedElement, "Save", new WaitEvidence());

        satisfied.Should().BeFalse();
        detail.Should().Be("nothing has keyboard focus",
            "a poll that gathered nothing reads the same as a screen where nothing is focused");
    }

    // ---- text_exists -------------------------------------------------------------------------

    [Fact]
    public void Text_exists_finds_the_text_in_an_element_name()
    {
        var evidence = Screen(interactive: [Element(id: "el_5", name: "Upload complete")]);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.TextExists, "complete", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Contain("element").And.Contain("Upload complete", "the agent is told WHERE the text was");
        element.Should().BeNull("text_exists answers about the screen, not about one element handle");
    }

    [Fact]
    public void Text_exists_finds_the_text_in_an_element_value()
    {
        // An Edit's name is its label; what the user typed is the VALUE. Searching names only is
        // the bug this row exists to prevent.
        var evidence = Screen(interactive:
            [Element(id: "el_6", name: "Search", controlType: "Edit", value: "order 12345 shipped")]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "12345", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Contain("Search");
    }

    [Fact]
    public void Text_exists_finds_the_text_in_a_scrollable_region_name()
    {
        var evidence = Screen(scrollable: [Scrollable(id: "el_20", name: "Build output: 0 errors")]);

        var (satisfied, _, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "0 errors", evidence);

        satisfied.Should().BeTrue("a scrollable region carries text the interactive list does not");
    }

    [Fact]
    public void Text_exists_finds_the_text_in_a_page_line_and_names_the_page()
    {
        // The use_dom half (A-5): the page's visible text is where a browser wait has to look -
        // the chrome around it never carries the page's words.
        var evidence = Screen(pages:
            [Page(title: "A5 Probe Page", text: ["Probe heading", "First paragraph of body text."])]);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.TextExists, "probe heading", evidence);

        satisfied.Should().BeTrue("the page text match is case-insensitive too");
        detail.Should().Contain("page").And.Contain("A5 Probe Page");
        element.Should().BeNull();
    }

    [Fact]
    public void Text_exists_says_when_the_hit_was_a_value_rather_than_a_name()
    {
        // "found in element 'Search'" and "found in element 'Search' value" are different facts:
        // the first says the label is on screen, the second says the field HOLDS the text. An
        // agent waiting for a search result acts on the second, not the first.
        var evidence = Screen(interactive:
        [
            Element(id: "el_5", name: "Search", controlType: "Edit", value: "order 12345 shipped"),
        ]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "12345", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be("found in element 'Search' (el_5) value");
    }

    [Fact]
    public void Text_exists_walks_past_an_element_that_does_not_carry_the_text()
    {
        // The name of element 1 and the value of element 2: the scan has to keep going through
        // both lists rather than judging on the first entry.
        var evidence = Screen(interactive:
        [
            Element(id: "el_1", name: "Toolbar", controlType: "ToolBar", value: null),
            Element(id: "el_2", name: "Status", controlType: "Text", value: "Upload complete"),
        ]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "complete", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be("found in element 'Status' (el_2) value");
    }

    [Fact]
    public void Text_exists_names_the_scrollable_region_it_found_the_text_in()
    {
        // Two regions, and the text is in the second: naming the region (and its id) is what lets
        // the agent scroll THAT one.
        var evidence = Screen(scrollable:
        [
            Scrollable(id: "el_19", name: "Explorer pane"),
            Scrollable(id: "el_20", name: "Build output: 0 errors"),
        ]);

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.TextExists, "0 errors", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be("found in scrollable region 'Build output: 0 errors' (el_20)");
        element.Should().BeNull();
    }

    [Fact]
    public void Text_exists_searches_every_page_not_only_the_first()
    {
        // Two browser windows are two pages (A-5). Stopping at page one would make the wait depend
        // on which window the walk happened to reach first.
        var evidence = Screen(pages:
        [
            Page(title: "Sign in", url: "http://127.0.0.1:9999/login", text: ["Username", "Password"]),
            Page(title: "Orders", url: "http://127.0.0.1:9999/orders", text: ["Nothing yet", "Order 12345 shipped"]),
        ]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "order 12345", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be("found in page 'Orders'", "the page the agent has to look at is named");
    }

    [Fact]
    public void Text_exists_is_not_satisfied_when_the_text_is_nowhere_and_says_what_was_wanted()
    {
        var evidence = Screen(
            interactive: [Element(id: "el_5", name: "Upload complete")],
            pages: [Page(text: ["Probe heading"])]);

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "catastrophe", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Contain("catastrophe");
    }

    [Fact]
    public void Text_exists_survives_a_snapshot_that_carried_no_pages()
    {
        // Pages is null for every non-DOM snapshot (A-5 keeps that response byte-identical), so the
        // page scan must be conditional, not a dereference.
        var evidence = Screen(interactive: [Element(id: "el_5", name: "Upload complete")], pages: null);

        var (satisfied, _, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "complete", evidence);

        satisfied.Should().BeTrue();
    }

    [Fact]
    public void Text_exists_without_a_snapshot_is_not_satisfied_and_does_not_throw()
    {
        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.TextExists, "anything", new WaitEvidence());

        satisfied.Should().BeFalse();
        detail.Should().Contain("anything");
    }

    // ---- active_window (B-10's matching rules, over A-1's list - no walk) ----------------------

    [Theory]
    [InlineData("Untitled - Notepad", "exact")]
    [InlineData("untitled - notepad", "exact")]
    [InlineData("Notepad", "substring")]
    [InlineData("notepad", "substring")]
    public void Active_window_matches_the_foreground_title_and_names_the_strategy(string wanted, string strategy)
    {
        var evidence = Desktop(
            Window(title: "Calculator", hwnd: 2, zOrder: 1, isActive: false),
            Window(title: "Untitled - Notepad", hwnd: 1, zOrder: 0, isActive: true));

        var (satisfied, detail, element) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, wanted, evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be($"active window is 'Untitled - Notepad' ({strategy})");
        element.Should().BeNull("a window is not an element");
    }

    [Fact]
    public void Active_window_accepts_a_fuzzy_title_at_or_above_seventy()
    {
        // "windows-mcp" is not a substring of "Windows MCP" (the hyphen), and token-set scores it
        // 100 - the row B-10's FuzzyMatch table already pins.
        var evidence = Desktop(Window(title: "Windows MCP", isActive: true));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "windows-mcp", evidence);

        satisfied.Should().BeTrue();
        detail.Should().Be("active window is 'Windows MCP' (fuzzy)");
    }

    [Fact]
    public void Active_window_refuses_a_title_that_scores_below_the_threshold()
    {
        // "edge" against a Notepad title: 27/50/30, every scorer under 70 (FuzzyMatchTests row 4).
        var evidence = Desktop(Window(title: "Untitled - Notepad", isActive: true));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "edge", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Be("active window is 'Untitled - Notepad', wanted 'edge'");
    }

    [Fact]
    public void Active_window_ignores_a_matching_window_that_is_not_the_active_one()
    {
        // The condition is "is Notepad in front", not "is Notepad open" - a background window with
        // a perfect title must NOT satisfy it.
        var evidence = Desktop(
            Window(title: "Untitled - Notepad", hwnd: 1, zOrder: 1, isActive: false),
            Window(title: "Calculator", hwnd: 2, zOrder: 0, isActive: true));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "Notepad", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Be("active window is 'Calculator', wanted 'Notepad'");
    }

    [Fact]
    public void Active_window_judges_the_first_window_the_inventory_flagged_active()
    {
        // A-1's list can carry more than one IsActive window (a transient foreground change
        // between the enumeration and the flagging). The condition takes the first and answers
        // about it rather than searching the flagged ones for a title that matches - the second
        // would report "Notepad is in front" while the user is looking at Calculator.
        var evidence = Desktop(
            Window(title: "Calculator", hwnd: 2, zOrder: 0, isActive: true),
            Window(title: "Untitled - Notepad", hwnd: 1, zOrder: 1, isActive: true));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "Notepad", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Be("active window is 'Calculator', wanted 'Notepad'");
    }

    [Fact]
    public void Active_window_with_an_empty_title_is_not_satisfied_and_says_what_was_wanted()
    {
        // Splash screens and some shell windows are active with a blank title. Every matcher has
        // to survive it: Contains("") would be true in the other direction, and the fuzzy scorers
        // return 0 for an empty side rather than throwing.
        var evidence = Desktop(Window(title: "", isActive: true));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "Notepad", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Be("active window is '', wanted 'Notepad'");
    }

    [Fact]
    public void Active_window_says_so_when_nothing_holds_the_foreground()
    {
        var evidence = Desktop(Window(title: "Calculator", isActive: false));

        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "Notepad", evidence);

        satisfied.Should().BeFalse();
        detail.Should().Be("no active window");
    }

    [Fact]
    public void Active_window_without_an_inventory_is_not_satisfied_and_does_not_throw()
    {
        var (satisfied, detail, _) = WaitConditions.Evaluate(WaitCondition.ActiveWindow, "Notepad", new WaitEvidence());

        satisfied.Should().BeFalse();
        detail.Should().Be("no active window");
    }

    // ---- the switch's default arm -------------------------------------------------------------

    [Fact]
    public void An_unknown_condition_value_is_refused_rather_than_silently_unsatisfied()
    {
        // A condition added to the enum and forgotten here would otherwise wait out its whole
        // timeout and report "not satisfied" - a silent wrong answer.
        var act = () => WaitConditions.Evaluate((WaitCondition)99, "Save", new WaitEvidence());

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*condition*");
    }

    // ---- the canonical names, which are what the result reports --------------------------------

    [Theory]
    [InlineData(WaitCondition.ElementExists, "element_exists")]
    [InlineData(WaitCondition.ElementEnabled, "element_enabled")]
    [InlineData(WaitCondition.FocusedElement, "focused_element")]
    [InlineData(WaitCondition.TextExists, "text_exists")]
    [InlineData(WaitCondition.ActiveWindow, "active_window")]
    public void Every_condition_has_the_snake_case_name_the_tool_accepts(WaitCondition condition, string expected)
    {
        // One table, read by the tool's refusal message, by the result's Condition field and by
        // the description: a name that drifts here makes the model's alias wrong everywhere.
        WaitConditions.NameOf(condition).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_condition_has_no_name_and_says_so_rather_than_returning_one()
    {
        var act = () => WaitConditions.NameOf((WaitCondition)99);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*condition*");
    }
}
