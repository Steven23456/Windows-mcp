namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// S-11: the takeover key combination. <paramref name="Modifiers"/> uses the <c>RegisterHotKey</c>
/// flags (<see cref="ModAlt"/>, <see cref="ModControl"/>, <see cref="ModShift"/>,
/// <see cref="ModWin"/>); <paramref name="Key"/> is the virtual-key code; <paramref name="Display"/>
/// is the canonical text, such as <c>Ctrl+Alt+Shift+Backspace</c>.
/// </summary>
public record HotkeyChord(uint Modifiers, ushort Key, string Display)
{
    public const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;

    public static HotkeyChord Default { get; } = new(ModControl | ModAlt | ModShift, 0x08, "Ctrl+Alt+Shift+Backspace");
}

/// <summary>S-11: the process-level indicator switch, the takeover combination and the fade delay.</summary>
public record ControlIndicatorOptions(bool Enabled, HotkeyChord Hotkey, TimeSpan Linger)
{
    public static ControlIndicatorOptions Default { get; } = new(true, HotkeyChord.Default, TimeSpan.FromSeconds(5));
}

/// <summary>S-11: a takeover the AI has not been told about yet: when it happened and which combination did it.</summary>
public record TakeoverRecord(DateTimeOffset At, string Hotkey);
