using Windows.Win32;
using WindowsInput;

namespace WindowsMcp.Services;

/// <summary>
/// Pure resolver from key names and chords ("ctrl+shift+s", "win", "a", "printscreen") to
/// H.InputSimulator <see cref="VirtualKeyCode"/>s. Nothing is injected here — <see cref="InputService"/>
/// only sends what this returns — so it is unit-testable without a desktop (D-1).
/// </summary>
/// <remarks>
/// Token resolution order: named key / alias → punctuation by name (<c>plus</c>, <c>comma</c>, …;
/// needed because <c>+</c> is the chord separator) → single character (letters and digits map
/// straight to <c>VK_A..</c> / <c>VK_0..</c>; anything else goes through <c>VkKeyScan</c>, which
/// also reports the Shift/Ctrl/Alt state the active keyboard layout needs to produce it) → error
/// naming the token. <c>VkKeyScan</c> is layout-dependent on purpose; it is injectable so tests
/// never touch the real layout.
/// </remarks>
internal static class ShortcutParser
{
    /// <summary>A resolved chord: modifiers to hold (de-duplicated, in order) and the key to tap.</summary>
    internal readonly record struct Chord(VirtualKeyCode[] Modifiers, VirtualKeyCode Key);

    /// <summary>
    /// A resolved single token: the key, plus any modifiers the keyboard layout needs to produce
    /// it (on a US layout <c>+</c> is Shift + <see cref="VirtualKeyCode.OEM_PLUS"/>).
    /// </summary>
    internal readonly record struct Token(VirtualKeyCode Key, VirtualKeyCode[] ImpliedModifiers);

    private const string Guidance =
        "Use a character, a-z, 0-9, f1-f24, or a named key such as enter, tab, esc, backspace, " +
        "delete, up, down, left, right, home, end, pageup, pagedown, win, printscreen, plus.";

    private static readonly Dictionary<string, VirtualKeyCode> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"]       = VirtualKeyCode.RETURN,   ["return"]      = VirtualKeyCode.RETURN,
        ["tab"]         = VirtualKeyCode.TAB,
        ["esc"]         = VirtualKeyCode.ESCAPE,   ["escape"]      = VirtualKeyCode.ESCAPE,
        ["space"]       = VirtualKeyCode.SPACE,
        ["backspace"]   = VirtualKeyCode.BACK,
        ["delete"]      = VirtualKeyCode.DELETE,   ["del"]         = VirtualKeyCode.DELETE,
        ["insert"]      = VirtualKeyCode.INSERT,   ["ins"]         = VirtualKeyCode.INSERT,
        ["up"]          = VirtualKeyCode.UP,       ["arrowup"]     = VirtualKeyCode.UP,
        ["down"]        = VirtualKeyCode.DOWN,     ["arrowdown"]   = VirtualKeyCode.DOWN,
        ["left"]        = VirtualKeyCode.LEFT,     ["arrowleft"]   = VirtualKeyCode.LEFT,
        ["right"]       = VirtualKeyCode.RIGHT,    ["arrowright"]  = VirtualKeyCode.RIGHT,
        ["home"]        = VirtualKeyCode.HOME,
        ["end"]         = VirtualKeyCode.END,
        ["pageup"]      = VirtualKeyCode.PRIOR,    ["pgup"]        = VirtualKeyCode.PRIOR,
        ["pagedown"]    = VirtualKeyCode.NEXT,     ["pgdn"]        = VirtualKeyCode.NEXT,

