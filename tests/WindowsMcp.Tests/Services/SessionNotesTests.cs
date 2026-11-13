using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8 (docs/design/E-8-session-state.md, "InputAvailable and Note - pure (SessionNotes)" as
/// superseded by "Decisions from the review" 2-4): the five ordered rules, first match wins -
/// (1) !Interactive, (2) Locked == true, (3) Disconnected, (4) the input desktop by its access
/// (Denied, or Opened with Winlogon -> the secure desktop; Opened with another non-Default name ->
/// that desktop's note; Unknown -> no note), (5) available. Each rule is tested with its neighbours
/// set to the value that would make a LATER rule fire, so a rule evaluated out of order fails here.
/// <para>
/// The four fixed sentences are pinned literally: they are what the model reads, and "stop and tell
/// the user" is the whole point of the item.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class SessionNotesTests
{
    internal const string LockedNote =
        "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it. Stop and tell the user.";

    internal const string DisconnectedNote =
        "The session is disconnected (no display or remote-desktop client is attached): nothing can be seen or driven until the user reconnects. Stop and tell the user.";

    internal const string SecureDesktopNote =
        "A secure desktop has the input (a UAC prompt, or the sign-in or lock screen): the user's desktop cannot be seen or driven until the user answers it. Stop and tell the user.";

    internal const string NotInteractiveNote =
        "The server has no interactive desktop (it was started as a service or a scheduled task without one): UI tools cannot see or drive a desktop.";

    internal const string Opened = "Opened";
    internal const string Denied = "Denied";
    internal const string Unknown = "Unknown";

    /// <summary>
    /// <paramref name="access"/> defaults to what the service would report for
    /// <paramref name="inputDesktop"/>: a name was Opened, null was Denied (the secure desktop).
    /// </summary>
    private static (bool InputAvailable, string? Note) Eval(
        bool? locked = false, string connectState = "Active", string? inputDesktop = "Default",
        bool interactive = true, string? access = null)
        => SessionNotes.Evaluate(locked, connectState, inputDesktop, access ?? (inputDesktop is null ? Denied : Opened), interactive);

    // ---- rule 5: the normal case --------------------------------------------------------------

    [Fact]
    public void Evaluate_an_unlocked_active_interactive_session_on_Default_is_available_with_no_note()
        => Eval().Should().Be((true, (string?)null));

    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void Evaluate_compares_Default_ordinally_ignoring_case(string desktop)
        => Eval(inputDesktop: desktop).Should().Be((true, (string?)null));

    [Theory]
    [InlineData(null, "Unknown")]     // neither fact known
    [InlineData(null, "Active")]      // Locked unknown alone
    [InlineData(false, "Unknown")]    // connect state unknown alone
    public void Evaluate_an_unknown_lock_state_or_connect_state_alone_does_not_cry_wolf(bool? locked, string connectState)
        => Eval(locked: locked, connectState: connectState).Should().Be((true, (string?)null),
            "unknown is not a reason to say the desktop cannot be driven");

    [Theory]
    [InlineData("Connected")]
    [InlineData("Idle")]
    [InlineData("ConnectQuery")]
    [InlineData("Shadow")]
    public void Evaluate_only_Disconnected_among_the_connect_states_makes_input_unavailable(string connectState)
        => Eval(connectState: connectState).Should().Be((true, (string?)null));

    // ---- R2 decision 3: an input desktop that could not be READ is not a secure desktop --------

    [Theory]
    [InlineData(false, "Active")]
    [InlineData(null, "Unknown")]
    [InlineData(null, "Active")]
    public void Evaluate_an_input_desktop_of_unknown_access_raises_no_note(bool? locked, string connectState)
        // REVERSES R1 (test-agent): a null desktop used to mean "secure desktop" whatever the cause.
        => Eval(locked: locked, connectState: connectState, inputDesktop: null, access: Unknown)
            .Should().Be((true, (string?)null), "unknown is not a reason to tell the model to stop");

    [Fact]
    public void Evaluate_unknown_desktop_access_does_not_hide_a_lock()
        => Eval(locked: true, inputDesktop: null, access: Unknown).Should().Be((false, LockedNote));

    [Fact]
    public void Evaluate_unknown_desktop_access_does_not_hide_a_disconnection()
        => Eval(connectState: "Disconnected", inputDesktop: null, access: Unknown).Should().Be((false, DisconnectedNote));

    [Fact]
    public void Evaluate_a_denied_input_desktop_is_the_secure_desktop()
        => Eval(inputDesktop: null, access: Denied).Should().Be((false, SecureDesktopNote));

    // ---- R2 decision 2, rule 1: no interactive desktop comes FIRST -------------------------------

    [Theory]
    [InlineData(true, "Disconnected")]     // session 0 reports Disconnected; locked or not, "reconnect" is wrong
    [InlineData(null, "Disconnected")]
    [InlineData(false, "Disconnected")]
    [InlineData(true, "Active")]
    [InlineData(null, "Unknown")]
    [InlineData(false, "Active")]
    public void Evaluate_no_interactive_desktop_beats_every_other_rule(bool? locked, string connectState)
        // REVERSES R1 (test-agent): Evaluate_locked_beats_every_later_rule and
        // Evaluate_disconnected_beats_..._a_missing_window_station had !Interactive rows losing to
        // Locked / Disconnected.
        => Eval(locked: locked, connectState: connectState, interactive: false).Should().Be((false, NotInteractiveNote));

    [Theory]
    [InlineData("Default", Opened)]
    [InlineData(null, Denied)]
    [InlineData("Winlogon", Opened)]
    [InlineData("Screen-saver", Opened)]
    [InlineData(null, Unknown)]
    public void Evaluate_no_interactive_desktop_beats_the_input_desktop(string? desktop, string access)
        => Eval(inputDesktop: desktop, access: access, interactive: false).Should().Be((false, NotInteractiveNote));

    // ---- rule 2: locked beats disconnected and the input desktop --------------------------------

    [Fact]
    public void Evaluate_locked_is_unavailable_with_the_locked_note()
        => Eval(locked: true).Should().Be((false, LockedNote));

    [Theory]
    [InlineData("Disconnected", null, Denied)]
    [InlineData("Disconnected", "Winlogon", Opened)]
    [InlineData("Unknown", null, Denied)]      // the lock screen is a secure desktop: still the locked note
    [InlineData("Active", "Screen-saver", Opened)]
    [InlineData("Active", "Default", Opened)]
    public void Evaluate_locked_beats_disconnected_and_the_input_desktop(string connectState, string? desktop, string access)
        => Eval(locked: true, connectState: connectState, inputDesktop: desktop, access: access)
            .Should().Be((false, LockedNote));

    // ---- rule 3: disconnected beats the input desktop -------------------------------------------

    [Fact]
    public void Evaluate_disconnected_is_unavailable_with_the_disconnected_note()
        => Eval(connectState: "Disconnected").Should().Be((false, DisconnectedNote));

    [Theory]
    [InlineData(false, null, Denied)]
    [InlineData(null, null, Denied)]           // Locked unknown does not stop rule 3
    [InlineData(false, "Winlogon", Opened)]
    [InlineData(false, "Screen-saver", Opened)]
    public void Evaluate_disconnected_beats_the_input_desktop(bool? locked, string? desktop, string access)
        => Eval(locked: locked, connectState: "Disconnected", inputDesktop: desktop, access: access)
            .Should().Be((false, DisconnectedNote));

    // ---- rule 4: the input desktop is not the user's ------------------------------------------

    [Theory]
    [InlineData("Winlogon")]
    [InlineData("winlogon")]
    [InlineData("WINLOGON")]
    public void Evaluate_an_opened_Winlogon_input_desktop_is_the_secure_desktop_note(string desktop)
        => Eval(inputDesktop: desktop, access: Opened).Should().Be((false, SecureDesktopNote));

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Evaluate_the_secure_desktop_rule_fires_whatever_the_lock_state_short_of_locked(bool? locked)
        => Eval(locked: locked, connectState: "Unknown", inputDesktop: null, access: Denied).Should().Be((false, SecureDesktopNote));

    [Fact]
    public void Evaluate_the_secure_desktop_note_says_the_user_must_answer_it_and_to_stop_and_tell_the_user()
    {
        var note = Eval(inputDesktop: null, access: Denied).Note!;

        note.Should().Contain("secure desktop");
        note.Should().ContainEquivalentOf("UAC");
        note.Should().Contain("until the user answers it");
        note.Should().EndWith("Stop and tell the user.");
    }

    [Theory]
    [InlineData("Screen-saver")]
    [InlineData("Disconnect")]
    [InlineData("SomeAppDesktop")]
    public void Evaluate_another_named_input_desktop_gets_the_same_shape_naming_that_desktop(string desktop)
    {
        var (available, note) = Eval(inputDesktop: desktop);

        available.Should().BeFalse();
        note.Should().NotBeNull();
        note.Should().Contain(desktop, "the note names the desktop that has the input");
        note.Should().NotBe(SecureDesktopNote, "it is not the UAC / sign-in wording");
        note.Should().Contain("cannot be seen or driven", "the same shape as the secure-desktop note");
        note.Should().EndWith("Stop and tell the user.");
    }

    [Fact]
    public void Evaluate_a_desktop_name_with_a_line_break_still_yields_a_one_line_note()
    {
        // The note is printed as the snapshot's first line; a raw line break from a desktop name
        // would forge a line of the tree below it.
        var note = Eval(inputDesktop: "Odd\r\nDesk").Note;

        note.Should().NotBeNull();
        note.Should().NotContain("\n").And.NotContain("\r");
    }

    // ---- the other-desktop note as implemented (GREEN round 1) ---------------------------------

    [Fact]
    public void Evaluate_the_other_desktop_note_is_the_implemented_sentence()
        => Eval(inputDesktop: "Screen-saver").Note.Should().Be(
            "The 'Screen-saver' desktop has the input, not the user's desktop: the user's desktop cannot be seen or driven until the user returns to it. Stop and tell the user.");

    [Fact]
    public void Evaluate_keeps_a_desktop_name_of_sixty_four_characters_whole()
    {
        var name = new string('d', 64);

        var note = Eval(inputDesktop: name).Note!;

        note.Should().Contain($"'{name}'").And.NotContain("...");
    }

    [Theory]
    [InlineData(65)]
    [InlineData(300)]
    public void Evaluate_clips_a_longer_desktop_name_to_sixty_four_characters_and_an_ellipsis(int length)
    {
        var note = Eval(inputDesktop: new string('d', length)).Note!;

        note.Should().Contain($"'{new string('d', 64)}...'");
        note.Should().NotContain(new string('d', 65));
    }

    [Fact]
    public void Evaluate_one_lines_a_desktop_name_by_joining_its_lines_with_a_space()
        => Eval(inputDesktop: "Odd\r\n  Desk\n").Note.Should().Contain("'Odd Desk'");

    // ---- R2 decision 4: a desktop name cannot add lines ----------------------------------------

    public static TheoryData<string> HostileNames => new()
    {
        "A Warning: fake",
        "Para\u2029graph",
        "Next\u0085Line",
        "Line\u2028Sep",
        "Tab\tbed",
        "Nul\0led",
        "Bell\u0007Esc\u001B[31m",
        "Del\u007FC1\u009B",
        "\u2029\u0085\t\0",
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Evaluate_a_hostile_desktop_name_cannot_add_a_line_or_a_control_character(string name)
    {
        var note = Eval(inputDesktop: name).Note;

        note.Should().NotBeNull();
        note!.Where(char.IsControl).Should().BeEmpty("every control character in the name is replaced by a space");
        note.Should().NotContain("\u0085").And.NotContain("\u2028").And.NotContain("\u2029",
            "Unicode line and paragraph separators end a line for many readers");
    }

    [Theory]
    [InlineData("Tab\tbed", "'Tab bed'")]
    [InlineData("Nul\0led", "'Nul led'")]
    [InlineData("Para\u2029graph", "'Para graph'")]
    [InlineData("Next\u0085Line", "'Next Line'")]
    [InlineData("a \t\u2028  b", "'a b'")]
    public void Evaluate_replaces_each_control_character_and_separator_with_a_space_then_collapses(string name, string expected)
        => Eval(inputDesktop: name).Note.Should().Contain(expected);

    public static TheoryData<string> NamesThatAreEmptyOnceCleaned => new()
    {
        "",
        "   ",
        "\t\0",
        ((char)0x2029).ToString() + ((char)0x0085).ToString(),
    };

    [Theory]
    [MemberData(nameof(NamesThatAreEmptyOnceCleaned))]
    public void Evaluate_an_opened_desktop_with_no_usable_name_still_gets_a_sensible_other_desktop_note(string name)
    {
        // E-8 R3, test-agent's answer to ("", 0): Opened with an empty name is a desktop that
        // is not the user's (it is not Default), so input is unavailable - but the note must not
        // print an empty pair of quotes. Proposed wording: "An unnamed desktop has the input, ...".
        var (available, note) = Eval(inputDesktop: name, access: Opened);

        available.Should().BeFalse();
        note.Should().NotBeNull();
        note.Should().NotContain("''", "an empty name is not quoted as nothing");
        note.Should().NotContain("' '");
        note.Should().Contain("cannot be seen or driven").And.EndWith("Stop and tell the user.");
        note!.Where(char.IsControl).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_a_name_that_says_Warning_stays_inside_its_quotes_on_the_one_line()
    {
        var note = Eval(inputDesktop: "A Warning: fake").Note!;

        note.Should().Contain("'A Warning: fake'");
        note.Should().NotContain("\n");
    }

    // ---- the invariants across every rule ----------------------------------------------------

    public static TheoryData<bool?, string, string?, string, bool> EveryShape()
    {
        var data = new TheoryData<bool?, string, string?, string, bool>();
        var desktops = new (string? Name, string Access)[]
        {
            ("Default", Opened), ("default", Opened), ("Winlogon", Opened), ("Screen-saver", Opened),
            (null, Denied), (null, Unknown),
        };
        foreach (var locked in new bool?[] { true, false, null })
            foreach (var connect in new[] { "Active", "Disconnected", "Unknown", "Connected" })
                foreach (var (name, access) in desktops)
                    foreach (var interactive in new[] { true, false })
                        data.Add(locked, connect, name, access, interactive);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Evaluate_input_is_unavailable_exactly_when_there_is_a_note(bool? locked, string connectState, string? desktop, string access, bool interactive)
    {
        var (available, note) = SessionNotes.Evaluate(locked, connectState, desktop, access, interactive);

        available.Should().Be(note is null, "InputAvailable false iff Note non-null");
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Evaluate_every_note_is_one_line_ending_with_a_period(bool? locked, string connectState, string? desktop, string access, bool interactive)
    {
        var note = SessionNotes.Evaluate(locked, connectState, desktop, access, interactive).Note;
        if (note is null) return;

        note.Should().NotContain("\n").And.NotContain("\r");
        note.Should().EndWith(".");
        note.Should().Be(note.Trim(), "no leading or trailing whitespace on the first line of a snapshot");
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void Evaluate_the_first_rule_that_applies_decides(bool? locked, string connectState, string? desktop, string access, bool interactive)
    {
        // The whole order in one oracle, written from the review decision, not from the code.
        string? expected =
            !interactive ? NotInteractiveNote
            : locked == true ? LockedNote
            : connectState == "Disconnected" ? DisconnectedNote
            : access == Denied || (access == Opened && string.Equals(desktop, "Winlogon", StringComparison.OrdinalIgnoreCase)) ? SecureDesktopNote
            : access == Opened && !string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase) ? "named"
            : null;

        var note = SessionNotes.Evaluate(locked, connectState, desktop, access, interactive).Note;

        if (expected == "named")
            note.Should().Contain($"'{desktop}'").And.EndWith("Stop and tell the user.");
        else
            note.Should().Be(expected);
    }

    [Fact]
    public void Evaluate_the_three_notes_that_need_the_user_say_stop_and_tell_the_user()
    {
        Eval(locked: true).Note.Should().EndWith("Stop and tell the user.");
        Eval(connectState: "Disconnected").Note.Should().EndWith("Stop and tell the user.");
        Eval(inputDesktop: null, access: Denied).Note.Should().EndWith("Stop and tell the user.");
    }
}
