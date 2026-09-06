using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// B-6: everything ONE poll gathered. Only the piece the condition needs is non-null — an
/// <c>active_window</c> wait reads A-1's window list and never walks the tree, a
/// <c>text_exists</c> wait takes an A-2 snapshot, an element condition runs D-5's guarded find.
/// </summary>
internal sealed record WaitEvidence(
    ElementInfo[]? Matches = null,
    SnapshotResult? Snapshot = null,
    WindowInfo[]? Windows = null);

/// <summary>
/// B-6 (roadmap C10): the pure half of <c>wait_for</c> — given one poll's evidence, is the
/// condition satisfied, and what does the caller get told? No UIA, no desktop, no clock.
/// </summary>
internal static class WaitConditions
{
    /// <summary>
    /// One poll's verdict: whether <paramref name="condition"/> holds for <paramref name="text"/>
    /// given what the poll gathered, a one-line detail the agent can act on (what was found and
    /// where, or what was wanted and what was seen instead), and the element when the condition
    /// is about one. Evidence a condition did not need — or a poll could not gather — is simply
    /// "not there yet", never a throw.
    /// </summary>
    internal static (bool Satisfied, string Detail, ElementInfo? Element) Evaluate(
        WaitCondition condition, string text, WaitEvidence evidence)
    {
        switch (condition)
        {
            case WaitCondition.ElementExists:
            {
                var hit = evidence.Matches is { Length: > 0 } m ? m[0] : null;
                return hit is null
                    ? (false, $"no element matching '{text}'", null)
                    : (true, $"found '{hit.Name}' ({hit.ElementId})", hit);
            }
            case WaitCondition.ElementEnabled:
            {
                if (evidence.Matches is not { Length: > 0 } matches)
                    return (false, $"no element matching '{text}'", null);
                var enabled = matches.FirstOrDefault(e => e.IsEnabled);
                if (enabled is not null)
                    return (true, $"found '{enabled.Name}' ({enabled.ElementId}), enabled", enabled);
                var first = matches[0];
                return (false, $"found '{first.Name}' ({first.ElementId}) but it is disabled", null);
            }
            case WaitCondition.FocusedElement:
            {
                var focused = evidence.Snapshot?.Interactive.FirstOrDefault(e => e.Focused);
                if (focused is null)
                    return (false, "nothing has keyboard focus", null);
                if (!focused.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return (false, $"'{focused.Name}' ({focused.ElementId}) has focus, wanted '{text}'", null);
                var projected = new ElementInfo(focused.ElementId, focused.Name, focused.ControlType,
                    IsEnabled: true, IsOffscreen: false, focused.Bounds, focused.Value, null, null);
                return (true, $"'{focused.Name}' ({focused.ElementId}) has focus", projected);
            }
            case WaitCondition.TextExists:
            {
                var snapshot = evidence.Snapshot;
                if (snapshot is not null)
                {
                    foreach (var e in snapshot.Interactive)
                    {
                        if (e.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                            return (true, $"found in element '{e.Name}' ({e.ElementId})", null);
                        if (e.Value is { } v && v.Contains(text, StringComparison.OrdinalIgnoreCase))
                            return (true, $"found in element '{e.Name}' ({e.ElementId}) value", null);
                    }
                    foreach (var r in snapshot.Scrollable)
                        if (r.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                            return (true, $"found in scrollable region '{r.Name}' ({r.ElementId})", null);
                    if (snapshot.Pages is { } pages)
                        foreach (var page in pages)
                            if (page.Text.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase)))
                                return (true, $"found in page '{page.Title}'", null);
                }
                return (false, $"'{text}' not found anywhere on screen", null);
            }
            case WaitCondition.ActiveWindow:
            {
                var active = evidence.Windows?.FirstOrDefault(w => w.IsActive);
                if (active is null) return (false, "no active window", null);
                if (string.Equals(active.Title, text, StringComparison.OrdinalIgnoreCase))
                    return (true, $"active window is '{active.Title}' (exact)", null);
                if (active.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return (true, $"active window is '{active.Title}' (substring)", null);
                int score = Math.Max(FuzzyMatch.PartialRatio(text, active.Title), FuzzyMatch.TokenSetRatio(text, active.Title));
                return score >= WindowMatcher.FuzzyThreshold
                    ? (true, $"active window is '{active.Title}' (fuzzy)", null)
                    : (false, $"active window is '{active.Title}', wanted '{text}'", null);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown wait condition.");
        }
    }

    /// <summary>The snake_case name the tool accepts and the result reports.</summary>
    internal static string NameOf(WaitCondition condition) => condition switch
    {
        WaitCondition.ElementExists => "element_exists",
        WaitCondition.ElementEnabled => "element_enabled",
        WaitCondition.FocusedElement => "focused_element",
        WaitCondition.TextExists => "text_exists",
        WaitCondition.ActiveWindow => "active_window",
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown wait condition."),
    };
}
