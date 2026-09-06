using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-6 (R1/R3): the annotate step of the capture pipeline. The drawing happens between A-9's
/// downscale and the encode, on a writable bitmap — so <see cref="ScreenshotService.EncodeAnnotated"/>
/// is the seam, and it needs no desktop: a synthetic bitmap in, encoded bytes out, decoded again
/// to check the pixels. <see cref="ScreenshotAnnotateDesktopTests"/> is the non-mocked sibling
/// that proves the same thing on a real capture (the zero-copy GDI view is locked ReadOnly, which
/// no synthetic bitmap can prove).
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotAnnotateTests
{
    private static readonly SKColor Grey = new(128, 128, 128, 255);

    private static SKBitmap MidGrey(int width = 200, int height = 100)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(Grey);
        return bmp;
    }

    private static readonly ScreenRegion Origin200x100 = new(0, 0, 200, 100);

    private static AnnotationBox Box(string label, int x, int y, int w, int h) =>
        new(label, new Bounds(x, y, w, h));

    /// <summary>Pixels of <paramref name="colour"/> on the three rows around the rect's top edge.</summary>
    internal static int TopEdge(SKBitmap bmp, SKRectI rect, SKColor colour)
    {
        var count = 0;
        for (var y = rect.Top - 1; y <= rect.Top + 1; y++)
        {
            if (y < 0 || y >= bmp.Height) continue;
            for (var x = rect.Left + 3; x < Math.Min(rect.Right - 3, bmp.Width); x++)
                if (bmp.GetPixel(x, y) == colour) count++;
        }
        return count;
    }

    // ---- R1 — the contract additions are trailing, so every existing construction compiles ----

    [Fact]
    public void CaptureOptions_has_no_annotations_and_no_grid_by_default()
    {
        var options = new CaptureOptions();

        options.Annotations.Should().BeNull();
        options.Grid.Should().BeNull("nothing is drawn unless the caller asks for it");
    }

    [Fact]
    public void CaptureOptions_annotations_and_grid_are_appended_after_the_cursor()
    {
        // Positional construction is how the tool and the tests build these; inserting the new
        // fields anywhere but last would silently re-bind their arguments.
        var options = new CaptureOptions(
            ImageFormat.Jpeg, 800, 600, 0.5, 70, true, new CursorPosition(1, 2),
            [Box("el_1", 0, 0, 10, 10)], new GridSpec(3, 2));

        options.Cursor.Should().Be(new CursorPosition(1, 2));
        options.Annotations.Should().ContainSingle().Which.Label.Should().Be("el_1");
        options.Grid.Should().Be(new GridSpec(3, 2));
    }

    [Fact]
    public void ScreenshotResult_reports_no_annotations_drawn_by_default()
    {
        new ScreenshotResult([1, 2, 3], 2, 2, ImageFormat.Png, 4, 4, 2.0).AnnotationsDrawn.Should().Be(0);
    }

    [Fact]
    public void ScreenshotResult_annotations_drawn_is_appended_after_the_cursor_field()
    {
        var result = new ScreenshotResult([1, 2, 3], 2, 2, ImageFormat.Png, 4, 4, 2.0, "ring", 5);

        result.CursorDrawn.Should().Be("ring");
        result.AnnotationsDrawn.Should().Be(5);
    }

    [Fact]
    public void AnnotationBox_carries_the_label_and_virtual_desktop_bounds()
    {
        var box = new AnnotationBox("el_7", new Bounds(10, 20, 30, 40));

        box.Label.Should().Be("el_7", "the label is the snapshot's element id, verbatim");
        box.Bounds.Should().Be(new Bounds(10, 20, 30, 40));
    }

    // ---- R3 — EncodeAnnotated -----------------------------------------------------------------

    [Fact]
    public void EncodeAnnotated_with_nothing_to_draw_is_byte_identical_to_a_plain_encode()
    {
        // The promise that makes annotate free when it is off: an unannotated capture must not
        // even be copied, let alone re-drawn.
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, null, Origin200x100, 1.0, null);

        drawn.Should().Be(0);
        bytes.Should().Equal(ScreenshotService.Encode(bmp, ImageFormat.Png, 90));
    }

    [Fact]
    public void EncodeAnnotated_with_an_empty_box_list_draws_nothing()
    {
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, [], Origin200x100, 1.0, null);

        drawn.Should().Be(0);
        bytes.Should().Equal(ScreenshotService.Encode(bmp, ImageFormat.Png, 90));
    }

    [Fact]
    public void EncodeAnnotated_draws_the_box_into_the_encoded_image()
    {
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, [Box("el_1", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(bytes);
        decoded.Width.Should().Be(200, "the encode is of the same bitmap, at the same size");
        TopEdge(decoded, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(0)).Should().BeGreaterThan(2,
            "the bytes the caller gets back are the annotated ones, not the clean capture");
    }

    [Fact]
    public void EncodeAnnotated_maps_the_boxes_through_the_coordinate_scale()
    {
        // The drawing happens AFTER A-9's downscale, so a box in virtual-desktop pixels has to be
        // divided by the coordinate scale or every label lands at twice its distance from the origin.
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, [Box("el_1", 20, 20, 40, 20)], new ScreenRegion(0, 0, 400, 200), 2.0, null);

        drawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(bytes);
        TopEdge(decoded, new SKRectI(10, 10, 30, 20), Annotator.ColorFor(0)).Should().BeGreaterThan(2,
            "virtual (20,20,40,20) at coordinateScale 2 is image (10,10)-(30,20)");
    }

    [Fact]
    public void EncodeAnnotated_does_not_count_or_draw_a_box_outside_the_captured_rect()
    {
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, [Box("el_1", 500, 500, 40, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(0, "AnnotationsDrawn is what the picture actually shows");
        bytes.Should().Equal(ScreenshotService.Encode(bmp, ImageFormat.Png, 90),
            "nothing was drawn, so the pixels are the capture's own");
    }

    [Fact]
    public void EncodeAnnotated_draws_a_grid_with_no_boxes_at_all()
    {
        using var bmp = MidGrey();

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, null, Origin200x100, 1.0, new GridSpec(4, 2));

        drawn.Should().Be(0, "the grid is not an annotation box and is not counted");
        bytes.Should().NotEqual(ScreenshotService.Encode(bmp, ImageFormat.Png, 90),
            "a grid with no boxes still has to reach the encoded image");
    }

    [Fact]
    public void EncodeAnnotated_honours_the_requested_format()
    {
        using var bmp = MidGrey();

        var (bytes, _) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Jpeg, 90, [Box("el_1", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        bytes.Take(2).Should().Equal(new byte[] { 0xFF, 0xD8 }, "the annotate step must not change the encoder");
    }

    [Fact]
    public void EncodeAnnotated_draws_on_a_copy_and_leaves_the_callers_bitmap_untouched()
    {
        // CaptureAsync hands in the SKBitmap wrapped around the GDI buffer it locked ReadOnly, so
        // the annotator must never draw straight onto the caller's bitmap. This is the headless
        // half of that guarantee (the pixels of the input are unchanged);
        // ScreenshotAnnotateDesktopTests is the half that proves it against a real ReadOnly lock.
        using var bmp = MidGrey();
        var clean = ScreenshotService.Encode(bmp, ImageFormat.Png, 90);

        var (bytes, drawn) = ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Png, 90, [Box("el_1", 10, 40, 60, 40)], Origin200x100, 1.0, new GridSpec(4, 2));

        drawn.Should().Be(1);
        bytes.Should().NotEqual(clean, "the returned bytes are the annotated ones");
        ScreenshotService.Encode(bmp, ImageFormat.Png, 90).Should().Equal(clean,
            "the bitmap that was passed in still holds the capture, not the annotations");
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                bmp.GetPixel(x, y).Should().Be(Grey, "pixel ({0},{1}) of the source was repainted", x, y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void EncodeAnnotated_rejects_a_quality_outside_1_to_100(int quality)
    {
        using var bmp = MidGrey();

        var act = () => ScreenshotService.EncodeAnnotated(
            bmp, ImageFormat.Jpeg, quality, null, Origin200x100, 1.0, null);

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("quality",
            "the annotate path is still an encode and keeps the same argument contract");
    }
}

/// <summary>
/// A-6 (R3) on a real capture. Nothing here can be mocked: the pixels come from
/// <c>CopyFromScreen</c> and the bitmap the annotator draws on is the one wrapped around the GDI
/// buffer that <c>CaptureAsync</c> locks <b>ReadOnly</b> — drawing straight onto that view is the
/// mistake a synthetic bitmap can never catch. Needs an interactive desktop, hence
/// <c>Category=UIAutomation</c>: excluded from headless runs.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class ScreenshotAnnotateDesktopTests
{
    /// <summary>The top-left 200x100 of the primary display; the primary's origin is (0,0) by definition.</summary>
    private static readonly ScreenRegion TopLeft = new(0, 0, 200, 100);

    private static CaptureOptions Options(
        IReadOnlyList<AnnotationBox>? boxes, GridSpec? grid = null, int maxWidth = 0) =>
        new(ImageFormat.Png, MaxWidth: maxWidth, MaxHeight: 0, Annotations: boxes, Grid: grid);

    private static readonly AnnotationBox Box = new("el_1", new Bounds(10, 10, 50, 30));

    [Fact]
    public async Task CaptureAsync_draws_the_annotation_box_onto_the_real_capture()
    {
        var result = await new ScreenshotService().CaptureAsync(TopLeft, Options([Box]));

        result.AnnotationsDrawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(result.Bytes);
        ScreenshotAnnotateTests.TopEdge(decoded, new SKRectI(10, 10, 60, 40), Annotator.ColorFor(0))
            .Should().BeGreaterThan(2,
                "the box is drawn on the bitmap that is encoded, not on the read-only GDI view");
    }

    [Fact]
    public async Task CaptureAsync_draws_the_annotation_box_at_the_downscaled_coordinates()
    {
        // The draw happens after A-9's resize: at MaxWidth 100 the 200 px capture halves, so the
        // box at virtual (10,10,50,30) must land at image (5,5)-(30,20), not at its full-size place.
        var result = await new ScreenshotService().CaptureAsync(TopLeft, Options([Box], maxWidth: 100));

        result.Width.Should().Be(100);
        result.CoordinateScale.Should().Be(2.0);
        result.AnnotationsDrawn.Should().Be(1);
        using var decoded = SKBitmap.Decode(result.Bytes);
        ScreenshotAnnotateTests.TopEdge(decoded, new SKRectI(5, 5, 30, 20), Annotator.ColorFor(0))
            .Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task CaptureAsync_with_a_box_outside_the_region_is_byte_identical_to_no_annotation()
    {
        var service = new ScreenshotService();
        var outside = new AnnotationBox("el_1", new Bounds(5000, 5000, 50, 30));

        // A caret blink or an animation under the region makes two captures differ on their own;
        // retry for a quiet moment rather than fail on the first flicker (ScreenshotCursorTests).
        ScreenshotResult before, annotated, after;
        var attempt = 0;
        while (true)
        {
            before = await service.CaptureAsync(TopLeft, Options(null));
            annotated = await service.CaptureAsync(TopLeft, Options([outside]));
            after = await service.CaptureAsync(TopLeft, Options(null));
            if (before.Bytes.AsSpan().SequenceEqual(after.Bytes) || ++attempt >= 8) break;
            await Task.Delay(150);
        }

        after.Bytes.Should().Equal(before.Bytes,
            "the pixels under the region must be static for this comparison to mean anything — " +
            "run this with a quiet desktop under the top-left 200x100 of the primary display (8 attempts)");
        annotated.AnnotationsDrawn.Should().Be(0);
        annotated.Bytes.Should().Equal(before.Bytes, "a box that is not in the picture changes no pixel");
    }

    [Fact]
    public async Task CaptureAsync_without_annotations_reports_none_drawn()
    {
        var result = await new ScreenshotService().CaptureAsync(TopLeft, Options(null));

        result.AnnotationsDrawn.Should().Be(0);
    }
}
