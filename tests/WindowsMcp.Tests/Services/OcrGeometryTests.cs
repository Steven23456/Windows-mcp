using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-7: the pure mapping from the OCR engine's word rectangles (image pixels, doubles) to the
/// virtual-desktop pixels <c>click</c> takes. The design note's formula, for an engine rect
/// <c>(x, y, w, h)</c>, a capture origin <c>(ox, oy)</c> and the capture's <c>CoordinateScale</c>
/// <c>s</c>: edges <c>left = ox + round(x·s)</c>, <c>top = oy + round(y·s)</c>,
/// <c>right = ox + round((x+w)·s)</c>, <c>bottom = oy + round((y+h)·s)</c>, midpoint away from zero,
/// and bounds <c>(left, top, right − left, bottom − top)</c>. Mapping EDGES, not sizes, is what keeps
/// adjacent words adjacent. The checklist's two named cases are a non-zero origin and a negative
/// origin (a monitor left of or above the primary).
/// <para>
/// <see cref="OcrGeometry.ToLines"/> is the seam that keeps the line rules (a line's bounds are the
/// union of its words; a line with no words is dropped) testable with no language pack. The real
/// engine path is <c>OcrServiceRecognizeIntegrationTests</c>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class OcrGeometryTests
{
    // ---- ToVirtualDesktop -------------------------------------------------------------------

    [Fact]
    public void ToVirtualDesktop_at_origin_zero_and_scale_one_is_the_engine_rect()
    {
        OcrGeometry.ToVirtualDesktop(10, 20, 30, 40, 0, 0, 1.0).Should().Be(new Bounds(10, 20, 30, 40));
    }

    [Fact]
    public void ToVirtualDesktop_adds_a_non_zero_capture_origin()
    {
        // A region or a second monitor: image (0,0) is the capture's origin, not the desktop's.
        OcrGeometry.ToVirtualDesktop(10, 20, 30, 40, 100, 200, 1.0).Should().Be(new Bounds(110, 220, 30, 40));
    }

    [Theory]
    [InlineData(-1920, -40, -1910, -20)]    // a monitor left of AND above the primary
    [InlineData(-2560, 0, -2550, 20)]       // left only
    [InlineData(0, -1080, 10, -1060)]       // above only
    public void ToVirtualDesktop_keeps_a_negative_capture_origin_negative(int ox, int oy, int ex, int ey)
    {
        OcrGeometry.ToVirtualDesktop(10, 20, 30, 40, ox, oy, 1.0).Should().Be(new Bounds(ex, ey, 30, 40));
    }

    [Fact]
    public void ToVirtualDesktop_scales_the_image_rect_before_adding_the_origin()
    {
        // left = 100 + 10*2, top = 50 + 20*2, right = 100 + 40*2, bottom = 50 + 60*2.
        OcrGeometry.ToVirtualDesktop(10, 20, 30, 40, 100, 50, 2.0).Should().Be(new Bounds(120, 90, 60, 80));
    }

    [Fact]
    public void ToVirtualDesktop_scales_with_a_negative_origin_too()
    {
        // left = -1920 + round(8*1.25)=10, top = -40 + 10, right = -1920 + round(24*1.25)=30, bottom = -40 + round(16*1.25)=20.
        OcrGeometry.ToVirtualDesktop(8, 8, 16, 8, -1920, -40, 1.25).Should().Be(new Bounds(-1910, -30, 20, 10));
    }

    [Fact]
    public void ToVirtualDesktop_defaults_to_scale_one()
    {
        OcrGeometry.ToVirtualDesktop(10, 20, 30, 40, 5, 6).Should().Be(new Bounds(15, 26, 30, 40));
    }

    [Theory]
    // x = 2.5 rounds AWAY from zero to 3 (banker's rounding would give 2); right = 2.5 + 5 = 7.5 -> 8.
    [InlineData(2.5, 0.0, 5.0, 10.0, 3, 0, 5, 10)]
    // y = 0.5 -> 1 (banker's: 0); bottom = 0.5 + 3 = 3.5 -> 4 (banker's: 4).
    [InlineData(0.0, 0.5, 10.0, 3.0, 0, 1, 10, 3)]
    // x = 1.5 -> 2 and right = 4.5 -> 5 (banker's: 2 and 4, a width of 2 instead of 3).
    [InlineData(1.5, 0.0, 3.0, 1.0, 2, 0, 3, 1)]
    // non-midpoint fractions round to nearest: 10.4 -> 10, right 10.4 + 20.3 = 30.7 -> 31.
    [InlineData(10.4, 7.6, 20.3, 9.8, 10, 8, 21, 9)]
    public void ToVirtualDesktop_rounds_each_edge_to_nearest_with_midpoints_away_from_zero(
        double x, double y, double w, double h, int ex, int ey, int ew, int eh)
    {
        OcrGeometry.ToVirtualDesktop(x, y, w, h, 0, 0, 1.0).Should().Be(new Bounds(ex, ey, ew, eh));
    }

    [Fact]
    public void ToVirtualDesktop_midpoint_rounding_is_applied_after_scaling()
    {
        // x*s = 1*1.5 = 1.5 -> 2 (away from zero); right = (1+1)*1.5 = 3 -> 3. Width = 1.
        OcrGeometry.ToVirtualDesktop(1, 0, 1, 2, 0, 0, 1.5).Should().Be(new Bounds(2, 0, 1, 3));
    }

    [Fact]
    public void ToVirtualDesktop_keeps_adjacent_words_adjacent()
    {
        // Two words sharing an edge at x = 10.6. Mapping edges, A's right and B's left are the same
        // pixel (11). Mapping size (round(10.2) = 10 from left round(0.4) = 0) would leave a 1 px gap
        // between them that is not in the picture.
        var a = OcrGeometry.ToVirtualDesktop(0.4, 0, 10.2, 10, 500, 0, 1.0);
        var b = OcrGeometry.ToVirtualDesktop(10.6, 0, 5, 10, 500, 0, 1.0);

        (a.X + a.Width).Should().Be(b.X, "A's right edge is B's left edge in the image, so it must be on the desktop");
        a.Should().Be(new Bounds(500, 0, 11, 10));
        b.Should().Be(new Bounds(511, 0, 5, 10));
    }

    [Fact]
    public void ToVirtualDesktop_keeps_adjacent_words_adjacent_under_a_scale()
    {
        var a = OcrGeometry.ToVirtualDesktop(0, 0, 7.3, 4, -1000, 0, 1.75);
        var b = OcrGeometry.ToVirtualDesktop(7.3, 0, 6.1, 4, -1000, 0, 1.75);

        (a.X + a.Width).Should().Be(b.X);
    }

    [Theory]
    [InlineData(5.0, 5.0, 0.0, 0.0, 10, 10, 15, 15)]
    [InlineData(5.0, 5.0, 0.0, 0.0, -1920, -40, -1915, -35)]
    public void ToVirtualDesktop_of_a_zero_size_rect_is_a_zero_size_bounds_at_the_mapped_point(
        double x, double y, double w, double h, int ox, int oy, int ex, int ey)
    {
        OcrGeometry.ToVirtualDesktop(x, y, w, h, ox, oy, 1.0).Should().Be(new Bounds(ex, ey, 0, 0));
    }

    // ---- ToLines -----------------------------------------------------------------------------

    private static EngineWord W(string text, double x, double y, double w, double h) => new(text, x, y, w, h);

    [Fact]
    public void ToLines_maps_every_word_through_the_capture_origin()
    {
        var lines = OcrGeometry.ToLines(
            [new EngineLine("Hello world", [W("Hello", 10, 20, 50, 12), W("world", 66, 20, 48, 12)])],
            -1920, -40);

        lines.Should().ContainSingle();
        lines[0].Words.Select(w => w.Text).Should().Equal("Hello", "world");
        lines[0].Words[0].Bounds.Should().Be(new Bounds(-1910, -20, 50, 12));
        lines[0].Words[1].Bounds.Should().Be(new Bounds(-1854, -20, 48, 12));
    }

    [Fact]
    public void ToLines_line_bounds_are_the_union_of_its_mapped_words()
    {
        // Different heights and a lower baseline on the second word: the union is the tightest
        // rect holding both, not the first word's height and not the engine's line rect.
        var lines = OcrGeometry.ToLines(
            [new EngineLine("ab cd", [W("ab", 10, 10, 20, 10), W("cd", 40, 5, 10, 20)])],
            100, 200);

        lines[0].Bounds.Should().Be(new Bounds(110, 205, 40, 20));
    }

    [Fact]
    public void ToLines_line_bounds_of_a_single_word_line_are_that_words_bounds()
    {
        var lines = OcrGeometry.ToLines([new EngineLine("solo", [W("solo", 3, 4, 30, 9)])], 0, 0);

        lines[0].Bounds.Should().Be(lines[0].Words[0].Bounds);
    }

    [Fact]
    public void ToLines_drops_a_line_with_no_words()
    {
        var lines = OcrGeometry.ToLines(
            [
                new EngineLine("first", [W("first", 0, 0, 30, 10)]),
                new EngineLine("", []),
                new EngineLine("third", [W("third", 0, 40, 30, 10)]),
            ],
            0, 0);

        lines.Select(l => l.Text).Should().Equal(new[] { "first", "third" }, "an empty line has no rect to report");
    }

    [Fact]
    public void ToLines_of_nothing_is_an_empty_array()
    {
        OcrGeometry.ToLines([], 0, 0).Should().BeEmpty();
    }

    [Fact]
    public void ToLines_keeps_the_engines_line_text_and_order()
    {
        var lines = OcrGeometry.ToLines(
            [
                new EngineLine("one two", [W("one", 0, 0, 20, 10), W("two", 25, 0, 20, 10)]),
                new EngineLine("three", [W("three", 0, 20, 30, 10)]),
            ],
            0, 0);

        lines.Select(l => l.Text).Should().Equal("one two", "three");
    }

    [Fact]
    public void ToLines_applies_the_scale_to_words_and_so_to_the_line()
    {
        var lines = OcrGeometry.ToLines([new EngineLine("x y", [W("x", 10, 10, 10, 10), W("y", 30, 10, 10, 10)])], 0, 0, 2.0);

        lines[0].Words[0].Bounds.Should().Be(new Bounds(20, 20, 20, 20));
        lines[0].Words[1].Bounds.Should().Be(new Bounds(60, 20, 20, 20));
        lines[0].Bounds.Should().Be(new Bounds(20, 20, 60, 20));
    }

    // ======== E-7 review F2 — rotating a deskewed engine rect back into image axes ==============
    //
    // Evidence (OcrServiceRecognizeIntegrationTests.RecognizeAsync_word_centres_lie_on_the_glyphs_of_rotated_text):
    // for text drawn rotated, Windows.Media.Ocr reports TextAngle ≈ the drawn rotation and the word
    // rects in the DESKEWED frame. Rotating a rect's four corners about the IMAGE centre by
    // +TextAngle, in y-down image pixels (x' = cx + dx·cosθ − dy·sinθ, y' = cy + dx·sinθ + dy·cosθ;
    // clockwise on screen for a positive angle), and taking their axis-aligned box puts the centre
    // back on the glyphs. Rotation happens in image pixels, BEFORE the scale and the origin.
    //
    // Fixture: a 100x40 rect at (200,280) in a 1000x600 image, centre (500,300).

    [Fact]
    public void ToVirtualDesktop_at_angle_zero_is_unchanged_whatever_the_image_size()
    {
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 0, 0, 1.0, angleDegrees: 0, imageWidth: 1000, imageHeight: 600)
            .Should().Be(new Bounds(200, 280, 100, 40));
    }

    [Fact]
    public void ToVirtualDesktop_rotates_a_positive_angle_clockwise_about_the_image_centre()
    {
        // dx in -300..-200, dy in -20..20: x' = 500 - dy, y' = 300 + dx.
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 0, 0, 1.0, angleDegrees: 90, imageWidth: 1000, imageHeight: 600)
            .Should().Be(new Bounds(480, 0, 40, 100));
    }

    [Fact]
    public void ToVirtualDesktop_rotates_a_negative_angle_the_other_way()
    {
        // x' = 500 + dy, y' = 300 - dx.
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 0, 0, 1.0, angleDegrees: -90, imageWidth: 1000, imageHeight: 600)
            .Should().Be(new Bounds(480, 500, 40, 100));
    }

    [Fact]
    public void ToVirtualDesktop_rotates_about_the_image_centre_not_the_virtual_desktop_origin()
    {
        // Same rotation as above, then the capture origin of a monitor left of and above the primary.
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, -1920, -40, 1.0, angleDegrees: 90, imageWidth: 1000, imageHeight: 600)
            .Should().Be(new Bounds(-1440, -40, 40, 100));
    }

    [Fact]
    public void ToVirtualDesktop_rotates_in_image_pixels_before_the_scale()
    {
        // Image-space result (480,0,40,100) scaled by 2 and moved to (10,20).
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 10, 20, 2.0, angleDegrees: 90, imageWidth: 1000, imageHeight: 600)
            .Should().Be(new Bounds(970, 20, 80, 200));
    }

    [Fact]
    public void ToVirtualDesktop_at_a_small_angle_reports_the_axis_aligned_box_of_the_rotated_corners()
    {
        // θ = 15°: rotated corners x 205.05..311.99, y 203.04..267.56 (hand-computed), centre
        // (258.5, 235.3). ±1 per edge allows rounding the corners or the edges.
        var b = OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 1000, -500, 1.0, angleDegrees: 15, imageWidth: 1000, imageHeight: 600);

        b.X.Should().BeCloseTo(1205, 1);
        b.Y.Should().BeCloseTo(-297, 1);
        (b.X + b.Width).Should().BeCloseTo(1312, 1);
        (b.Y + b.Height).Should().BeCloseTo(-232, 1);
    }

    [Fact]
    public void ToLines_rotates_every_word_and_the_line_is_the_union_of_the_rotated_words()
    {
        var lines = OcrGeometry.ToLines(
            [new EngineLine("a b", [W("a", 200, 280, 100, 40), W("b", 320, 280, 80, 40)])],
            0, 0, 1.0, angleDegrees: 90, imageWidth: 1000, imageHeight: 600);

        lines[0].Words[0].Bounds.Should().Be(new Bounds(480, 0, 40, 100));
        lines[0].Words[1].Bounds.Should().Be(new Bounds(480, 120, 40, 80));
        lines[0].Bounds.Should().Be(new Bounds(480, 0, 40, 200));
    }

    [Fact]
    public void ToVirtualDesktop_of_a_long_word_at_15_degrees_is_a_tall_upright_box_centred_on_the_rotated_word()
    {
        // Review round 3 (R4): a 200x20 word at (100,100) in a 1000x600 image, 15°. Its rotated
        // centre is (259.40, 38.82) (hand-computed from centre (200,110) about (500,300)); the upright
        // box is 200·sin15 + 20·cos15 ≈ 71 px high — much taller than the word, centre unchanged.
        var b = OcrGeometry.ToVirtualDesktop(100, 100, 200, 20, 0, 0, 1.0, angleDegrees: 15, imageWidth: 1000, imageHeight: 600);

        b.Height.Should().BeGreaterThanOrEqualTo(60);
        (b.X + b.Width / 2.0).Should().BeApproximately(259.40, 1.0);
        (b.Y + b.Height / 2.0).Should().BeApproximately(38.82, 1.0);
    }

    // ---- review round 3 (R3) — a non-finite angle, and an angle with no image size ----------------

    [Theory]
    [InlineData(double.NaN, 1000, 600)]
    [InlineData(double.PositiveInfinity, 1000, 600)]
    [InlineData(double.NegativeInfinity, 1000, 600)]
    [InlineData(double.NaN, 0, 0)]                 // non-finite is "no angle", so no image size is needed
    [InlineData(double.PositiveInfinity, 0, 0)]
    public void ToVirtualDesktop_maps_a_non_finite_angle_exactly_as_angle_zero(double angle, int imageWidth, int imageHeight)
    {
        OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 10, 20, 1.0, angleDegrees: angle, imageWidth: imageWidth, imageHeight: imageHeight)
            .Should().Be(new Bounds(210, 300, 100, 40));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ToLines_maps_a_non_finite_angle_exactly_as_angle_zero(double angle)
    {
        var lines = OcrGeometry.ToLines([new EngineLine("a b", [W("a", 10, 10, 20, 10), W("b", 40, 5, 10, 20)])],
            100, 200, 1.0, angleDegrees: angle, imageWidth: 1000, imageHeight: 600);

        lines[0].Words[0].Bounds.Should().Be(new Bounds(110, 210, 20, 10));
        lines[0].Words[1].Bounds.Should().Be(new Bounds(140, 205, 10, 20));
        lines[0].Bounds.Should().Be(new Bounds(110, 205, 40, 20));
    }

    [Theory]
    [InlineData(15.0, 0, 600)]
    [InlineData(15.0, 1000, 0)]
    [InlineData(15.0, -1, 600)]
    [InlineData(-5.0, 0, 0)]   // the defaults: must not silently rotate about the image origin
    public void ToVirtualDesktop_with_an_angle_but_no_image_size_is_an_ArgumentException(double angle, int imageWidth, int imageHeight)
    {
        var act = () => OcrGeometry.ToVirtualDesktop(200, 280, 100, 40, 0, 0, 1.0, angleDegrees: angle, imageWidth: imageWidth, imageHeight: imageHeight);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToLines_with_an_angle_but_no_image_size_is_an_ArgumentException()
    {
        var act = () => OcrGeometry.ToLines([new EngineLine("a", [W("a", 10, 10, 20, 10)])], 0, 0, 1.0, angleDegrees: 15);

        act.Should().Throw<ArgumentException>();
    }
}
