using System.Globalization;
using System.Text;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// The compact text form of a snapshot — what the model reads by default (roadmap C6: several
/// times cheaper in tokens than the JSON). One fixed layout so the model only ever parses one
/// shape; the element tree is JSON-only and is never rendered here.
/// </summary>
internal static class SnapshotRenderer
{
    private const int MaxValueChars = 80;

    internal static string Render(SnapshotResult r)
    {
        var lines = new List<string>();

        lines.Add(r.CursorMonitorIndex < 0
            ? $"Cursor: ({r.Cursor.X}, {r.Cursor.Y}) on no display"
            : $"Cursor: ({r.Cursor.X}, {r.Cursor.Y}) on display {r.CursorMonitorIndex}");

        lines.Add(r.ActiveWindow is { } a
            ? $"Active window: \"{Esc(a.Title)}\" (pid {a.Pid}, {a.State})"
            : "Active window: none");

        lines.Add("Windows (z-order, topmost first):");
        foreach (var w in r.Windows)
        {
            var b = w.Bounds;
            lines.Add($"  {w.ZOrder}. \"{Esc(w.Title)}\" [{w.State}] {b.Width}x{b.Height} @ ({b.X},{b.Y}) pid={w.Pid}{(w.IsBrowser ? " browser" : "")}");
        }

        lines.Add($"Interactive ({r.Interactive.Length} of {r.ElementCount}, ids valid until the next snapshot):");
        // Grouped by window in first-appearance order even if the walk interleaved them: GroupBy
        // keeps both the key order and the element order stable.
        foreach (var group in r.Interactive.GroupBy(e => e.Window, StringComparer.Ordinal))
        {
            lines.Add($"window \"{Esc(group.Key)}\"");
            foreach (var e in group)
                lines.Add(ElementLine(e));
        }

        lines.Add($"Scrollable ({r.Scrollable.Length}):");
        foreach (var s in r.Scrollable)
            lines.Add(ScrollableLine(s));

        if (r.Truncated)
            lines.Add(ElementBudget.NoteFor(r.ElementLimit));

        // A-14: only when the server was started with --profile-snapshot (Stages is null otherwise).
        if (r.Stages is { } stages)
        {
            var parts = string.Join(", ", stages.Select(s => $"{s.Stage} {s.Ms} ms"));
            lines.Add(parts.Length == 0 ? $"Timing: (total {r.CaptureMs} ms)" : $"Timing: {parts} (total {r.CaptureMs} ms)");
        }

        return string.Join("\n", lines);
    }

    private static string ElementLine(SnapshotElement e)
    {
        var sb = new StringBuilder();
        sb.Append("  ").Append(e.ElementId)
          .Append(" (").Append(e.CenterX).Append(',').Append(e.CenterY).Append(") ")
          .Append(e.ControlType.ToLowerInvariant())
          .Append(" \"").Append(Esc(e.Name)).Append('"');

        // Fixed tag order: action, focused, password, value, toggle, expand, shortcut, range.
        Tag(sb, $"action: {e.Action}");
        if (e.Focused) Tag(sb, "focused");
        if (e.IsPassword) Tag(sb, "password");
        else if (e.Value is { } v) Tag(sb, $"value: \"{Esc(Clip(v))}\"");
        if (!string.IsNullOrWhiteSpace(e.Toggle)) Tag(sb, $"toggle: {e.Toggle}");
        if (!string.IsNullOrWhiteSpace(e.Expand)) Tag(sb, $"expand: {e.Expand}");
        if (!string.IsNullOrWhiteSpace(e.Shortcut)) Tag(sb, $"shortcut: {e.Shortcut}");
        if (e.RangeValue is { } rv)
        {
            Tag(sb, e.RangeMin is { } lo && e.RangeMax is { } hi
                ? $"range: {Num(rv)} of {Num(lo)}..{Num(hi)}"
                : $"range: {Num(rv)}");
        }
        return sb.ToString();
    }

    private static string ScrollableLine(SnapshotScrollable s)
    {
        var sb = new StringBuilder();
        sb.Append("  ").Append(s.ElementId)
          .Append(" (").Append(s.CenterX).Append(',').Append(s.CenterY).Append(") ")
          .Append(s.ControlType.ToLowerInvariant())
          .Append(" \"").Append(Esc(s.Name)).Append('"');

        int v = Percent(s.Scroll.VerticalPercent), h = Percent(s.Scroll.HorizontalPercent);
        Tag(sb, $"v: {v}%");
        Tag(sb, $"h: {h}%");
        if (s.Scroll.VerticallyScrollable)
        {
            if (v <= 0) Tag(sb, "reached top");
            if (v >= 100) Tag(sb, "reached bottom");
        }
        return sb.ToString();
    }

    private static void Tag(StringBuilder sb, string text) => sb.Append("  [").Append(text).Append(']');

    /// <summary>
    /// Keeps one element on one row whatever its text contains. A Document value is a whole
    /// multi-line file and a browser name is page content nobody here controls, so CR/LF/tab are
    /// escaped, and the backslash first so a Windows path stays distinguishable from an escape.
    /// </summary>
    private static string Esc(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");

    private static string Clip(string v) => v.Length > MaxValueChars ? v[..MaxValueChars] + "…" : v;

    private static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static int Percent(double p) => (int)Math.Round(p, MidpointRounding.AwayFromZero);
}
