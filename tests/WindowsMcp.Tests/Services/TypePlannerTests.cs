using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-1 / roadmap C8 (R2): the pure typing plan. Everything `type` decides before a single key is
/// injected lives here — clear, caret, keys-vs-paste, the newline/tab split, press-enter — so the
/// decisions are pinned without a desktop and the executor's tests only have to prove it OBEYS
/// the plan.
/// </summary>
[Trait("Category", "Unit")]
public class TypePlannerTests
{
    private static TypeOptions Options(
        bool clear = false, CaretPosition caret = CaretPosition.Idle, bool pressEnter = false, int paceMs = 5)
        => new(clear, caret, pressEnter, paceMs);

    // ---- the defaults the tool's parameters mirror -------------------------------------------

    [Fact]
    public void Default_options_are_no_clear_idle_caret_no_enter_and_a_five_millisecond_pace()
    {
        var options = new TypeOptions();

        options.Clear.Should().BeFalse();
        options.Caret.Should().Be(CaretPosition.Idle);
        options.PressEnter.Should().BeFalse();
        options.PaceMs.Should().Be(5, "roadmap C8 sets the pace at 5 ms; the tool's pace_ms default must match");
    }

    // ---- keys mode ---------------------------------------------------------------------------

    [Fact]
    public void Short_text_is_typed_as_one_literal_chunk()
    {
        var plan = TypePlanner.Plan("hello", Options());

        plan.Method.Should().Be("keys");
        plan.Steps.Should().Equal(TypeStep.Text("hello"));
    }

    [Fact]
    public void Empty_text_types_nothing_at_all()
    {
        var plan = TypePlanner.Plan("", Options());

        plan.Method.Should().Be("keys");
        plan.Steps.Should().BeEmpty("an empty chunk would be a TextEntry of nothing");
    }

    [Fact]
    public void Newlines_become_enter_and_tabs_become_tab_between_the_literal_chunks()
    {
        // TextEntry of a "\n" types nothing in most controls; Enter is what a caller means.
        var plan = TypePlanner.Plan("a\nb\tc", Options());

        plan.Method.Should().Be("keys");
        plan.Steps.Should().Equal(
            TypeStep.Text("a"), TypeStep.Key("enter"),
            TypeStep.Text("b"), TypeStep.Key("tab"),
            TypeStep.Text("c"));
    }

    [Fact]
    public void A_crlf_is_one_enter_not_two()
    {
        var plan = TypePlanner.Plan("a\r\nb", Options());

        plan.Steps.Should().Equal(TypeStep.Text("a"), TypeStep.Key("enter"), TypeStep.Text("b"));
    }

    [Fact]
    public void A_lone_carriage_return_is_also_one_enter()
    {
        var plan = TypePlanner.Plan("a\rb", Options());

        plan.Steps.Should().Equal(TypeStep.Text("a"), TypeStep.Key("enter"), TypeStep.Text("b"));
    }

    [Fact]
    public void No_empty_text_chunk_is_ever_emitted()
    {
        var plan = TypePlanner.Plan("\na\n\n", Options());

        plan.Steps.Should().Equal(
            TypeStep.Key("enter"),
            TypeStep.Text("a"),
            TypeStep.Key("enter"),
            TypeStep.Key("enter"));
    }

    // ---- the paste threshold -----------------------------------------------------------------

    [Theory]
    [InlineData(0, "keys")]
    [InlineData(1, "keys")]
    [InlineData(199, "keys")]
    [InlineData(200, "paste")]
    [InlineData(5000, "paste")]
    public void The_paste_threshold_is_exactly_two_hundred_characters(int length, string expected)
    {
        // 5 000 characters at 5 ms is 25 seconds of injection; a paste is one keystroke. 199 vs
        // 200 is the boundary the roadmap fixes, and an off-by-one here is a silent 25 s call.
        var plan = TypePlanner.Plan(new string('a', length), Options());

        plan.Method.Should().Be(expected);
    }

