using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// C-1: the pure line window behind <c>file_read(offset_lines, limit_lines)</c>. Lines split on
/// <c>\n</c> with a trailing <c>\r</c> stripped, so a CRLF file counts the same as an LF one; a
/// final newline does not add an empty line. <paramref name="offsetLines"/> is 1-based like
/// upstream (0 and 1 both mean the first line); <paramref name="limitLines"/> 0 means to the
/// end. The content joins the window with <c>\n</c>.
/// </summary>
internal static class LineWindow
{
    internal static TextWindow Slice(string text, int offsetLines, int limitLines)
    {
        if (offsetLines < 0)
            throw new ArgumentException("'offset_lines' must be 0 or more (1-based; 0 and 1 both mean the first line)", nameof(offsetLines));
        if (limitLines < 0)
            throw new ArgumentException("'limit_lines' must be 0 (to the end) or more", nameof(limitLines));

        var lines = SplitLines(text);
        int start = Math.Max(offsetLines, 1);
        int startIndex = start - 1;
        if (startIndex >= lines.Length)
            return new TextWindow(lines.Length, start, 0, false, string.Empty);

        int remaining = lines.Length - startIndex;
        int take = limitLines == 0 ? remaining : Math.Min(limitLines, remaining);
        bool truncated = startIndex + take < lines.Length;
        return new TextWindow(lines.Length, start, take, truncated,
            string.Join('\n', lines, startIndex, take));
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var parts = text.Split('\n');
        int count = parts.Length;
        if (count > 0 && parts[count - 1].Length == 0) count--;   // a final newline is not a line
        var lines = new string[count];
        for (int i = 0; i < count; i++)
            lines[i] = parts[i].EndsWith('\r') ? parts[i][..^1] : parts[i];
        return lines;
    }
}
