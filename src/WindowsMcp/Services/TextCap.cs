namespace WindowsMcp.Services;

/// <summary>
/// C-5: one cut for every text <c>scrape</c> caps, so the http and dom sources agree on what
/// <c>max_chars</c> means. Never splits a surrogate pair: a cut that would land between the two
/// halves of one character takes one character less instead.
/// </summary>
internal static class TextCap
{
    internal static string Cut(string text, int maxChars, out bool truncated)
    {
        truncated = text.Length > maxChars;
        if (!truncated) return text;
        int cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }
}