    [Fact]
    public void A_paste_carries_the_text_verbatim_in_a_single_step()
    {
        var text = new string('a', 100) + "\nline\ttabbed\n" + new string('b', 100);

        var plan = TypePlanner.Plan(text, Options());

        plan.Method.Should().Be("paste");
        // The clipboard carries newlines and tabs itself; splitting them would defeat the point.
        plan.Steps.Should().Equal(TypeStep.Paste(text));
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\0')]
    [InlineData('\b')]
    [InlineData('\u001B')]
    [InlineData('\u0007')]
    [InlineData('\u001F')]
    public void A_control_character_other_than_newline_or_tab_forces_keys_however_long_the_text(char control)
    {
        // A control character in the clipboard is either dropped or interpreted by the target;
        // typing it is the only way the caller's intent survives (and \r is a line ending, which
        // keys mode turns into a single Enter).
        var text = new string('a', 300) + control + new string('b', 300);

        TypePlanner.Plan(text, Options()).Method.Should().Be("keys");
    }

    [Theory]
    [InlineData('\n')]
    [InlineData('\t')]
    public void Newlines_and_tabs_do_not_force_keys(char allowed)
    {
        var text = new string('a', 300) + allowed + new string('b', 300);

        TypePlanner.Plan(text, Options()).Method.Should().Be("paste");
    }

    [Fact]
    public void Non_ascii_text_is_still_pasted()
    {
        // Unicode above the control range is not a control character: emoji and accented text are
        // exactly the payloads per-key injection mangles, so they must reach the paste path.
        var text = string.Concat(Enumerable.Repeat("héllo wörld ✓ ", 30));

        TypePlanner.Plan(text, Options()).Method.Should().Be("paste");
    }

    // ---- clear, caret, press_enter and their ORDER -------------------------------------------

    [Fact]
    public void Clear_selects_all_and_deletes_before_anything_is_typed()
    {
        var plan = TypePlanner.Plan("hi", Options(clear: true));

        plan.Steps.Should().Equal(
            TypeStep.Shortcut("ctrl+a"), TypeStep.Key("backspace"), TypeStep.Text("hi"));
    }

    [Theory]
    [InlineData(CaretPosition.Start, "ctrl+home")]
    [InlineData(CaretPosition.End, "ctrl+end")]
    public void The_caret_is_moved_with_a_chord_before_the_text(CaretPosition caret, string chord)
    {
        // ctrl+home / ctrl+end, not home / end: a Document is multi-line, and "end" would only
        // reach the end of the current line. It is a Shortcut step, not a Key step, because
        // PressKeyAsync resolves ONE key and would throw on "ctrl+end".
        var plan = TypePlanner.Plan("hi", Options(caret: caret));

        plan.Steps.Should().Equal(TypeStep.Shortcut(chord), TypeStep.Text("hi"));
    }

    [Fact]
    public void An_idle_caret_moves_nothing()
    {
        TypePlanner.Plan("hi", Options(caret: CaretPosition.Idle)).Steps
            .Should().Equal(TypeStep.Text("hi"));
    }

    [Fact]
    public void Clear_runs_before_the_caret_move_which_runs_before_the_text()
    {
        // Order matters: ctrl+a after a caret move would undo it, and a caret move after the text
        // would put the caret somewhere the caller did not ask for.
        var plan = TypePlanner.Plan("hi", Options(clear: true, caret: CaretPosition.End, pressEnter: true));

        plan.Steps.Should().Equal(
            TypeStep.Shortcut("ctrl+a"),
            TypeStep.Key("backspace"),
            TypeStep.Shortcut("ctrl+end"),
            TypeStep.Text("hi"),
            TypeStep.Key("enter"));
    }

    [Fact]
    public void Press_enter_is_the_last_step_of_a_pasted_plan_too()
    {
        var text = new string('a', 250);

        var plan = TypePlanner.Plan(text, Options(clear: true, pressEnter: true));

        plan.Method.Should().Be("paste");
        plan.Steps.Should().Equal(
            TypeStep.Shortcut("ctrl+a"),
            TypeStep.Key("backspace"),
            TypeStep.Paste(text),
            TypeStep.Key("enter"));
    }

    [Fact]
    public void Press_enter_on_empty_text_still_presses_enter()
    {
        TypePlanner.Plan("", Options(pressEnter: true)).Steps.Should().Equal(TypeStep.Key("enter"));
    }

    // ---- pace --------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void A_negative_pace_is_refused_by_name(int paceMs)
    {
        var act = () => TypePlanner.Plan("hi", Options(paceMs: paceMs));

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("pace",
            "the offending parameter is named, as everywhere else in the tool surface");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(40)]
    public void A_zero_or_positive_pace_is_accepted(int paceMs)
    {
        var act = () => TypePlanner.Plan("hi", Options(paceMs: paceMs));

        act.Should().NotThrow("pace_ms:0 is 'as fast as the simulator will go', a legitimate request");
    }

    [Fact]
    public void Exactly_two_hundred_characters_with_a_carriage_return_are_still_typed_key_by_key()
    {
        // Both rules meet here: long enough to paste, but a CR is a control character the
        // clipboard path must not carry (the target would see a stray line ending, or nothing).
        // The CR rule wins, and the CRLF still collapses to ONE Enter.
        var text = new string('a', 99) + "\r\n" + new string('b', 99);
        text.Length.Should().Be(200, "the guard is the threshold itself, so the length must sit exactly on it");

        var plan = TypePlanner.Plan(text, Options());

        plan.Method.Should().Be("keys", "a CR is a control character, whatever the length");
        plan.Steps.Should().Equal(
            TypeStep.Text(new string('a', 99)),
            TypeStep.Key("enter"),
            TypeStep.Text(new string('b', 99)));
    }

    [Fact]
    public void A_blank_line_written_as_two_crlfs_is_two_enters_and_no_empty_chunk()
    {
        // Windows text arrives CRLF-delimited; a blank line is two of them. Four keys (one per CR
        // and one per LF) would leave two extra blank lines in the field.
        var plan = TypePlanner.Plan("a\r\n\r\nb", Options());

        plan.Steps.Should().Equal(
            TypeStep.Text("a"),
            TypeStep.Key("enter"),
            TypeStep.Key("enter"),
            TypeStep.Text("b"));
    }
}
