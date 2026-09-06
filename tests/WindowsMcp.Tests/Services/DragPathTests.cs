using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-2 (R1): the drag path, pure. Today's <c>DragAsync</c> is press → one absolute jump → release,
/// which file managers, canvases and browser DnD do not recognise as a drag at all: they need the
/// intermediate <c>WM_MOUSEMOVE</c>s and an initial movement past <c>SM_CXDRAG</c>. The geometry
/// is separated from the injection so it can be pinned exactly without moving the pointer.
/// </summary>
[Trait("Category", "Unit")]
public class DragPathTests
{
    [Fact]
    public void With_no_nudge_the_path_is_a_plain_linear_interpolation_to_the_destination()
    {
        // nudge 0 puts the first point on the origin, so this pins the interpolation itself:
        // four equal steps of 25 px, the last exactly on the destination.
        var points = DragPath.Points((0, 0), (100, 0), steps: 4, nudge: 0);

        points.Should().Equal((0, 0), (25, 0), (50, 0), (75, 0), (100, 0));
    }

    [Fact]
    public void The_path_starts_with_a_nudge_toward_the_destination()
    {
        // Windows only treats a press-and-move as a drag once the pointer has travelled
        // SM_CXDRAG pixels; without this first small move the target sees a click.
        var points = DragPath.Points((100, 100), (300, 100), steps: 20, nudge: 5);

        points[0].Should().Be((105, 100));
    }

    [Fact]
    public void The_nudge_is_the_requested_distance_along_a_diagonal_drag()
    {
        var points = DragPath.Points((0, 0), (100, 100), steps: 20, nudge: 10);

        var distance = Math.Sqrt(points[0].X * points[0].X + (double)points[0].Y * points[0].Y);
        distance.Should().BeApproximately(10, 1.5, "the nudge is a distance, not a per-axis offset");
        points[0].X.Should().BeGreaterThan(0);
        points[0].Y.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    [InlineData(200)]
    public void The_path_is_the_nudge_plus_one_point_per_step(int steps)
    {
        DragPath.Points((10, 10), (410, 210), steps, nudge: 5).Should().HaveCount(steps + 1);
    }

    [Theory]
    [InlineData(0, 0, 100, 100)]
    [InlineData(500, 400, 100, 100)]     // up and to the left
    [InlineData(-1900, -80, 40, 600)]    // across a monitor left of the primary
    [InlineData(0, 0, 0, 250)]           // straight down
    [InlineData(0, 0, 250, 0)]           // straight across
    public void The_path_ends_exactly_on_the_destination(int fromX, int fromY, int toX, int toY)
    {
        // "Nearly there" drops the file on the wrong target; the release has to be on the point
        // the caller named, whatever the interpolation rounding did on the way.
        var points = DragPath.Points((fromX, fromY), (toX, toY), steps: 20, nudge: 5);

        points[^1].Should().Be((toX, toY));
    }

    [Theory]
    [InlineData(0, 0, 100, 100)]
    [InlineData(500, 400, 100, 100)]
    [InlineData(-1900, -80, 40, 600)]
    [InlineData(0, 0, 0, 250)]
    public void The_path_never_doubles_back_on_either_axis(int fromX, int fromY, int toX, int toY)
    {
        // A non-monotone path reads as a shake to some drop targets and cancels the drag.
        var points = DragPath.Points((fromX, fromY), (toX, toY), steps: 20, nudge: 5);

        for (int i = 1; i < points.Count; i++)
        {
            if (toX >= fromX) points[i].X.Should().BeGreaterThanOrEqualTo(points[i - 1].X);
            else points[i].X.Should().BeLessThanOrEqualTo(points[i - 1].X);

            if (toY >= fromY) points[i].Y.Should().BeGreaterThanOrEqualTo(points[i - 1].Y);
            else points[i].Y.Should().BeLessThanOrEqualTo(points[i - 1].Y);
        }
    }

    [Fact]
    public void Every_point_stays_inside_the_rectangle_the_drag_spans()
    {
        var points = DragPath.Points((100, 100), (300, 40), steps: 12, nudge: 5);

        points.Should().OnlyContain(p => p.X >= 100 && p.X <= 300 && p.Y >= 40 && p.Y <= 100,
            "overshooting the destination hovers a target the caller never named");
    }

    [Fact]
    public void A_zero_distance_drag_is_just_the_destination()
    {
        // press and release on the same point - a click, geometrically. Twenty identical moves
        // would be noise, and a nudge would move OFF the point the caller asked for.
        DragPath.Points((42, 42), (42, 42), steps: 20, nudge: 5)
            .Should().Equal((42, 42));
    }

    [Fact]
    public void A_drag_shorter_than_the_nudge_never_passes_the_destination()
    {
        var points = DragPath.Points((0, 0), (3, 0), steps: 20, nudge: 10);

        points.Should().OnlyContain(p => p.X >= 0 && p.X <= 3 && p.Y == 0);
        points[^1].Should().Be((3, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fewer_than_one_step_is_refused_by_name(int steps)
    {
        var act = () => DragPath.Points((0, 0), (100, 100), steps, nudge: 5);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*steps*");
    }

    [Fact]
    public void A_drag_exactly_as_long_as_the_nudge_starts_on_the_origin()
    {
        // The boundary of "the drag is longer than the nudge": at equality a nudge point would BE
        // the destination, so the path would start at the end and the interpolation would be a
        // walk backwards. The origin is the honest first point.
        var points = DragPath.Points((0, 0), (10, 0), steps: 2, nudge: 10);

        points.Should().Equal((0, 0), (5, 0), (10, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_nudge_is_refused_by_name(int nudge)
    {
        // SM_CXDRAG cannot be negative, so this is a caller bug, and a negative nudge would place
        // the first point BEHIND the origin - the drag would start by moving the wrong way.
        var act = () => DragPath.Points((0, 0), (100, 100), steps: 20, nudge: nudge);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*nudge*");
    }
}
