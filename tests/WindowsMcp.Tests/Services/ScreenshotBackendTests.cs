using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-10 (R1/R2/R3): the capture backend contract without a desktop — the three trailing record
/// fields that carry the choice, and <see cref="ScreenshotService.ResolveBackend"/>, the whole
/// selection rule as a pure function (roadmap C10: the part that can be unit-tested is extracted
/// so it is). What each backend actually produces is <c>ScreenshotWgcCaptureTests</c>
/// (UIAutomation) and <see cref="ScreenshotBackendIntegrationTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotBackendTests
{
    // ---- R1 — the contract additions are TRAILING fields with the documented defaults ---------

    [Fact]
    public void CaptureOptions_asks_for_the_process_default_backend_by_default()
    {
        new CaptureOptions().Backend.Should().Be("auto",
            "a call that names no backend takes whatever the process was started with");
    }

    [Fact]
    public void CaptureOptions_backend_is_appended_after_every_earlier_setting()
    {
        // Positional construction is how the tool and the tests build these records; inserting the
        // new field anywhere but last would silently re-bind their arguments.
        var options = new CaptureOptions(
            ImageFormat.Jpeg, 800, 600, 0.5, 70, true, new CursorPosition(1, 2),
            [new AnnotationBox("el_1", new Bounds(0, 0, 10, 10))], new GridSpec(3, 2), true, "wgc");

        options.Profile.Should().BeTrue();
        options.Backend.Should().Be("wgc");
    }

    [Fact]
    public void ScreenshotResult_reports_gdi_when_nothing_says_otherwise()
    {
        new ScreenshotResult([1, 2, 3], 2, 2, ImageFormat.Png, 2, 2, 1.0).Backend
            .Should().Be("gdi", "GDI is what every pre-A-10 capture came from");
    }

    [Fact]
    public void ScreenshotResult_backend_is_appended_after_the_stage_timings()
    {
        var result = new ScreenshotResult(
            [1], 2, 2, ImageFormat.Png, 4, 4, 2.0, "ring", 5, [new StageTiming("capture", 7)], "wgc");

        result.Stages.Should().ContainSingle();
        result.Backend.Should().Be("wgc", "the result names the backend that actually produced the frame");
    }

    [Fact]
    public void ScreenshotOptions_defaults_to_auto()
    {
        new ScreenshotOptions(1.0).Backend.Should().Be("auto");
        ScreenshotOptions.Default.Backend.Should().Be("auto",
            "an unconfigured server prefers the compositor and falls back to GDI");
    }

    [Fact]
    public void ScreenshotOptions_backend_is_appended_after_the_earlier_switches()
    {
        var options = new ScreenshotOptions(0.5, false, true, "gdi");

        options.Flash.Should().BeFalse();
        options.Profile.Should().BeTrue();
        options.Backend.Should().Be("gdi");
    }

    // ---- R2 — ResolveBackend, the whole selection rule -----------------------------------------

    [Theory]
    [InlineData("auto", "gdi", "gdi")]          // auto defers to the process default...
    [InlineData("auto", "wgc", "wgc")]
    [InlineData("auto", "auto", "auto")]        // ...which may itself be auto
    [InlineData("gdi", "auto", "gdi")]
    [InlineData("wgc", "auto", "wgc")]
    [InlineData("gdi", "wgc", "gdi")]           // a named call wins over the process default
    [InlineData("wgc", "gdi", "wgc")]
    [InlineData("gdi", "gdi", "gdi")]
    public void ResolveBackend_lets_a_named_call_win_and_auto_defer_to_the_process(
        string requested, string processDefault, string expected)
    {
        ScreenshotService.ResolveBackend(requested, processDefault).Should().Be(expected);
    }

    [Theory]
    [InlineData("AUTO", "GDI", "gdi")]
    [InlineData("Auto", "Wgc", "wgc")]
    [InlineData("WGC", "auto", "wgc")]
    [InlineData("Gdi", "AUTO", "gdi")]
    [InlineData("AUTO", "AUTO", "auto")]
    public void ResolveBackend_is_case_insensitive_and_answers_in_lower_case(
        string requested, string processDefault, string expected)
    {
        // The answer is what the result's 'backend' metadata field reports, so it has one spelling
        // whatever the caller typed.
        ScreenshotService.ResolveBackend(requested, processDefault).Should().Be(expected);
    }

    [Theory]
    [InlineData("dxcam")]        // upstream's backend names are not ours
    [InlineData("mss")]
    [InlineData("pillow")]
    [InlineData("dxgi")]
    [InlineData("g")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" wgc")]         // un-trimmed, like every other option in this repo
    [InlineData("wgc ")]
    public void ResolveBackend_rejects_an_unknown_request_naming_the_three_values(string requested)
    {
        var act = () => ScreenshotService.ResolveBackend(requested, "auto");

        var message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain("auto").And.Contain("gdi").And.Contain("wgc",
            "the caller cannot guess the vocabulary from a bare 'invalid value'");
    }

    [Theory]
    [InlineData("dxcam")]
    [InlineData("")]
    [InlineData(" gdi")]
    public void ResolveBackend_rejects_an_unknown_process_default_even_for_a_valid_request(string processDefault)
    {
        // Both inputs are validated: a process started with a typo must not look healthy just
        // because this particular call named its own backend.
        var act = () => ScreenshotService.ResolveBackend("gdi", processDefault);

        var message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain("auto").And.Contain("gdi").And.Contain("wgc");
    }

    [Fact]
    public void ResolveBackend_rejects_a_null_on_either_side()
    {
        var requested = () => ScreenshotService.ResolveBackend(null!, "auto");
        var processDefault = () => ScreenshotService.ResolveBackend("auto", null!);

        requested.Should().Throw<ArgumentException>();
        processDefault.Should().Throw<ArgumentException>();
    }

    // ---- R3 — the service's shape: an owned WGC backend means an owned lifetime -----------------

    [Fact]
    public void ScreenshotService_is_disposable_so_the_container_releases_the_wgc_device()
    {
        typeof(ScreenshotService).Should().Implement<IDisposable>(
            "the service owns a D3D device and a WinRT capture device; the DI container disposes singletons");

        var service = new ScreenshotService();
        var act = () =>
        {
            service.Dispose();
            service.Dispose();
        };

        act.Should().NotThrow("a second dispose must be harmless — the container is not the only caller");
    }

    [Fact]
    public void ScreenshotService_takes_the_process_options_as_an_optional_constructor_argument()
    {
        // Roadmap C7: the process-level backend crosses into the service as the options record,
        // never as an environment read inside it. Optional, because every existing caller (and
        // every test above) constructs the service with no arguments at all.
        var parameter = typeof(ScreenshotService).GetConstructors().Should().ContainSingle().Subject
            .GetParameters().SingleOrDefault(p => p.ParameterType == typeof(ScreenshotOptions));

        parameter.Should().NotBeNull("the service cannot honour --screenshot-backend it was never given");
        parameter!.Name.Should().Be("options");
        parameter.IsOptional.Should().BeTrue();
        parameter.DefaultValue.Should().BeNull("null means ScreenshotOptions.Default, i.e. auto");
    }

    // ---- R3 — the backend is resolved BEFORE anything is captured ------------------------------

    [Theory]
    [InlineData("dxcam")]
    [InlineData("")]
    [InlineData(" wgc")]
    public async Task CaptureAsync_with_an_unknown_backend_throws_before_touching_the_screen(string backend)
    {
        // The 0x0 region is the tripwire ScreenshotEncodeTests uses: reaching the capture setup
        // throws ArgumentException from `new Bitmap(0, 0, ...)`, so only a resolution that runs
        // FIRST can produce a message that names the three backends.
        using var service = new ScreenshotService();

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), new CaptureOptions(Backend: backend));

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("auto").And.Contain("gdi").And.Contain("wgc");
    }

    [Fact]
    public async Task CaptureAsync_with_an_unknown_process_backend_throws_before_touching_the_screen()
    {
        using var service = new ScreenshotService(options: new ScreenshotOptions(1.0, Backend: "mss"));

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), new CaptureOptions());

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("auto").And.Contain("gdi").And.Contain("wgc");
    }
}