        // Modifiers. Left/right-specific names are honoured; the plain names are the generic codes.
        ["ctrl"]        = VirtualKeyCode.CONTROL,  ["control"]     = VirtualKeyCode.CONTROL,
        ["lctrl"]       = VirtualKeyCode.LCONTROL, ["rctrl"]       = VirtualKeyCode.RCONTROL,
        ["alt"]         = VirtualKeyCode.MENU,     ["option"]      = VirtualKeyCode.MENU,
        ["lalt"]        = VirtualKeyCode.LMENU,    ["ralt"]        = VirtualKeyCode.RMENU,
        ["shift"]       = VirtualKeyCode.SHIFT,
        ["lshift"]      = VirtualKeyCode.LSHIFT,   ["rshift"]      = VirtualKeyCode.RSHIFT,
        ["win"]         = VirtualKeyCode.LWIN,     ["windows"]     = VirtualKeyCode.LWIN,
        ["super"]       = VirtualKeyCode.LWIN,     ["cmd"]         = VirtualKeyCode.LWIN,
        ["meta"]        = VirtualKeyCode.LWIN,     ["lwin"]        = VirtualKeyCode.LWIN,
        ["rwin"]        = VirtualKeyCode.RWIN,

        ["printscreen"] = VirtualKeyCode.SNAPSHOT, ["prtsc"]       = VirtualKeyCode.SNAPSHOT,
        ["prtscn"]      = VirtualKeyCode.SNAPSHOT,
        ["capslock"]    = VirtualKeyCode.CAPITAL,  ["caps"]        = VirtualKeyCode.CAPITAL,
        ["numlock"]     = VirtualKeyCode.NUMLOCK,
        ["scrolllock"]  = VirtualKeyCode.SCROLL,
        ["apps"]        = VirtualKeyCode.APPS,     ["menu"]        = VirtualKeyCode.APPS,
        ["context"]     = VirtualKeyCode.APPS,
        ["pause"]       = VirtualKeyCode.PAUSE,    ["break"]       = VirtualKeyCode.PAUSE,

        ["add"]         = VirtualKeyCode.ADD,      ["numpadplus"]  = VirtualKeyCode.ADD,
        ["subtract"]    = VirtualKeyCode.SUBTRACT, ["numpadminus"] = VirtualKeyCode.SUBTRACT,
        ["multiply"]    = VirtualKeyCode.MULTIPLY,
        ["divide"]      = VirtualKeyCode.DIVIDE,
        ["decimal"]     = VirtualKeyCode.DECIMAL,

