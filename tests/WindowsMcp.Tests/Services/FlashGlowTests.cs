using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-14 (R2): the glow's pure core — the window rect and the pixels — with no window, no Win32 and
/// no desktop (roadmap C10). <see cref="FlashOverlayTests"/> proves the window wiring on a live
/// session; everything about what the glow LOOKS like is decided here.
/// </summary>
[Trait("Category", "Unit")]
public class FlashGlowTests
{
    private const int M = FlashGlow.Margin;

    /// <summary>A rect the glow comfortably fits around, and its window rect.</summary>
    private static SKBitmap RenderFor(ScreenRegion captured)
    {
        var window = FlashGlow.WindowRect(captured);
        return FlashGlow.Render(window.Width, window.Height);
    }

    // ---- R2.1 / R2.2 — the window rect --------------------------------------------------------

    [Fact]
    public void Margin_is_ten_pixels()
        => FlashGlow.Margin.Should().Be(10, "the band width is the one number the geometry and the pixels share");

    [Fact]
    public void WindowRect_inflates_the_captured_rect_by_the_margin_on_every_side()
    {
        // The band frames the picture instead of covering its edge, so the window is 2*Margin
        // wider and taller than what was captured and starts Margin px up and to the left.
        FlashGlow.WindowRect(new ScreenRegion(100, 200, 800, 600))
            .Should().Be(new ScreenRegion(100 - M, 200 - M, 800 + 2 * M, 600 + 2 * M));
    }

    [Fact]
    public void WindowRect_at_the_origin_starts_at_minus_the_margin()
    {
        // A capture of the primary display starts at (0,0), so the glow hangs off the top-left of
        // the virtual desktop - negative coordinates are legal for a window and must not be clamped.
        FlashGlow.WindowRect(new ScreenRegion(0, 0, 1920, 1080))
            .Should().Be(new ScreenRegion(-M, -M, 1920 + 2 * M, 1080 + 2 * M));
    }

    [Fact]
    public void WindowRect_handles_a_monitor_left_of_and_above_the_primary()
    {
        FlashGlow.WindowRect(new ScreenRegion(-1920, -40, 1920, 1080))
            .Should().Be(new ScreenRegion(-1920 - M, -40 - M, 1920 + 2 * M, 1080 + 2 * M));
    }

    [Fact]
    public void WindowRect_of_the_smallest_sensible_capture_is_still_a_frame()
    {
        // 1x1: the band still surrounds it, which is what makes Render's minimum 2*Margin+1.
        FlashGlow.WindowRect(new ScreenRegion(5, 5, 1, 1))
            .Should().Be(new ScreenRegion(5 - M, 5 - M, 1 + 2 * M, 1 + 2 * M));
    }

    // ---- R2.3 — the bitmap itself -------------------------------------------------------------

    [Fact]
    public void Render_returns_a_bitmap_of_exactly_the_requested_size()
    {
        using var bmp = FlashGlow.Render(220, 120);

        bmp.Width.Should().Be(220);
        bmp.Height.Should().Be(120);
    }

    [Fact]
    public void Render_is_premultiplied_bgra_which_is_what_UpdateLayeredWindow_takes()
    {
        // ULW_ALPHA blends a 32-bit BGRA surface with premultiplied alpha; any other layout would
        // put the glow on screen in the wrong colours or with a black box behind it.
        using var bmp = FlashGlow.Render(220, 120);

        bmp.ColorType.Should().Be(SKColorType.Bgra8888);
        bmp.AlphaType.Should().Be(SKAlphaType.Premul);
    }

    // ---- R2.4 / R2.9 — the hole in the middle -------------------------------------------------

    [Fact]
    public void Render_leaves_the_centre_pixel_fully_transparent()
    {
        using var bmp = FlashGlow.Render(220, 120);

        bmp.GetPixel(110, 60).Alpha.Should().Be(0,
            "the overlay covers the captured area; anything but zero alpha there tints the desktop");
    }

    [Theory]
    [InlineData(M, M)]                       // the inner rect's top-left pixel
    [InlineData(220 - M - 1, M)]             // its top-right
    [InlineData(M, 120 - M - 1)]             // its bottom-left
    [InlineData(220 - M - 1, 120 - M - 1)]   // its bottom-right
    [InlineData(60, 30)]
    [InlineData(180, 90)]
    public void Render_leaves_the_whole_inner_rect_transparent(int x, int y)
    {
        using var bmp = FlashGlow.Render(220, 120);

        bmp.GetPixel(x, y).Alpha.Should().Be(0, $"({x},{y}) is inside the captured area");
    }

    // ---- R2.5 / R2.7 — the band --------------------------------------------------------------

    [Fact]
    public void Render_paints_the_band_just_inside_the_outer_edge_orange()
    {
        using var bmp = FlashGlow.Render(220, 120);

        var c = bmp.GetPixel(110, 0);   // top edge, mid-width: the outermost row of the band

        c.Alpha.Should().BeGreaterThan(0, "the outer edge of the band is drawn, only faintly");
        c.Red.Should().BeGreaterThan(c.Green, "orange is red-dominant");
        c.Green.Should().BeGreaterThan(c.Blue, "orange has more green than blue");
    }

