namespace WindowsMcp.Services;

/// <summary>
/// B-1: the seam a typing plan is executed against. The production sink drives H.InputSimulator;
/// the tests pass a recorder, which is the only way to assert the ORDER of the keystrokes a plan
/// produces without injecting real input (roadmap C10: anything that injects is UIAutomation).
/// </summary>
internal interface IKeyboardSink
{
    /// <summary>A chord, e.g. <c>ctrl+a</c> or <c>ctrl+v</c>.</summary>
    void Shortcut(string chord);

    /// <summary>One key, e.g. <c>enter</c>, <c>tab</c>, <c>backspace</c>.</summary>
    void Key(string key);

    /// <summary>A literal chunk of text.</summary>
    void Text(string text);

    /// <summary>B-7: press <paramref name="key"/> and hold it (the Ctrl of a multi-select).</summary>
    void KeyDown(string key);

    /// <summary>B-7: release a held <paramref name="key"/>.</summary>
    void KeyUp(string key);
}
