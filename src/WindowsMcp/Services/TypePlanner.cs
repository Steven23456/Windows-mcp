using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-1: one step of a typing plan. <see cref="Kind"/> is <c>shortcut</c> (a chord through
/// <c>PressShortcutAsync</c>), <c>key</c> (one key through <c>PressKeyAsync</c>), <c>text</c>
/// (a literal chunk through the simulator's TextEntry) or <c>paste</c> (clipboard + Ctrl+V).
/// </summary>
internal sealed record TypeStep(string Kind, string Value)
{
    internal static TypeStep Shortcut(string chord) => new("shortcut", chord);
    internal static TypeStep Key(string key) => new("key", key);
    internal static TypeStep Text(string chunk) => new("text", chunk);
    internal static TypeStep Paste(string text) => new("paste", text);
}

/// <param name="Method">"keys" or "paste" — what <c>TypeResult.Method</c> reports.</param>
internal sealed record TypePlan(string Method, IReadOnlyList<TypeStep> Steps);

/// <summary>
/// B-1 (roadmap C8): the pure decision behind <c>type</c> — clear, caret, keys-vs-paste, the
/// newline/tab split, press-enter. No Windows API, no clipboard, no simulator, so the whole
/// contract is unit-testable without a desktop.
/// </summary>
internal static class TypePlanner
{
    /// <summary>Text this long goes through the clipboard: one keystroke instead of thousands.</summary>
    internal const int PasteThreshold = 200;

    /// <summary>
    /// The ordered steps for <paramref name="text"/> under <paramref name="options"/>. Throws
    /// <see cref="ArgumentException"/> when <c>PaceMs</c> is negative.
    /// </summary>
    internal static TypePlan Plan(string text, TypeOptions options)
    {
        if (options.PaceMs < 0)
            throw new ArgumentException($"pace must be 0 or more, got {options.PaceMs}", nameof(options));

        var steps = new List<TypeStep>();
        if (options.Clear)
        {
            steps.Add(TypeStep.Shortcut("ctrl+a"));
            steps.Add(TypeStep.Key("backspace"));
        }
        switch (options.Caret)
        {
            case CaretPosition.Start: steps.Add(TypeStep.Shortcut("ctrl+home")); break;
            case CaretPosition.End: steps.Add(TypeStep.Shortcut("ctrl+end")); break;
        }

        // Paste only what the clipboard can carry verbatim: long, and no control character other
        // than the two the keyboard path translates. Anything else is typed key by key.
        bool paste = text.Length >= PasteThreshold && text.All(c => c >= ' ' || c == '\n' || c == '\t');
        if (paste)
            steps.Add(TypeStep.Paste(text));
        else
            steps.AddRange(KeySteps(text));

        if (options.PressEnter) steps.Add(TypeStep.Key("enter"));
        return new TypePlan(paste ? "paste" : "keys", steps);
    }

    /// <summary>Literal chunks with every newline (LF, CR or CRLF) as Enter and every tab as Tab.</summary>
    private static IEnumerable<TypeStep> KeySteps(string text)
    {
        var chunk = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n' || c == '\t')
            {
                if (chunk.Length > 0) { yield return TypeStep.Text(chunk.ToString()); chunk.Clear(); }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;   // CRLF is one line break
                yield return TypeStep.Key(c == '\t' ? "tab" : "enter");
            }
            else
            {
                chunk.Append(c);
            }
        }
        if (chunk.Length > 0) yield return TypeStep.Text(chunk.ToString());
    }
}
