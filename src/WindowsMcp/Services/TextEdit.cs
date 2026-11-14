using System.Text;

namespace WindowsMcp.Services;

/// <summary>
/// E-9 (pure): the encoding and replacement rules behind <c>file_edit</c>
/// (docs/design/E-9-file-edit-content-search.md). No disk access; the content refusals are thrown
/// from here so they are unit-tested without a file.
/// </summary>
internal static class TextEdit
{
    /// <summary>The largest file <c>file_edit</c> opens: 16 MiB.</summary>
    internal const long MaxFileBytes = 16L * 1024 * 1024;

    private const string EncodingChoices = "auto, utf-8, utf-16, ascii, latin1";

    // Strict in both directions: an edit must never rewrite bytes it did not understand, nor
    // write a character the file's encoding cannot hold.
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
    private static readonly Encoding Ascii = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>
    /// The encoding of <paramref name="bytes"/>: a BOM decides when present (UTF-32 LE checked
    /// before UTF-16 LE), otherwise <paramref name="encoding"/> (auto|utf-8|utf-16|ascii|latin1,
    /// any case). An unknown name is an <see cref="ArgumentException"/> naming <c>encoding</c>.
    /// </summary>
    internal static DetectedEncoding Detect(ReadOnlySpan<byte> bytes, string encoding)
    {
        // The name is checked even when a BOM will decide: a bad argument is a bad call.
        DetectedEncoding fromName = (encoding ?? "").ToLowerInvariant() switch
        {
            // auto assumes UTF-8; Decode then refuses text that looks like it was something else.
            "auto" => new DetectedEncoding(Utf8, 0, "utf-8", Guessed: true),
            "utf-8" => new DetectedEncoding(Utf8, 0, "utf-8"),
            "utf-16" => new DetectedEncoding(Utf16Le, 0, "utf-16"),
            "ascii" => new DetectedEncoding(Ascii, 0, "ascii"),
            "latin1" => new DetectedEncoding(Latin1, 0, "latin1"),
            _ => throw new ArgumentException($"Unknown encoding '{encoding}'; expected one of: {EncodingChoices}", nameof(encoding)),
        };

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return new DetectedEncoding(Utf32Le, 4, "utf-32");
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DetectedEncoding(Utf8, 3, "utf-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new DetectedEncoding(Utf16Le, 2, "utf-16");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new DetectedEncoding(Utf16Be, 2, "utf-16-be");
        return fromName;
    }

    /// <summary>
    /// Strict decode of <paramref name="bytes"/> past the BOM. Invalid bytes are an
    /// <see cref="InvalidOperationException"/> naming the encoding and the <c>encoding</c> values.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, DetectedEncoding detected)
    {
        string text;
        try
        {
            text = detected.Encoding.GetString(bytes[detected.BomLength..]);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException(
                $"The file is not valid {detected.Name} text, so it was not edited (an edit must not rewrite bytes it cannot read). " +
                $"Pass the file's real encoding: {EncodingChoices}. latin1 reads any single-byte file without loss.");
        }

        // UTF-16 ASCII text is valid UTF-8 (NUL is a legal character), and a one-character edit
        // would then shift every character after it. A guess does not get to make that mistake.
        if (detected.Guessed && text.Contains('\0'))
            throw new InvalidOperationException(
                "The file has no byte-order mark and contains NUL characters, so it is not UTF-8 text: it looks like utf-16 " +
                "without a byte-order mark, or a binary file. It was not edited. If it is UTF-16 text, pass encoding:\"utf-16\"; " +
                "if it really is UTF-8 text that contains NUL characters, pass encoding:\"utf-8\".");
        return text;
    }

    /// <summary>
    /// Strict encode of <paramref name="text"/>, with the BOM exactly when
    /// <see cref="DetectedEncoding.BomLength"/> &gt; 0. A character the encoding cannot represent
    /// (or a lone surrogate) is an <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static byte[] Encode(string text, DetectedEncoding detected)
    {
        byte[] body;
        try
        {
            body = detected.Encoding.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"new_string has a character the file's encoding ({detected.Name}) cannot represent, so nothing was written. " +
                "Use only characters that encoding has, or rewrite the file in another encoding with file_write.");
        }
        if (detected.BomLength == 0) return body;

        byte[] bom = detected.Name switch
        {
            "utf-8" => [0xEF, 0xBB, 0xBF],
            "utf-16" => [0xFF, 0xFE],
            "utf-16-be" => [0xFE, 0xFF],
            _ => [0xFF, 0xFE, 0x00, 0x00],   // utf-32
        };
        var all = new byte[bom.Length + body.Length];
        bom.CopyTo(all, 0);
        body.CopyTo(all, bom.Length);
        return all;
    }

