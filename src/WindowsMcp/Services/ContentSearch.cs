using System.Text.RegularExpressions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// E-9 (pure): the per-file line scan behind <c>file_search</c>'s content mode
/// (docs/design/E-9-file-edit-content-search.md), over a <see cref="TextReader"/>.
/// </summary>
internal static class ContentSearch
{
    /// <summary>A matched line and each context line are clipped at this many characters, then <c>...</c>.</summary>
    internal const int MaxLineChars = 400;

    /// <summary>How much of a file's head <see cref="LooksBinary"/> inspects.</summary>
    internal const int BinaryProbeBytes = 8192;

    /// <summary>A larger file is skipped (counted in filesSkipped).</summary>
    internal const long MaxFileBytes = TextEdit.MaxFileBytes;

    /// <summary>A content search stops once the matched and context lines returned total this many characters.</summary>
    internal const int MaxResultChars = 60_000;

    /// <summary>The per-line regex match timeout.</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How many lines pass between two looks at the cancellation token.</summary>
    private const int LinesPerCancellationCheck = 1000;

    /// <summary>No BOM and a NUL byte in the first <see cref="BinaryProbeBytes"/> bytes.</summary>
    internal static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length > BinaryProbeBytes) head = head[..BinaryProbeBytes];
        // A Unicode BOM says text, and UTF-16/32 text is full of NUL bytes.
        bool bom = (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                   || (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
                   || (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF);
        return !bom && head.Contains((byte)0);
    }

    /// <summary>
    /// Reads <paramref name="reader"/> a line at a time and returns the lines
    /// <paramref name="regex"/> matches (1-based, clipped), with <paramref name="contextLines"/>
    /// before/after each, stopping at <paramref name="maxMatches"/> (<see cref="ContentScan.Truncated"/>).
    /// A regex timeout is an <see cref="InvalidOperationException"/> naming the pattern and
    /// <paramref name="path"/>; <paramref name="ct"/> is observed every 1,000 lines.
    /// </summary>
    /// <param name="maxChars">
    /// The remaining output budget: the scan also stops once the matched and context lines it
    /// has returned total this many characters (the match that crosses it still finishes its context).
    /// </param>
    internal static ContentScan Scan(TextReader reader, string path, Regex regex, int contextLines, int maxMatches, CancellationToken ct = default, int maxChars = MaxResultChars)
    {
        var found = new List<(int Line, string Text, string[]? Before, List<string>? After)>();
        // Only the matches still collecting after-context are visited per line: the cost of a
        // line must not grow with the number of matches already found.
        var collecting = new List<List<string>>();
        var recent = new Queue<string>(Math.Max(contextLines, 1));
        bool capReached = maxMatches <= 0 || maxChars <= 0;
        int lineNumber = 0;
        long chars = 0;

        while (!(capReached && collecting.Count == 0) && reader.ReadLine() is { } line)
        {
            if (lineNumber % LinesPerCancellationCheck == 0) ct.ThrowIfCancellationRequested();
            lineNumber++;

            string? clipped = null;
            string Clipped() => clipped ??= Clip(line);

            // This line is the after-context of the matches just above it.
            for (int i = collecting.Count - 1; i >= 0; i--)
            {
                collecting[i].Add(Clipped());
                chars += Clipped().Length;
                if (collecting[i].Count >= contextLines) collecting.RemoveAt(i);
            }

            SpendBudget();
            if (!capReached && IsMatch(regex, line, path, lineNumber))
            {
                string[]? before = contextLines > 0 ? recent.ToArray() : null;
                List<string>? after = contextLines > 0 ? new List<string>(contextLines) : null;
                found.Add((lineNumber, Clipped(), before, after));
                if (after is not null) collecting.Add(after);
                chars += Clipped().Length + (before?.Sum(l => (long)l.Length) ?? 0);
                if (found.Count >= maxMatches) capReached = true;
            }
            SpendBudget();

            if (contextLines > 0)
            {
                recent.Enqueue(Clipped());
                if (recent.Count > contextLines) recent.Dequeue();
            }
        }

        var matches = found.Select(m => new ContentMatch(path, m.Line, m.Text, m.Before, m.After?.ToArray())).ToArray();
        return new ContentScan(matches, capReached);

        // Checked before a line is tried as a match and again after one is taken. Once the output
        // budget is spent only the newest match still finishes its context, so the overshoot is
        // bounded by one match however dense the matches are.
        void SpendBudget()
        {
            if (chars < maxChars) return;
            capReached = true;
            if (collecting.Count > 1) collecting.RemoveRange(0, collecting.Count - 1);
        }
    }

    private static bool IsMatch(Regex regex, string line, string path, int lineNumber)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            var pattern = regex.ToString();
            if (pattern.Length > 80) pattern = pattern[..80] + "...";
            throw new InvalidOperationException(
                $"The content pattern '{pattern}' is too slow on '{path}' (line {lineNumber}): one match took longer than " +
                $"{regex.MatchTimeout.TotalSeconds:0.###} s, so the search was stopped. Simplify the pattern (nested repeats such as (a+)+ are the usual cause).");
        }
    }

    /// <summary>At most <see cref="MaxLineChars"/> characters, then "...", never half a surrogate pair.</summary>
    private static string Clip(string line)
    {
        if (line.Length <= MaxLineChars) return line;
        int cut = char.IsHighSurrogate(line[MaxLineChars - 1]) ? MaxLineChars - 1 : MaxLineChars;
        return line[..cut] + "...";
    }
}

/// <summary>E-9: one file's matches, and whether the scan stopped at the cap.</summary>
internal sealed record ContentScan(IReadOnlyList<ContentMatch> Matches, bool Truncated);
