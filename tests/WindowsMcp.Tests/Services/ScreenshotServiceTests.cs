using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// CaptureAsync calls Graphics.CopyFromScreen, which requires an interactive desktop
// session — it throws Win32Exception "The handle is invalid" under headless/service
// sessions (local non-interactive runs and GitHub-hosted Windows runners alike). That
// is the same constraint as the UIAutomation bucket, so it is categorized here to be
// excluded by the documented headless-safe filter (Category!=UIAutomation), not left
// mislabeled as read-only Integration.
[Trait("Category", "UIAutomation")]
public class ScreenshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_returns_non_empty_png_with_dimensions()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), new CaptureOptions(ImageFormat.Png));

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Png);
        // PNG magic bytes: 89 50 4E 47
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    // A-7 made JPEG the default for inline output, so this is the format nearly every
    // screenshot call now encodes. ScreenToolsTests mocks IScreenshotService, so without this
    // the real SkiaSharp JPEG encode path would ship with no test through the real collaborator
    // at all (the failure mode CLAUDE.md records for disk_inspect mode:reclaimable).
    [Fact]
    public async Task CaptureAsync_jpeg_returns_jpeg_bytes()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), new CaptureOptions(ImageFormat.Jpeg));

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Jpeg);
        // JPEG SOI marker plus the start of the next marker: FF D8 FF
        result.Bytes.Take(3).Should().Equal(new byte[] { 0xFF, 0xD8, 0xFF });
    }

    // ---- A-9 (R4) — the capture pipeline: capture -> Fit -> Downscale -> Encode --------------

    [Fact]
    public async Task CaptureAsync_null_options_captures_at_full_size_with_scale_one()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 200, 100));

        result.Width.Should().Be(200);
        result.Height.Should().Be(100);
        result.OriginalWidth.Should().Be(200);
        result.OriginalHeight.Should().Be(100);
        result.CoordinateScale.Should().Be(1.0, "nothing was scaled");
        result.Format.Should().Be(ImageFormat.Png, "the CaptureOptions default format is png");
    }

    [Fact]
    public async Task CaptureAsync_under_the_cap_is_not_resized()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(
            new ScreenRegion(0, 0, 200, 100), new CaptureOptions(ImageFormat.Png));

        result.Width.Should().Be(200, "200x100 is well inside the 1920x1080 default cap");
        result.Height.Should().Be(100);
        result.OriginalWidth.Should().Be(200);
        result.OriginalHeight.Should().Be(100);
        result.CoordinateScale.Should().Be(1.0);
    }

    [Fact]
    public async Task CaptureAsync_downscales_to_the_cap_and_reports_the_original_size()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(
            new ScreenRegion(0, 0, 200, 100), new CaptureOptions(ImageFormat.Png, MaxWidth: 100));

        result.Width.Should().Be(100);
        result.Height.Should().Be(50, "the aspect ratio is preserved");
        result.OriginalWidth.Should().Be(200, "the originals are the CAPTURED size, not the encoded one");
        result.OriginalHeight.Should().Be(100);
        result.CoordinateScale.Should().Be(2.0);
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "still a valid PNG after the resize");
    }

    [Fact]
    public async Task CaptureAsync_applies_the_user_scale_on_top_of_the_cap()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(
            new ScreenRegion(0, 0, 200, 100), new CaptureOptions(ImageFormat.Jpeg, MaxWidth: 100, Scale: 0.5));

        result.Width.Should().Be(50);
        result.Height.Should().Be(25);
        result.CoordinateScale.Should().Be(4.0);
        result.Format.Should().Be(ImageFormat.Jpeg);
    }

    [Fact]
    public async Task CaptureAsync_with_no_limit_keeps_the_captured_size()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(
            new ScreenRegion(0, 0, 200, 100), new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0));

        result.Width.Should().Be(200);
        result.Height.Should().Be(100);
        result.CoordinateScale.Should().Be(1.0);
    }

    // ---- A-8 (R6) — the capture rect is in virtual-desktop coordinates -----------------------

    /// <summary>
    /// A-8's capture claim, which only a real GDI capture can prove: <c>CopyFromScreen</c> already
    /// takes virtual-desktop coordinates, so the union of every monitor — whose origin is negative
    /// whenever a monitor sits left of or above the primary — captures at the union's size with no
    /// change to the copy. Every tool-level test mocks <c>IScreenshotService</c> and would stay
    /// green if this were wrong (the <c>disk_inspect</c> failure mode in CLAUDE.md).
    /// <para>
    /// On a single-monitor box the union is the primary display, and the test still proves the
    /// resolved rect round-trips; the multi-monitor assertion is only meaningful on a desk with
    /// two screens, which is called out in the message.
    /// </para>
    /// <para>
    /// Note what is deliberately NOT tested here: the service does not validate the rect against
    /// the virtual screen — that is the tool's job (<c>RegionMathTests.Validate_*</c> and
    /// <c>ScreenToolsTests.Screenshot_region_outside_the_virtual_screen_throws_with_the_bounds</c>).
    /// A test that a one-pixel-outside rect fails here would pin behaviour GDI defines, not ours.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CaptureAsync_captures_the_union_of_every_monitor()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        monitors.Should().NotBeEmpty("EnumerateMonitorsAsync reports at least the primary display");
        var union = RegionMath.Union(monitors);

        var result = await new ScreenshotService().CaptureAsync(
            union, new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0));

        result.Width.Should().Be(union.Width,
            monitors.Length > 1
                ? "the bitmap is sized to the union of {0} monitors, origin ({1},{2})"
                : "single-monitor box: the union is the primary display, origin ({1},{2})",
            monitors.Length, union.X, union.Y);
        result.Height.Should().Be(union.Height);
        result.OriginalWidth.Should().Be(union.Width);
        result.OriginalHeight.Should().Be(union.Height);
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "a real PNG came back");
    }
}
