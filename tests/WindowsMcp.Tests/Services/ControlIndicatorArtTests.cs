using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// S-11 (R4): what the indicator looks like and where its two windows go, as pure SkiaSharp and
/// arithmetic - no window, no desktop (the <see cref="FlashGlowTests"/> precedent). The real windows
/// are <see cref="ControlIndicatorTests"/>; whether they are painted and kept out of captures is
/// <see cref="ControlIndicatorDesktopTests"/>.
/// <para>
/// <c>SKBitmap.GetPixel</c> returns the UN-premultiplied colour, so colour checks use it; the
/// premultiplication check reads the raw BGRA bytes, where "no channel above alpha" is the rule
/// <c>UpdateLayeredWindow(ULW_ALPHA)</c> needs.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ControlIndicatorArtTests
{
    private const string Text = "AI is controlling mouse & keyboard · Ctrl+Alt+Shift+Backspace to take over";

    private static double Luminance(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

    private static bool IsBlue(SKColor c) => c.Blue > c.Red + 40 && c.Blue > c.Green;

    /// <summary>Every pixel of a Bgra8888 bitmap, raw: B, G, R, A.</summary>
    private static IEnumerable<(int X, int Y, byte B, byte G, byte R, byte A)> RawPixels(SKBitmap bmp)
    {
        var bytes = bmp.Bytes;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var i = y * bmp.RowBytes + x * 4;
                yield return (x, y, bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
            }
    }

    private static void ShouldBePremultipliedBgra(SKBitmap bmp, string what)
    {
        bmp.ColorType.Should().Be(SKColorType.Bgra8888, $"{what}: UpdateLayeredWindow takes 32-bit BGRA");
        bmp.AlphaType.Should().Be(SKAlphaType.Premul, $"{what}: ULW_ALPHA blends premultiplied pixels");
        var bad = RawPixels(bmp).Where(p => p.B > p.A || p.G > p.A || p.R > p.A).Take(5).ToArray();
        bad.Should().BeEmpty($"{what}: a premultiplied channel can never exceed its alpha - a straight-alpha bitmap "
            + "puts a bright fringe around every antialiased edge");
    }

    // ---- constants -----------------------------------------------------------------------------

    [Fact]
    public void The_geometry_constants_are_the_design_notes()
    {
        ControlIndicatorArt.RingDiameter.Should().Be(56);
        ControlIndicatorArt.BannerHeight.Should().Be(32);
        ControlIndicatorArt.BannerTopMargin.Should().Be(12);
    }

    [Fact]
    public void The_ring_colour_is_blue_so_it_is_never_mistaken_for_the_orange_capture_flash()
    {
        IsBlue(ControlIndicatorArt.RingColor).Should().BeTrue("A-14's capture flash is orange; the indicator is blue");
    }

    // ---- the cursor ring -------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 56)]
    [InlineData(1.25, 70)]
    [InlineData(1.5, 84)]
    [InlineData(1.75, 98)]
    [InlineData(2.0, 112)]
    [InlineData(1.1, 62)]      // 61.6 rounds up
    [InlineData(0.9, 50)]      // 50.4 rounds down
    public void CursorRing_is_square_and_its_side_scales_with_the_dpi(double scale, int side)
    {
        using var ring = ControlIndicatorArt.CursorRing(scale);

        ring.Width.Should().Be(side, $"round(56 x {scale})");
        ring.Height.Should().Be(side, "the ring is square");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CursorRing_is_premultiplied_bgra(double scale)
    {
        using var ring = ControlIndicatorArt.CursorRing(scale);

        ShouldBePremultipliedBgra(ring, $"the ring at {scale}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CursorRing_leaves_its_centre_transparent_so_the_pointer_shows_through(double scale)
    {
        using var ring = ControlIndicatorArt.CursorRing(scale);
        var c = ring.Width / 2;
        var half = ring.Width / 8;

        for (var dy = -half; dy <= half; dy++)
            for (var dx = -half; dx <= half; dx++)
                ring.GetPixel(c + dx, c + dy).Alpha.Should().Be(0,
                    $"({c + dx},{c + dy}) is under the pointer; anything but zero alpha tints the cursor");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CursorRing_has_a_blue_band_on_all_four_sides(double scale)
    {
        using var ring = ControlIndicatorArt.CursorRing(scale);
        var s = ring.Width;
        var c = s / 2;

        // Straight in from each edge along the centre lines: somewhere between the edge and the
        // centre there is a solidly drawn blue pixel.
        bool BandOn(IEnumerable<(int X, int Y)> path) =>
            path.Select(p => ring.GetPixel(p.X, p.Y)).Any(px => px.Alpha >= 128 && IsBlue(px));

        BandOn(Enumerable.Range(0, c).Select(y => (c, y))).Should().BeTrue("top");
        BandOn(Enumerable.Range(c, s - c).Select(y => (c, y))).Should().BeTrue("bottom");
        BandOn(Enumerable.Range(0, c).Select(x => (x, c))).Should().BeTrue("left");
        BandOn(Enumerable.Range(c, s - c).Select(x => (x, c))).Should().BeTrue("right");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CursorRing_is_round_so_its_corners_are_transparent(double scale)
    {
        // A ring, not a square frame: the bitmap's corners are outside the circle.
        using var ring = ControlIndicatorArt.CursorRing(scale);
        var last = ring.Width - 1;

        foreach (var (x, y) in new[] { (0, 0), (last, 0), (0, last), (last, last) })
            ring.GetPixel(x, y).Alpha.Should().Be(0, $"({x},{y}) is a corner of the square, outside the ring");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void CursorRing_refuses_a_scale_that_is_not_positive(double scale)
    {
        var act = () => ControlIndicatorArt.CursorRing(scale);

        act.Should().Throw<ArgumentOutOfRangeException>($"{scale} is not a DPI scale");
    }

    // ---- the banner ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 32)]
    [InlineData(1.25, 40)]
    [InlineData(1.5, 48)]
    [InlineData(2.0, 64)]
    [InlineData(1.1, 35)]      // 35.2 rounds down
    public void Banner_height_scales_with_the_dpi(double scale, int height)
    {
        using var banner = ControlIndicatorArt.Banner(Text, scale);

        banner.Height.Should().Be(height, $"round(32 x {scale})");
    }

    [Fact]
    public void Banner_is_wider_than_it_is_tall_for_real_text()
    {
        using var banner = ControlIndicatorArt.Banner(Text, 1.0);

        banner.Width.Should().BeGreaterThan(banner.Height * 4, "a one-line pill holding a sentence");
    }

    [Fact]
    public void Banner_width_grows_with_the_text()
    {
        using var shortOne = ControlIndicatorArt.Banner("AI", 1.0);
        using var longOne = ControlIndicatorArt.Banner(Text, 1.0);

        longOne.Width.Should().BeGreaterThan(shortOne.Width, "the banner is sized to its text, not a fixed box");
        longOne.Height.Should().Be(shortOne.Height, "the height is the DPI's, not the text's");
    }

    [Fact]
    public void Banner_width_scales_with_the_dpi_too()
    {
        using var one = ControlIndicatorArt.Banner(Text, 1.0);
        using var two = ControlIndicatorArt.Banner(Text, 2.0);

        two.Width.Should().BeGreaterThan((int)(one.Width * 1.5), "the text is drawn at the monitor's DPI, not at 96");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Banner_is_premultiplied_bgra(double scale)
    {
        using var banner = ControlIndicatorArt.Banner(Text, scale);

        ShouldBePremultipliedBgra(banner, $"the banner at {scale}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Banner_is_a_pill_so_its_corners_are_transparent(double scale)
    {
        using var banner = ControlIndicatorArt.Banner(Text, scale);
        var (w, h) = (banner.Width - 1, banner.Height - 1);

        foreach (var (x, y) in new[] { (0, 0), (w, 0), (0, h), (w, h) })
            banner.GetPixel(x, y).Alpha.Should().Be(0, $"({x},{y}) is outside the rounded ends");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Banner_has_a_dark_fill(double scale)
    {
        // A row near the top, between the rounded ends: above any glyph, inside the pill.
        using var banner = ControlIndicatorArt.Banner(Text, scale);
        var y = (int)Math.Round(3 * scale);
        var row = Enumerable.Range(banner.Height, banner.Width - 2 * banner.Height)
            .Select(x => banner.GetPixel(x, y)).ToArray();

        row.Should().NotBeEmpty();
        row.Count(p => p.Alpha >= 160 && Luminance(p) <= 100).Should().BeGreaterThanOrEqualTo((int)(row.Length * 0.9),
            "the pill is a dark, mostly opaque fill so light text reads on any wallpaper");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Banner_has_light_text_on_the_dark_fill(double scale)
    {
        using var banner = ControlIndicatorArt.Banner(Text, scale);

        var light = 0;
        for (var y = 0; y < banner.Height; y++)
            for (var x = 0; x < banner.Width; x++)
            {
                var p = banner.GetPixel(x, y);
                if (p.Alpha >= 200 && Luminance(p) >= 180) light++;
            }

        light.Should().BeGreaterThan((int)(30 * scale * scale), "the text is drawn, and drawn light");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void Banner_refuses_a_scale_that_is_not_positive(double scale)
    {
        var act = () => ControlIndicatorArt.Banner(Text, scale);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- the banner text -------------------------------------------------------------------------

    [Fact]
    public void BannerText_with_a_hotkey_says_how_to_take_over()
    {
        ControlIndicatorArt.BannerText("Ctrl+Alt+Shift+Backspace")
            .Should().Be("AI is controlling mouse & keyboard · Ctrl+Alt+Shift+Backspace to take over");
    }

    [Fact]
    public void BannerText_uses_whatever_combination_it_is_given()
    {
        ControlIndicatorArt.BannerText("Shift+Win+F12")
            .Should().Be("AI is controlling mouse & keyboard · Shift+Win+F12 to take over");
    }

    [Fact]
    public void BannerText_without_a_hotkey_drops_the_takeover_hint()
    {
        // The combination could not be registered (another app owns it): promising a key that does
        // nothing would be worse than saying nothing.
        ControlIndicatorArt.BannerText(null).Should().Be("AI is controlling mouse & keyboard");
    }

    // ---- where the windows go --------------------------------------------------------------------

    [Theory]
    [InlineData(100, 200, 56, 72, 172)]
    [InlineData(0, 0, 56, -28, -28)]                 // the pointer at the origin: the ring hangs off it
    [InlineData(-1900, -40, 84, -1942, -82)]         // a monitor left of and above the primary
    [InlineData(500, 500, 57, 472, 472)]             // an odd side: the pointer is the exact middle pixel
    public void RingRect_is_centred_on_the_pointer(int x, int y, int side, int left, int top)
    {
        ControlIndicatorArt.RingRect(x, y, side).Should().Be(new ScreenRegion(left, top, side, side));
    }

    [Fact]
    public void BannerRect_is_horizontally_centred_at_the_top_of_the_work_area()
    {
        var work = new ScreenRegion(0, 0, 1920, 1040);

        ControlIndicatorArt.BannerRect(work, 400, 32, 1.0)
            .Should().Be(new ScreenRegion(760, 12, 400, 32));
    }

    [Fact]
    public void BannerRect_respects_a_work_area_that_does_not_start_at_the_monitor_top()
    {
        // A taskbar docked at the top pushes the work area down; the banner goes under it.
        var work = new ScreenRegion(0, 48, 1920, 1032);

        ControlIndicatorArt.BannerRect(work, 400, 32, 1.0)
            .Should().Be(new ScreenRegion(760, 60, 400, 32));
    }

    [Fact]
    public void BannerRect_honours_negative_monitor_origins_and_scales_the_margin()
    {
        var work = new ScreenRegion(-2560, -300, 2560, 1400);

        ControlIndicatorArt.BannerRect(work, 600, 48, 1.5)
            .Should().Be(new ScreenRegion(-2560 + 980, -300 + 18, 600, 48), "round(12 x 1.5) = 18 below the work area's top");
    }

    [Fact]
    public void BannerRect_on_an_odd_remainder_rounds_toward_the_left()
    {
        var work = new ScreenRegion(0, 0, 1920, 1040);

        ControlIndicatorArt.BannerRect(work, 401, 32, 1.0).X.Should().Be(759, "(1920 - 401) / 2 = 759.5, integer division");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void BannerRect_refuses_a_scale_that_is_not_positive(double scale)
    {
        var act = () => ControlIndicatorArt.BannerRect(new ScreenRegion(0, 0, 1920, 1040), 400, 32, scale);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