/// <summary>
/// A-10 (R3), through the real <see cref="WgcCaptureBackend"/> — the collaborator every mocked
/// test above would keep green if it never produced a frame at all (the <c>disk_inspect
/// mode:reclaimable</c> failure mode in CLAUDE.md). Everything here is headless-safe: no monitor
/// is ever captured, only the refusal paths are driven, so no GDI or WGC frame is required.
/// The frames themselves are <c>ScreenshotWgcCaptureTests</c> (UIAutomation).
/// </summary>
[Trait("Category", "Integration")]
public class ScreenshotBackendIntegrationTests
{
    /// <summary>A rect no monitor can cover, on any desk: WGC has nothing to compose from.</summary>
    private static readonly ScreenRegion OffEveryMonitor = new(200_000, 200_000, 8, 8);

    private static CaptureOptions FullSize(string backend) =>
        new(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0, Backend: backend);

    [Fact]
    public void IsSupported_answers_without_throwing_on_this_session()
    {
        // The contract the fallback rests on: every failure inside the backend is a false, not an
        // exception the caller has to catch. A throw here would take out every 'auto' capture.
        var act = () => WgcCaptureBackend.IsSupported();

        act.Should().NotThrow();
    }

    [Fact]
    public void TryCapture_with_no_monitors_refuses_and_hands_back_no_bitmap()
    {
        using var backend = new WgcCaptureBackend();

        var captured = backend.TryCapture(new ScreenRegion(0, 0, 10, 10), Array.Empty<MonitorInfo>(), out var bitmap);

        captured.Should().BeFalse("no monitor overlaps the rect, so no frame was produced");
        bitmap.Should().BeNull("a refusal must not hand back a half-filled bitmap for the caller to encode");
    }

