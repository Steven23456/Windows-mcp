using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-2: the one traversal rule that needs no desktop — clipping a node to the window it was found
/// under (upstream's <c>iou_bounding_box</c>). It decides two things the model depends on: the
/// coordinates <c>click</c> is handed (a control that runs off the edge of its window must report
/// the part that is actually on screen) and whether the node is reported at all (nothing inside
/// the window means nothing to click). The rest of <see cref="UiTraverser"/> only runs against
/// live UIA and is covered by <c>UIAutomationSnapshotIntegrationTests</c> /
/// <c>UIAutomationSnapshotDesktopTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class UiTraverserClipTests
{
    /// <summary>x 100..900, y 100..700 — the window every row below is clipped against.</summary>
    private static readonly Bounds Window = new(100, 100, 800, 600);

    [Theory]
    // wholly inside: untouched, to the pixel
    [InlineData(200, 200, 50, 40, 200, 200, 50, 40)]
    // exactly the window
    [InlineData(100, 100, 800, 600, 100, 100, 800, 600)]
    // over one edge at a time: the far edge moves in, the near edge and the other axis do not
    [InlineData(850, 200, 100, 40, 850, 200, 50, 40)]     // over the right edge
    [InlineData(50, 200, 100, 40, 100, 200, 50, 40)]      // over the left edge
    [InlineData(200, 50, 40, 100, 200, 100, 40, 50)]      // over the top
    [InlineData(200, 650, 40, 100, 200, 650, 40, 50)]     // over the bottom
    // a node bigger than its window (a maximised child, a scrolled canvas) becomes the window
    [InlineData(0, 0, 2000, 2000, 100, 100, 800, 600)]
    // the smallest overlap that still exists: one pixel in the bottom-right corner
    [InlineData(899, 699, 10, 10, 899, 699, 1, 1)]
    public void Clip_returns_the_part_of_the_node_inside_the_window(
        int x, int y, int w, int h, int ex, int ey, int ew, int eh)
    {
        var clipped = UiTraverser.Clip(new Bounds(x, y, w, h), Window);

        clipped.Should().Be(new Bounds(ex, ey, ew, eh));
    }

    [Theory]
    [InlineData(900, 200, 50, 40)]     // starts exactly on the right edge: touching is not overlapping
    [InlineData(0, 200, 100, 40)]      // ends exactly on the left edge
    [InlineData(200, 0, 40, 100)]      // ends exactly on the top edge
    [InlineData(200, 700, 40, 100)]    // starts exactly on the bottom edge
    [InlineData(2000, 2000, 10, 10)]   // on another monitor entirely
    [InlineData(-500, -500, 10, 10)]   // off the left/top of the virtual desktop
    [InlineData(200, 200, 0, 40)]      // zero width inside the window is still nothing to click
    [InlineData(200, 200, 40, 0)]      // zero height likewise
    public void Clip_returns_null_when_nothing_of_the_node_is_inside_the_window(int x, int y, int w, int h)
        => UiTraverser.Clip(new Bounds(x, y, w, h), Window).Should().BeNull();

    // A window whose own rectangle could not be read is reported as 0x0 by the traverser. Clipping
    // to that would delete every node in the window, so an unknown window rect trusts the node.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(800, 0)]
    [InlineData(0, 600)]
    [InlineData(-10, -10)]
    public void Clip_trusts_the_node_when_the_window_rectangle_is_unknown(int windowWidth, int windowHeight)
    {
        var node = new Bounds(200, 200, 50, 40);

        var clipped = UiTraverser.Clip(node, new Bounds(0, 0, windowWidth, windowHeight));

        clipped.Should().BeSameAs(node, "an unknown window rect must not silently drop every node");
    }
}
