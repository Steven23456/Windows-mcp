using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions;
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

    // ---- A-14 (R4): per-stage timings of the real capture pipeline ----------------------------
    // The four stages are the four things CaptureAsync does, and the only way to see them is to
    // run the real pipeline: a mocked IScreenshotService would report whatever the test wrote.

    [Fact]
    public async Task CaptureAsync_reports_its_four_stages_when_profiling_is_on()
    {
        var result = await new ScreenshotService().CaptureAsync(
            new ScreenRegion(0, 0, 200, 100),
            new CaptureOptions(ImageFormat.Png, MaxWidth: 100, MaxHeight: 0, IncludeCursor: true, Profile: true));

        result.Stages.Should().NotBeNull();
        result.Stages!.Select(x => x.Stage).Should().Equal(["capture", "cursor", "resize", "encode"],
            "the pipeline's four steps, in the order they run");
        result.Stages.Should().OnlyContain(x => x.Ms >= 0);
    }

    [Fact]
    public async Task CaptureAsync_reports_no_stages_when_profiling_is_off()
    {
        var result = await new ScreenshotService().CaptureAsync(
            new ScreenRegion(0, 0, 200, 100), new CaptureOptions(ImageFormat.Png));

        result.Stages.Should().BeNull("off is the default and an unprofiled result is unchanged");
    }

    [Fact]
    public async Task CaptureAsync_reports_every_stage_even_when_the_step_did_nothing()
    {
        // No downscale (no caps) and no cursor: those stages still appear, at ~0 ms. A stage that
        // vanishes when it is cheap makes two profiled runs impossible to compare.
        var result = await new ScreenshotService().CaptureAsync(
            new ScreenRegion(0, 0, 200, 100),
            new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0, IncludeCursor: false, Profile: true));

        result.Stages!.Select(x => x.Stage).Should().Equal(["capture", "cursor", "resize", "encode"]);
    }
}