        ["volumeup"]    = VirtualKeyCode.VOLUME_UP,
        ["volumedown"]  = VirtualKeyCode.VOLUME_DOWN,
        ["mute"]        = VirtualKeyCode.VOLUME_MUTE,
        ["playpause"]   = VirtualKeyCode.MEDIA_PLAY_PAUSE,
    };

    // Punctuation that cannot be written as itself in a chord (or is easier to name). Each maps
    // to a character and is then resolved like a typed character, so it follows the active layout.
    private static readonly Dictionary<string, char> NamedChars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plus"] = '+', ["minus"] = '-', ["equals"] = '=',
        ["comma"] = ',', ["period"] = '.', ["dot"] = '.',
        ["slash"] = '/', ["backslash"] = '\\',
        ["semicolon"] = ';', ["quote"] = '\'',
        ["backtick"] = '`', ["grave"] = '`',
        ["lbracket"] = '[', ["rbracket"] = ']',
    };

    static ShortcutParser()
    {
        for (int i = 1; i <= 24; i++)
            Named[$"f{i}"] = VirtualKeyCode.F1 + (i - 1);
        for (int i = 0; i <= 9; i++)
        {
            Named[$"numpad{i}"] = VirtualKeyCode.NUMPAD0 + i;
            Named[$"num{i}"]    = VirtualKeyCode.NUMPAD0 + i;
        }
    }

    /// <summary>
    /// Parses a chord such as <c>ctrl+shift+s</c>. Parts are split on <c>+</c> (trimmed, empties
    /// dropped — write <c>plus</c> for the + key); all but the last are modifiers, the last is the
    /// key. A single part (<c>win</c>, <c>esc</c>) is a bare key press. Modifiers the layout implies
    /// for the key (Shift for <c>!</c>) are merged in and de-duplicated.
    /// </summary>
    internal static Chord Parse(string shortcut, Func<char, short>? vkKeyScan = null)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
            throw new ArgumentException("Shortcut is empty.", nameof(shortcut));

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"Shortcut '{shortcut}' names no key. Write 'plus' for the + key.", nameof(shortcut));

        var modifiers = new List<VirtualKeyCode>(parts.Length + 2);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var token = ResolveToken(parts[i], shortcut, vkKeyScan);
            foreach (var m in token.ImpliedModifiers) AddDistinct(modifiers, m);
            AddDistinct(modifiers, token.Key);
        }

        var last = ResolveToken(parts[^1], shortcut, vkKeyScan);
        foreach (var m in last.ImpliedModifiers) AddDistinct(modifiers, m);
        return new Chord(modifiers.ToArray(), last.Key);
    }

    /// <summary>
    /// Resolves exactly one key for the <c>key</c> tool. A multi-character token containing
    /// <c>+</c> is a chord and is rejected with a pointer to <c>shortcut</c>.
    /// </summary>
    internal static Token ResolveKey(string key, Func<char, short>? vkKeyScan = null)
    {
        var trimmed = (key ?? "").Trim();
        if (trimmed.Length > 1 && trimmed.Contains('+'))
            throw new ArgumentException($"'{key}' looks like a chord; use the shortcut tool for key combinations.", nameof(key));
        return ResolveToken(trimmed, context: null, vkKeyScan);
    }

    private static Token ResolveToken(string token, string? context, Func<char, short>? vkKeyScan)
    {
        if (token.Length == 0)
            throw new ArgumentException("Key name is empty.", nameof(token));

        if (Named.TryGetValue(token, out var vk))
            return new Token(vk, []);

        if (NamedChars.TryGetValue(token, out var namedChar))
            return FromChar(namedChar, token, context, vkKeyScan);

        if (token.Length == 1)
            return FromChar(token[0], token, context, vkKeyScan);

        var where = context is null ? "" : $" in '{context}'";
        throw new ArgumentException($"Unknown key '{token}'{where}. {Guidance}");
    }

    private static Token FromChar(char c, string token, string? context, Func<char, short>? vkKeyScan)
    {
        // Letters and digits are layout-independent virtual keys.
        if (c is >= 'a' and <= 'z') return new Token(VirtualKeyCode.VK_A + (c - 'a'), []);
        if (c is >= 'A' and <= 'Z') return new Token(VirtualKeyCode.VK_A + (c - 'A'), []);
        if (c is >= '0' and <= '9') return new Token(VirtualKeyCode.VK_0 + (c - '0'), []);

        // Everything else depends on the keyboard layout: low byte = virtual key, high byte =
        // shift state (bit 1 Shift, bit 2 Ctrl, bit 4 Alt); -1 = no key produces this character.
        short scan = (vkKeyScan ?? DefaultVkKeyScan)(c);
        if (scan == -1)
        {
            var where = context is null ? "" : $" in '{context}'";
            throw new ArgumentException($"'{token}'{where} has no key on the active keyboard layout. {Guidance}");
        }

        var key = (VirtualKeyCode)(scan & 0xFF);
        int state = (scan >> 8) & 0xFF;
        var implied = new List<VirtualKeyCode>(3);
        if ((state & 1) != 0) implied.Add(VirtualKeyCode.SHIFT);
        if ((state & 2) != 0) implied.Add(VirtualKeyCode.CONTROL);
        if ((state & 4) != 0) implied.Add(VirtualKeyCode.MENU);
        return new Token(key, implied.ToArray());
    }

    private static short DefaultVkKeyScan(char c) => PInvoke.VkKeyScan(c);

    private static void AddDistinct(List<VirtualKeyCode> list, VirtualKeyCode code)
    {
        if (!list.Contains(code)) list.Add(code);
    }
}
