using System.Globalization;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// C-5 (roadmap R8): the pure renderer for <c>scrape(source:"dom")</c> — a browser page as A-5
/// walked it, turned into the text an agent reads, with upstream's edge hints so the model knows
/// there is more of the page above or below what it was given.
/// </summary>
internal static class DomPage
{
    internal const string TopHint = "Reached top of the page; scroll down to see more.";
    internal const string BottomHint = "Reached bottom of the page; scroll up to see more.";

    /// <summary>
    /// The page's visible text lines joined with <c>\n</c>, then — when <see cref="Hint"/> has
    /// something to say — a blank line and the hint. No hint means no trailing blank line.
    /// </summary>
    internal static string Render(SnapshotPage page)
    {
        var text = string.Join('\n', page.Text);
        var hint = Hint(page.Scroll);
        if (hint is null) return text;
        return text.Length == 0 ? hint : text + "\n\n" + hint;
    }

    /// <summary>
    /// The one-line scroll hint for a document: null when there is no scroll pattern or the
    /// document does not scroll vertically (the whole page is on screen), otherwise "Reached top
    /// of the page; scroll down to see more." at 0 or below, "Reached bottom of the page; scroll
    /// up to see more." at 100 or above, and "Scrolled N% down the page; scroll up or down to see
    /// more." (N rounded) in between.
    /// </summary>
    internal static string? Hint(ScrollInfo? scroll)
    {
        if (scroll is null || !scroll.VerticallyScrollable) return null;
        var percent = scroll.VerticalPercent;
        if (percent <= 0) return TopHint;
        if (percent >= 100) return BottomHint;
        var rounded = ((int)Math.Round(percent, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        return $"Scrolled {rounded}% down the page; scroll up or down to see more.";
    }
}