/// <summary>
/// S-11 (R6): the control indicator is never in a picture. <c>CaptureAsync</c> is the single path
/// <c>screenshot</c>, its annotated form and <c>ocr</c> share, so it brackets the frame acquisition
/// with <see cref="IControlIndicator.SuspendForCapture"/> - a no-op when display affinity took
/// effect, a hide/restore otherwise. Driven through the <c>WgcFrameSource</c> seam (backend "wgc"),
/// so no desktop is read and this is a Unit test; whether affinity alone keeps the windows out of
/// a real GDI/WGC frame is <see cref="ControlIndicatorDesktopTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotServiceControlIndicatorTests
{
    private static readonly ScreenRegion Rect = new(100, 50, 40, 20);

    /// <summary>The 0x0 tripwire: reaching the GDI frame path throws ArgumentException on any session.</summary>
    private static readonly ScreenRegion Tripwire = new(0, 0, 0, 0);

    /// <summary>An indicator that records every suspension: how many opened, how many closed, and whether one is open now.</summary>
    private sealed class SuspensionRecorder : IControlIndicator
    {
        private int _open;
        public int Opened;
        public int Closed;
        public bool IsOpen => Volatile.Read(ref _open) > 0;

        public void BeginAction() { }
        public void EndAction() { }
        public bool IsVisible => true;
        public bool IsCaptureExcluded => false;
        public TakeoverRecord? ConsumeTakeover() => null;

        public IDisposable SuspendForCapture()
        {
            Interlocked.Increment(ref Opened);
            Interlocked.Increment(ref _open);
            return new Scope(this);
        }

        private sealed class Scope(SuspensionRecorder owner) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) != 0) return;   // a double dispose is not a second close
                Interlocked.Increment(ref owner.Closed);
                Interlocked.Decrement(ref owner._open);
            }
        }
    }

    private static SKBitmap Frame(int width = 40, int height = 20)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(new SKColor(128, 128, 128, 255));
        return bmp;
    }

    private static CaptureOptions Options(string backend = "wgc") =>
        new(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0, Backend: backend);

    [Fact]
    public async Task CaptureAsync_acquires_the_frame_inside_a_capture_suspension()
    {
        var indicator = new SuspensionRecorder();
        bool? openWhenGrabbed = null;
        using var service = new ScreenshotService(indicator: indicator)
        {
            WgcFrameSource = _ => { openWhenGrabbed = indicator.IsOpen; return Frame(); },
        };

        var result = await service.CaptureAsync(Rect, Options());

        result.Backend.Should().Be("wgc");
        openWhenGrabbed.Should().BeTrue("the indicator's windows must be out of the way at the moment the frame is taken");
        indicator.Opened.Should().Be(1, "one capture, one suspension");
        indicator.Closed.Should().Be(1, "and the suspension is closed again, so the indicator comes back");
        indicator.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task CaptureAsync_closes_the_suspension_when_the_compositor_refuses()
    {
        // backend "wgc" refuses loudly when the compositor cannot serve: the windows must not be
        // left hidden behind the refusal.
        var indicator = new SuspensionRecorder();
        using var service = new ScreenshotService(indicator: indicator) { WgcFrameSource = _ => null };

        Func<Task> act = () => service.CaptureAsync(Rect, Options());

        await act.Should().ThrowAsync<InvalidOperationException>();
        indicator.Opened.Should().Be(1, "the acquisition was attempted inside a suspension");
        indicator.Closed.Should().Be(1, "a failed grab still restores the indicator (try/finally, not a happy-path dispose)");
    }

    [Fact]
    public async Task CaptureAsync_holds_one_suspension_across_the_auto_fallback_to_gdi()
    {
        // auto asks the compositor first and falls back to GDI: one acquisition, one suspension -
        // not a restore between the two attempts, which would put the ring in the GDI frame.
        var indicator = new SuspensionRecorder();
        bool? openWhenAsked = null;
        using var service = new ScreenshotService(indicator: indicator)
        {
            WgcFrameSource = _ => { openWhenAsked = indicator.IsOpen; return null; },
        };

        Func<Task> act = () => service.CaptureAsync(Tripwire, Options("auto"));

        await act.Should().ThrowAsync<ArgumentException>("the GDI path was reached through the 0x0 tripwire");
        openWhenAsked.Should().BeTrue();
        indicator.Opened.Should().Be(1, "the fallback happens inside the same suspension");
        indicator.Closed.Should().Be(1);
    }

    [Fact]
    public async Task CaptureAsync_with_an_invalid_backend_never_suspends()
    {
        // The backend is decided before anything is allocated: a bad argument must not flicker the
        // indicator either.
        var indicator = new SuspensionRecorder();
        using var service = new ScreenshotService(indicator: indicator) { WgcFrameSource = _ => Frame() };

        Func<Task> act = () => service.CaptureAsync(Rect, Options("dxcam"));

        await act.Should().ThrowAsync<ArgumentException>();
        indicator.Opened.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_with_a_cancelled_token_never_suspends()
    {
        var indicator = new SuspensionRecorder();
        using var service = new ScreenshotService(indicator: indicator) { WgcFrameSource = _ => Frame() };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => service.CaptureAsync(Rect, Options(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        indicator.Opened.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_suspends_once_per_capture()
    {
        var indicator = new SuspensionRecorder();
        using var service = new ScreenshotService(indicator: indicator) { WgcFrameSource = _ => Frame() };

        await service.CaptureAsync(Rect, Options());
        await service.CaptureAsync(Rect, Options());
        await service.CaptureAsync(Rect, Options());

        indicator.Opened.Should().Be(3);
        indicator.Closed.Should().Be(3);
    }

    [Fact]
    public async Task CaptureAsync_without_an_indicator_works_unchanged()
    {
        // Every existing caller constructs the service without one (tests, ocr's own instance):
        // the parameter is optional and its absence is not an error.
        using var service = new ScreenshotService(options: new ScreenshotOptions(1.0, Backend: "wgc")) { WgcFrameSource = _ => Frame() };

        var result = await service.CaptureAsync(Rect, Options());

        result.Width.Should().Be(40);
        result.Height.Should().Be(20);
        result.Backend.Should().Be("wgc");
    }
}
