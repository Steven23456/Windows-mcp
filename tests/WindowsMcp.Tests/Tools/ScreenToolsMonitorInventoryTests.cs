using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// A-8 through the <b>real</b> monitor inventory. Every other A-8 tool test hands
/// <see cref="ScreenTools"/> a <c>Mock&lt;IWindowService&gt;</c> with a hand-written desktop, so all
/// of them would stay green if <c>WindowService.EnumerateMonitorsAsync</c> numbered its monitors
/// differently from what a <c>display</c> index means, or if our idea of the virtual screen did not
/// match Windows' (the <c>disk_inspect mode:reclaimable</c> failure mode recorded in CLAUDE.md).
/// <para>
/// Read-only and headless-safe: <c>EnumDisplayMonitors</c>/<c>GetSystemMetrics</c> need a desktop
/// session but no foreground window and no capture, so these carry <c>Category=Integration</c>
/// (same bracket as <c>InputServiceTests.HoverAsync_lands_exactly_on_every_monitor</c>).
/// <see cref="IScreenshotService"/> stays mocked on purpose — the real capture is
/// <c>ScreenshotServiceTests.CaptureAsync_captures_the_union_of_every_monitor</c> (UIAutomation).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ScreenToolsMonitorInventoryTests
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static Mock<IScreenshotService> ShotMock()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var mock = new Mock<IScreenshotService>();
        mock.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 100, 100, ImageFormat.Png, 100, 100, 1.0));
        return mock;
    }

    private static ScreenTools MakeTools(IScreenshotService shot) =>
        new(shot, new Mock<IOcrService>().Object, new WindowService());

    private static JsonElement Meta(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The assumption the whole <c>display</c> argument rests on: <c>ResolveRegionAsync</c> looks
    /// the selection up by <b>position</b> (<c>monitors[i]</c>) while the response — and
    /// <c>multi_monitor</c>, which is where the model reads the indices — carries each monitor's
    /// <c>Index</c> field. If those two ever disagreed, <c>display:"1"</c> would capture a monitor
    /// the model did not ask for.
    /// </summary>
    [Fact]
    public async Task Real_monitor_inventory_is_numbered_by_position_from_zero()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();

        monitors.Should().NotBeEmpty("EnumerateMonitorsAsync reports at least the primary display");
        for (int i = 0; i < monitors.Length; i++)
            monitors[i].Index.Should().Be(i,
                "'display' indices are positions in this list, so position {0} must report index {0}", i);
        monitors.Count(m => m.IsPrimary).Should().Be(1, "Windows has exactly one primary monitor");
        monitors.Should().OnlyContain(m => m.Width > 0 && m.Height > 0);
    }

    /// <summary>
    /// <c>RegionMath.VirtualScreen</c> is what every region is validated against, and it is built
    /// from our own enumeration rather than from <c>SM_*VIRTUALSCREEN</c>. Windows' own metrics are
    /// the independent oracle: if the two disagree, valid regions get rejected or invalid ones get
    /// captured.
    /// </summary>
    [Fact]
    public async Task Virtual_screen_of_the_real_inventory_matches_the_Win32_metrics()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        monitors.Should().NotBeEmpty();

        var virtualScreen = RegionMath.VirtualScreen(monitors);

        virtualScreen.Should().Be(new ScreenRegion(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)),
            "the union of every monitor IS the virtual screen Windows reports");
    }

    /// <summary>
    /// The default and <c>display:"0"</c> resolved against the real inventory: the rect handed to
    /// the capture service is a real monitor's rect in virtual-desktop coordinates, and the
    /// response describes the real desktop.
    /// </summary>
    [Fact]
    public async Task Screenshot_resolves_the_real_primary_and_the_real_first_monitor()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        monitors.Should().NotBeEmpty();
        var primary = monitors.First(m => m.IsPrimary);
        var first = monitors[0];

        // A mock per call: on a single-monitor box the two rects are the same, so one shared mock
        // could not tell "captured once per call" from "captured twice on the second call".
        var shotDefault = ShotMock();
        var byDefault = Meta(await MakeTools(shotDefault.Object).Screenshot(format: "png", output: "inline"));
        shotDefault.Verify(s => s.CaptureAsync(new ScreenRegion(primary.X, primary.Y, primary.Width, primary.Height),
            It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()), Times.Once,
            "the default is the real primary display (roadmap C3)");
        byDefault.GetProperty("displays").GetArrayLength().Should().Be(monitors.Length);
        byDefault.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");

        var shotIndex = ShotMock();
        var byIndex = Meta(await MakeTools(shotIndex.Object).Screenshot(display: "0", format: "png", output: "inline"));
        shotIndex.Verify(s => s.CaptureAsync(new ScreenRegion(first.X, first.Y, first.Width, first.Height),
            It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()), Times.Once,
            "display:0 is the monitor at position 0 of the real inventory");
        byIndex.GetProperty("selectedDisplays").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(new[] { 0 });
        byIndex.GetProperty("displays")[0].GetProperty("x").GetInt32().Should().Be(first.X);
    }

    /// <summary>
    /// <c>display:"all"</c> against the real inventory is the whole virtual screen — the rect
    /// <c>ScreenshotServiceTests.CaptureAsync_captures_the_union_of_every_monitor</c> then proves
    /// GDI can actually copy.
    /// </summary>
    [Fact]
    public async Task Screenshot_display_all_resolves_the_real_virtual_screen()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);
        var expected = new ScreenRegion(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var meta = Meta(await tools.Screenshot(display: "all", format: "png", output: "inline"));

        shot.Verify(s => s.CaptureAsync(expected, It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "'all' is the union of every monitor, which is the virtual screen");
        var region = meta.GetProperty("region");
        region.GetProperty("x").GetInt32().Should().Be(expected.X);
        region.GetProperty("width").GetInt32().Should().Be(expected.Width);
    }

    /// <summary>
    /// The rejection path against the real desktop: one pixel left of the real virtual screen is
    /// out of bounds, and the error states the real bounds (not a hand-written 0..3839).
    /// </summary>
    [Fact]
    public async Task Screenshot_rejects_a_region_outside_the_real_virtual_screen()
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1;
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1;
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        Func<Task> act = () => tools.Screenshot($"{left - 1},{top},10,10", format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>("out-of-bounds regions raise, they are not clipped"))
            .Which.Message.Should().Contain($"x {left}..{right}").And.Contain($"y {top}..{bottom}");
        shot.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
