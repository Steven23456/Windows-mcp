using System.Runtime.InteropServices;
using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// S-11 (R8): the indicator on the actual screen, and out of every picture. Needs the interactive
/// desktop and captures it, so it is <c>Category=UIAutomation</c> and in <see cref="DesktopCollection"/>
/// - excluded from headless runs like every other test that reads screen pixels. Nothing here
/// injects input: the pointer is wherever the person left it (the ring follows it) and the
/// takeover is a posted <c>WM_HOTKEY</c>.
/// <para>
/// The capture tests use <c>new ScreenshotService()</c> with NO indicator injected, so nothing
/// suspends anything: what they measure is display affinity alone, on each backend. The design
/// note's risk list says this is what decides whether <c>SuspendForCapture</c> may stay a no-op
/// when affinity took effect. The <c>ExcludeFromCapture = false</c> rows are the same comparison
/// with affinity switched off; they must FAIL to match, which is what proves the comparison can
/// see the windows at all on that backend (if the gdi row cannot, GDI never sees layered windows
/// and its "match" row is vacuous - report it, do not delete it).
/// </para>
/// <para>
/// DPI: the server runs per-monitor-v2 aware (<c>Program.Main</c>); the test host does not. In a
/// DPI-unaware process on a scaled monitor, <c>GetWindowRect</c> answers in virtualised logical
/// coordinates while both capture backends read physical pixels, so the compared rects miss the
/// windows entirely (both rows then "match" for the wrong reason, and the bite rows fail). The
/// constructor therefore makes the process per-monitor-v2 aware, as production is, and every test
/// asserts the EFFECTIVE awareness - of the thread reading rects and of each indicator window -
/// so a run that silently stayed unaware fails loudly instead of comparing the wrong pixels.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class ControlIndicatorDesktopTests
{
    public ControlIndicatorDesktopTests() => PerMonitorDpi.Ensure();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    internal static ScreenRegion RectOf(long hwnd)
    {
        PerMonitorDpi.AssertThread();   // an await may resume on another thread: each must read physical pixels
        GetWindowRect(new IntPtr(hwnd), out var r).Should().BeTrue("the window exists while the indicator is up");
        return new ScreenRegion(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static async Task<SKBitmap> CaptureAsync(ScreenshotService screenshot, ScreenRegion rect, string backend)
    {
        var result = await screenshot.CaptureAsync(rect, new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0, Backend: backend));
        result.Backend.Should().Be(backend, "the comparison is about this backend, so a fallback would make it meaningless");
        var bmp = SKBitmap.Decode(result.Bytes);
        bmp.Should().NotBeNull("the capture must decode as an image");
        return bmp!;
    }

    /// <summary>The share of pixels whose colour differs by more than a compression-proof margin.</summary>
    private static double DifferingShare(SKBitmap a, SKBitmap b)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);
        var differing = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) > 12 || Math.Abs(p.Green - q.Green) > 12 || Math.Abs(p.Blue - q.Blue) > 12) differing++;
            }
        return differing / (double)(a.Width * a.Height);
    }

    /// <summary>
    /// Shows the indicator, captures the ring's and the banner's rects on <paramref name="backend"/>,
    /// takes it down, captures the same rects again, and returns how much of each differs. Retries
    /// for a quiet moment: the pointer dragging the ring away mid-capture, or anything animating
    /// under the rects, makes the comparison about something else.
    /// </summary>
    private static async Task<(double Ring, double Banner)> DifferenceWithIndicatorUpAsync(
        bool excludeFromCapture, string backend, bool suspendThroughService = false)
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(lingerMs: 30_000)) { ExcludeFromCapture = excludeFromCapture };
        // By default no indicator is injected: nothing suspends, so affinity alone is measured.
        // suspendThroughService injects it as the container does, so CaptureAsync's suspension runs.
        var screenshot = suspendThroughService ? new ScreenshotService(indicator: ci) : new ScreenshotService();

        for (var attempt = 1; ; attempt++)
        {
            ci.BeginAction();
            (await ControlIndicatorTests.WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue();
            PerMonitorDpi.AssertWindow(ci.RingHandle, "ring");
            PerMonitorDpi.AssertWindow(ci.BannerHandle, "banner");
            await Task.Delay(300);   // let the compositor put the layered windows up

            var ring = RectOf(ci.RingHandle);
            var banner = RectOf(ci.BannerHandle);
            using var shownRing = await CaptureAsync(screenshot, ring, backend);
            using var shownBanner = await CaptureAsync(screenshot, banner, backend);
            var ringStayed = RectOf(ci.RingHandle) == ring;

            ControlIndicatorTests.PostTakeover(ci.BannerHandle).Should().BeTrue();
            (await ControlIndicatorTests.WithinAsync(2000, () => !ci.IsVisible)).Should().BeTrue();
            ci.ConsumeTakeover();
            ci.EndAction();
            await Task.Delay(300);

            using var hiddenRing = await CaptureAsync(screenshot, ring, backend);
            using var hiddenBanner = await CaptureAsync(screenshot, banner, backend);
            await Task.Delay(150);
            using var hiddenRing2 = await CaptureAsync(screenshot, ring, backend);
            using var hiddenBanner2 = await CaptureAsync(screenshot, banner, backend);
            var quiet = DifferingShare(hiddenRing, hiddenRing2) == 0 && DifferingShare(hiddenBanner, hiddenBanner2) == 0;

            if ((ringStayed && quiet) || attempt >= 6)
            {
                ringStayed.Should().BeTrue("keep the pointer still during this test: the ring follows it (6 attempts)");
                quiet.Should().BeTrue("run this on a quiet desktop - something under the ring or the banner kept changing (6 attempts)");
                return (DifferingShare(shownRing, hiddenRing), DifferingShare(shownBanner, hiddenBanner));
            }
            await Task.Delay(300);
        }
    }

    [Fact]
    public async Task While_visible_the_ring_is_on_screen_centred_on_the_pointer_and_the_banner_tops_its_monitor()
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(lingerMs: 30_000));
        ci.BeginAction();
        (await ControlIndicatorTests.WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue();
        PerMonitorDpi.AssertWindow(ci.RingHandle, "ring");
        PerMonitorDpi.AssertWindow(ci.BannerHandle, "banner");
        await Task.Delay(200);   // at least one pointer-follow tick

        // A still pointer: read it either side of the window rect and retry if it moved.
        POINT p = default;
        ScreenRegion ring = new(0, 0, 0, 0);
        for (var i = 0; i < 40; i++)
        {
            GetCursorPos(out var p1);
            await Task.Delay(50);   // a tick to catch up with where the pointer is now
            ring = RectOf(ci.RingHandle);
            GetCursorPos(out p);
            if (p1.X == p.X && p1.Y == p.Y) break;
        }

        ControlIndicatorTests.IsWindowVisible(new IntPtr(ci.RingHandle)).Should().BeTrue("the ring is on screen");
        ring.Width.Should().Be(ring.Height, "the ring window is square");
        (ring.X + ring.Width / 2).Should().BeCloseTo(p.X, 2, "the ring is centred on the pointer");
        (ring.Y + ring.Height / 2).Should().BeCloseTo(p.Y, 2);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST), ref info).Should().BeTrue();
        var work = info.rcWork;
        var banner = RectOf(ci.BannerHandle);
        ControlIndicatorTests.IsWindowVisible(new IntPtr(ci.BannerHandle)).Should().BeTrue("the banner is on screen");
        (banner.X + banner.Width / 2).Should().BeCloseTo((work.Left + work.Right) / 2, 2,
            "the banner is centred on the work area of the monitor the pointer is on");
        banner.Y.Should().BeInRange(work.Top, work.Top + 40, "it sits at the top of the work area, 12 px x the monitor's scale below it");
        banner.X.Should().BeGreaterThanOrEqualTo(work.Left);
        (banner.X + banner.Width).Should().BeLessThanOrEqualTo(work.Right);
        ci.EndAction();
    }

    [Fact]
    public async Task Both_windows_are_drawn_at_the_pointer_monitors_dpi_exactly_where_the_art_puts_them()
    {
        // R4's pure geometry on the real screen, wherever the person left the pointer (no input):
        // the ring is CursorRing's side at that monitor's DPI centred on the pointer, and the banner
        // is Banner(BannerText(...))'s size at that DPI, at BannerRect of that monitor's work area.
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(lingerMs: 30_000));
        ci.BeginAction();
        (await ControlIndicatorTests.WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue();
        await Task.Delay(200);   // at least one pointer-follow tick

        POINT p = default;
        ScreenRegion ring = new(0, 0, 0, 0), banner = new(0, 0, 0, 0);
        for (var i = 0; i < 40; i++)
        {
            GetCursorPos(out var p1);
            await Task.Delay(50);
            ring = RectOf(ci.RingHandle);
            banner = RectOf(ci.BannerHandle);
            GetCursorPos(out p);
            if (p1.X == p.X && p1.Y == p.Y) break;
        }

        var monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info).Should().BeTrue();
        GetDpiForMonitor(monitor, 0, out var dpi, out _).Should().Be(0);
        var scale = dpi / 96.0;
        var work = new ScreenRegion(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);

        PerMonitorDpi.AssertWindow(ci.RingHandle, "ring").Should().Be(dpi, "the ring is on the pointer's monitor");
        using (var art = ControlIndicatorArt.CursorRing(scale))
            ring.Should().Be(ControlIndicatorArt.RingRect(p.X, p.Y, art.Width), $"the ring is CursorRing({scale}) centred on the pointer");
        var text = ControlIndicatorArt.BannerText(ci.IsHotkeyRegistered ? ControlIndicatorTests.TestChord.Display : null);
        using (var art = ControlIndicatorArt.Banner(text, scale))
            banner.Should().Be(ControlIndicatorArt.BannerRect(work, art.Width, art.Height, scale),
                $"the banner is Banner(text, {scale}) at the top centre of the pointer monitor's work area {work}");
        ci.EndAction();
    }

    [Theory]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public async Task Captures_of_the_ring_and_banner_areas_match_captures_with_the_indicator_hidden(string backend)
    {
        var (ring, banner) = await DifferenceWithIndicatorUpAsync(excludeFromCapture: true, backend);

        ring.Should().BeLessThanOrEqualTo(0.02, $"{backend}: WDA_EXCLUDEFROMCAPTURE keeps the ring out of the picture");
        banner.Should().BeLessThanOrEqualTo(0.02, $"{backend}: and the banner");
    }

    [Theory]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public async Task Without_display_affinity_the_same_comparison_sees_the_indicator(string backend)
    {
        // The bite check for the row above: same rects, same captures, affinity off through the
        // ExcludeFromCapture seam. If this does not see the windows, the row above proves nothing.
        var (ring, banner) = await DifferenceWithIndicatorUpAsync(excludeFromCapture: false, backend);

        ring.Should().BeGreaterThanOrEqualTo(0.10, $"{backend}: with affinity off the ring's band is in the picture");
        banner.Should().BeGreaterThanOrEqualTo(0.10, $"{backend}: and so is the banner");
    }

    [Theory]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public async Task Without_display_affinity_the_capture_suspension_keeps_the_indicator_out_of_the_picture(string backend)
    {
        // The fallback end to end: affinity off through the seam, and the indicator injected into the
        // ScreenshotService as the container does, so CaptureAsync's SuspendForCapture is the only
        // thing keeping the windows out - hidden before the grab, and the compositor must have caught
        // up by then. The row above is this row's bite check: same rects, same captures, no suspension.
        var (ring, banner) = await DifferenceWithIndicatorUpAsync(excludeFromCapture: false, backend, suspendThroughService: true);

        ring.Should().BeLessThanOrEqualTo(0.02, $"{backend}: the suspension hides the ring before the frame is taken");
        banner.Should().BeLessThanOrEqualTo(0.02, $"{backend}: and the banner");
    }
}

