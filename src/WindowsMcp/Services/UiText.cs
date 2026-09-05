using System.Text;

namespace WindowsMcp.Services;

/// <summary>
/// A-13: the one place UI-supplied text is made safe to hand to the model. Pure — no UIA, no
/// Win32 — so every rule is unit-tested without a desktop (roadmap C10).
/// </summary>
/// <remarks>
/// On .NET 10 <c>System.Text.Json</c> does not throw on a lone surrogate: it silently writes
/// U+FFFD, so the model receives a value that differs from what UIA reported with nothing in the
/// response saying so, and Private Use glyphs (VS Code's codicons) pass through as token noise.
/// This makes the repair explicit and drops the noise before anything is serialised.
/// </remarks>
internal static class UiText
{
    /// <summary>
    /// Strips Private Use Area code points (U+E000–U+F8FF and the two supplementary PUA planes),
    /// replaces lone UTF-16 surrogates with U+FFFD, drops C0/C1 controls except tab/LF/CR, then
    /// trims. Null becomes "". A string that needs nothing returns equal (and untouched).
    /// </summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Allocated only when the first change is needed; until then the input is returned as-is.
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    int cp = char.ConvertToUtf32(c, text[i + 1]);
                    bool supplementaryPua = cp is (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD);
                    if (supplementaryPua)
                        Drop(ref sb, text, i);
                    else
                        sb?.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
                Replace(ref sb, text, i);
                continue;
            }
            if (char.IsLowSurrogate(c))
            {
                Replace(ref sb, text, i);
                continue;
            }
            if (c is >= '\uE000' and <= '\uF8FF')
            {
                Drop(ref sb, text, i);
                continue;
            }
            if ((c < ' ' && c is not ('\t' or '\n' or '\r')) || c is >= '\u007F' and <= '\u009F')
            {
                Drop(ref sb, text, i);
                continue;
            }
            sb?.Append(c);
        }

        return (sb is null ? text : sb.ToString()).Trim();
    }

    /// <summary>Starts the copy at the first change: everything before index <paramref name="i"/> was kept verbatim.</summary>
    private static void Drop(ref StringBuilder? sb, string text, int i)
        => sb ??= new StringBuilder(text.Length).Append(text, 0, i);

    private static void Replace(ref StringBuilder? sb, string text, int i)
    {
        Drop(ref sb, text, i);
        sb!.Append('\uFFFD');
    }
}
