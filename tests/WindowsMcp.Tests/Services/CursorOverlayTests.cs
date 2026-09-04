using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-11 (R3): the fallback cursor mark, drawn when the real cursor bitmap cannot be composited.
/// Both halves are pure — the bitmap-coordinate arithmetic and the Skia drawing — so the ring is
/// proven on a synthetic 64x64 with no desktop and no capture (roadmap C10); the wiring into a
/// real capture is <c>ScreenshotCursorTests</c> (UIAutomation).
/// </summary>
[Trait("Category", "Unit")]
public class CursorOverlayTests
{
    private static readonly SKColor Grey = new(128, 128, 128, 255);

    private static SKBitmap MidGrey(int size = 64)
    {
        var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(Grey);
        return bmp;
    }

    private static bool IsNearWhite(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;
    private static bool IsNearBlack(SKColor c) => c.Red < 60 && c.Green < 60 && c.Blue < 60;

    // ---- RingPoint — capture rect to bitmap pixels -------------------------------------------

    [Fact]
    public void RingPoint_subtracts_the_captured_regions_origin()
    {
        // A capture of the second monitor starts at (1920,0), so a cursor at (1930,15) is 10 px
        // into the image — drawing at the raw virtual-desktop coordinate would miss the bitmap.
        CursorOverlay.RingPoint(new CursorPosition(1930, 15), new ScreenRegion(1920, 0, 1920, 1080))
            .Should().Be((10, 15));
    }

    [Fact]
    public void RingPoint_at_the_regions_origin_is_the_bitmaps_origin()
    {
        CursorOverlay.RingPoint(new CursorPosition(1920, 0), new ScreenRegion(1920, 0, 1920, 1080))
            .Should().Be((0, 0));
    }

    [Fact]
    public void RingPoint_includes_the_regions_last_pixel()
    {
        // Inclusive of the last pixel: (x+w-1, y+h-1) is the bottom-right pixel of the capture.
        CursorOverlay.RingPoint(new CursorPosition(199, 99), new ScreenRegion(0, 0, 200, 100))
            .Should().Be((199, 99));
    }

    [Fact]
    public void RingPoint_handles_a_region_with_a_negative_origin()
    {
        CursorOverlay.RingPoint(new CursorPosition(-1910, -30), new ScreenRegion(-1920, -40, 1920, 1080))
            .Should().Be((10, 10));
    }

    [Theory]
    [InlineData(200, 50)]     // one past the right edge
    [InlineData(50, 100)]     // one past the bottom edge
    [InlineData(-1, 50)]      // one left of it
    [InlineData(50, -1)]      // one above it
    [InlineData(5000, 5000)]  // on another monitor entirely
    public void RingPoint_is_null_when_the_cursor_is_outside_the_capture(int x, int y)
    {
        CursorOverlay.RingPoint(new CursorPosition(x, y), new ScreenRegion(0, 0, 200, 100))
            .Should().BeNull("nothing is drawn for a cursor that is not in the picture");
    }

    // ---- DrawRing — the two-tone mark --------------------------------------------------------

    [Theory]
    [InlineData(12, 0)]
    [InlineData(-12, 0)]
    [InlineData(0, 12)]
    [InlineData(0, -12)]
    public void DrawRing_draws_a_white_ring_at_radius_12(int dx, int dy)
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 32, 32);

        var pixel = bmp.GetPixel(32 + dx, 32 + dy);
        IsNearWhite(pixel).Should().BeTrue(
            "the outer ring is white so the mark reads on a dark background; got {0}", pixel);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(-8, 0)]
    [InlineData(0, 8)]
    [InlineData(0, -8)]
    public void DrawRing_draws_a_black_ring_at_radius_8(int dx, int dy)
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 32, 32);

        var pixel = bmp.GetPixel(32 + dx, 32 + dy);
        IsNearBlack(pixel).Should().BeTrue(
            "the inner ring is black so the mark reads on a light background; got {0}", pixel);
    }

    [Fact]
    public void DrawRing_leaves_the_centre_pixel_untouched()
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 32, 32);

        bmp.GetPixel(32, 32).Should().Be(Grey,
            "the rings are strokes, not discs — what the cursor is pointing AT must stay visible");
    }

    [Fact]
    public void DrawRing_leaves_the_gap_between_the_two_rings_alone()
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 32, 32);

        var pixel = bmp.GetPixel(42, 32);   // radius 10: outside the black stroke, inside the white one
        IsNearWhite(pixel).Should().BeFalse("radius 10 is between the two strokes; got {0}", pixel);
        IsNearBlack(pixel).Should().BeFalse("radius 10 is between the two strokes; got {0}", pixel);
    }

    [Fact]
    public void DrawRing_is_anti_aliased()
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 32, 32);

        var colours = new HashSet<SKColor>();
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                colours.Add(bmp.GetPixel(x, y));

        colours.Count.Should().BeGreaterThan(3,
            "an aliased ring on a solid background yields exactly three colours (grey, white, black); " +
            "anti-aliasing yields the blends in between");
    }

    // ---- DrawRing — the edges ----------------------------------------------------------------

    [Theory]
    [InlineData(2, 2)]
    [InlineData(61, 61)]
    [InlineData(0, 0)]
    [InlineData(-5, 30)]
    [InlineData(70, 30)]
    public void DrawRing_clips_at_the_bitmap_edge_without_throwing(int x, int y)
    {
        using var bmp = MidGrey();

        Action act = () => CursorOverlay.DrawRing(bmp, x, y);

        act.Should().NotThrow("a cursor near or past the edge of the capture is normal, not an error");
    }

    [Fact]
    public void DrawRing_two_pixels_from_the_edge_still_marks_what_fits()
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 2, 2);

        var pixel = bmp.GetPixel(2, 10);   // radius 8 below the centre — inside the bitmap
        IsNearBlack(pixel).Should().BeTrue(
            "the part of the ring that fits is still drawn; got {0}", pixel);
    }

    [Fact]
    public void DrawRing_far_outside_the_bitmap_changes_nothing()
    {
        using var bmp = MidGrey();

        CursorOverlay.DrawRing(bmp, 1000, 1000);

        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                bmp.GetPixel(x, y).Should().Be(Grey, "no part of a ring at (1000,1000) reaches a 64x64 bitmap");
    }
}
