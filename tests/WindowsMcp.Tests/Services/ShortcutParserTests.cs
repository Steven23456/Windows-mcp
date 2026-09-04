using FluentAssertions;
using WindowsInput;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-1: the parser is pure, so these run anywhere. VkKeyScan is replaced by a fixed US-layout
/// subset so results never depend on the machine's keyboard layout.
/// </summary>
[Trait("Category", "Unit")]
public class ShortcutParserTests
{
    private const int ShiftState = 0x0100;

    private static short FakeScan(char c) => c switch
    {
        '+' => (short)(ShiftState | (int)VirtualKeyCode.OEM_PLUS),
        '=' => (short)VirtualKeyCode.OEM_PLUS,
        '-' => (short)VirtualKeyCode.OEM_MINUS,
        '/' => (short)VirtualKeyCode.OEM_2,
        '!' => (short)(ShiftState | (int)VirtualKeyCode.VK_1),
        _   => -1,
    };

    [Theory]
    [InlineData("ctrl+c",        new[] { VirtualKeyCode.CONTROL },                        VirtualKeyCode.VK_C)]
    [InlineData("ctrl+shift+s",  new[] { VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT }, VirtualKeyCode.VK_S)]
    [InlineData("win+r",         new[] { VirtualKeyCode.LWIN },                           VirtualKeyCode.VK_R)]
    [InlineData("alt+f4",        new[] { VirtualKeyCode.MENU },                           VirtualKeyCode.F4)]
    [InlineData("ctrl+1",        new[] { VirtualKeyCode.CONTROL },                        VirtualKeyCode.VK_1)]
    [InlineData("ctrl+shift+esc", new[] { VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT }, VirtualKeyCode.ESCAPE)]
    [InlineData("win",           new VirtualKeyCode[0],                                   VirtualKeyCode.LWIN)]
    [InlineData("esc",           new VirtualKeyCode[0],                                   VirtualKeyCode.ESCAPE)]
    [InlineData("f24",           new VirtualKeyCode[0],                                   VirtualKeyCode.F24)]
    [InlineData("printscreen",   new VirtualKeyCode[0],                                   VirtualKeyCode.SNAPSHOT)]
    [InlineData("numpad5",       new VirtualKeyCode[0],                                   VirtualKeyCode.NUMPAD5)]
    [InlineData("CTRL + C",      new[] { VirtualKeyCode.CONTROL },                        VirtualKeyCode.VK_C)]
    [InlineData("Windows+E",     new[] { VirtualKeyCode.LWIN },                           VirtualKeyCode.VK_E)]
    public void Parse_resolves_chords_and_bare_keys(string shortcut, VirtualKeyCode[] modifiers, VirtualKeyCode key)
    {
        var chord = ShortcutParser.Parse(shortcut, FakeScan);

        chord.Key.Should().Be(key);
        chord.Modifiers.Should().Equal(modifiers);
    }

    [Fact]
    public void Parse_merges_the_shift_state_the_layout_implies()
    {
        var chord = ShortcutParser.Parse("ctrl+plus", FakeScan);

        chord.Key.Should().Be(VirtualKeyCode.OEM_PLUS);
        chord.Modifiers.Should().Equal(VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT);
    }

    [Fact]
    public void Parse_does_not_duplicate_a_modifier_the_layout_also_implies()
    {
        var chord = ShortcutParser.Parse("shift+!", FakeScan);

        chord.Key.Should().Be(VirtualKeyCode.VK_1);
        chord.Modifiers.Should().Equal(VirtualKeyCode.SHIFT);
    }

    [Fact]
    public void Parse_resolves_a_punctuation_character_through_the_layout()
    {
        var chord = ShortcutParser.Parse("ctrl+/", FakeScan);

        chord.Key.Should().Be(VirtualKeyCode.OEM_2);
        chord.Modifiers.Should().Equal(VirtualKeyCode.CONTROL);
    }

    [Fact]
    public void Parse_names_the_offending_token_and_the_whole_chord()
    {
        Action act = () => ShortcutParser.Parse("ctrl+foo", FakeScan);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*'foo'*")
            .WithMessage("*'ctrl+foo'*");
    }

    [Theory]
    [InlineData("",    "empty")]
    [InlineData("   ", "empty")]
    [InlineData("+",   "no key")]
    public void Parse_rejects_an_empty_chord(string shortcut, string expectedFragment)
    {
        Action act = () => ShortcutParser.Parse(shortcut, FakeScan);

        act.Should().Throw<ArgumentException>().WithMessage($"*{expectedFragment}*");
    }

    [Fact]
    public void Parse_reports_a_character_the_layout_cannot_produce()
    {
        Action act = () => ShortcutParser.Parse("ctrl+é", FakeScan);

        act.Should().Throw<ArgumentException>().WithMessage("*layout*");
    }

    [Fact]
    public void ResolveKey_accepts_a_letter()
    {
        var token = ShortcutParser.ResolveKey("a", FakeScan);

        token.Key.Should().Be(VirtualKeyCode.VK_A);
        token.ImpliedModifiers.Should().BeEmpty();
    }

    [Fact]
    public void ResolveKey_accepts_the_plus_character_itself()
    {
        var token = ShortcutParser.ResolveKey("+", FakeScan);

        token.Key.Should().Be(VirtualKeyCode.OEM_PLUS);
        token.ImpliedModifiers.Should().Equal(VirtualKeyCode.SHIFT);
    }

    [Fact]
    public void ResolveKey_points_chords_at_the_shortcut_tool()
    {
        Action act = () => ShortcutParser.ResolveKey("ctrl+c", FakeScan);

        act.Should().Throw<ArgumentException>().WithMessage("*shortcut*");
    }
}
