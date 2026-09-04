using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WindowsMcp.Services;

/// <summary>
/// Parses the CLIXML that Windows PowerShell 5.1 writes to stderr when stderr is redirected, and
/// which it uses for EVERY non-stdout stream — error, warning, verbose, debug, progress.
/// </summary>
/// <remarks>
/// Two consumers, one parser, so they cannot drift: <see cref="PowerShellService.ExtractErrors"/>
/// keeps only the <c>Error</c> records (they decide <c>PSResult.Success</c>), while
/// <see cref="Decode"/> turns the whole stream into readable text for <c>PSResult.Stderr</c>
/// (checklist D-8). Before D-8 the raw XML blob went to the model on every call — measured
/// 2026-09-04, a one-liner with a single <c>Write-Progress</c> produced 596 characters of it.
///
/// Anything that is not CLIXML (a native child writing raw bytes) or that fails to parse (raw bytes
/// interleaved with the XML) is passed through untouched — losing output is worse than a big blob.
/// </remarks>
internal static class ClixmlStderr
{
    private const string ClixmlHeader = "#< CLIXML";

    /// <summary>Streams worth showing. <c>progress</c> is deliberately absent: there is no console
    /// to draw a progress bar on, so those records are pure noise.</summary>
    private static readonly string[] ReportedStreams = ["Error", "Warning", "Verbose", "Debug"];

    /// <summary>
    /// Parses the records out of a CLIXML stderr stream. Returns false — with no records — when
    /// <paramref name="stderr"/> is not CLIXML or cannot be parsed, which is the caller's signal to
    /// fall back to the raw text.
    /// </summary>
    internal static bool TryParseRecords(string stderr, out IReadOnlyList<(string Stream, string Text)> records)
    {
        records = Array.Empty<(string, string)>();
        if (string.IsNullOrEmpty(stderr)) return false;
        if (!stderr.StartsWith(ClixmlHeader, StringComparison.Ordinal)) return false;

        int xmlStart = stderr.IndexOf('<', ClixmlHeader.Length);
        if (xmlStart < 0) return true;   // header only, no records at all — parsed, and empty

        var payload = stderr[xmlStart..];
        if (TryParsePayload(payload, out records)) return true;

        // The payload did not parse as a whole. The usual cause is a TRAILING PARTIAL document: a
        // background job's stderr is read incrementally (JobService pumps it into a
        // BoundedTextBuffer), so a read can land mid-flush. Retry on everything up to the last
        // complete document and drop the fragment — for a running job it arrives whole on the next
        // read, and a fragment is XML nobody could have read anyway. Anything still unparseable
        // falls back to the raw stream (checklist D-9).
        int lastClose = payload.LastIndexOf(ObjsClose, StringComparison.Ordinal);
        if (lastClose < 0) return false;

        return TryParsePayload(payload[..(lastClose + ObjsClose.Length)], out records);
    }

    private const string ObjsClose = "</Objs>";

    private static bool TryParsePayload(string payload, out IReadOnlyList<(string Stream, string Text)> records)
    {
        records = Array.Empty<(string, string)>();
        try
        {
            // One <Objs> document per stream flush can be concatenated; wrap to parse as one.
            var root = XElement.Parse("<r>" + payload + "</r>");
            records = root.Descendants()
                .Where(e => e.Name.LocalName == "S")
                .Select(e => ((string?)e.Attribute("S") ?? "", DecodeEscapes(e.Value)))
                .Where(r => r.Item1.Length > 0)
                .ToArray();
            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns a raw stderr stream into text a model can read: one stream-prefixed line per line of
    /// each error / warning / verbose / debug record, progress dropped entirely. Non-CLIXML and
    /// unparseable CLIXML are returned unchanged.
    /// </summary>
    internal static string Decode(string stderr)
    {
        if (!TryParseRecords(stderr, out var records)) return stderr;

        var sb = new StringBuilder();
        foreach (var (stream, text) in records)
        {
            if (!ReportedStreams.Contains(stream, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // The prefix is what the PowerShell host itself prints, and it is the only way a
                // reader can tell a verbose trace from a real warning once the XML is gone.
                sb.Append(stream.ToUpperInvariant()).Append(": ").Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    // CLIXML escapes characters that are invalid in XML text as _xHHHH_ (CRLF arrives as
    // _x000D__x000A_); a surrogate pair is two consecutive escapes, so per-char decode is exact.
    private static readonly Regex Escape = new("_x([0-9A-Fa-f]{4})_", RegexOptions.Compiled);

    internal static string DecodeEscapes(string value) =>
        Escape.Replace(value, m => ((char)ushort.Parse(
            m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
}