    [Fact]
    public async Task TryCapture_of_a_rect_no_monitor_covers_refuses()
    {
        using var backend = new WgcCaptureBackend();
        var monitors = await new WindowService().EnumerateMonitorsAsync();

        var captured = backend.TryCapture(OffEveryMonitor, monitors, out var bitmap);

        captured.Should().BeFalse("the rect is off every display in the inventory");
        bitmap.Should().BeNull();
    }

    [Fact]
    public void The_backend_can_be_disposed_twice()
    {
        var backend = new WgcCaptureBackend();

        var act = () =>
        {
            backend.Dispose();
            backend.Dispose();
        };

        act.Should().NotThrow("ScreenshotService.Dispose and a container shutdown can both reach it");
    }

    [Fact]
    public async Task CaptureAsync_wgc_that_cannot_be_served_refuses_naming_the_backend()
    {
        // The caller asked for wgc BY NAME, so a refusal is an error rather than a quiet GDI
        // frame: a call that says 'wgc' because GDI returns black for the window it wants must
        // never be answered with the black GDI frame.
        using var service = new ScreenshotService();

        Func<Task> act = () => service.CaptureAsync(OffEveryMonitor, FullSize("wgc"));

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.ToLowerInvariant().Should().Contain("wgc", "the message names the backend that could not serve the call");
    }

    [Fact]
    public async Task CaptureAsync_uses_the_process_default_backend_when_the_call_says_auto()
    {
        // The headless-safe proof that the injected ScreenshotOptions reach the capture: a process
        // default of wgc over a rect no monitor covers must refuse exactly as an explicit
        // backend:"wgc" call does. Options that never arrived would take the GDI path and return
        // a black 8x8 instead.
        using var service = new ScreenshotService(options: new ScreenshotOptions(1.0, Backend: "wgc"));

        Func<Task> act = () => service.CaptureAsync(OffEveryMonitor, FullSize("auto"));

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.ToLowerInvariant().Should().Contain("wgc");
    }

