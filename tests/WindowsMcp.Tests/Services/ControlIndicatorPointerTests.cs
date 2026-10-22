using System.Runtime.InteropServices;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// S-11 visuals that only a MOVING pointer reaches: the ring follows the pointer (the pointer-follow
/// tick's move branch), and on another monitor the banner re-homes to that monitor's work area and
/// both windows are repainted at that monitor's DPI (the tick's monitor- and DPI-change branches).
/// <see cref="ControlIndicatorDesktopTests"/> covers the pointer wherever it happens to be.
/// <para>
/// This class MOVES THE POINTER (<c>SetCursorPos</c>), so it is <c>Category=UIAutomation</c> and
/// must never run unattended (CLAUDE.md). The pointer is put back where it was. It runs per-monitor-v2
/// aware like the server (<see cref="PerMonitorDpi"/>), so <c>SetCursorPos</c>, the monitor rects
/// and the window rects are all physical pixels. The cross-monitor test needs two monitors and is a
/// no-op on one (the precedent is the OS-version early return in <see cref="ControlIndicatorTests"/>).
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class ControlIndicatorPointerTests
{
    public ControlIndicatorPointerTests() => PerMonitorDpi.Ensure();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private sealed record MonitorArea(ScreenRegion Work, uint Dpi)
    {
        public (int X, int Y) Centre => (Work.X + Work.Width / 2, Work.Y + Work.Height / 2);
    }

    private static List<MonitorArea> Monitors()
    {
        var found = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(h, ref info) && GetDpiForMonitor(h, 0, out var dpi, out _) == 0)
            {
                var w = info.rcWork;
                found.Add(new MonitorArea(new ScreenRegion(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), dpi));
            }
            return true;
        }, IntPtr.Zero).Should().BeTrue();
        found.Should().NotBeEmpty();
        return found;
    }

    private static MonitorArea MonitorAt(List<MonitorArea> monitors, int x, int y) =>
        monitors.FirstOrDefault(m => x >= m.Work.X && x < m.Work.X + m.Work.Width && y >= m.Work.Y && y < m.Work.Y + m.Work.Height)
        ?? monitors[0];

    /// <summary>Where the ring and the banner must be for a pointer at (x, y) on <paramref name="m"/>.</summary>
    private static (ScreenRegion Ring, ScreenRegion Banner) Expected(ControlIndicator ci, MonitorArea m, int x, int y)
    {
        var scale = m.Dpi / 96.0;
        using var ring = ControlIndicatorArt.CursorRing(scale);
        var text = ControlIndicatorArt.BannerText(ci.IsHotkeyRegistered ? ControlIndicatorTests.TestChord.Display : null);
        using var banner = ControlIndicatorArt.Banner(text, scale);
        return (ControlIndicatorArt.RingRect(x, y, ring.Width), ControlIndicatorArt.BannerRect(m.Work, banner.Width, banner.Height, scale));
    }

    [Fact]
    public async Task The_ring_follows_the_pointer_and_stays_centred_on_it()
    {
        GetCursorPos(out var home).Should().BeTrue();
        var monitor = MonitorAt(Monitors(), home.X, home.Y);
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(lingerMs: 30_000));
        try
        {
            ci.BeginAction();
            (await ControlIndicatorTests.WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue();

            var w = monitor.Work;
            foreach (var (x, y) in new[] { (w.X + w.Width / 3, w.Y + w.Height / 2), (w.X + 2 * w.Width / 3, w.Y + w.Height / 3), (w.X + w.Width / 2, w.Y + 2 * w.Height / 3) })
            {
                SetCursorPos(x, y).Should().BeTrue();
                var want = Expected(ci, monitor, x, y).Ring;

                (await ControlIndicatorTests.WithinAsync(500, () => ControlIndicatorDesktopTests.RectOf(ci.RingHandle) == want)).Should()
                    .BeTrue($"the ring re-centres on the pointer at {x},{y} within a few 16 ms ticks: wanted {want}, "
                        + $"found {ControlIndicatorDesktopTests.RectOf(ci.RingHandle)}");
            }
        }
        finally
        {
            SetCursorPos(home.X, home.Y);
            ci.EndAction();
        }
    }

    [Fact]
    public async Task On_each_monitor_the_banner_tops_that_monitors_work_area_and_both_windows_are_drawn_at_its_dpi()
    {
        var monitors = Monitors();
        if (monitors.Count < 2) return;   // one monitor: nothing to move between (ControlIndicatorDesktopTests covers it)

        GetCursorPos(out var home).Should().BeTrue();
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(lingerMs: 30_000));
        try
        {
            ci.BeginAction();
            (await ControlIndicatorTests.WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue();

            // Every monitor, then back to the first: the banner follows the pointer both ways, and a
            // DPI change in either direction repaints the ring.
            foreach (var m in monitors.Append(monitors[0]))
            {
                var (x, y) = m.Centre;
                SetCursorPos(x, y).Should().BeTrue();
                var (ring, banner) = Expected(ci, m, x, y);

                (await ControlIndicatorTests.WithinAsync(1000, () =>
                        ControlIndicatorDesktopTests.RectOf(ci.RingHandle) == ring && ControlIndicatorDesktopTests.RectOf(ci.BannerHandle) == banner))
                    .Should().BeTrue($"monitor {m.Work} at {m.Dpi} dpi: ring wanted {ring} found {ControlIndicatorDesktopTests.RectOf(ci.RingHandle)}; "
                        + $"banner wanted {banner} found {ControlIndicatorDesktopTests.RectOf(ci.BannerHandle)}");
                PerMonitorDpi.AssertWindow(ci.RingHandle, "ring").Should().Be(m.Dpi, "the ring is drawn at the DPI of the monitor it is on");
                PerMonitorDpi.AssertWindow(ci.BannerHandle, "banner").Should().Be(m.Dpi, "and so is the banner");
            }
        }
        finally
        {
            SetCursorPos(home.X, home.Y);
            ci.EndAction();
        }
    }
}
