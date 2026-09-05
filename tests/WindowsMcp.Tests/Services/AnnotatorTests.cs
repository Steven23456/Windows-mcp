using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-6 (R2): the annotator's pure core. The palette, the virtual-desktop → image mapping, the
/// label placement, the text contrast and the drawing itself are all SkiaSharp-only, so every one
/// of them is proven here on a synthetic bitmap with no capture and no desktop (roadmap C10) —
/// <c>ScreenshotAnnotateTests</c> proves the encode wiring and
/// <c>ScreenshotAnnotateDesktopTests</c> the live capture.
/// </summary>
[Trait("Category", "Unit")]
public class AnnotatorTests
{
    private static readonly SKColor Grey = new(128, 128, 128, 255);

    /// <summary>A mid-grey canvas: nothing the annotator draws is mid-grey, so any change shows.</summary>
    private static SKBitmap MidGrey(int width = 200, int height = 100)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(Grey);
        return bmp;
    }

    /// <summary>The whole of a 200x100 bitmap, captured at the origin at scale 1.</summary>
    private static readonly ScreenRegion Origin200x100 = new(0, 0, 200, 100);

    private static AnnotationBox Box(string label, int x, int y, int w, int h) =>
        new(label, new Bounds(x, y, w, h));

    // ---- R2.1 — ColorFor: a fixed palette of 12, cycling --------------------------------------

    [Fact]
    public void ColorFor_gives_twelve_distinct_colours()
    {
        var palette = Enumerable.Range(0, 12).Select(Annotator.ColorFor).ToArray();

        palette.Distinct().Should().HaveCount(12,
            "two elements that share a colour cannot be told apart in the picture");
    }

    [Fact]
    public void ColorFor_is_opaque()
    {
        // A translucent box would take the colour of whatever it is drawn over, which is exactly
        // the screenshot the model is trying to read.
        Enumerable.Range(0, 12).Select(Annotator.ColorFor).Should().OnlyContain(c => c.Alpha == 255);
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(13, 1)]
    [InlineData(23, 11)]
    [InlineData(24, 0)]
    public void ColorFor_cycles_after_twelve(int index, int equivalent)
    {
        Annotator.ColorFor(index).Should().Be(Annotator.ColorFor(equivalent),
            "a snapshot can hand out more than twelve labels; the palette repeats rather than running out");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    public void ColorFor_rejects_a_negative_index(int index)
    {
        // Ambiguity resolved (flagged in the RED report): a negative index is a caller bug, not a
        // colour — the boxes are enumerated from 0, so it can only come from broken arithmetic.
        var act = () => Annotator.ColorFor(index);

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("index");
    }

    // ---- R2.2 — ToImage: virtual-desktop rect to image pixels ---------------------------------

    [Fact]
    public void ToImage_at_the_origin_at_scale_one_is_the_identity()
    {
        Annotator.ToImage(new Bounds(10, 10, 50, 30), Origin200x100, 1.0, 200, 100)
            .Should().Be(new SKRectI(10, 10, 60, 40));
    }

    [Fact]
    public void ToImage_subtracts_the_captured_rects_origin()
    {
        // A capture of the second monitor starts at (1920,0): an element at (1930,10) is 10 px in.
        Annotator.ToImage(new Bounds(1930, 10, 50, 30), new ScreenRegion(1920, 0, 200, 100), 1.0, 200, 100)
            .Should().Be(new SKRectI(10, 10, 60, 40));
    }

    [Fact]
    public void ToImage_divides_by_the_coordinate_scale()
    {
        // A-9 downscaled a 400x200 capture to 200x100, so every element coordinate halves.
        Annotator.ToImage(new Bounds(20, 20, 40, 20), new ScreenRegion(0, 0, 400, 200), 2.0, 200, 100)
            .Should().Be(new SKRectI(10, 10, 30, 20));
    }

    [Fact]
    public void ToImage_rounds_half_away_from_zero_not_to_even()
    {
        // 5/2 = 2.5 and 15/2 = 7.5: banker's rounding (Math.Round's default) would give 2 and 8,
        // which puts the box half a pixel left of where the element actually is.
        Annotator.ToImage(new Bounds(5, 5, 10, 10), new ScreenRegion(0, 0, 400, 200), 2.0, 200, 100)
            .Should().Be(new SKRectI(3, 3, 8, 8));
    }

    [Fact]
    public void ToImage_clips_a_box_that_hangs_off_the_right_and_bottom()
    {
        Annotator.ToImage(new Bounds(180, 90, 50, 50), Origin200x100, 1.0, 200, 100)
            .Should().Be(new SKRectI(180, 90, 200, 100), "the part that is in the picture is still drawn");
    }

    [Fact]
    public void ToImage_clips_a_box_that_hangs_off_the_left_and_top()
    {
        Annotator.ToImage(new Bounds(-20, -20, 40, 40), Origin200x100, 1.0, 200, 100)
            .Should().Be(new SKRectI(0, 0, 20, 20));
    }

    [Theory]
    [InlineData(500, 500, 10, 10)]     // far past the bottom-right
    [InlineData(-100, 10, 50, 10)]     // ends at x = -50, entirely left of the image
    [InlineData(200, 10, 50, 10)]      // starts exactly at the right edge: zero overlap
    [InlineData(10, 100, 50, 10)]      // starts exactly at the bottom edge: zero overlap
    public void ToImage_is_null_when_the_box_does_not_overlap_the_image(int x, int y, int w, int h)
    {
        Annotator.ToImage(new Bounds(x, y, w, h), Origin200x100, 1.0, 200, 100)
            .Should().BeNull("nothing to draw is not the same as something to draw at the edge");
    }

    [Fact]
    public void ToImage_keeps_a_box_flush_against_the_edge()
    {
        Annotator.ToImage(new Bounds(190, 0, 10, 10), Origin200x100, 1.0, 200, 100)
            .Should().Be(new SKRectI(190, 0, 200, 10));
    }

    [Fact]
    public void ToImage_widens_a_box_that_rounds_away_to_nothing_inside_the_image()
    {
        // A 1 px element on a 10x downscale is 0.1 px: rounding both edges to 10 would draw
        // nothing at all, and an element the model cannot see is an element it cannot click.
        Annotator.ToImage(new Bounds(100, 100, 1, 1), new ScreenRegion(0, 0, 2000, 1000), 10.0, 200, 100)
            .Should().Be(new SKRectI(10, 10, 11, 11));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void ToImage_treats_a_non_positive_coordinate_scale_as_one(double scale)
    {
        // Defensive, not a documented mode: ScaleMath.Fit only ever yields a positive scale. But
        // dividing by 0 gives infinity, and (int)Math.Round(infinity) is int.MinValue — a box at a
        // nonsense coordinate rather than no box at all. The guard has to be the identity.
        Annotator.ToImage(new Bounds(10, 10, 50, 30), Origin200x100, scale, 200, 100)
            .Should().Be(new SKRectI(10, 10, 60, 40));
    }

    // ---- R2.3 — ChipRect: the label chip, clamped into the image ------------------------------

    [Fact]
    public void ChipRect_sits_just_above_the_boxs_top_left()
    {
        Annotator.ChipRect(new SKRectI(50, 40, 90, 80), 30, 14, 200, 100)
            .Should().Be(new SKRectI(50, 26, 80, 40), "the chip labels the box without covering it");
    }

    [Fact]
    public void ChipRect_moves_inside_the_box_when_there_is_no_room_above()
    {
        // A window title bar at the top of the screen has no pixels above it to label into.
        Annotator.ChipRect(new SKRectI(50, 5, 90, 40), 30, 14, 200, 100)
            .Should().Be(new SKRectI(50, 5, 80, 19));
    }

    [Fact]
    public void ChipRect_shifts_left_so_it_never_leaves_the_right_edge()
    {
        Annotator.ChipRect(new SKRectI(180, 40, 200, 80), 30, 14, 200, 100)
            .Should().Be(new SKRectI(170, 26, 200, 40));
    }

    [Fact]
    public void ChipRect_clamps_to_the_origin_when_the_image_is_smaller_than_the_chip()
    {
        // Degenerate but reachable: a 20x10 capture. Nothing fits, so the chip starts at (0,0)
        // and the canvas clips the overflow — it must not be pushed to a negative origin.
        Annotator.ChipRect(new SKRectI(0, 0, 5, 5), 30, 14, 20, 10)
            .Should().Be(new SKRectI(0, 0, 30, 14));
    }

    // ---- R2.4 — UseDarkText: readable label text on any palette colour ------------------------

    [Theory]
    [InlineData(0, 0, 0, false)]          // black: white text
    [InlineData(255, 255, 255, true)]     // white: black text
    [InlineData(255, 255, 0, true)]       // yellow is bright, whatever its hue
    [InlineData(0, 0, 128, false)]        // navy is dark, and blue counts least toward luminance
    public void UseDarkText_follows_luminance_not_hue(byte r, byte g, byte b, bool dark)
    {
        Annotator.UseDarkText(new SKColor(r, g, b, 255)).Should().Be(dark);
    }

    [Fact]
    public void UseDarkText_picks_a_readable_colour_for_every_palette_entry()
    {
        // The palette is the only input this is ever called with in production; a label that
        // disappears into its own chip is the failure mode.
        foreach (var colour in Enumerable.Range(0, 12).Select(Annotator.ColorFor))
        {
            var luminance = (0.2126 * colour.Red + 0.7152 * colour.Green + 0.0722 * colour.Blue) / 255.0;
            Annotator.UseDarkText(colour).Should().Be(luminance > 0.5,
                "colour {0} has relative luminance {1}", colour, luminance);
        }
    }

    // ---- R2.5 — Draw: the boxes ---------------------------------------------------------------

    /// <summary>Every pixel of <paramref name="bmp"/> that is no longer the background.</summary>
    private static int MarkedCount(SKBitmap bmp)
    {
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != Grey) count++;
        return count;
    }

    private static bool Contains(SKBitmap bmp, SKColor colour)
    {
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) == colour) return true;
        return false;
    }

    /// <summary>
    /// How many pixels of <paramref name="colour"/> lie on the box's top edge — the three rows
    /// around it, so a 2 px stroke counts however Skia centres it, and the ends are trimmed so a
    /// corner or a chip cannot be mistaken for the edge.
    /// </summary>
    private static int TopEdge(SKBitmap bmp, SKRectI rect, SKColor colour)
    {
        var count = 0;
        for (var y = rect.Top - 1; y <= rect.Top + 1; y++)
        {
            if (y < 0 || y >= bmp.Height) continue;
            for (var x = rect.Left + 3; x < rect.Right - 3; x++)
                if (bmp.GetPixel(x, y) == colour) count++;
        }
        return count;
    }

    /// <summary>Pixels of <paramref name="colour"/> in the band above the box, where the chip goes.</summary>
    private static int ChipBand(SKBitmap bmp, SKRectI rect, SKColor colour)
    {
        var count = 0;
        for (var y = Math.Max(0, rect.Top - 24); y <= rect.Top - 3; y++)
            for (var x = rect.Left; x < Math.Min(bmp.Width, rect.Left + 40); x++)
                if (bmp.GetPixel(x, y) == colour) count++;
        return count;
    }

    [Fact]
    public void Draw_strokes_the_box_in_the_palette_colour_of_its_index()
    {
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp, [Box("el_1", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(1);
        TopEdge(bmp, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(0)).Should().BeGreaterThan(2,
            "the box's top edge is drawn in the first palette colour");
        bmp.GetPixel(40, 60).Should().Be(Grey,
            "the box is a stroke, not a fill — the screenshot underneath is the point of the call");
    }

    [Fact]
    public void Draw_puts_a_label_chip_in_the_same_colour_above_the_box()
    {
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [Box("el_1", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        ChipBand(bmp, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(0)).Should().BeGreaterThan(20,
            "the chip is a filled rectangle in the box's colour, so the label is tied to the box");
    }

    [Fact]
    public void Draw_gives_the_second_box_a_different_colour()
    {
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp,
            [Box("el_1", 10, 40, 40, 40), Box("el_2", 120, 40, 40, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(2);
        Contains(bmp, Annotator.ColorFor(0)).Should().BeTrue();
        Contains(bmp, Annotator.ColorFor(1)).Should().BeTrue();
    }

    [Fact]
    public void Draw_skips_a_box_that_is_not_in_the_picture_but_keeps_its_palette_index()
    {
        // The index is the box's position in the list, not a count of what was drawn: the colour
        // has to agree with the label the text block lists, and that list is not re-numbered.
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp,
            [Box("el_1", 500, 500, 40, 40), Box("el_2", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(1, "only the box that landed on the image counts");
        Contains(bmp, Annotator.ColorFor(0)).Should().BeFalse("nothing was drawn for the off-image box");
        TopEdge(bmp, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(1)).Should().BeGreaterThan(2);
    }

    [Fact]
    public void Draw_paints_the_boxes_in_order_so_a_later_one_covers_an_earlier_one()
    {
        // Two elements at the same place (a button inside its own container) must not produce a
        // half-and-half edge whose colour matches neither label.
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp,
            [Box("el_1", 10, 40, 60, 40), Box("el_2", 10, 40, 60, 40)], Origin200x100, 1.0, null);

        drawn.Should().Be(2);
        TopEdge(bmp, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(1)).Should().BeGreaterThan(2);
        Contains(bmp, Annotator.ColorFor(0)).Should().BeFalse("the later box is drawn over the earlier one");
    }

    [Fact]
    public void Draw_maps_the_boxes_through_the_coordinate_scale()
    {
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp, [Box("el_1", 20, 80, 120, 80)], new ScreenRegion(0, 0, 400, 200), 2.0, null);

        drawn.Should().Be(1);
        TopEdge(bmp, new SKRectI(10, 40, 70, 80), Annotator.ColorFor(0)).Should().BeGreaterThan(2,
            "a virtual-desktop box of (20,80,120,80) on a 2x downscale is (10,40)-(70,80) in the image");
    }

    [Fact]
    public void Draw_returns_zero_and_leaves_the_bitmap_alone_with_nothing_to_draw()
    {
        using var bmp = MidGrey();

        var drawn = Annotator.Draw(bmp, [], Origin200x100, 1.0, null);

        drawn.Should().Be(0);
        MarkedCount(bmp).Should().Be(0, "an unannotated capture must come back exactly as it was captured");
    }

    // ---- R2.6 — Draw: the grid ----------------------------------------------------------------

    /// <summary>The x positions on row <paramref name="y"/> that the annotator changed.</summary>
    private static List<int> MarkedColumns(SKBitmap bmp, int y, int from, int to)
    {
        var marked = new List<int>();
        for (var x = from; x <= to; x++)
            if (bmp.GetPixel(x, y) != Grey) marked.Add(x);
        return marked;
    }

    /// <summary>The y positions in column <paramref name="x"/> that the annotator changed.</summary>
    private static List<int> MarkedRows(SKBitmap bmp, int x, int from, int to)
    {
        var marked = new List<int>();
        for (var y = from; y <= to; y++)
            if (bmp.GetPixel(x, y) != Grey) marked.Add(y);
        return marked;
    }

    // A 1 px line at x = 50 may be painted on column 50 or split across 49/50 depending on how the
    // implementation centres it; either is a line at the right place, and neither may bleed further.
    private static readonly int[] AllowedVertical = [49, 50, 99, 100, 149, 150];

    [Fact]
    public void Draw_grid_puts_a_line_at_every_interior_division()
    {
        using var bmp = MidGrey();   // 200x100

        var drawn = Annotator.Draw(bmp, [], Origin200x100, 1.0, new GridSpec(4, 2));

        drawn.Should().Be(0, "the grid is not a box and is not counted");
        // Row 70 is below the top-edge captions and away from the horizontal line at y = 50.
        var columns = MarkedColumns(bmp, y: 70, from: 30, to: 199);
        columns.Should().BeSubsetOf(AllowedVertical, "4 columns means interior lines at x = 50, 100 and 150");
        columns.Should().Contain(x => x == 49 || x == 50)
            .And.Contain(x => x == 99 || x == 100)
            .And.Contain(x => x == 149 || x == 150);
        // Column 70 is away from every vertical line and past the left-edge caption.
        var rows = MarkedRows(bmp, x: 70, from: 20, to: 99);
        rows.Should().NotBeEmpty("2 rows means one interior line at y = 50")
            .And.BeSubsetOf(new[] { 49, 50 });
    }

    [Fact]
    public void Draw_grid_lines_are_grey_not_a_palette_colour()
    {
        // Semi-transparent grey over a grey background stays grey: the grid must not be mistaken
        // for a box, and must not repaint the screenshot underneath it.
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [], Origin200x100, 1.0, new GridSpec(2, 0));

        var columns = MarkedColumns(bmp, y: 70, from: 30, to: 199);
        columns.Should().NotBeEmpty("the grid line is what this test samples");
        var pixel = bmp.GetPixel(columns[0], 70);
        Math.Abs(pixel.Red - pixel.Green).Should().BeLessThan(12, "the line is grey; got {0}", pixel);
        Math.Abs(pixel.Green - pixel.Blue).Should().BeLessThan(12, "the line is grey; got {0}", pixel);
    }

    [Fact]
    public void Draw_grid_columns_only_draws_no_horizontal_lines()
    {
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [], Origin200x100, 1.0, new GridSpec(2, 0));

        MarkedRows(bmp, x: 70, from: 20, to: 99).Should().BeEmpty("rows: 0 means no horizontal lines");
        MarkedColumns(bmp, y: 70, from: 30, to: 199).Should().NotBeEmpty().And.BeSubsetOf(new[] { 99, 100 });
    }

    [Fact]
    public void Draw_grid_rows_only_draws_no_vertical_lines()
    {
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [], Origin200x100, 1.0, new GridSpec(0, 2));

        MarkedColumns(bmp, y: 70, from: 0, to: 199).Should().BeEmpty("columns: 0 means no vertical lines");
        MarkedRows(bmp, x: 70, from: 20, to: 99).Should().NotBeEmpty().And.BeSubsetOf(new[] { 49, 50 });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(-3, 0)]
    [InlineData(1, 1)]      // one column and one row have no INTERIOR divisions
    public void Draw_grid_with_no_interior_divisions_draws_nothing(int columns, int rows)
    {
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [], Origin200x100, 1.0, new GridSpec(columns, rows));

        MarkedCount(bmp).Should().Be(0);
    }

    [Fact]
    public void Draw_without_a_grid_draws_nothing()
    {
        using var bmp = MidGrey();

        Annotator.Draw(bmp, [], Origin200x100, 1.0, null);

        MarkedCount(bmp).Should().Be(0);
    }

    /// <summary>The pixels of the top caption band, as a comparable snapshot.</summary>
    private static List<SKColor> TopBand(SKBitmap bmp)
    {
        var pixels = new List<SKColor>();
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < bmp.Width; x++)
                pixels.Add(bmp.GetPixel(x, y));
        return pixels;
    }

    [Fact]
    public void Draw_grid_captions_the_lines_with_virtual_desktop_coordinates()
    {
        // The caption is what makes the grid useful: the model reads a number off the picture and
        // passes it to click. Captioning the IMAGE pixel would be wrong on a second monitor or
        // after any downscale — the same line at x = 100 is 100 here and 1200 there.
        using var atOrigin = MidGrey();
        using var offOriginAndScaled = MidGrey();

        Annotator.Draw(atOrigin, [], Origin200x100, 1.0, new GridSpec(2, 0));
        Annotator.Draw(offOriginAndScaled, [], new ScreenRegion(1000, 0, 400, 200), 2.0, new GridSpec(2, 0));

        MarkedColumns(atOrigin, y: 6, from: 0, to: 199).Should()
            .Contain(x => x < 99 || x > 100, "the line carries a caption near the top edge");
        TopBand(offOriginAndScaled).Should().NotEqual(TopBand(atOrigin),
            "the caption must read 1200 there and 100 here — same line, different coordinate");
    }

    [Fact]
    public void Draw_grid_with_a_non_positive_scale_captions_as_if_the_scale_were_one()
    {
        // The same guard on the caption path: captured.X + x * infinity is not a coordinate.
        using var guarded = MidGrey();
        using var atScaleOne = MidGrey();

        Annotator.Draw(guarded, [], Origin200x100, 0.0, new GridSpec(2, 0));
        Annotator.Draw(atScaleOne, [], Origin200x100, 1.0, new GridSpec(2, 0));

        TopBand(guarded).Should().Equal(TopBand(atScaleOne),
            "a scale of zero is normalised to one, so the caption reads the image coordinate");
    }
}
