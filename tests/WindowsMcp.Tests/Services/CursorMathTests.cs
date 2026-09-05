using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-11 (R2): the pure half of the cursor metadata — which monitor a virtual-desktop point is on.
/// Extracted so <c>screenshot</c>'s <c>cursor.monitorIndex</c> is provable against a hand-written
/// inventory with no desktop attached (roadmap C10); the live end is
/// <c>InputServiceTests.GetCursorPositionAsync_*</c>.
/// </summary>
[Trait("Category", "Unit")]
public class CursorMathTests
{
    /// <summary>One primary 1920x1080 at the origin.</summary>
    private static MonitorInfo[] Single => [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    /// <summary>Two 1920x1080 side by side; the seam is x = 1920.</summary>
    private static MonitorInfo[] SideBySide =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, 0, 1920, 1080, false),
    ];

    /// <summary>The secondary sits left of and above the primary: it owns x -1920..-1, y -40..1039.</summary>
    private static MonitorInfo[] LeftOfPrimary =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", -1920, -40, 1920, 1080, false),
    ];

    // ---- the point is on a monitor ----------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]           // top-left corner: left and top are inclusive
    [InlineData(10, 10)]
    [InlineData(960, 540)]       // the middle
    [InlineData(1919, 1079)]     // bottom-right corner: the last pixel is still on it
    public void MonitorIndexOf_finds_the_primary(int x, int y)
    {
        CursorMath.MonitorIndexOf(x, y, Single).Should().Be(0);
    }

    [Theory]
    [InlineData(2000, 10)]
    [InlineData(3839, 1079)]
    public void MonitorIndexOf_finds_the_second_monitor(int x, int y)
    {
        CursorMath.MonitorIndexOf(x, y, SideBySide).Should().Be(1);
    }

    [Fact]
    public void MonitorIndexOf_puts_the_seam_pixel_on_the_second_monitor()
    {
        // Right/bottom are EXCLUSIVE, so x=1920 is the first pixel of monitor 1, not the last of
        // monitor 0 — the off-by-one that decides which monitor a cursor on the join belongs to.
        CursorMath.MonitorIndexOf(1920, 0, SideBySide).Should().Be(1);
        CursorMath.MonitorIndexOf(1919, 0, SideBySide).Should().Be(0);
    }

    [Theory]
    [InlineData(-1920, -40)]     // its top-left corner
    [InlineData(-100, -10)]
    [InlineData(-1, 1039)]       // its bottom-right corner
    public void MonitorIndexOf_finds_a_monitor_with_a_negative_origin(int x, int y)
    {
        CursorMath.MonitorIndexOf(x, y, LeftOfPrimary).Should().Be(1);
    }

    [Fact]
    public void MonitorIndexOf_returns_the_monitors_own_index_not_its_list_position()
    {
        // multi_monitor's indices are what the model passes as 'display', and MonitorInfo carries
        // its own Index — reporting the list position would be right by accident on most desktops.
        MonitorInfo[] renumbered =
        [
            new(5, "Monitor5", 0, 0, 1920, 1080, true),
            new(7, "Monitor7", 1920, 0, 1920, 1080, false),
        ];

        CursorMath.MonitorIndexOf(2000, 10, renumbered).Should().Be(7);
    }

    [Fact]
    public void MonitorIndexOf_first_match_wins_when_monitors_overlap()
    {
        // Mirrored displays report the same rect twice; the point is on both, and the answer must
        // be deterministic — the first monitor that contains it.
        MonitorInfo[] mirrored =
        [
            new(0, "Monitor0", 0, 0, 1920, 1080, true),
            new(1, "Monitor1", 0, 0, 1920, 1080, false),
        ];

        CursorMath.MonitorIndexOf(10, 10, mirrored).Should().Be(0);
    }

    // ---- the point is on no monitor ----------------------------------------------------------

    [Theory]
    [InlineData(1920, 0)]        // one past the right edge: right is exclusive
    [InlineData(0, 1080)]        // one past the bottom edge: bottom is exclusive
    [InlineData(-1, 0)]          // one left of the origin
    [InlineData(0, -1)]          // one above the origin
    [InlineData(10000, 10000)]   // nowhere near
    public void MonitorIndexOf_is_minus_one_off_every_monitor(int x, int y)
    {
        CursorMath.MonitorIndexOf(x, y, Single).Should().Be(-1);
    }

    [Fact]
    public void MonitorIndexOf_is_minus_one_in_the_gap_between_two_monitors()
    {
        // Monitors of different heights leave virtual-desktop coordinates that belong to nothing.
        MonitorInfo[] ragged =
        [
            new(0, "Monitor0", 0, 0, 1920, 1080, true),
            new(1, "Monitor1", 1920, 0, 1280, 720, false),
        ];

        CursorMath.MonitorIndexOf(2000, 900, ragged).Should().Be(-1);
    }

    [Fact]
    public void MonitorIndexOf_is_minus_one_when_the_inventory_is_empty()
    {
        // The screenshot must still report a cursor when the inventory came back empty; it is the
        // rect resolution that refuses, not this.
        CursorMath.MonitorIndexOf(0, 0, []).Should().Be(-1);
    }
}