    [Fact]
    public async Task CaptureAsync_wgc_behaves_the_same_after_the_service_was_disposed()
    {
        // The container disposes the singleton at shutdown; a capture that arrives after that (or
        // after a test disposed early) must go through the same refusal, not an
        // ObjectDisposedException from a released D3D device. The backend is created lazily, so
        // the second call builds a new one — that it is a NEW one is only visible on a desktop
        // (ScreenshotWgcCaptureTests.CaptureAsync_wgc_still_captures_after_the_service_was_disposed);
        // what is checkable here is that the observable behaviour does not change.
        var service = new ScreenshotService();
        Func<Task> capture = () => service.CaptureAsync(OffEveryMonitor, FullSize("wgc"));

        (await capture.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .ToLowerInvariant().Should().Contain("wgc", "the first call is what creates the backend");

        service.Dispose();

        (await capture.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .ToLowerInvariant().Should().Contain("wgc", "and the call after Dispose is answered identically");
    }
}

/// <summary>
/// A-10 (R7) — the WGC half of the pipeline without a desktop, through
/// <c>ScreenshotService.WgcFrameSource</c>: the seam that stands in for the compositor (null =
/// it refused). What is proved here is everything the selection rule leads to — which backend a
/// frame comes from, what happens when the compositor refuses or throws, and that a WGC frame
/// goes through the same cursor / downscale / annotate / profile pipeline a GDI one does — with
/// the frame itself supplied, so no monitor is read and nothing here needs an interactive
/// session. The real frames are <c>ScreenshotWgcCaptureTests</c> (UIAutomation) and the real
/// refusals <see cref="ScreenshotBackendIntegrationTests"/>; this class is the fast net between
/// them.
/// <para>
/// The GDI side is only ever reached through the 0x0 tripwire (<c>new Bitmap(0, 0, ...)</c>
/// throws <see cref="ArgumentException"/> before <c>CopyFromScreen</c> needs a desktop), because
/// a real GDI capture is <c>Category=UIAutomation</c> in this repo — so "fell back to GDI" is
/// asserted as "reached the GDI frame path", never as a picture.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotFrameSourceTests
{
    private static readonly SKColor Red = new(220, 30, 30, 255);
    private static readonly SKColor Grey = new(128, 128, 128, 255);

    /// <summary>Not at the origin: a WGC frame is cropped to the caller's rect, wherever it is.</summary>
    private static readonly ScreenRegion Rect = new(100, 50, 40, 20);

    /// <summary>The 0x0 tripwire: reaching <c>GdiFrame</c> throws here, on any session.</summary>
    private static readonly ScreenRegion Tripwire = new(0, 0, 0, 0);

    private static SKBitmap Frame(SKColor colour, int width = 40, int height = 20)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(colour);
        return bmp;
    }

    private static CaptureOptions Options(
        string backend = "auto", int maxWidth = 0, bool cursor = false, CursorPosition? at = null,
        IReadOnlyList<AnnotationBox>? boxes = null, bool profile = false) =>
        new(ImageFormat.Png, MaxWidth: maxWidth, MaxHeight: 0, IncludeCursor: cursor, Cursor: at,
            Annotations: boxes, Profile: profile, Backend: backend);

    /// <summary>A stand-in compositor: counts the asks, records the rect, answers with what it was given.</summary>
    private sealed class FakeCompositor(Func<ScreenRegion, SKBitmap?> answer)
    {
        public int Calls { get; private set; }
        public ScreenRegion? AskedFor { get; private set; }

        public SKBitmap? Capture(ScreenRegion r)
        {
            Calls++;
            AskedFor = r;
            return answer(r);
        }
    }

    private static (ScreenshotService Service, FakeCompositor Compositor) Serving(
        Func<ScreenRegion, SKBitmap?> answer, string processDefault = "auto")
    {
        var compositor = new FakeCompositor(answer);
        var service = new ScreenshotService(options: new ScreenshotOptions(1.0, Backend: processDefault))
        {
            WgcFrameSource = compositor.Capture,
        };
        return (service, compositor);
    }

    // ---- the frame comes from the compositor, and the result says so ---------------------------

    [Theory]
    [InlineData("wgc")]     // asked for by name...
    [InlineData("auto")]    // ...and preferred by auto when it can serve
    public async Task CaptureAsync_encodes_the_frame_the_compositor_produced(string backend)
    {
        var (service, compositor) = Serving(_ => Frame(Red));
        using var _ = service;

        var result = await service.CaptureAsync(Rect, Options(backend));

        result.Backend.Should().Be("wgc", "the result names the producer, not the request");
        result.Width.Should().Be(40);
        result.Height.Should().Be(20);
        result.OriginalWidth.Should().Be(40);
        result.OriginalHeight.Should().Be(20);
        using var decoded = SKBitmap.Decode(result.Bytes);
        decoded.GetPixel(5, 5).Should().Be(Red,
            "these are the compositor's pixels — a GDI frame of the same rect would be the screen");
        compositor.Calls.Should().Be(1, "one capture, one ask");
    }

    [Fact]
    public async Task CaptureAsync_asks_the_compositor_for_the_rect_the_caller_wanted()
    {
        var (service, compositor) = Serving(_ => Frame(Red));
        using var _ = service;

        await service.CaptureAsync(Rect, Options("wgc"));

        compositor.AskedFor.Should().Be(Rect,
            "the backend crops to the caller's virtual-desktop rect, so it has to be told which one");
    }

    // ---- refusal: silent for auto, loud for wgc ------------------------------------------------

    [Fact]
    public async Task CaptureAsync_auto_falls_back_to_gdi_when_the_compositor_refuses()
    {
        var (service, compositor) = Serving(_ => null);
        using var _ = service;

        Func<Task> act = () => service.CaptureAsync(Tripwire, Options());

        var message = (await act.Should().ThrowAsync<ArgumentException>(
            "the GDI frame path was reached, and 0x0 is what it refuses")).Which.Message;
        message.Should().NotContain("wgc", "an auto caller is never told the compositor was tried");
        compositor.Calls.Should().Be(1, "auto asks the compositor first and only then falls back");
    }

    [Fact]
    public async Task CaptureAsync_auto_falls_back_to_gdi_when_the_compositor_throws()
    {
        // A COM failure inside the backend is a refusal like any other: 'auto' promised a picture,
        // not a working compositor.
        var (service, compositor) = Serving(_ => throw new NotSupportedException("no D3D device"));
        using var _ = service;

        Func<Task> act = () => service.CaptureAsync(Tripwire, Options());

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().NotContain("no D3D device", "the throw is swallowed into a fallback, not surfaced");
        compositor.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CaptureAsync_wgc_that_the_compositor_refuses_throws_naming_the_backend_and_the_rect()
    {
        // Rect is a REAL rect: if the refusal fell through to GDI this would come back as a
        // picture (or a Win32Exception headless), never as this exception.
        var (service, compositor) = Serving(_ => null);
        using var _ = service;

        Func<Task> act = () => service.CaptureAsync(Rect, Options("wgc"));

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("wgc", "the caller asked for this backend by name and is told it could not serve");
        message.Should().Contain("100,50,40,20", "and which rect it could not serve");
        message.Should().Contain("gdi", "the way out is named: fall back explicitly or ask for auto");
        compositor.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CaptureAsync_wgc_that_throws_refuses_rather_than_falling_back()
    {
        var (service, _) = Serving(_ => throw new NotSupportedException("no D3D device"));
        using var __ = service;

        Func<Task> act = () => service.CaptureAsync(Rect, Options("wgc"));

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("wgc");
        message.Should().NotContain("no D3D device", "the inner failure is logged, not raised at the caller");
    }

    // ---- gdi never consults the compositor -----------------------------------------------------

    [Fact]
    public async Task CaptureAsync_gdi_never_asks_the_compositor()
    {
        var (service, compositor) = Serving(_ => Frame(Red));
        using var _ = service;

        Func<Task> act = () => service.CaptureAsync(Tripwire, Options("gdi"));

        await act.Should().ThrowAsync<ArgumentException>();
        compositor.Calls.Should().Be(0,
            "backend 'gdi' means the classic copy: a compositor frame would be a different picture");
    }

    [Fact]
    public async Task CaptureAsync_auto_on_a_gdi_process_default_never_asks_the_compositor()
    {
        // The RESOLVED backend drives the frame, not the raw request: --screenshot-backend gdi is
        // how an operator turns the compositor off for the whole server.
        var (service, compositor) = Serving(_ => Frame(Red), processDefault: "gdi");
        using var _ = service;

        Func<Task> act = () => service.CaptureAsync(Tripwire, Options());

        await act.Should().ThrowAsync<ArgumentException>();
        compositor.Calls.Should().Be(0);
    }

    // ---- the pipeline after the frame is the same one GDI feeds ---------------------------------

    [Fact]
    public async Task CaptureAsync_draws_the_cursor_onto_a_compositor_frame()
    {
        var (service, _) = Serving(_ => Frame(Red));
        using var __ = service;
        var inside = new CursorPosition(Rect.X + 20, Rect.Y + 10);

        var plain = await service.CaptureAsync(Rect, Options("wgc"));
        var withCursor = await service.CaptureAsync(Rect, Options("wgc", cursor: true, at: inside));

        withCursor.Backend.Should().Be("wgc");
        withCursor.CursorDrawn.Should().BeOneOf("icon", "ring",
            "the GDI view over the Skia pixels is what A-11 draws through, whichever backend filled them");
        withCursor.Bytes.Should().NotEqual(plain.Bytes, "the mark is actually on the compositor's frame");
    }

    [Fact]
    public async Task CaptureAsync_leaves_a_compositor_frame_alone_when_the_cursor_is_outside_the_rect()
    {
        var (service, _) = Serving(_ => Frame(Red));
        using var __ = service;
        var outside = new CursorPosition(Rect.X - 10, Rect.Y - 10);

        var plain = await service.CaptureAsync(Rect, Options("wgc"));
        var withCursor = await service.CaptureAsync(Rect, Options("wgc", cursor: true, at: outside));

        withCursor.CursorDrawn.Should().BeNull("nothing was drawn: the pointer is not in this picture");
        withCursor.Bytes.Should().Equal(plain.Bytes);
    }

    [Fact]
    public async Task CaptureAsync_downscales_a_compositor_frame_through_the_same_A9_step()
    {
        var (service, _) = Serving(_ => Frame(Red));
        using var __ = service;

        var result = await service.CaptureAsync(Rect, Options("wgc", maxWidth: 20));

        result.Backend.Should().Be("wgc");
        result.Width.Should().Be(20);
        result.Height.Should().Be(10, "the aspect ratio is preserved");
        result.OriginalWidth.Should().Be(40, "the originals are the frame the compositor handed over");
        result.OriginalHeight.Should().Be(20);
        result.CoordinateScale.Should().Be(2.0);
        using var decoded = SKBitmap.Decode(result.Bytes);
        decoded.GetPixel(10, 5).Should().Be(Red, "the resize kept the compositor's pixels");
    }

    [Fact]
    public async Task CaptureAsync_annotates_a_compositor_frame_through_the_same_A6_step()
    {
        var region = new ScreenRegion(100, 50, 200, 100);
        var (service, _) = Serving(_ => Frame(Grey, 200, 100));
        using var __ = service;
        var box = new AnnotationBox("el_1", new Bounds(region.X + 10, region.Y + 10, 50, 30));

        var result = await service.CaptureAsync(region, Options("wgc", boxes: [box]));

        result.Backend.Should().Be("wgc");
        result.AnnotationsDrawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(result.Bytes);
        ScreenshotAnnotateTests.TopEdge(decoded, new SKRectI(10, 10, 60, 40), Annotator.ColorFor(0))
            .Should().BeGreaterThan(2,
                "the box lands on the compositor's frame at rect-relative coordinates, as it does on a GDI one");
    }

    [Fact]
    public async Task CaptureAsync_reports_the_same_four_stages_for_a_compositor_frame()
    {
        var (service, _) = Serving(_ => Frame(Red));
        using var __ = service;

        var result = await service.CaptureAsync(Rect,
            Options("wgc", maxWidth: 20, cursor: true, at: new CursorPosition(Rect.X + 5, Rect.Y + 5), profile: true));

        result.Backend.Should().Be("wgc");
        result.Stages.Should().NotBeNull();
        result.Stages!.Select(s => s.Stage).Should().Equal(["capture", "cursor", "resize", "encode"],
            "A-14's stage names are one contract the two capture paths must not fork");
        result.Stages.Should().OnlyContain(s => s.Ms >= 0);
    }

    // ---- the defaults the pipeline fills in for a caller that supplies neither ------------------

    [Fact]
    public async Task CaptureAsync_with_no_options_at_all_still_resolves_a_backend()
    {
        // A direct caller (OcrService is one) may pass no options: the defaults have to carry a
        // backend, or the capture would resolve nothing and the result could not name a producer.
        var (service, compositor) = Serving(_ => Frame(Red));
        using var _ = service;

        var result = await service.CaptureAsync(Rect);

        result.Backend.Should().Be("wgc", "the default CaptureOptions ask for auto, and auto prefers the compositor");
        result.Width.Should().Be(40, "the default 1920x1080 cap leaves a 40x20 frame alone");
        compositor.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CaptureAsync_with_no_region_asks_the_compositor_for_the_primary_display()
    {
        // The rect the backend is given is the one the pipeline would have handed GDI: the primary
        // display at the origin, not a monitor-local (0,0,w,h) the compositor would fill differently.
        var (service, compositor) = Serving(r => Frame(Red, Math.Max(1, r.Width), Math.Max(1, r.Height)));
        using var _ = service;

        var result = await service.CaptureAsync(region: null, new CaptureOptions(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0, Backend: "wgc"));

        result.Backend.Should().Be("wgc");
        compositor.Calls.Should().Be(1);
        compositor.AskedFor!.X.Should().Be(0, "the default rect starts at the virtual-desktop origin");
        compositor.AskedFor.Y.Should().Be(0);
    }

    [Fact]
    public async Task CaptureAsync_reads_the_live_pointer_when_the_caller_names_no_position()
    {
        // A-11: the caller's own read wins so the picture and the metadata agree, but a direct
        // caller that passes none gets a live GetCursorPos. This rect is off every desktop, so the
        // live pointer is certainly outside it and nothing may be drawn.
        var offEveryDesktop = new ScreenRegion(200_000, 200_000, 40, 20);
        var (service, compositor) = Serving(_ => Frame(Red));
        using var _ = service;

        var result = await service.CaptureAsync(offEveryDesktop, Options("wgc", cursor: true));

        result.CursorDrawn.Should().BeNull("the live pointer cannot be inside a rect no display covers");
        result.Backend.Should().Be("wgc");
        compositor.AskedFor.Should().Be(offEveryDesktop);
    }

    // ---- the service owns the frame it was handed -----------------------------------------------

    [Fact]
    public async Task CaptureAsync_disposes_the_frame_the_compositor_handed_over()
    {
        // The backend allocates a full-screen BGRA bitmap per capture; if the pipeline did not
        // dispose it, an agent loop taking a screenshot a second would leak native pixels until
        // the finalizer got round to it.
        var frame = new ObservableBitmap(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        var (service, _) = Serving(_ => frame);
        using var __ = service;

        await service.CaptureAsync(Rect, Options("wgc"));

        frame.Disposed.Should().BeTrue("the frame belongs to the pipeline the moment it is handed over");
    }

    [Fact]
    public async Task CaptureAsync_still_serves_after_the_service_was_disposed()
    {
        // The container disposes the singleton at shutdown, but a capture that arrives after that
        // (or a test that disposes early) must not get an ObjectDisposedException: the backend is
        // created lazily, so the next capture simply makes a new one.
        var (service, _) = Serving(_ => Frame(Red));
        service.Dispose();
        service.Dispose();

        var result = await service.CaptureAsync(Rect, Options("wgc"));

        result.Backend.Should().Be("wgc");
        result.Width.Should().Be(40);
    }

    /// <summary>An <see cref="SKBitmap"/> that remembers whether the pipeline disposed it.</summary>
    private sealed class ObservableBitmap(SKImageInfo info) : SKBitmap(info)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
