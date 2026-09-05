using System.Globalization;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-8's pure core: region and display-selection arithmetic in virtual-desktop coordinates, with
/// no dependency on Win32, a screen or a capture — so every rule is unit-tested without a desktop
/// (roadmap C10). <c>screenshot</c> and <c>ocr</c> share this one parser (A-8 replaced the private
/// <c>ScreenTools.ParseRegion</c>), so the two tools cannot drift apart in what they accept.
/// </summary>
internal static class RegionMath
{
    private static readonly string[] RegionPartNames = ["x", "y", "width", "height"];

    /// <summary>
    /// Parses <c>"x,y,w,h"</c> in virtual-desktop pixels; null/blank means "no region given".
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Wrong arity, a part that is not an integer, or a non-positive width/height. Always this
    /// type — a caller-facing message, never a <see cref="FormatException"/> from the parser.
    /// </exception>
    internal static ScreenRegion? ParseRegion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            throw new ArgumentException($"Invalid region '{text}'; expected 'x,y,w,h'");

        var values = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length == 0)
                throw new ArgumentException($"Invalid region '{text}': the {RegionPartNames[i]} part is empty; expected 'x,y,w,h'");
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
                throw new ArgumentException($"Invalid region '{text}': '{parts[i]}' is not an integer ({RegionPartNames[i]}); expected 'x,y,w,h'");
        }

        if (values[2] <= 0)
            throw new ArgumentException($"Invalid region '{text}': width must be positive, got {values[2]}");
        if (values[3] <= 0)
            throw new ArgumentException($"Invalid region '{text}': height must be positive, got {values[3]}");

        return new ScreenRegion(values[0], values[1], values[2], values[3]);
    }

    /// <summary>
    /// Parses the <c>display</c> argument — <c>"all"</c> or a comma-separated list of zero-based
    /// monitor indices — into indices into the <c>multi_monitor</c> order, de-duplicated, in the
    /// order given. Null/blank means "no display given" (the caller's default applies).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A non-integer part, an index outside <c>0..monitorCount-1</c>, or an empty list. The
    /// message lists the valid indices.
    /// </exception>
    internal static int[]? ParseDisplays(string? text, int monitorCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var valid = string.Join(",", Enumerable.Range(0, monitorCount));
        if (text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(0, monitorCount).ToArray();

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"Invalid display '{text}': no indices given; expected 'all' or indices from {valid}");

        var indices = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                throw new ArgumentException($"Invalid display '{text}': '{part}' is not a monitor index; expected 'all' or indices from {valid}");
            if (index < 0 || index >= monitorCount)
                throw new ArgumentException($"Invalid display '{text}': index {index} is not a monitor; valid: {valid} (see multi_monitor)");
            if (!indices.Contains(index)) indices.Add(index);
        }
        return indices.ToArray();
    }

    /// <summary>The bounding box of <paramref name="monitors"/> in virtual-desktop coordinates (a monitor left of or above the primary gives a negative origin).</summary>
    /// <exception cref="ArgumentException"><paramref name="monitors"/> is empty.</exception>
    internal static ScreenRegion Union(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
            throw new ArgumentException("No monitors to capture: the monitor inventory is empty.", nameof(monitors));

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.X);
            top = Math.Min(top, m.Y);
            right = Math.Max(right, m.X + m.Width);
            bottom = Math.Max(bottom, m.Y + m.Height);
        }
        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    /// <summary>The whole virtual screen: the union of every monitor.</summary>
    /// <exception cref="ArgumentException"><paramref name="all"/> is empty.</exception>
    internal static ScreenRegion VirtualScreen(IReadOnlyList<MonitorInfo> all) => Union(all);

    /// <summary>
    /// Rejects a region that is not entirely inside <paramref name="virtualScreen"/> — upstream
    /// raises instead of clipping, and a silently clipped capture is a picture whose coordinates
    /// no longer mean what the model thinks. A region straddling two monitors is fine.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any edge of <paramref name="region"/> falls outside <paramref name="virtualScreen"/>; the
    /// message states the virtual screen bounds the way <c>click</c>'s off-screen error does.
    /// </exception>
    internal static void Validate(ScreenRegion region, ScreenRegion virtualScreen)
    {
        int l = virtualScreen.X, t = virtualScreen.Y;
        int r = virtualScreen.X + virtualScreen.Width - 1, b = virtualScreen.Y + virtualScreen.Height - 1;

        // long: a width near int.MaxValue would wrap the far edge negative and pass as "inside".
        if (region.X < l || region.Y < t
            || (long)region.X + region.Width - 1 > r || (long)region.Y + region.Height - 1 > b)
        {
            throw new ArgumentException(
                $"Region {region.X},{region.Y},{region.Width},{region.Height} is not inside the virtual screen, which spans " +
                $"x {l}..{r}, y {t}..{b} in virtual-desktop pixels; see multi_monitor for each monitor's bounds.");
        }
    }

    /// <summary>
    /// The primary monitor — the default capture target (roadmap C3); the first monitor when none
    /// is flagged primary.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="all"/> is empty.</exception>
    internal static MonitorInfo Primary(IReadOnlyList<MonitorInfo> all)
    {
        if (all.Count == 0)
            throw new ArgumentException("No monitors to capture: the monitor inventory is empty.", nameof(all));
        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }
}
