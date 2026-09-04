using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-8's pure core (roadmap C10): every region/display rule with no screen, no capture and no
/// Win32. <c>screenshot</c> and <c>ocr</c> both resolve their rect through this class, so a bug
/// here is a bug in both tools; the tool-level wiring is pinned by <c>ScreenToolsTests</c> and the
/// real multi-monitor capture by <c>ScreenshotServiceTests</c> (UIAutomation).
/// </summary>
[Trait("Category", "Unit")]
public class RegionMathTests
{
    // Monitor sets used below. The index is the multi_monitor enumeration order, which is what a
    // 'display' argument refers to; it is NOT guaranteed to put the primary first.
    private static MonitorInfo Mon(int index, int x, int y, int w, int h, bool primary = false) =>
        new(index, $"Monitor{index}", x, y, w, h, primary);

    // ---- R1a — ParseRegion: blank means "no region" ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseRegion_blank_is_null(string? text)
    {
        RegionMath.ParseRegion(text).Should().BeNull("null means 'no region given', not an error");
    }

    [Theory]
    [InlineData("10,20,300,200", 10, 20, 300, 200)]
    [InlineData(" 10 , 20 , 300 , 200 ", 10, 20, 300, 200)]        // TrimEntries, as A-7 shipped it
    [InlineData("-1920,-40,640,480", -1920, -40, 640, 480)]        // virtual desktop: negatives are legal
    [InlineData("0,0,1,1", 0, 0, 1, 1)]                            // 1x1 is the smallest legal size
    public void ParseRegion_reads_x_y_w_h(string text, int x, int y, int w, int h)
    {
        RegionMath.ParseRegion(text).Should().Be(new ScreenRegion(x, y, w, h));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1,2")]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    public void ParseRegion_wrong_arity_throws_naming_the_syntax(string text)
    {
        var act = () => RegionMath.ParseRegion(text);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("x,y,w,h", "the message must show the caller the expected syntax");
    }

    // ---- R1b — ParseRegion: a part that is not an integer (NEW in A-8) -----------------------

    [Theory]
    [InlineData("a,2,3,4", "a")]
    [InlineData("10,b,300,200", "b")]
    [InlineData("10,20,30.5,40", "30.5")]           // a decimal is not a pixel count
    [InlineData("10,20,300,2 0", "2 0")]            // TrimEntries does not remove inner spaces
    [InlineData("0x10,20,300,200", "0x10")]
    [InlineData("99999999999,20,300,200", "99999999999")]   // overflows int: still ArgumentException, not OverflowException
    public void ParseRegion_non_integer_part_throws_naming_that_part(string text, string offender)
    {
        var act = () => RegionMath.ParseRegion(text);

        act.Should().Throw<ArgumentException>("int.Parse's FormatException/OverflowException is not a caller-facing error")
            .Which.Message.Should().Contain(offender, "the message must name the part that could not be read");
    }

    [Theory]
    [InlineData("10,,300,200")]
    [InlineData(",,,")]
    public void ParseRegion_empty_part_throws(string text)
    {
        var act = () => RegionMath.ParseRegion(text);

        act.Should().Throw<ArgumentException>().Which.Message
            .ToLowerInvariant().Should().Contain("region");
    }

    // ---- R1c — ParseRegion: a zero or negative size is not a rectangle -----------------------

    [Theory]
    [InlineData("0,0,0,100", "width")]
    [InlineData("0,0,-5,100", "width")]
    [InlineData("0,0,100,0", "height")]
    [InlineData("0,0,100,-5", "height")]
    [InlineData("-10,-10,0,0", "width")]
    public void ParseRegion_non_positive_size_throws(string text, string named)
    {
        var act = () => RegionMath.ParseRegion(text);

        act.Should().Throw<ArgumentException>("a 0- or negative-sized capture is a Bitmap constructor crash, not a picture")
            .Which.Message.ToLowerInvariant().Should().Contain(named);
    }

    // ---- R1d — ParseDisplays --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDisplays_blank_is_null(string? text)
    {
        RegionMath.ParseDisplays(text, 2).Should().BeNull("null means 'no display given' — the caller's default applies");
    }

    [Theory]
    [InlineData("all", 1, new[] { 0 })]
    [InlineData("all", 2, new[] { 0, 1 })]
    [InlineData("ALL", 3, new[] { 0, 1, 2 })]
    [InlineData("All", 3, new[] { 0, 1, 2 })]
    [InlineData(" all ", 2, new[] { 0, 1 })]
    public void ParseDisplays_all_is_every_index_in_order(string text, int count, int[] expected)
    {
        RegionMath.ParseDisplays(text, count).Should().Equal(expected);
    }

    [Theory]
    [InlineData("1", 2, new[] { 1 })]
    [InlineData("0", 1, new[] { 0 })]
    [InlineData("0,2", 3, new[] { 0, 2 })]
    [InlineData(" 0 , 1 ", 2, new[] { 0, 1 })]
    [InlineData("1,0", 2, new[] { 1, 0 })]              // the order given is kept
    [InlineData("1,1,0", 2, new[] { 1, 0 })]            // de-duplicated, first occurrence wins
    [InlineData("0,0", 1, new[] { 0 })]
    public void ParseDisplays_reads_indices_in_order_without_duplicates(string text, int count, int[] expected)
    {
        RegionMath.ParseDisplays(text, count).Should().Equal(expected);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("7", 2)]
    [InlineData("-1", 2)]
    [InlineData("0,5", 2)]
    [InlineData("1", 1)]
    public void ParseDisplays_index_outside_the_inventory_throws_listing_the_valid_ones(string text, int count)
    {
        var act = () => RegionMath.ParseDisplays(text, count);

        var message = act.Should().Throw<ArgumentException>().Which.Message;
        message.Should().Contain(string.Join(",", Enumerable.Range(0, count)),
            "the model cannot guess how many displays there are — the message lists them");
        message.ToLowerInvariant().Should().Contain("display");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("0,x")]
    [InlineData("1.5")]
    [InlineData("all,0")]           // 'all' is not composable with indices
    [InlineData("primary")]
    public void ParseDisplays_non_integer_throws(string text)
    {
        var act = () => RegionMath.ParseDisplays(text, 3);

        act.Should().Throw<ArgumentException>().Which.Message
            .ToLowerInvariant().Should().Contain("display");
    }

    [Theory]
    [InlineData(",")]
    [InlineData(" , ")]
    [InlineData(",,")]
    public void ParseDisplays_empty_list_throws(string text)
    {
        var act = () => RegionMath.ParseDisplays(text, 2);

        act.Should().Throw<ArgumentException>("an empty selection would silently capture nothing")
            .Which.Message.ToLowerInvariant().Should().Contain("display");
    }

    // ---- R1e — Union ------------------------------------------------------------------------

    [Fact]
    public void Union_of_two_side_by_side_monitors_spans_both()
    {
        var union = RegionMath.Union([Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, 1920, 0, 1920, 1080)]);

        union.Should().Be(new ScreenRegion(0, 0, 3840, 1080));
    }

