using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-9 extracted the resize and the encode out of <see cref="ScreenshotService.CaptureAsync"/>
/// (the A-7 follow-up "extract the encode step"), so both are testable on a synthetic bitmap with
/// no desktop — unlike <c>ScreenshotServiceTests</c>, which needs a real screen. These are the
/// fast regression net; the capture path only wires them together. The one part of
/// <c>CaptureAsync</c> itself that needs no desktop — the cancellation guard that runs before the
/// screen is touched — is at the bottom.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotEncodeTests
{
    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(color);
        return bmp;
    }

    /// <summary>Deterministic noise — a solid or gradient bitmap compresses the same at every JPEG quality.</summary>
    private static SKBitmap Noise(int width, int height, int seed = 12345)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor(
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255));
            }
        }
        return bmp;
    }

    // ---- R3a — Downscale ---------------------------------------------------------------------

    [Theory]
    [InlineData(100, 50, 50, 25)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(64, 64, 1, 1)]
    public void Downscale_returns_a_bitmap_of_exactly_the_requested_size(int srcW, int srcH, int dstW, int dstH)
    {
        using var src = Solid(srcW, srcH, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, dstW, dstH);

        dst.Width.Should().Be(dstW);
        dst.Height.Should().Be(dstH);
    }

    [Fact]
    public void Downscale_returns_a_new_bitmap_and_leaves_the_source_untouched()
    {
        using var src = Solid(200, 100, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, 100, 50);

        dst.Should().NotBeSameAs(src, "the caller owns and disposes the result");
        src.Width.Should().Be(200, "the source must survive the resize — the capture buffer is still mapped");
        src.Height.Should().Be(100);
    }

    [Fact]
    public void Downscale_preserves_the_colour_of_a_solid_bitmap()
    {
        using var src = Solid(200, 100, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, 100, 50);

        var centre = dst.GetPixel(50, 25);
        centre.Red.Should().BeGreaterThan(200, "solid red must still be red after the resize");
        centre.Green.Should().BeLessThan(60);
        centre.Blue.Should().BeLessThan(60);
    }

    // ---- R3b — Encode ------------------------------------------------------------------------

    [Fact]
    public void Encode_png_starts_with_the_png_magic_bytes()
    {
        using var bmp = Solid(32, 32, SKColors.Blue);

        var bytes = ScreenshotService.Encode(bmp, ImageFormat.Png, 90);

        bytes.Should().NotBeEmpty();
        bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    [Fact]
    public void Encode_jpeg_starts_with_the_jpeg_soi_marker()
    {
        using var bmp = Solid(32, 32, SKColors.Blue);

        var bytes = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 90);

        bytes.Should().NotBeEmpty();
        bytes.Take(2).Should().Equal(new byte[] { 0xFF, 0xD8 });
    }

    [Fact]
    public void Encode_jpeg_quality_changes_the_size_of_the_output()
    {
        // The A-9 'quality' parameter is worthless if it is not actually handed to the encoder;
        // a hardcoded 90 (what A-7 shipped) makes these two arrays identical in length.
        using var bmp = Noise(256, 256);

        var low = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 30);
        var high = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 95);

        low.Length.Should().BeLessThan(high.Length, "a lower JPEG quality must produce fewer bytes");
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg, 0)]
    [InlineData(ImageFormat.Jpeg, -1)]
    [InlineData(ImageFormat.Jpeg, 101)]
    [InlineData(ImageFormat.Png, 0)]
    [InlineData(ImageFormat.Png, 101)]
    public void Encode_rejects_a_quality_outside_1_to_100(ImageFormat format, int quality)
    {
        using var bmp = Solid(8, 8, SKColors.Green);

        // Ambiguity resolved (flagged in the RED report): quality is validated for BOTH formats,
        // so an out-of-range value cannot slip through by choosing png.
        var act = () => ScreenshotService.Encode(bmp, format, quality);

        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("quality");
        ex.Message.Should().Contain("100", "the message must name the 1-100 range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Encode_accepts_the_inclusive_quality_bounds(int quality)
    {
        using var bmp = Solid(8, 8, SKColors.Green);

        ScreenshotService.Encode(bmp, ImageFormat.Jpeg, quality).Should().NotBeEmpty();
    }

    [Fact]
    public void Encode_png_ignores_the_quality_argument()
    {
        // The tool advertises "quality: JPEG encoder quality, 1-100 (default 90); ignored for
        // png". Same bitmap, opposite ends of the range: PNG is lossless, so the bytes must be
        // identical — if a future change routed quality into the PNG encoder the promise breaks.
        using var bmp = Noise(64, 64);

        var lowest = ScreenshotService.Encode(bmp, ImageFormat.Png, 1);
        var highest = ScreenshotService.Encode(bmp, ImageFormat.Png, 100);

        highest.Should().Equal(lowest, "png output must not depend on the jpeg quality argument");
    }

    // ---- R4a — CaptureAsync's pre-capture guard (the only part of it that needs no desktop) ---

    [Fact]
    public async Task CaptureAsync_throws_before_touching_the_screen_when_already_cancelled()
    {
        // A 0x0 region is the tripwire: reaching `new Bitmap(0, 0, ...)` throws ArgumentException,
        // so only a cancellation check that runs BEFORE the capture setup can produce an
        // OperationCanceledException here. Asserting on a 10x10 region instead would pass on a
        // box with a desktop even if the guard were deleted (the later in-flight checks would
        // still catch it after the screen had already been copied).
        var service = new ScreenshotService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CaptureAsync_cancellation_guard_runs_before_the_options_are_validated()
    {
        // A cancelled call must not be reported as a bad-argument error: the caller gave up, it
        // did not misuse the API. Scale 5.0 would be an ArgumentOutOfRangeException from
        // ScaleMath.Fit if the guard were not first.
        var service = new ScreenshotService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.CaptureAsync(
            new ScreenRegion(0, 0, 0, 0), new CaptureOptions(ImageFormat.Png, Scale: 5.0), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- A-11 (R4) — the capture contract's new fields, pinned without a desktop -------------

    [Fact]
    public void CaptureOptions_does_not_include_the_cursor_by_default()
    {
        // The service default is OFF: only screenshot(include_cursor:true) draws, and every other
        // caller of CaptureAsync (ocr, and A-6's annotate later) gets a clean bitmap.
        new CaptureOptions().IncludeCursor.Should().BeFalse();
    }

    [Fact]
    public void CaptureOptions_include_cursor_is_appended_after_the_existing_settings()
    {
        // Positional construction is how every existing caller and test builds these records;
        // inserting the new field anywhere but last would silently re-bind their arguments.
        var options = new CaptureOptions(ImageFormat.Jpeg, 800, 600, 0.5, 70, true);

        options.Format.Should().Be(ImageFormat.Jpeg);
        options.MaxWidth.Should().Be(800);
        options.MaxHeight.Should().Be(600);
        options.Scale.Should().Be(0.5);
        options.Quality.Should().Be(70);
        options.IncludeCursor.Should().BeTrue();
    }

    [Fact]
    public void ScreenshotResult_reports_no_cursor_drawn_by_default()
    {
        var result = new ScreenshotResult([1, 2, 3], 2, 2, ImageFormat.Png, 4, 4, 2.0);

        result.CursorDrawn.Should().BeNull("absent, never an empty string — the tool omits the field when it is null");
    }

    [Fact]
    public void ScreenshotResult_cursor_drawn_is_appended_after_the_coordinate_scale()
    {
        var result = new ScreenshotResult([1, 2, 3], 2, 2, ImageFormat.Png, 4, 4, 2.0, "ring");

        result.CoordinateScale.Should().Be(2.0);
        result.CursorDrawn.Should().Be("ring");
    }

    // ---- A-11 (R4, GREEN) — DrawCursor: icon, ring, or nothing, decided without a desktop ----

    /// <summary>
    /// The GDI half of A-11 is only reachable on a live desktop through <c>CaptureAsync</c>, but the
    /// <b>decision</b> — try the real cursor image, fall back to the ring, draw nothing when the
    /// pointer is off the captured rect — is not: <c>DrawCursor</c> takes the icon step as a
    /// <c>Func</c>, so a synthetic bitmap and a stub that always/never succeeds proves all three
    /// outcomes here. <c>ScreenshotCursorTests</c> (UIAutomation) proves the live wiring.
    /// </summary>
    private static readonly System.Drawing.Color GdiGrey = System.Drawing.Color.FromArgb(255, 128, 128, 128);

    /// <summary>An opaque mid-grey 32bpp GDI bitmap — the shape <c>CaptureAsync</c> hands DrawCursor.</summary>
    private static System.Drawing.Bitmap GreyCapture(int width = 64, int height = 64)
    {
        var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(GdiGrey);
        return bmp;
    }

    private static HashSet<System.Drawing.Color> Colours(System.Drawing.Bitmap bmp)
    {
        var colours = new HashSet<System.Drawing.Color>();
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                colours.Add(bmp.GetPixel(x, y));
        return colours;
    }

    private static void ShouldBeUntouched(System.Drawing.Bitmap bmp) =>
        Colours(bmp).Should().ContainSingle("nothing was drawn, so every pixel is still the fill colour")
            .Which.Should().Be(GdiGrey);

    private static bool IsNearWhite(System.Drawing.Color c) => c.R > 200 && c.G > 200 && c.B > 200;
    private static bool IsNearBlack(System.Drawing.Color c) => c.R < 60 && c.G < 60 && c.B < 60;

    /// <summary>Always/never composites the icon, and records what it was offered.</summary>
    private sealed class IconStub(bool succeeds)
    {
        public int Calls { get; private set; }
        public System.Drawing.Bitmap? Bitmap { get; private set; }
        public (int X, int Y)? Point { get; private set; }

        public bool Try(System.Drawing.Bitmap bmp, int x, int y)
        {
            Calls++;
            Bitmap = bmp;
            Point = (x, y);
            return succeeds;
        }
    }

    [Fact]
    public void DrawCursor_reports_icon_and_draws_no_ring_when_the_cursor_image_composites()
    {
        // The real cursor bitmap went on (the stub draws nothing, which is the point: the pixels
        // that changed must be the icon's, never the ring's on top of it).
        using var bmp = GreyCapture();
        var icon = new IconStub(succeeds: true);

        var drawn = ScreenshotService.DrawCursor(bmp, new ScreenRegion(0, 0, 64, 64), new CursorPosition(32, 32), icon.Try);

        drawn.Should().Be("icon");
        icon.Calls.Should().Be(1, "the icon is attempted exactly once");
        ShouldBeUntouched(bmp);
    }

    [Fact]
    public void DrawCursor_falls_back_to_the_ring_when_the_cursor_image_cannot_be_composited()
    {
        // A hidden cursor or a DrawIconEx refusal must still mark the spot, not silently produce a
        // picture with no pointer in it.
        using var bmp = GreyCapture();
        var icon = new IconStub(succeeds: false);

        var drawn = ScreenshotService.DrawCursor(bmp, new ScreenRegion(0, 0, 64, 64), new CursorPosition(32, 32), icon.Try);

        drawn.Should().Be("ring");
        icon.Calls.Should().Be(1, "the icon is tried first and the ring is the fallback, not the default");
        IsNearWhite(bmp.GetPixel(44, 32)).Should().BeTrue("radius 12 is the white outer stroke; got {0}", bmp.GetPixel(44, 32));
        IsNearBlack(bmp.GetPixel(40, 32)).Should().BeTrue("radius 8 is the black inner stroke; got {0}", bmp.GetPixel(40, 32));
        bmp.GetPixel(32, 32).Should().Be(GdiGrey, "the strokes leave what the cursor points at visible");
    }

    [Fact]
    public void DrawCursor_draws_nothing_and_reports_null_when_the_cursor_is_off_the_captured_rect()
    {
        // The third outcome: include_cursor was asked for, but the pointer is on another monitor,
        // so the field is absent rather than a mark drawn at a clamped, wrong position.
        using var bmp = GreyCapture();
        var icon = new IconStub(succeeds: true);

        var drawn = ScreenshotService.DrawCursor(bmp, new ScreenRegion(0, 0, 64, 64), new CursorPosition(500, 500), icon.Try);

        drawn.Should().BeNull();
        icon.Calls.Should().Be(0, "an off-rect cursor costs no GDI work at all");
        ShouldBeUntouched(bmp);
    }

    [Fact]
    public void DrawCursor_offers_the_icon_the_bitmap_point_not_the_virtual_desktop_point()
    {
        // A capture of the second monitor starts at (1920,0): compositing at the raw virtual-desktop
        // coordinate would put the cursor 1920 px off the right edge of a 64 px bitmap.
        using var bmp = GreyCapture();
        var icon = new IconStub(succeeds: true);

        ScreenshotService.DrawCursor(bmp, new ScreenRegion(1920, 0, 64, 64), new CursorPosition(1930, 15), icon.Try);

        icon.Point.Should().Be((10, 15), "the captured rect's origin is subtracted before anything is drawn");
        icon.Bitmap.Should().BeSameAs(bmp, "the icon goes onto the capture itself, not onto a copy");
    }

    [Fact]
    public void DrawCursor_draws_the_ring_at_the_bitmap_point_not_the_virtual_desktop_point()
    {
        using var bmp = GreyCapture();

        var drawn = ScreenshotService.DrawCursor(bmp, new ScreenRegion(1920, 0, 64, 64),
            new CursorPosition(1952, 32), (_, _, _) => false);

        drawn.Should().Be("ring");
        IsNearWhite(bmp.GetPixel(44, 32)).Should().BeTrue(
            "the ring is centred on (32,32) of the bitmap, which is (1952,32) of the virtual desktop; got {0}",
            bmp.GetPixel(44, 32));
    }

    [Fact]
    public void DrawCursor_writes_the_ring_through_the_bitmaps_own_pixels()
    {
        // The ring path locks the GDI buffer and wraps it in a Skia view; a view over a copy would
        // draw a perfect ring into memory the caller never encodes. Anti-aliasing is the tell that
        // the real CursorOverlay ran, not a placeholder.
        using var bmp = GreyCapture();

        ScreenshotService.DrawCursor(bmp, new ScreenRegion(0, 0, 64, 64), new CursorPosition(32, 32), (_, _, _) => false);

        Colours(bmp).Count.Should().BeGreaterThan(3,
            "grey plus white plus black is three; the anti-aliased blends are the fourth and beyond");
    }
}
