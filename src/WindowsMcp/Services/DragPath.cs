namespace WindowsMcp.Services;

/// <summary>
/// B-2: the pointer positions of a drag, pure. Many drop targets (file managers, canvases,
/// browser drag-and-drop) only recognise a drag after a first move past the system drag
/// threshold followed by real intermediate motion, so the path is a short nudge toward the
/// destination and then <c>steps</c> evenly spaced points that end exactly on it. Every point
/// stays inside the rectangle the drag spans and never doubles back on either axis.
/// </summary>
internal static class DragPath
{
    /// <summary>
    /// The points after the press: the nudge first (omitted when <paramref name="nudge"/> is 0 or
    /// the drag is shorter than it), then <paramref name="steps"/> interpolated points from the
    /// origin to the destination, the last one exactly <paramref name="to"/>. A zero-distance drag
    /// is just the destination.
    /// </summary>
    internal static IReadOnlyList<(int X, int Y)> Points((int X, int Y) from, (int X, int Y) to, int steps, int nudge)
    {
        if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps), steps, "steps must be at least 1");
        if (nudge < 0) throw new ArgumentOutOfRangeException(nameof(nudge), nudge, "nudge cannot be negative");

        double dx = to.X - from.X, dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance == 0) return [to];

        // The first point is the nudge when there is one, the origin otherwise, so the path always
        // has steps + 1 points and always starts where the button went down.
        var points = new List<(int X, int Y)>(steps + 1)
        {
            nudge > 0 && distance > nudge
                ? (Round(from.X + dx * nudge / distance), Round(from.Y + dy * nudge / distance))   // a distance along the travel, not per axis
                : from,
        };
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            points.Add(i == steps ? to : (Round(from.X + dx * t), Round(from.Y + dy * t)));
        }
        return points;
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
