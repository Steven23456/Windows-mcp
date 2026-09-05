using System.Globalization;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-2 / roadmap C6 (R5): the compact text form, pinned by golden strings. This is the whole
/// value of the snapshot - the model reads this text and turns a row into a click - so the layout
/// is a contract, not a detail: the tag order, the two-space separator, the quoting, and what is
/// NEVER printed (a password value) are all asserted literally.
/// </summary>
/// <remarks>
/// Expected text is built with <see cref="SnapshotFixtures.Lines"/> rather than a verbatim string
/// literal on purpose: this file is stored with CRLF like every other file in the repo, and a
/// multi-line literal would silently assert CRLF while the contract is LF.
/// </remarks>
[Trait("Category", "Unit")]
public class SnapshotRendererTests
{
    /// <summary>U+2026 HORIZONTAL ELLIPSIS, written numerically so this file stays pure ASCII.</summary>
    private const string Ellipsis = "\u2026";

    /// <summary>Renders a one-element snapshot and returns that element's row.</summary>
    private static string RowFor(SnapshotElement element)
    {
        var text = SnapshotRenderer.Render(Result(interactive: [element]));
        var row = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(element.ElementId + " ", StringComparison.Ordinal));
        row.Should().NotBeNull($"the rendered text must contain a row for {element.ElementId}:\n{text}");
        return row!;
    }

    /// <summary>Renders a one-scrollable snapshot and returns that row.</summary>
    private static string ScrollRowFor(SnapshotScrollable scrollable)
    {
        var text = SnapshotRenderer.Render(Result(scrollable: [scrollable]));
        var row = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(scrollable.ElementId + " ", StringComparison.Ordinal));
        row.Should().NotBeNull($"the rendered text must contain a row for {scrollable.ElementId}:\n{text}");
        return row!;
    }

    private static SnapshotResult TwoWindowSnapshot(bool truncated = false, ElementTree? tree = null)
    {
        var notepad = Window(zOrder: 0, isActive: true);
        var chrome = Window("Google Chrome", hwnd: 2, pid: 77, process: "chrome", state: WindowState.Maximized,
            bounds: new Bounds(0, 0, 1920, 1080), zOrder: 1, isBrowser: true);

        return Result(
            windows: [notepad, chrome],
            active: notepad,
            cursor: new CursorPosition(612, 388),
            cursorMonitorIndex: 0,
            interactive:
            [
                Element("el_12", shortcut: "Ctrl+S"),
                Element("el_13", window: "Google Chrome", controlType: "Edit", name: "Address bar",
                    centerX: 300, centerY: 200, action: "fill", focused: true, value: "hello"),
                Element("el_14", controlType: "CheckBox", name: "Word wrap", centerX: 900, centerY: 40,
                    action: "toggle", toggle: "On"),
            ],
            scrollable: [Scrollable()],
            tree: tree,
            truncated: truncated,
            elementLimit: 500,
            elementCount: 57);
    }

    // ---- R5.1 the whole layout ----------------------------------------------------------------

    [Fact]
    public void Render_lays_out_header_windows_interactive_and_scrollable()
    {
        SnapshotRenderer.Render(TwoWindowSnapshot()).Should().Be(Lines(
            "Cursor: (612, 388) on display 0",
            "Active window: \"Untitled - Notepad\" (pid 4242, Normal)",
            "Windows (z-order, topmost first):",
            "  0. \"Untitled - Notepad\" [Normal] 800x600 @ (100,100) pid=4242",
            "  1. \"Google Chrome\" [Maximized] 1920x1080 @ (0,0) pid=77 browser",
            "Interactive (3 of 57, ids valid until the next snapshot):",
            "window \"Untitled - Notepad\"",
            "  el_12 (612,388) button \"Save\"  [action: click]  [shortcut: Ctrl+S]",
            "  el_14 (900,40) checkbox \"Word wrap\"  [action: toggle]  [toggle: On]",
            "window \"Google Chrome\"",
            "  el_13 (300,200) edit \"Address bar\"  [action: fill]  [focused]  [value: \"hello\"]",
            "Scrollable (1):",
            "  el_20 (500,400) document \"Text Editor\"  [v: 37%]  [h: 0%]"));
    }

    [Fact]
    public void Render_uses_LF_and_leaves_no_trailing_whitespace()
    {
        var text = SnapshotRenderer.Render(TwoWindowSnapshot(truncated: true));

        text.Should().NotContain("\r", "the text block is LF-only, whatever the host line ending is");
        text.Should().NotEndWith("\n", "the block ends at its last line; the caller decides about spacing");
        foreach (var line in text.Split('\n'))
            line.Should().Be(line.TrimEnd(), "no rendered line carries trailing whitespace");
    }

    [Fact]
    public void Render_of_an_empty_desktop_still_prints_every_section_header()
    {
        // A snapshot that found nothing must still say so in the same shape, or the model has to
        // parse two different formats.
        SnapshotRenderer.Render(Result(cursor: new CursorPosition(0, 0), cursorMonitorIndex: -1, elementCount: 0))
            .Should().Be(Lines(
                "Cursor: (0, 0) on no display",
                "Active window: none",
                "Windows (z-order, topmost first):",
                "Interactive (0 of 0, ids valid until the next snapshot):",
                "Scrollable (0):"));
    }

    [Fact]
    public void Render_ignores_the_element_tree()
    {
        // include_tree is a JSON-only affordance (roadmap C6); rendering it as text would undo the
        // 5-10x token saving the text form exists for.
        var tree = new ElementTree(
            new ElementInfo("el_1", "Untitled - Notepad", "Window", true, false, new Bounds(100, 100, 800, 600), null, null, null),
            [], true, 500);

        SnapshotRenderer.Render(TwoWindowSnapshot(tree: tree))
            .Should().Be(SnapshotRenderer.Render(TwoWindowSnapshot()));
    }

    // ---- R5.2 the header ----------------------------------------------------------------------

    [Fact]
    public void Render_reports_the_cursor_monitor_when_the_point_is_on_no_display()
        => SnapshotRenderer.Render(Result(cursor: new CursorPosition(-2000, 40), cursorMonitorIndex: -1))
            .Split('\n')[0].Should().Be("Cursor: (-2000, 40) on no display");

    [Fact]
    public void Render_says_none_when_there_is_no_foreground_window()
        => SnapshotRenderer.Render(Result(active: null)).Split('\n')[1].Should().Be("Active window: none");

    [Theory]
    [InlineData(WindowState.Normal, "Normal")]
    [InlineData(WindowState.Minimized, "Minimized")]
    [InlineData(WindowState.Maximized, "Maximized")]
    public void Render_names_the_active_window_state(WindowState state, string expected)
        => SnapshotRenderer.Render(Result(active: Window(state: state))).Split('\n')[1]
            .Should().Be($"Active window: \"Untitled - Notepad\" (pid 4242, {expected})");

    [Fact]
    public void Render_numbers_the_window_list_by_its_z_order_not_by_its_position()
    {
        // ZOrder is the window's real place in the desktop stack (A-1); a filtered or single-window
        // list must keep the number the inventory gave it.
        var text = SnapshotRenderer.Render(Result(windows: [Window("Second", zOrder: 3), Window("Ninth", hwnd: 9, zOrder: 7)]));

        text.Split('\n')[3].Should().Be("  3. \"Second\" [Normal] 800x600 @ (100,100) pid=4242");
        text.Split('\n')[4].Should().Be("  7. \"Ninth\" [Normal] 800x600 @ (100,100) pid=4242");
    }

    [Fact]
    public void Render_marks_a_browser_window()
        => SnapshotRenderer.Render(Result(windows: [Window("Google Chrome", pid: 77, process: "chrome", isBrowser: true)]))
            .Split('\n')[3].Should().Be("  0. \"Google Chrome\" [Normal] 800x600 @ (100,100) pid=77 browser");

    [Fact]
    public void Render_escapes_a_quote_in_a_window_title_everywhere_it_prints_it()
    {
        // A browser tab title is arbitrary web text; it reaches the window list, the active-window
        // line and the group header, and all three quote it the same way.
        var quoted = Window("say \"hi\" - Chrome", isActive: true);
        var text = SnapshotRenderer.Render(Result(
            windows: [quoted], active: quoted, interactive: [Element("el_1", window: "say \"hi\" - Chrome")]));

        text.Split('\n')[1].Should().Be("Active window: \"say \\\"hi\\\" - Chrome\" (pid 4242, Normal)");
        text.Split('\n')[3].Should().Be("  0. \"say \\\"hi\\\" - Chrome\" [Normal] 800x600 @ (100,100) pid=4242");
        text.Split('\n').Should().Contain("window \"say \\\"hi\\\" - Chrome\"");
    }

    [Fact]
    public void Render_counts_the_listed_elements_against_everything_walked()
        // "3 of 57" is what tells the agent the desktop is bigger than the list it is looking at.
        => SnapshotRenderer.Render(Result(interactive: [Element("el_1"), Element("el_2")], elementCount: 900))
            .Split('\n').Should().Contain("Interactive (2 of 900, ids valid until the next snapshot):");

    // ---- R5.3 the element row and its tags ----------------------------------------------------

    [Fact]
    public void Render_prints_every_tag_in_a_fixed_order()
    {
        // action, focused, password, value, toggle, expand, shortcut, range - fixed so the text is
        // scannable and diffable between two snapshots.
        RowFor(Element("el_30", controlType: "ComboBox", name: "Zoom", centerX: 40, centerY: 50, action: "select",
                focused: true, value: "100%", toggle: "Off", expand: "Expanded", shortcut: "Ctrl+M",
                rangeValue: 30, rangeMin: 0, rangeMax: 100))
            .Should().Be("  el_30 (40,50) combobox \"Zoom\"  [action: select]  [focused]  [value: \"100%\"]  " +
                         "[toggle: Off]  [expand: Expanded]  [shortcut: Ctrl+M]  [range: 30 of 0..100]");
    }

    [Fact]
    public void Render_never_prints_the_value_of_a_password_field()
    {
        var row = RowFor(Element("el_15", controlType: "Edit", name: "Password", centerX: 10, centerY: 10,
            action: "fill", isPassword: true, value: "hunter2"));

        row.Should().Be("  el_15 (10,10) edit \"Password\"  [action: fill]  [password]");
        row.Should().NotContain("hunter2");
    }

    [Fact]
    public void Render_keeps_the_password_tag_in_its_slot_between_focused_and_toggle()
        => RowFor(Element("el_15", controlType: "Edit", name: "Password", centerX: 10, centerY: 10,
                action: "fill", focused: true, isPassword: true, value: "hunter2", shortcut: "Alt+P"))
            .Should().Be("  el_15 (10,10) edit \"Password\"  [action: fill]  [focused]  [password]  [shortcut: Alt+P]");

    [Fact]
    public void Render_omits_every_tag_the_element_has_nothing_to_say_about()
        => RowFor(Element("el_12")).Should().Be("  el_12 (612,388) button \"Save\"  [action: click]");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_treats_a_blank_metadata_string_as_absent(string blank)
        => RowFor(Element("el_12", toggle: blank, expand: blank, shortcut: blank))
            .Should().Be("  el_12 (612,388) button \"Save\"  [action: click]");

    [Theory]
    [InlineData("CheckBox", "checkbox")]
    [InlineData("TreeItem", "treeitem")]
    [InlineData("SplitButton", "splitbutton")]
    [InlineData("Document", "document")]
    public void Render_lower_cases_the_control_type(string controlType, string expected)
        => RowFor(Element("el_12", controlType: controlType)).Should().Contain($" {expected} \"Save\"");

    [Fact]
    public void Render_escapes_a_quote_inside_a_name()
        // Window and element names come from applications; a quote in one must not close the field.
        => RowFor(Element("el_12", name: "He said \"hi\""))
            .Should().Be("  el_12 (612,388) button \"He said \\\"hi\\\"\"  [action: click]");

    [Fact]
    public void Render_escapes_a_quote_inside_a_value()
        => RowFor(Element("el_12", controlType: "Edit", action: "fill", value: "a\"b"))
            .Should().Be("  el_12 (612,388) edit \"Save\"  [action: fill]  [value: \"a\\\"b\"]");

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    public void Render_keeps_an_element_on_one_row_when_its_text_contains_a_line_break(string field)
    {
        // Names and values are application text - in a browser, page text nobody here controls -
        // and A-13's sanitiser preserves \r and \n on purpose. A raw line break splits the row in
        // two: the tail becomes a line the model reads as another row, and the LF-only guarantee
        // this block makes is broken. HOW it is neutralised (escaped, or replaced with a space) is
        // open; that one element is one row is not. The JSON form keeps the raw text - it is this
        // line-oriented format that has to defend itself.
        var element = field == "name"
            ? Element("el_12", name: "a\r\nb")
            : Element("el_12", controlType: "Edit", action: "fill", value: "a\r\nb");

        var text = SnapshotRenderer.Render(Result(interactive: [element]));
        var lines = text.Split('\n');

        text.Should().NotContain("\r");
        lines.Should().HaveCount(7,
            "cursor, active window, the window header, the interactive header, the group header, ONE element row, the scrollable header");
        lines.Should().ContainSingle(l => l.StartsWith("  el_12 ", StringComparison.Ordinal))
            .Which.Should().EndWith("]", "the row must still carry its last tag");
    }

    [Fact]
    public void Render_keeps_a_value_of_eighty_characters_whole()
    {
        var value = new string('a', 80);
        RowFor(Element("el_12", controlType: "Edit", action: "fill", value: value))
            .Should().EndWith($"[value: \"{value}\"]").And.NotContain(Ellipsis);
    }

    [Fact]
    public void Render_truncates_a_long_value_at_eighty_characters()
    {
        // A document's ValuePattern is the WHOLE document; unbounded, one element could be the
        // entire response.
        RowFor(Element("el_12", controlType: "Edit", action: "fill", value: new string('a', 500)))
            .Should().EndWith($"[value: \"{new string('a', 80)}{Ellipsis}\"]");
    }

    [Fact]
    public void Render_clips_a_value_of_eighty_one_characters_to_eighty_and_an_ellipsis()
        // The other side of the off-by-one: 80 is kept whole (above), 81 is the first length that
        // loses anything, and what it loses is exactly the one character over.
        => RowFor(Element("el_12", controlType: "Edit", action: "fill", value: new string('a', 81)))
            .Should().EndWith($"[value: \"{new string('a', 80)}{Ellipsis}\"]");

    [Fact]
    public void Render_prints_an_empty_value_rather_than_dropping_the_tag()
        // "" is a value; absent is not - the same distinction the classifier draws for role=text.
        // An empty edit box has to read as empty, not as "this control has no value at all".
        => RowFor(Element("el_12", controlType: "Edit", action: "fill", value: ""))
            .Should().Be("  el_12 (612,388) edit \"Save\"  [action: fill]  [value: \"\"]");

    [Fact]
    public void Render_keeps_a_whitespace_only_value()
        // Unlike toggle/expand/shortcut, a blank value is NOT treated as absent: a box holding
        // three spaces is a fact the model needs before it types into it.
        => RowFor(Element("el_12", controlType: "Edit", action: "fill", value: "   "))
            .Should().Be("  el_12 (612,388) edit \"Save\"  [action: fill]  [value: \"   \"]");

    [Fact]
    public void Render_prints_a_range_with_its_bounds()
        => RowFor(Element("el_16", controlType: "Slider", name: "Volume", centerX: 50, centerY: 50,
                action: "slide", rangeValue: 30, rangeMin: 0, rangeMax: 100))
            .Should().Be("  el_16 (50,50) slider \"Volume\"  [action: slide]  [range: 30 of 0..100]");

    [Fact]
    public void Render_prints_a_range_value_alone_when_the_bounds_are_unknown()
        => RowFor(Element("el_16", controlType: "Slider", name: "Volume", centerX: 50, centerY: 50,
                action: "slide", rangeValue: 30))
            .Should().Be("  el_16 (50,50) slider \"Volume\"  [action: slide]  [range: 30]");

    [Fact]
    public void Render_omits_the_range_tag_when_there_is_no_value()
        => RowFor(Element("el_16", controlType: "Slider", name: "Volume", centerX: 50, centerY: 50,
                action: "slide", rangeMin: 0, rangeMax: 100))
            .Should().Be("  el_16 (50,50) slider \"Volume\"  [action: slide]");

    [Fact]
    public void Render_prints_an_expand_state()
        => RowFor(Element("el_17", controlType: "TreeItem", name: "Node", centerX: 70, centerY: 70, expand: "Collapsed"))
            .Should().Be("  el_17 (70,70) treeitem \"Node\"  [action: click]  [expand: Collapsed]");

    [Fact]
    public void Render_prints_a_negative_centre_unchanged()
        // Virtual-desktop coordinates (roadmap C1): a monitor left of the primary is negative and
        // click takes it as-is.
        => RowFor(Element("el_12", centerX: -960, centerY: 540))
            .Should().Be("  el_12 (-960,540) button \"Save\"  [action: click]");

    [Fact]
    public void Render_formats_numbers_invariantly_whatever_the_machine_culture()
    {
        // A de-DE host would otherwise print "[range: 0,5 of 0..1]" and the value would round-trip
        // as a different number.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            RowFor(Element("el_16", controlType: "Slider", name: "Volume", centerX: 50, centerY: 50,
                    action: "slide", rangeValue: 0.5, rangeMin: 0, rangeMax: 1))
                .Should().EndWith("[range: 0.5 of 0..1]");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // ---- R5.4 grouping ------------------------------------------------------------------------

    [Fact]
    public void Render_groups_elements_by_window_in_first_appearance_order()
    {
        var text = SnapshotRenderer.Render(Result(interactive:
        [
            Element("el_1", window: "B"),
            Element("el_2", window: "A"),
            Element("el_3", window: "B"),
            Element("el_4", window: "C"),
            Element("el_5", window: "A"),
        ]));

        var rows = text.Split('\n').Where(l => l.StartsWith("window ", StringComparison.Ordinal) || l.StartsWith("  el_", StringComparison.Ordinal));
        rows.Select(l => l.Trim().Split(' ')[0]).Should().Equal(
            "window", "el_1", "el_3", "window", "el_2", "el_5", "window", "el_4");
        text.Split('\n').Where(l => l.StartsWith("window ", StringComparison.Ordinal))
            .Should().Equal("window \"B\"", "window \"A\"", "window \"C\"");
    }

    [Fact]
    public void Render_groups_two_windows_whose_titles_differ_only_in_case_separately()
        // Ordinal, not case-insensitive: two genuinely different windows can carry titles that
        // differ only in case, and folding them together files elements under a window they are
        // not in - which is a click sent to the wrong process.
        => SnapshotRenderer.Render(Result(interactive: [Element("el_1", window: "Notes"), Element("el_2", window: "notes")]))
            .Split('\n').Where(l => l.StartsWith("window ", StringComparison.Ordinal))
            .Should().Equal("window \"Notes\"", "window \"notes\"");

    // ---- R5.5 the scrollable section ----------------------------------------------------------

    [Fact]
    public void Render_prints_both_scroll_percentages()
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(37, 12, true, true)))
            .Should().Be("  el_20 (500,400) document \"Text Editor\"  [v: 37%]  [h: 12%]");

    [Theory]
    [InlineData(37.4, 37)]
    [InlineData(36.6, 37)]
    [InlineData(99.9, 100)]
    public void Render_rounds_a_scroll_percentage_to_a_whole_number(double percent, int expected)
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(percent, 0, true, false)))
            .Should().Contain($"[v: {expected}%]");

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(36.5, 37)]
    [InlineData(98.5, 99)]
    public void Render_rounds_a_half_percent_away_from_zero_not_to_even(double percent, int expected)
        // Math.Round's DEFAULT is banker's rounding, which would print 0%, 36% and 98% here, and a
        // truncating cast would print the same three. Every row is a midpoint whose floor is even,
        // so each one separates away-from-zero from both.
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(percent, 0, true, false)))
            .Should().Contain($"[v: {expected}%]");

    [Fact]
    public void Render_says_reached_top_at_zero_percent()
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(0, 0, true, false)))
            .Should().Be("  el_20 (500,400) document \"Text Editor\"  [v: 0%]  [h: 0%]  [reached top]");

    [Fact]
    public void Render_says_reached_bottom_at_a_hundred_percent()
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(100, 50, true, true)))
            .Should().Be("  el_20 (500,400) document \"Text Editor\"  [v: 100%]  [h: 50%]  [reached bottom]");

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Render_says_nothing_about_reaching_an_end_that_cannot_scroll(double percent)
        // A non-scrolling pane reports 0% forever; calling that "reached top" tells the agent it
        // scrolled something when it did not.
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(percent, 0, false, false)))
            .Should().Be($"  el_20 (500,400) document \"Text Editor\"  [v: {(int)percent}%]  [h: 0%]");

    [Fact]
    public void Render_says_nothing_about_the_top_when_only_the_horizontal_axis_can_scroll()
        // The end-of-range tags are gated on VERTICALLY scrollable specifically, not on "is
        // scrollable at all": a pane that only scrolls sideways sits at v: 0% forever, and
        // "reached top" would be a claim about an axis that does not move.
        => ScrollRowFor(Scrollable(scroll: new ScrollInfo(0, 40, false, true)))
            .Should().Be("  el_20 (500,400) document \"Text Editor\"  [v: 0%]  [h: 40%]");

    // ---- R5.6 the truncation footer -----------------------------------------------------------

    [Fact]
    public void Render_appends_the_budget_note_when_the_walk_was_truncated()
    {
        var text = SnapshotRenderer.Render(TwoWindowSnapshot(truncated: true));

        text.Split('\n').Last().Should().Be(TruncationNote(500));
    }

    [Fact]
    public void Render_uses_the_limit_the_snapshot_reports_in_the_note()
        => SnapshotRenderer.Render(Result(truncated: true, elementLimit: 25))
            .Split('\n').Last().Should().Be(TruncationNote(25));

    [Fact]
    public void Render_footer_is_the_budgets_own_sentence_not_a_second_copy_of_it()
        // One sentence, one source. Comparing against ElementBudget at RUN time (not against the
        // fixture literal) means a reworded budget note drags the rendered footer with it; a
        // duplicated literal in the renderer would fail here the day the budget's wording moves.
        => SnapshotRenderer.Render(Result(truncated: true, elementLimit: 25))
            .Split('\n').Last().Should().Be(new ElementBudget(25).Note());

    [Fact]
    public void Render_says_nothing_about_truncation_when_the_walk_finished()
        => SnapshotRenderer.Render(TwoWindowSnapshot()).Should().NotContain("Truncated at");

    // ---- A-14 (R4): the timing footer, only when the snapshot was profiled --------------------
    // One line, last, so a model reading the element rows never has to skip past it, and an
    // unprofiled snapshot's text is byte-identical to what it was before A-14.

    private static readonly StageTiming[] HeaderAndWalk = [new("header", 12), new("walk", 130)];

    [Fact]
    public void Render_appends_the_timing_line_when_the_snapshot_was_profiled()
        => SnapshotRenderer.Render(Result(captureMs: 142, stages: HeaderAndWalk))
            .Split('\n').Last().Should().Be("Timing: header 12 ms, walk 130 ms (total 142 ms)");

    [Fact]
    public void Render_says_nothing_about_timing_when_the_snapshot_was_not_profiled()
        => SnapshotRenderer.Render(TwoWindowSnapshot()).Should().NotContain("Timing:");

    [Fact]
    public void Render_timing_total_is_the_snapshots_own_elapsed_time_not_the_sum_of_the_stages()
    {
        // The stages do not have to add up to the whole call (nothing is measured between them),
        // so the total is CaptureMs - the number the JSON form reports - not a re-derived sum.
        SnapshotRenderer.Render(Result(captureMs: 999, stages: HeaderAndWalk))
            .Split('\n').Last().Should().Be("Timing: header 12 ms, walk 130 ms (total 999 ms)");
    }

    [Fact]
    public void Render_lists_the_stages_in_the_order_the_service_reported_them()
    {
        // Reported order is running order; re-sorting it would hide which stage came first.
        SnapshotRenderer.Render(Result(captureMs: 142, stages: [new("walk", 130), new("header", 12)]))
            .Split('\n').Last().Should().Be("Timing: walk 130 ms, header 12 ms (total 142 ms)");
    }

    [Fact]
    public void Render_timing_line_handles_a_single_stage_and_a_zero_duration()
        => SnapshotRenderer.Render(Result(captureMs: 0, stages: [new("header", 0)]))
            .Split('\n').Last().Should().Be("Timing: header 0 ms (total 0 ms)");

    [Fact]
    public void Render_timing_line_survives_a_profiled_snapshot_that_reported_no_stages()
    {
        // Non-null but empty: profiling was on, so the line is printed; there is just nothing to
        // list. Exact spacing is deliberately NOT pinned here - only that the line is well formed.
        var last = SnapshotRenderer.Render(Result(captureMs: 12, stages: [])).Split('\n').Last();

        last.Should().StartWith("Timing:").And.Contain("(total 12 ms)").And.NotContain(",");
    }

    [Fact]
    public void Render_puts_the_timing_line_after_the_truncation_note()
    {
        // Order matters: the note is advice about the RESULT and belongs with it; the timing is
        // diagnostics about the CALL and goes last.
        var lines = SnapshotRenderer.Render(
            TwoWindowSnapshot(truncated: true) with { CaptureMs = 142, Stages = HeaderAndWalk })
            .Split('\n');

        lines[^2].Should().Be(TruncationNote(500));
        lines[^1].Should().Be("Timing: header 12 ms, walk 130 ms (total 142 ms)");
    }
}
