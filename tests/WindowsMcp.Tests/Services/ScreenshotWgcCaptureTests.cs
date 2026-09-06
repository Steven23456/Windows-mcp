using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-10 (R3) on real frames: what <c>Windows.Graphics.Capture</c> actually produces, and that the
/// rest of the pipeline — cursor, downscale, annotations, encode — treats a WGC frame exactly as
/// it treats a GDI one. None of this can be mocked: the pixels come from the compositor and the
/// comparison baseline from <c>CopyFromScreen</c>, so this is the only place the wgc half of A-10
/// is exercised at all (<c>ScreenshotBackendTests</c> covers the selection rule and
/// <c>ScreenshotBackendIntegrationTests</c> the refusal paths).
/// <para>
/// Needs an interactive desktop, and one test moves the mouse and does not put it back (same
/// bracket as <see cref="ScreenshotCursorTests"/>), hence <c>Category=UIAutomation</c>: excluded
/// from headless runs, never run by the test-agent.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class ScreenshotWgcCaptureTests
{
    /// <summary>Per-channel difference two captures of the same rect may show and still agree.</summary>
    private const int Tolerance = 24;

    /// <summary>Sampled points per capture: a 20x20 grid = 400 pixels.</summary>
    private const int GridSide = 20;

    private static CaptureOptions Wgc(
        int maxWidth = 0, bool cursor = false, IReadOnlyList<AnnotationBox>? boxes = null) =>
        new(ImageFormat.Png, MaxWidth: maxWidth, MaxHeight: 0, IncludeCursor: cursor,
            Annotations: boxes, Backend: "wgc");

    private static CaptureOptions Gdi(int maxWidth = 0) =>
        new(ImageFormat.Png, MaxWidth: maxWidth, MaxHeight: 0, Backend: "gdi");

    private static CaptureOptions Auto() =>
        new(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0);

    /// <summary>The top-left <paramref name="width"/>x<paramref name="height"/> of the primary display.</summary>
    private static async Task<ScreenRegion> PrimaryRectAsync(int width, int height)
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        var primary = RegionMath.Primary(monitors);
        return new ScreenRegion(primary.X, primary.Y,
            Math.Min(width, primary.Width), Math.Min(height, primary.Height));
    }

    /// <summary>~400 pixels on an evenly spaced grid, in a fixed order so two samples line up.</summary>
    private static SKColor[] Sample(SKBitmap bmp)
    {
        var colours = new SKColor[GridSide * GridSide];
        for (var row = 0; row < GridSide; row++)
        {
            for (var col = 0; col < GridSide; col++)
            {
                var x = Math.Min(bmp.Width - 1, (int)((col + 0.5) * bmp.Width / GridSide));
                var y = Math.Min(bmp.Height - 1, (int)((row + 0.5) * bmp.Height / GridSide));
                colours[(row * GridSide) + col] = bmp.GetPixel(x, y);
            }
        }
        return colours;
    }

    /// <summary>The fraction of sampled pixels that match within <see cref="Tolerance"/> on every channel.</summary>
    private static double Agreement(SKColor[] a, SKColor[] b)
    {
        var agreed = a.Zip(b).Count(pair =>
            Math.Abs(pair.First.Red - pair.Second.Red) <= Tolerance &&
            Math.Abs(pair.First.Green - pair.Second.Green) <= Tolerance &&
            Math.Abs(pair.First.Blue - pair.Second.Blue) <= Tolerance);
        return (double)agreed / a.Length;
    }

    // ---- (a) the whole point of A-10: a WGC frame is the same picture GDI would have taken -----

    [Fact]
    public async Task CaptureAsync_wgc_captures_the_primary_display_and_matches_the_gdi_frame()
    {
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(800, 600);

        ScreenshotResult wgc = null!;
        var agreement = 0.0;
        var black = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            wgc = await service.CaptureAsync(rect, Wgc());
            var gdi = await service.CaptureAsync(rect, Gdi());
            using var wgcBitmap = SKBitmap.Decode(wgc.Bytes);
            using var gdiBitmap = SKBitmap.Decode(gdi.Bytes);
            var samples = Sample(wgcBitmap);
            black = samples.All(c => c.Red + c.Green + c.Blue <= 30);
            agreement = Agreement(samples, Sample(gdiBitmap));
            if (!black && agreement >= 0.95) break;
            await Task.Delay(250);   // a busy desktop redraws between the two shots: look once more
        }

        wgc.Backend.Should().Be("wgc", "the result names the backend that produced the frame");
        wgc.Width.Should().Be(rect.Width, "the frame is cropped to the requested rect, not to the monitor");
        wgc.Height.Should().Be(rect.Height);
        black.Should().BeFalse(
            "a uniformly black frame is exactly the failure WGC exists to avoid — it means the copy never landed");
        agreement.Should().BeGreaterThanOrEqualTo(0.95,
            "the compositor's frame and GDI's copy of the same rect are the same picture ({0} of 400 samples agreed within {1}/255)",
            agreement * GridSide * GridSide, Tolerance);
    }

    // ---- (b)/(c) what each choice resolves to ---------------------------------------------------

    [Fact]
    public async Task CaptureAsync_gdi_reports_gdi()
    {
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);

        var result = await service.CaptureAsync(rect, Gdi());

        result.Backend.Should().Be("gdi", "the caller asked for GDI by name and must be told it got it");
        result.Width.Should().Be(rect.Width);
        result.Height.Should().Be(rect.Height);
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "a real PNG came back");
    }

    [Fact]
    public async Task CaptureAsync_auto_takes_wgc_where_it_is_supported_and_gdi_where_it_is_not()
    {
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);

        var result = await service.CaptureAsync(rect, Auto());

        result.Backend.Should().Be(WgcCaptureBackend.IsSupported() ? "wgc" : "gdi",
            "auto prefers the compositor and falls back to GDI, and the metadata says which happened");
        result.Width.Should().Be(rect.Width);
        result.Height.Should().Be(rect.Height);
    }

    [Fact]
    public async Task CaptureAsync_auto_falls_back_silently_when_wgc_cannot_serve_the_rect()
    {
        // A rect no monitor covers is a WGC refusal. 'auto' means the caller did not care which
        // backend served them, so it comes back as a GDI frame instead of an error - the contrast
        // with ScreenshotBackendIntegrationTests' explicit backend:"wgc", which throws.
        using var service = new ScreenshotService();

        var result = await service.CaptureAsync(new ScreenRegion(200_000, 200_000, 8, 8), Auto());

        result.Backend.Should().Be("gdi", "the fallback is silent, and the result reports what really produced it");
        result.Width.Should().Be(8);
    }

    // ---- (d)-(f) the rest of the pipeline runs on a WGC frame unchanged --------------------------

    [Fact]
    public async Task CaptureAsync_wgc_draws_the_cursor_onto_the_frame_too()
    {
        var rect = await PrimaryRectAsync(200, 100);
        await new InputService().HoverAsync(rect.X + 50, rect.Y + 50);   // inside the rect
        using var service = new ScreenshotService();

        // A caret blink or an animation under the region makes two identical captures differ;
        // retry for a quiet moment rather than fail on the first flicker (ScreenshotCursorTests).
        ScreenshotResult before, with, after;
        var attempt = 0;
        while (true)
        {
            before = await service.CaptureAsync(rect, Wgc());
            with = await service.CaptureAsync(rect, Wgc(cursor: true));
            after = await service.CaptureAsync(rect, Wgc());
            if (before.Bytes.AsSpan().SequenceEqual(after.Bytes) || ++attempt >= 8) break;
            await Task.Delay(150);
        }

        after.Bytes.Should().Equal(before.Bytes,
            "the pixels under the region must be static for this comparison to mean anything — " +
            "run this with a quiet desktop under the top-left 200x100 of the primary display (8 attempts)");
        with.Backend.Should().Be("wgc");
        with.CursorDrawn.Should().BeOneOf("icon", "ring",
            "the icon composite needs an HDC over the WGC pixels; the ring is the fallback, as on GDI");
        with.Bytes.Should().NotEqual(before.Bytes, "the cursor is actually painted onto the WGC frame");
    }

    [Fact]
    public async Task CaptureAsync_wgc_is_downscaled_by_the_same_A9_step()
    {
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);

        var result = await service.CaptureAsync(rect, Wgc(maxWidth: 100));

        result.Backend.Should().Be("wgc");
        result.Width.Should().Be(100);
        result.Height.Should().Be(50, "the aspect ratio is preserved");
        result.OriginalWidth.Should().Be(rect.Width, "the originals are the CAPTURED size");
        result.OriginalHeight.Should().Be(rect.Height);
        result.CoordinateScale.Should().Be(2.0);
    }

    [Fact]
    public async Task CaptureAsync_wgc_is_annotated_by_the_same_A6_step()
    {
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);
        var box = new AnnotationBox("el_1", new Bounds(rect.X + 10, rect.Y + 10, 50, 30));

        var result = await service.CaptureAsync(rect, Wgc(boxes: [box]));

        result.Backend.Should().Be("wgc");
        result.AnnotationsDrawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(result.Bytes);
        ScreenshotAnnotateTests.TopEdge(decoded, new SKRectI(10, 10, 60, 40), Annotator.ColorFor(0))
            .Should().BeGreaterThan(2,
                "the box is drawn on the WGC frame that is encoded, at the rect-relative coordinates");
    }

    [Fact]
    public async Task CaptureAsync_wgc_reports_the_same_four_stages_as_the_gdi_path()
    {
        // A-14's stage names are a contract two capture paths must not fork: "capture" is whichever
        // backend produced the frame, and the three that follow are the shared pipeline. A profiled
        // wgc run that named its stages differently would make two servers incomparable.
        using var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);

        var result = await service.CaptureAsync(rect, new CaptureOptions(
            ImageFormat.Png, MaxWidth: 100, MaxHeight: 0, IncludeCursor: true, Profile: true, Backend: "wgc"));

        result.Backend.Should().Be("wgc");
        result.Stages.Should().NotBeNull();
        result.Stages!.Select(x => x.Stage).Should().Equal(["capture", "cursor", "resize", "encode"],
            "the pipeline's four steps, in the order they run, whichever backend filled the first one");
        result.Stages.Should().OnlyContain(x => x.Ms >= 0);
    }

    // ---- (g) the union path: one WGC item per monitor, composed into one frame -------------------

    [Fact]
    public async Task CaptureAsync_wgc_spans_two_monitors_in_one_frame()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        var primary = RegionMath.Primary(monitors);
        var second = monitors.FirstOrDefault(m => !m.IsPrimary);
        var rect = second is null
            ? new ScreenRegion(primary.X, primary.Y, Math.Min(400, primary.Width), Math.Min(200, primary.Height))
            : RegionMath.Union([primary, second]);
        using var service = new ScreenshotService();

        var result = await service.CaptureAsync(rect, Wgc());

        result.Backend.Should().Be("wgc");
        result.Width.Should().Be(rect.Width, second is null
            ? "single-monitor box: the rect degrades to the primary, so this run only proves the one-monitor path"
            : "the frame is the union of the two monitors the rect touches, composed from one capture item each");
        result.Height.Should().Be(rect.Height);
        result.OriginalWidth.Should().Be(rect.Width);
        result.OriginalHeight.Should().Be(rect.Height);
    }

    // ---- (h) the parts of the real backend only a live compositor can show ----------------------

    [Fact]
    public async Task CaptureAsync_wgc_still_captures_after_the_service_was_disposed()
    {
        // ScreenshotService.Dispose releases the D3D device the backend holds, and the container
        // calls it at shutdown. The backend is created lazily, so the next capture has to build a
        // new one: reusing the disposed device would come back as a refusal (backend 'wgc'
        // throwing) or a black frame. ScreenshotBackendIntegrationTests can only show that the
        // REFUSAL path is unchanged after a dispose; that a frame still arrives needs the desktop.
        var service = new ScreenshotService();
        var rect = await PrimaryRectAsync(200, 100);

        var before = await service.CaptureAsync(rect, Wgc());
        service.Dispose();
        var after = await service.CaptureAsync(rect, Wgc());

        before.Backend.Should().Be("wgc");
        after.Backend.Should().Be("wgc", "a new backend was built for the second call");
        after.Width.Should().Be(rect.Width);
        using var decoded = SKBitmap.Decode(after.Bytes);
        Sample(decoded).All(c => c.Red + c.Green + c.Blue <= 30).Should().BeFalse(
            "a black frame is what a released D3D device would produce");
        service.Dispose();
    }

    [Fact]
    public async Task CaptureAsync_wgc_leaves_the_pointer_out_of_the_frame()
    {
        // The session sets IsCursorCaptureEnabled = false, because A-11 composites the pointer
        // itself: a compositor frame that already contained it would put a cursor in every
        // include_cursor:false capture (GDI's CopyFromScreen never contains one) and would draw
        // two of them when include_cursor is set. Moving the pointer in and out of the rect must
        // therefore not change a single pixel.
        var rect = await PrimaryRectAsync(200, 100);
        var inside = (X: rect.X + (rect.Width / 2), Y: rect.Y + (rect.Height / 2));
        var outside = (X: rect.X + rect.Width + 150, Y: rect.Y + rect.Height + 150);
        var input = new InputService();
        using var service = new ScreenshotService();

        ScreenshotResult over = null!, again = null!, away = null!;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await input.HoverAsync(inside.X, inside.Y);
            await Task.Delay(150);
            over = await service.CaptureAsync(rect, Wgc());
            again = await service.CaptureAsync(rect, Wgc());
            await input.HoverAsync(outside.X, outside.Y);
            await Task.Delay(150);
            away = await service.CaptureAsync(rect, Wgc());
            if (over.Bytes.AsSpan().SequenceEqual(again.Bytes)) break;
            await Task.Delay(150);
        }

        again.Bytes.Should().Equal(over.Bytes,
            "the pixels under the region must be static for this comparison to mean anything — " +
            "run this with a quiet desktop under the top-left 200x100 of the primary display (8 attempts)");
        away.Bytes.Should().Equal(over.Bytes,
            "the frame is identical whether or not the pointer is over the rect: the compositor was told not to draw it");
        over.CursorDrawn.Should().BeNull("include_cursor was not asked for, so nothing was painted either");
    }
}
