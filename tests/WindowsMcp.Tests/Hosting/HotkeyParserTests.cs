using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// S-11 (R1): <c>--control-hotkey</c> text to a <see cref="HotkeyChord"/>. Built on the existing
/// <c>ShortcutParser</c>, which reads every part but the last as a "modifier" - so <c>ctrl+a+b</c>
/// parses there as a chord holding A. A hotkey needs real modifiers, so the refusals below are
/// HotkeyParser's own, and each one is an <see cref="OptionsException"/> naming the option (the
/// type <c>Program</c> prints with the usage text), never <c>ShortcutParser</c>'s ArgumentException.
/// <para>
/// Layout-dependent keys (punctuation through <c>VkKeyScan</c>, which can imply Shift) are left out
/// on purpose: what they parse to depends on the machine's keyboard layout.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class HotkeyParserTests
{
    private const string Option = "control-hotkey";
    private const uint Ctrl = HotkeyChord.ModControl, Alt = HotkeyChord.ModAlt, Shift = HotkeyChord.ModShift, Win = HotkeyChord.ModWin;

    [Fact]
    public void The_default_combination_parses_to_HotkeyChord_Default()
    {
        HotkeyParser.Parse("ctrl+alt+shift+backspace", Option).Should().Be(HotkeyChord.Default);
    }

    [Theory]
    [InlineData("ctrl+alt+shift+backspace", Ctrl | Alt | Shift, 0x08, "Ctrl+Alt+Shift+Backspace")]
    [InlineData("shift+ctrl+alt+backspace", Ctrl | Alt | Shift, 0x08, "Ctrl+Alt+Shift+Backspace")]   // canonical order whatever was typed
    [InlineData("alt+shift+ctrl+backspace", Ctrl | Alt | Shift, 0x08, "Ctrl+Alt+Shift+Backspace")]
    [InlineData("ctrl+alt+shift+f13", Ctrl | Alt | Shift, 0x7C, "Ctrl+Alt+Shift+F13")]
    [InlineData("win+shift+p", Shift | Win, 0x50, "Shift+Win+P")]                                     // Win comes last of the modifiers
    [InlineData("control+alt+f12", Ctrl | Alt, 0x7B, "Ctrl+Alt+F12")]                                 // alias -> generic name
    [InlineData("lctrl+ralt+x", Ctrl | Alt, 0x58, "Ctrl+Alt+X")]                                      // left/right variants collapse
    [InlineData("rshift+lwin+f1", Shift | Win, 0x70, "Shift+Win+F1")]
    [InlineData("option+delete", Alt, 0x2E, "Alt+Delete")]
    [InlineData("cmd+space", Win, 0x20, "Win+Space")]
    [InlineData("ctrl+esc", Ctrl, 0x1B, "Ctrl+Esc")]
    [InlineData("alt+enter", Alt, 0x0D, "Alt+Enter")]
    [InlineData("ctrl+alt+1", Ctrl | Alt, 0x31, "Ctrl+Alt+1")]
    [InlineData("ctrl + alt + f13", Ctrl | Alt, 0x7C, "Ctrl+Alt+F13")]                               // ShortcutParser trims each part
    [InlineData("ctrl+ctrl+a", Ctrl, 0x41, "Ctrl+A")]                                                 // a repeated modifier is one flag
    [InlineData("lctrl+rctrl+a", Ctrl, 0x41, "Ctrl+A")]
    [InlineData("CTRL+ALT+SHIFT+BACKSPACE", Ctrl | Alt | Shift, 0x08, "Ctrl+Alt+Shift+BACKSPACE")]   // the key as written, first letter up
    [InlineData("Ctrl+Alt+BackSpace", Ctrl | Alt, 0x08, "Ctrl+Alt+BackSpace")]
    public void A_valid_combination_parses_to_its_flags_key_and_canonical_display(
        string value, uint modifiers, int key, string display)
    {
        HotkeyParser.Parse(value, Option).Should().Be(new HotkeyChord(modifiers, (ushort)key, display));
    }

    [Theory]
    [InlineData("backspace")]          // no modifier: a bare key would steal that key from every app
    [InlineData("a")]
    [InlineData("f13")]
    [InlineData("ctrl+a+b")]           // a non-modifier before the last part (ShortcutParser would hold A)
    [InlineData("a+ctrl+b")]
    [InlineData("ctrl+f1+f2")]
    [InlineData("ctrl+alt")]           // a modifier as the last part: no key to press
    [InlineData("ctrl+shift")]
    [InlineData("win")]
    [InlineData("shift")]
    [InlineData("ctrl+lwin")]
    [InlineData("a+ctrl")]             // both at once
    [InlineData("ctrl+bogus")]         // an unknown key
    [InlineData("hyper+a")]            // an unknown "modifier"
    [InlineData("ctrl+")]              // the empty part is dropped, leaving a lone modifier
    [InlineData("+")]                  // no parts at all
    [InlineData("+++")]
    public void An_invalid_combination_is_refused_naming_the_option_and_the_value(string value)
    {
        var act = () => HotkeyParser.Parse(value, Option);

        var message = act.Should().Throw<OptionsException>(
                "an OptionsException is what Program prints with the usage; ShortcutParser's ArgumentException would crash startup")
            .Which.Message;
        message.Should().Contain(Option, "the message names the option the operator has to fix");
        message.Should().Contain(value, "and quotes what they typed, like every other refusal in ServerOptions");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_combination_is_refused_naming_the_option(string value)
    {
        var act = () => HotkeyParser.Parse(value, Option);

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain(Option);
    }

    [Fact]
    public void The_message_names_whichever_option_it_was_given()
    {
        // The env-var path and the flag path may name the source differently; the parser must use
        // what it is told rather than a hard-coded string.
        var act = () => HotkeyParser.Parse("a", "some-other-option");

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain("some-other-option");
    }

    [Fact]
    public void The_refusal_for_a_missing_modifier_says_a_modifier_is_needed()
    {
        var act = () => HotkeyParser.Parse("backspace", Option);

        act.Should().Throw<OptionsException>().Which.Message.Should().MatchRegex("(?i)modifier|ctrl|alt|shift|win",
            "the operator has to learn what is missing, not just that something is");
    }

    // ---- F3: modifiers count only when WRITTEN; Shift alone is typing; implied ones still register --
    // The keyboard layout is injected (ShortcutParser's VkKeyScan seam), so these rows mean the
    // same thing on every machine. VkKeyScan's answer: low byte = virtual key, high byte = the shift
    // state needed to type the character (1 Shift, 2 Ctrl, 4 Alt; AltGr is Ctrl+Alt).

    /// <summary>A US layout for the punctuation below: '!' is Shift+1, '?' Shift+/, '@' Shift+2, '+' Shift+=.</summary>
    private static short UsLayout(char c) => c switch
    {
        '!' => 0x0131,
        '?' => 0x01BF,
        '@' => 0x0132,
        '+' => 0x01BB,
        _ => -1,
    };

    /// <summary>A layout where '@' is AltGr+Q - Ctrl+Alt+Q, as on a German keyboard.</summary>
    private static short AltGrLayout(char c) => c == '@' ? (short)0x0651 : UsLayout(c);

    private static void ShouldBeRefused(Func<HotkeyChord> act, string value)
    {
        var message = act.Should().Throw<OptionsException>("an OptionsException is what Program prints with the usage").Which.Message;
        message.Should().Contain(Option, "the message names the option the operator has to fix");
        message.Should().Contain(value, "and quotes what they typed");
    }

    [Theory]
    [InlineData("!")]           // nothing written: the Shift is only what the layout needs to type '!'
    [InlineData("?")]
    [InlineData("@")]
    [InlineData("plus")]
    [InlineData("shift+a")]     // Shift alone: Shift+letter is ordinary typing (a capital A)
    [InlineData("shift+1")]
    [InlineData("shift+f13")]
    [InlineData("shift+!")]     // a written Shift plus an implied one is still Shift alone
    [InlineData("lshift+rshift+a")]
    public void A_combination_without_a_written_ctrl_alt_or_win_is_refused(string value)
    {
        ShouldBeRefused(() => HotkeyParser.Parse(value, Option, UsLayout), value);
    }

    [Fact]
    public void Modifiers_the_layout_implies_do_not_count_as_written()
    {
        // '@' on an AltGr layout IS a Ctrl+Alt chord - but the operator wrote no modifier, so it is a
        // single character they type every day, and registering it would steal it system-wide.
        ShouldBeRefused(() => HotkeyParser.Parse("@", Option, AltGrLayout), "@");
    }

    [Theory]
    [InlineData("ctrl+!", Ctrl | Shift, 0x31, "Ctrl+Shift+!")]          // the implied Shift is registered and shown
    [InlineData("ctrl+shift+!", Ctrl | Shift, 0x31, "Ctrl+Shift+!")]    // written and implied Shift are one flag
    [InlineData("win+?", Shift | Win, 0xBF, "Shift+Win+?")]             // canonical order: Shift before Win
    [InlineData("alt+plus", Alt | Shift, 0xBB, "Alt+Shift+Plus")]
    public void Modifiers_the_layout_implies_are_added_to_the_chord_and_the_display(string value, uint modifiers, int key, string display)
    {
        // RegisterHotKey needs the real chord: Ctrl+! on a US keyboard is Ctrl+Shift+1, and a
        // registration of Ctrl+1 would never fire for it.
        HotkeyParser.Parse(value, Option, UsLayout).Should().Be(new HotkeyChord(modifiers, (ushort)key, display));
    }

    [Fact]
    public void An_AltGr_character_with_a_written_modifier_registers_the_implied_ctrl_alt()
    {
        HotkeyParser.Parse("alt+@", Option, AltGrLayout).Should()
            .Be(new HotkeyChord(Ctrl | Alt, 0x51, "Ctrl+Alt+@"), "AltGr is Ctrl+Alt; Alt was written, so the chord counts");
    }

    [Fact]
    public void The_layout_is_the_injected_one_not_the_machines()
    {
        // The seam is forwarded to ShortcutParser: '!' resolves through the given scanner, so a
        // layout where '!' is an unshifted key gives an unshifted chord.
        HotkeyParser.Parse("ctrl+!", Option, c => c == '!' ? (short)0x00DF : (short)-1).Should()
            .Be(new HotkeyChord(Ctrl, 0xDF, "Ctrl+!"));
    }
}