/// <summary>
/// Production's DPI awareness for a desktop test: <c>Program.Main</c> makes the server
/// per-monitor-v2 aware, so every rect it computes and every pixel it captures is physical. The
/// test host is DPI-unaware, which on a scaled monitor virtualises <c>GetWindowRect</c>,
/// <c>GetCursorPos</c> and <c>GetMonitorInfo</c> but not the pixels a capture reads.
/// <para>
/// <c>SetProcessDpiAwarenessContext</c> is process-wide and succeeds once: it returns false when
/// an earlier class (or the host) already set the awareness, which is tolerated - the assertions
/// on the EFFECTIVE awareness are what decide. Side effect, by design: every class that runs later
/// in the same test process is per-monitor-v2 aware too, exactly like the server it tests.
/// </para>
/// </summary>
internal static class PerMonitorDpi
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Makes the process per-monitor-v2 aware (once) and asserts this thread is.</summary>
    internal static void Ensure()
    {
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);   // false when already set: the assertion decides
        AssertThread();
    }

    /// <summary>The calling thread reads physical coordinates, as the server does.</summary>
    internal static void AssertThread() =>
        AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2).Should().BeTrue(
            "the server is per-monitor-v2 aware; a DPI-virtualised test thread reads logical rects while the captures read "
            + "physical pixels, so the comparison would be about the wrong pixels");

    /// <summary>
    /// The indicator window was created per-monitor-v2 aware and is drawn at its monitor's effective
    /// DPI - the proof that the rects and the pixels are in the same space. Returns that DPI.
    /// </summary>
    internal static uint AssertWindow(long hwnd, string what)
    {
        var h = new IntPtr(hwnd);
        h.Should().NotBe(IntPtr.Zero, $"the {what} window exists while the indicator is up");
        AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(h), PerMonitorAwareV2).Should()
            .BeTrue($"the {what} window is created per-monitor-v2 aware, as it is in the server");
        GetDpiForMonitor(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, out var monitorDpi, out _)
            .Should().Be(0, "GetDpiForMonitor answers for a real monitor");
        var dpi = GetDpiForWindow(h);
        dpi.Should().Be(monitorDpi, $"the {what} window is at its monitor's effective DPI, not a virtualised 96");
        return dpi;
    }
}
