using WindowsInput;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

namespace WindowsMcp.Hosting;

/// <summary>
/// S-11: <c>--control-hotkey</c> / <c>WINDOWSMCP_CONTROL_HOTKEY</c> text to a <see cref="HotkeyChord"/>.
/// Each <c>+</c>-separated part is resolved by <see cref="ShortcutParser.ResolveKey"/>: every part
/// but the last must be a modifier, the last must be a key, and at least one of ctrl, alt or win
/// must be WRITTEN. Shift alone is ordinary typing, and a modifier the keyboard layout implies for
/// the key (Shift for <c>!</c>, AltGr's Ctrl+Alt for <c>@</c>) is not one the user chose - while the
/// indicator is up the combination is taken from every other application, so it must never be a
/// plain key. Implied modifiers are still part of what is registered, or the key could not be typed.
/// </summary>
internal static class HotkeyParser
{
    private const string Expected = "expected ctrl, alt or win (shift may be added) plus one key, e.g. ctrl+alt+shift+backspace";

    /// <param name="vkKeyScan">The keyboard layout for punctuation (tests); null = the machine's.</param>
    /// <exception cref="OptionsException">Any combination the hotkey cannot be; the message names <paramref name="option"/> and the value.</exception>
    internal static HotkeyChord Parse(string value, string option, Func<char, short>? vkKeyScan = null)
    {
        var parts = (value ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new OptionsException($"Invalid --{option} '{value}'; {Expected}.");

        uint written = 0;
        foreach (var part in parts[..^1])
        {
            written |= ModifierFlag(Resolve(part).Key)
                ?? throw new OptionsException($"Invalid --{option} '{value}'; '{part}' is not a modifier - only the last part can be a key; {Expected}.");
        }

        var key = Resolve(parts[^1]);
        if (ModifierFlag(key.Key) is not null)
            throw new OptionsException($"Invalid --{option} '{value}'; the last part must be a key, not a modifier; {Expected}.");
        if ((written & (HotkeyChord.ModControl | HotkeyChord.ModAlt | HotkeyChord.ModWin)) == 0)
            throw new OptionsException(
                $"Invalid --{option} '{value}'; a hotkey needs ctrl, alt or win written out, so it never takes a key " +
                "people type (shift alone, or one the keyboard layout adds, does not count).");

        var modifiers = written;
        foreach (var implied in key.ImpliedModifiers)
            modifiers |= ModifierFlag(implied) ?? 0;

        var names = new List<string>(5);
        if ((modifiers & HotkeyChord.ModControl) != 0) names.Add("Ctrl");
        if ((modifiers & HotkeyChord.ModAlt) != 0) names.Add("Alt");
        if ((modifiers & HotkeyChord.ModShift) != 0) names.Add("Shift");
        if ((modifiers & HotkeyChord.ModWin) != 0) names.Add("Win");
        names.Add(char.ToUpperInvariant(parts[^1][0]) + parts[^1][1..]);

        return new HotkeyChord(modifiers, (ushort)key.Key, string.Join('+', names));

        ShortcutParser.Token Resolve(string part)
        {
            try { return ShortcutParser.ResolveKey(part, vkKeyScan); }
            catch (ArgumentException ex) { throw new OptionsException($"Invalid --{option} '{value}': {ex.Message}"); }
        }
    }

    private static uint? ModifierFlag(VirtualKeyCode vk) => vk switch
    {
        VirtualKeyCode.CONTROL or VirtualKeyCode.LCONTROL or VirtualKeyCode.RCONTROL => HotkeyChord.ModControl,
        VirtualKeyCode.MENU or VirtualKeyCode.LMENU or VirtualKeyCode.RMENU => HotkeyChord.ModAlt,
        VirtualKeyCode.SHIFT or VirtualKeyCode.LSHIFT or VirtualKeyCode.RSHIFT => HotkeyChord.ModShift,
        VirtualKeyCode.LWIN or VirtualKeyCode.RWIN => HotkeyChord.ModWin,
        _ => null,
    };
}