    [Fact]
    public void Render_paints_the_innermost_band_pixel_orange_and_nearly_opaque()
    {
        using var bmp = FlashGlow.Render(220, 120);

        // One row above the inner rect: the band's inner edge, where the glow is strongest.
        var c = bmp.GetPixel(110, M - 1);

        c.Alpha.Should().BeGreaterThanOrEqualTo(200, "the band is opaque where it meets the picture");
        c.Red.Should().BeGreaterThan(200, "R ~ 255 before premultiplication");
        c.Green.Should().BeInRange(90, 200, "G ~ 140 before premultiplication");
        c.Blue.Should().BeLessThan(80, "B ~ 0 before premultiplication");
    }

    [Theory]
    [InlineData(0, 0)]                       // top-left
    [InlineData(220 - 1, 0)]                 // top-right
    [InlineData(0, 120 - 1)]                 // bottom-left
    [InlineData(220 - 1, 120 - 1)]           // bottom-right
    public void Render_paints_the_four_corners_so_the_frame_is_closed(int x, int y)
    {
        using var bmp = FlashGlow.Render(220, 120);

        bmp.GetPixel(x, y).Alpha.Should().BeGreaterThan(0,
            "a frame with holes at the corners does not read as a frame");
    }

    [Fact]
    public void Render_paints_all_four_sides_not_just_the_top()
    {
        using var bmp = FlashGlow.Render(220, 120);

        bmp.GetPixel(110, 0).Alpha.Should().BeGreaterThan(0, "top");
        bmp.GetPixel(110, 119).Alpha.Should().BeGreaterThan(0, "bottom");
        bmp.GetPixel(0, 60).Alpha.Should().BeGreaterThan(0, "left");
        bmp.GetPixel(219, 60).Alpha.Should().BeGreaterThan(0, "right");
    }

    // ---- R2.6 — the fade ----------------------------------------------------------------------

    [Fact]
    public void Render_fades_from_opaque_at_the_inner_edge_to_faint_at_the_outer_edge()
    {
        using var bmp = FlashGlow.Render(220, 120);

        // Straight down the top band at mid-width: row 0 is the outer edge, row Margin-1 the inner.
        var alphas = Enumerable.Range(0, M).Select(y => (int)bmp.GetPixel(110, y).Alpha).ToArray();

        alphas.Should().BeInAscendingOrder("the glow gets stronger towards the picture, never weaker");
        alphas[0].Should().BeLessThan(alphas[^1], "and the two ends are not the same value");
        alphas[^1].Should().BeGreaterThanOrEqualTo(200, "opaque at the inner edge");
        alphas[0].Should().BeInRange(1, 128, "faint - but drawn - at the outer edge");
    }

    [Fact]
    public void Render_fades_the_same_way_on_the_left_band()
    {
        using var bmp = FlashGlow.Render(220, 120);

        var alphas = Enumerable.Range(0, M).Select(x => (int)bmp.GetPixel(x, 60).Alpha).ToArray();

        alphas.Should().BeInAscendingOrder("the fade is the same on every side");
        alphas[0].Should().BeLessThan(alphas[^1]);
    }

    // ---- R2.8 — too small to frame -----------------------------------------------------------

    [Theory]
    [InlineData(2 * M, 2 * M + 1)]       // one pixel short on the width
    [InlineData(2 * M + 1, 2 * M)]       // and on the height
    [InlineData(2 * M, 2 * M)]
    [InlineData(1, 1)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void Render_refuses_a_size_with_no_room_for_an_inner_rect(int width, int height)
    {
        var act = () => FlashGlow.Render(width, height);

        act.Should().Throw<ArgumentOutOfRangeException>(
            $"{width}x{height} is smaller than the {2 * M + 1}x{2 * M + 1} the band needs");
    }

    [Fact]
    public void Render_accepts_the_smallest_size_that_still_has_an_inner_pixel()
    {
        using var bmp = FlashGlow.Render(2 * M + 1, 2 * M + 1);

        bmp.Width.Should().Be(2 * M + 1);
        bmp.GetPixel(M, M).Alpha.Should().Be(0, "the single inner pixel is the picture");
        bmp.GetPixel(0, 0).Alpha.Should().BeGreaterThan(0, "and it is framed");
    }

    [Fact]
    public void Render_of_a_window_rect_from_WindowRect_is_the_size_WindowRect_asked_for()
    {
        // The two halves have to agree: the bitmap UpdateLayeredWindow is handed must be exactly
        // the size of the window it is painted into, or Windows stretches or clips the glow.
        var captured = new ScreenRegion(0, 0, 200, 100);
        var window = FlashGlow.WindowRect(captured);

        using var bmp = RenderFor(captured);

        bmp.Width.Should().Be(window.Width);
        bmp.Height.Should().Be(window.Height);
    }
}
