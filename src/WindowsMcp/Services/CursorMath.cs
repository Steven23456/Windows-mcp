using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>A-11's pure core: which monitor a virtual-desktop point is on. No Win32.</summary>
internal static class CursorMath
{
    /// <summary>
    /// The <see cref="MonitorInfo.Index"/> of the first monitor whose rect contains the point
    /// (left/top inclusive, right/bottom exclusive — the seam pixel belongs to the right-hand
    /// monitor), or -1 when no monitor does. First match wins for overlapping (mirrored) monitors.
    /// </summary>
    internal static int MonitorIndexOf(int x, int y, IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (var m in monitors)
        {
            if (x >= m.X && y >= m.Y && x < m.X + m.Width && y < m.Y + m.Height)
                return m.Index;
        }
        return -1;
    }
}