    [Fact]
    public void Union_with_a_monitor_left_of_the_primary_has_a_negative_origin()
    {
        // The case the checklist calls out: CopyFromScreen takes virtual-desktop coordinates, so
        // the union must keep the negative origin rather than clamp it to 0.
        var union = RegionMath.Union([Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, -1920, 0, 1920, 1080)]);

        union.Should().Be(new ScreenRegion(-1920, 0, 3840, 1080));
    }

    [Fact]
    public void Union_of_stacked_monitors_spans_the_height()
    {
        var union = RegionMath.Union([Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, 0, -1080, 1920, 1080)]);

        union.Should().Be(new ScreenRegion(0, -1080, 1920, 2160));
    }

    [Fact]
    public void Union_of_differently_sized_offset_monitors_is_the_bounding_box()
    {
        // A 2560x1440 secondary, hung 200 px above the primary and to its right.
        var union = RegionMath.Union([Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, 1920, -200, 2560, 1440)]);

        union.Should().Be(new ScreenRegion(0, -200, 4480, 1440),
            "the box spans x 0..4479 and y -200..1239");
    }

    [Fact]
    public void Union_of_one_monitor_is_that_monitor()
    {
        RegionMath.Union([Mon(1, -1920, -40, 1280, 720)]).Should().Be(new ScreenRegion(-1920, -40, 1280, 720));
    }

    [Fact]
    public void Union_does_not_depend_on_the_order_of_the_monitors()
    {
        MonitorInfo a = Mon(0, 0, 0, 1920, 1080, primary: true), b = Mon(1, -1920, -200, 2560, 1440);

        RegionMath.Union([a, b]).Should().Be(RegionMath.Union([b, a]));
    }

    [Fact]
    public void Union_of_mirrored_monitors_is_the_shared_rect()
    {
        // Duplicated (mirrored) displays report the same rect: the union must not double it.
        var union = RegionMath.Union([Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, 0, 0, 1920, 1080)]);

        union.Should().Be(new ScreenRegion(0, 0, 1920, 1080));
    }

    [Fact]
    public void Union_of_no_monitors_throws()
    {
        var act = () => RegionMath.Union([]);

        act.Should().Throw<ArgumentException>("there is no bounding box of nothing, and returning 0,0,0,0 would capture a crash");
    }

    // ---- R1f — VirtualScreen ---------------------------------------------------------------

    [Fact]
    public void VirtualScreen_is_the_union_of_every_monitor()
    {
        MonitorInfo[] all = [Mon(0, 0, 0, 1920, 1080, primary: true), Mon(1, -1920, 0, 1920, 1080), Mon(2, 1920, -300, 1280, 1024)];

        RegionMath.VirtualScreen(all).Should().Be(RegionMath.Union(all));
        RegionMath.VirtualScreen(all).Should().Be(new ScreenRegion(-1920, -300, 5120, 1380));
    }

    [Fact]
    public void VirtualScreen_of_no_monitors_throws()
    {
        var act = () => RegionMath.VirtualScreen([]);

        act.Should().Throw<ArgumentException>();
    }

    // ---- R1g — Validate --------------------------------------------------------------------

    private static readonly ScreenRegion TwoWide = new(0, 0, 3840, 1080);        // 0..3839, 0..1079

    [Theory]
    [InlineData(0, 0, 3840, 1080)]        // exactly the virtual screen
    [InlineData(0, 0, 1, 1)]              // top-left corner
    [InlineData(3839, 1079, 1, 1)]        // bottom-right corner
    [InlineData(1800, 100, 240, 200)]     // straddles the seam between the two monitors
    [InlineData(1920, 0, 1920, 1080)]     // exactly the second monitor
    public void Validate_accepts_a_region_inside_the_virtual_screen(int x, int y, int w, int h)
    {
        var act = () => RegionMath.Validate(new ScreenRegion(x, y, w, h), TwoWide);

        act.Should().NotThrow("a region straddling two monitors is legal — the virtual screen is one space");
    }

    [Theory]
    [InlineData(-1, 0, 100, 100)]         // one pixel off the left edge
    [InlineData(0, -1, 100, 100)]         // one pixel off the top
    [InlineData(3740, 0, 101, 100)]       // one pixel past the right edge
    [InlineData(0, 980, 100, 101)]        // one pixel past the bottom
    [InlineData(4000, 0, 100, 100)]       // entirely off the desktop
    [InlineData(0, 0, 3841, 1080)]        // wider than the desktop
    public void Validate_rejects_a_region_outside_and_states_the_bounds(int x, int y, int w, int h)
    {
        var act = () => RegionMath.Validate(new ScreenRegion(x, y, w, h), TwoWide);

        var message = act.Should().Throw<ArgumentException>(
            "upstream raises instead of clipping: a silently clipped capture has coordinates that no longer mean what the model thinks")
            .Which.Message;
        // Same wording style as InputService.MoveCursor's out-of-desktop error.
        message.Should().Contain("x 0..3839").And.Contain("y 0..1079");
    }

    [Fact]
    public void Validate_bounds_are_stated_for_a_negative_origin_desktop_too()
    {
        var virtualScreen = new ScreenRegion(-1920, -200, 3840, 1280);   // x -1920..1919, y -200..1079

        var inside = () => RegionMath.Validate(new ScreenRegion(-1920, -200, 10, 10), virtualScreen);
        inside.Should().NotThrow("the top-left corner of a negative-origin desktop is inside it");

        var outside = () => RegionMath.Validate(new ScreenRegion(-1921, 0, 10, 10), virtualScreen);
        outside.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain("x -1920..1919").And.Contain("y -200..1079");
    }

    // ---- R1h — Primary ----------------------------------------------------------------------

    [Fact]
    public void Primary_is_the_monitor_flagged_primary_not_the_first()
    {
        MonitorInfo[] all = [Mon(0, -1920, 0, 1920, 1080), Mon(1, 0, 0, 2560, 1440, primary: true)];

        RegionMath.Primary(all).Should().Be(all[1], "EnumDisplayMonitors order does not put the primary first");
    }

    [Fact]
    public void Primary_falls_back_to_the_first_monitor_when_none_is_flagged()
    {
        MonitorInfo[] all = [Mon(0, 0, 0, 1920, 1080), Mon(1, 1920, 0, 1920, 1080)];

        RegionMath.Primary(all).Should().Be(all[0], "a desktop with no primary flag must still capture something");
    }

    [Fact]
    public void Primary_of_no_monitors_throws()
    {
        var act = () => RegionMath.Primary([]);

        act.Should().Throw<ArgumentException>();
    }
    // ---- R1i (GREEN) — an empty inventory: the tool's collaborator can return nothing --------

    [Fact]
    public void ParseDisplays_all_on_an_empty_inventory_selects_nothing()
    {
        // Not an error here: 'all' of zero monitors is an empty selection, and the caller
        // (ScreenTools.ResolveRegionAsync -> Union) is what turns it into the "no monitors to
        // capture" message. Pinned so the two halves of that contract cannot drift apart.
        RegionMath.ParseDisplays("all", 0).Should().BeEmpty();
    }

    [Fact]
    public void ParseDisplays_an_index_on_an_empty_inventory_throws()
    {
        var act = () => RegionMath.ParseDisplays("0", 0);

        act.Should().Throw<ArgumentException>("there is no monitor 0 when there are no monitors")
            .Which.Message.ToLowerInvariant().Should().Contain("display");
    }

    // ---- R1j (GREEN) — Validate's far-edge arithmetic must not wrap ---------------------------

    [Theory]
    [InlineData(2, 0, int.MaxValue, 10)]          // x + width overflows to a negative number
    [InlineData(0, 2, 10, int.MaxValue)]          // y + height overflows
    public void Validate_rejects_a_region_whose_far_edge_overflows(int x, int y, int w, int h)
    {
        // ParseRegion accepts any positive width/height, so "2,0,2147483647,10" is a region a
        // model can send. Validate computes region.X + region.Width - 1 in unchecked arithmetic:
        // if that wraps negative the region looks like it ends left of the desktop and passes,
        // and a rect wider than the virtual screen reaches Bitmap/CopyFromScreen.
        var act = () => RegionMath.Validate(new ScreenRegion(x, y, w, h), TwoWide);

        act.Should().Throw<ArgumentException>(
            "a region this size is not inside a 3840x1080 desktop and must be rejected, not clipped");
    }
}