    /// <summary>
    /// The edit: ordinal, non-overlapping, exact match first, then the line-ending-converted
    /// <paramref name="oldString"/>; <paramref name="newString"/> written in the file's convention.
    /// Zero matches, or more than one without <paramref name="replaceAll"/>, is an
    /// <see cref="InvalidOperationException"/>; an empty <paramref name="oldString"/> or one equal to
    /// <paramref name="newString"/> is an <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="path">Named in the refusals when given; the pure tests leave it out.</param>
    internal static TextEditOutcome Apply(string text, string oldString, string newString, bool replaceAll, string? path = null)
    {
        if (string.IsNullOrEmpty(oldString))
            throw new ArgumentException("old_string is empty; give the exact text to replace.", "old_string");
        if (newString is null)
            throw new ArgumentException("new_string is missing; pass an empty string to delete old_string.", "new_string");
        if (string.Equals(oldString, newString, StringComparison.Ordinal))
            throw new ArgumentException("old_string and new_string are the same, so nothing would change.", "new_string");

        string where = path is null ? "the file" : $"'{path}'";
        bool hasCrlf = text.Contains("\r\n", StringComparison.Ordinal);
        bool hasCr = text.Contains('\r');
        string endings = LineEndingsOf(text);

        // Attempt 1: exactly as given. Attempt 2, only when that found nothing: old_string with
        // its line endings in the file's convention (the model sends \n; the file has \r\n).
        var search = oldString;
        bool converted = false;
        var positions = FindAll(text, search);
        if (positions.Count == 0)
        {
            var alternative = hasCrlf ? ToCrlf(oldString) : !hasCr ? ToLf(oldString) : oldString;
            if (!string.Equals(alternative, oldString, StringComparison.Ordinal))
            {
                search = alternative;
                converted = true;
                positions = FindAll(text, search);
            }
        }

        if (positions.Count == 0)
            throw new InvalidOperationException(
                $"old_string was not found in {where}; nothing was changed. It must match the file's text exactly " +
                "(case, spaces and indentation): read the lines with file_read and copy them.");
        if (positions.Count > 1 && !replaceAll)
            throw new InvalidOperationException(
                $"old_string matches {positions.Count} times in {where}; nothing was changed. Add surrounding context " +
                "to make it unique, or pass replace_all:true to replace every occurrence.");

        // new_string in the file's convention. A mixed file has none, and neither does a file
        // with no line endings at all, so there it is written as given - unless old_string had to
        // be converted to match, in which case new_string gets the same conversion.
        string replacement =
            converted ? (hasCrlf ? ToCrlf(newString) : ToLf(newString))
            : endings == "crlf" ? ToCrlf(newString)
            : endings == "lf" && !hasCr ? ToLf(newString)
            : newString;

        var result = new StringBuilder(text.Length + (replacement.Length - search.Length) * positions.Count);
        int from = 0;
        foreach (int at in positions)
        {
            result.Append(text, from, at - from).Append(replacement);
            from = at + search.Length;
        }
        result.Append(text, from, text.Length - from);

        // \r\n, \n and a lone \r each end one line.
        int firstLine = 1;
        for (int i = 0; i < positions[0]; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'))) firstLine++;
        }
        return new TextEditOutcome(result.ToString(), positions.Count, firstLine, endings);
    }

    /// <summary>crlf | lf | mixed | none for <paramref name="text"/>. A lone \r is not a line ending here.</summary>
    internal static string LineEndingsOf(string text)
    {
        bool crlf = false, lf = false;
        for (int i = 0; i < text.Length && !(crlf && lf); i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf = true;
            else lf = true;
        }
        return crlf && lf ? "mixed" : crlf ? "crlf" : lf ? "lf" : "none";
    }

    /// <summary>
    /// Every occurrence, left to right, not overlapping - except one that would split a \r\n pair
    /// (it starts with \n right after a \r, or ends with \r right before a \n): replacing that
    /// would leave half a line ending behind.
    /// </summary>
    private static List<int> FindAll(string text, string value)
    {
        var found = new List<int>();
        int from = 0;
        while (from <= text.Length - value.Length)
        {
            int at = text.IndexOf(value, from, StringComparison.Ordinal);
            if (at < 0) break;
            int end = at + value.Length;
            bool splitsPair = (value[0] == '\n' && at > 0 && text[at - 1] == '\r')
                              || (value[^1] == '\r' && end < text.Length && text[end] == '\n');
            if (splitsPair)
            {
                from = at + 1;
                continue;
            }
            found.Add(at);
            from = end;
        }
        return found;
    }

    /// <summary>Bare \n becomes \r\n; an existing \r\n is not doubled.</summary>
    private static string ToCrlf(string value) => ToLf(value).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string ToLf(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>
/// E-9: what <see cref="TextEdit.Detect"/> found. <see cref="Name"/> is
/// utf-8|utf-16|utf-16-be|utf-32|ascii|latin1; <see cref="BomLength"/> is 0 when there is no BOM.
/// </summary>
/// <remarks><see cref="Guessed"/>: no BOM and <c>encoding:"auto"</c> - UTF-8 was assumed, not asked for.</remarks>
internal sealed record DetectedEncoding(Encoding Encoding, int BomLength, string Name, bool Guessed = false);

/// <summary>
/// E-9: the result of <see cref="TextEdit.Apply"/>. <see cref="FirstLine"/> is the 1-based line of
/// the first replacement in the original text; <see cref="LineEndings"/> is the original's kind.
/// </summary>
internal sealed record TextEditOutcome(string Text, int Replacements, int FirstLine, string LineEndings);
