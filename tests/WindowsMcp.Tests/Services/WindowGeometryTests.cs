using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-9: everything <c>window(action: move|resize|set_bounds)</c> decides once the matcher has
/// chosen a window — which SetWindowPos flags go out, whether a minimised or maximised window is
/// refused or restored, and that the reported rectangle is re-read from the window rather than
/// echoed back from the request (roadmap C11). Driven through
/// <see cref="IWindowGeometryNative"/> with a recording fake, the same shape B-10 gave
/// <c>ForegroundLadder</c>, so no window on this desktop moves.
/// </summary>
[Trait("Category", "Unit")]
public class WindowGeometryTests
{
    /// <summary>
    /// Records every call in order and answers <c>GetRect</c> from a queue, so "before" and
    /// "after" are provably two different reads.
    /// </summary>
    private sealed class FakeNative : IWindowGeometryNative
    {
        private readonly Queue<Bounds> _rects;

        public FakeNative(params Bounds[] rects) => _rects = new Queue<Bounds>(rects);

        public List<string> Calls { get; } = [];
        public bool Iconic { get; init; }
        public bool Zoomed { get; init; }
        public bool SetWindowPosResult { get; init; } = true;
        public (int X, int Y, int Width, int Height, uint Flags)? LastSet { get; private set; }
        public Bounds LastRect { get; private set; } = new(0, 0, 0, 0);

        public bool IsIconic(long hwnd) { Calls.Add("IsIconic"); return Iconic; }
        public bool IsZoomed(long hwnd) { Calls.Add("IsZoomed"); return Zoomed; }
        public bool Restore(long hwnd) { Calls.Add("Restore"); return true; }

        public bool SetWindowPos(long hwnd, int x, int y, int width, int height, uint flags)
        {
            Calls.Add("SetWindowPos");
            LastSet = (x, y, width, height, flags);
            return SetWindowPosResult;
        }

        public Bounds GetRect(long hwnd)
        {
            Calls.Add("GetRect");
            LastRect = _rects.Count > 1 ? _rects.Dequeue() : _rects.Peek();
            return LastRect;
        }
    }

    private static WindowInfo Info(WindowState state = WindowState.Normal, long hwnd = 0x1234)
        => new("Untitled - Notepad", hwnd, 9001, "notepad", state, new Bounds(10, 20, 300, 200),
               0, true, false, 0);

    private static WindowMatch Match(WindowState state = WindowState.Normal, string strategy = "substring", int score = 100)
        => new(Info(state), strategy, score);

    private static readonly Bounds Before = new(10, 20, 300, 200);
    private static readonly Bounds After = new(100, 100, 800, 600);

    private const uint Base = WindowGeometry.SWP_NOZORDER | WindowGeometry.SWP_NOACTIVATE;

    // ---- Validate: the argument rules ---------------------------------------------------------

    [Fact]
    public void Validate_refuses_a_call_that_asks_for_nothing()
    {
        var act = () => WindowGeometry.Validate(null, null, null, null);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("x").And.Contain("y").And.Contain("width").And.Contain("height",
                "the model is told all four ways to say what it wants, not just that it said nothing");
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(-1, 600)]
    [InlineData(800, 0)]
    [InlineData(800, -5)]
    public void Validate_refuses_a_width_or_height_that_is_not_positive(int width, int height)
    {
        var act = () => WindowGeometry.Validate(null, null, width, height);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().ContainAny("width", "height");
    }

    [Theory]
    [InlineData(100, 200, null, null)]
    [InlineData(null, null, 800, 600)]
    [InlineData(100, 200, 800, 600)]
    [InlineData(-1920, 0, null, null)]     // a monitor to the left of the primary is legal
    [InlineData(0, null, null, null)]      // a single coordinate is enough to be a request
    public void Validate_accepts_any_call_that_asks_for_something(int? x, int? y, int? width, int? height)
    {
        var act = () => WindowGeometry.Validate(x, y, width, height);

        act.Should().NotThrow();
    }

    [Fact]
    public void Apply_runs_the_same_validation_before_it_touches_the_window()
    {
        var native = new FakeNative(Before);

        var act = () => WindowGeometry.Apply(Match(), null, null, null, null, false, native);

        act.Should().Throw<ArgumentException>();
        native.Calls.Should().BeEmpty("a call that asks for nothing must not read or move a window");
    }

    // ---- the SetWindowPos flags ---------------------------------------------------------------

    [Fact]
    public void Apply_never_raises_or_activates_the_window()
    {
        // A move that stole the foreground would break every "act on the window behind" workflow.
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), 100, 100, 800, 600, false, native);

        (native.LastSet!.Value.Flags & WindowGeometry.SWP_NOZORDER).Should().NotBe(0);
        (native.LastSet!.Value.Flags & WindowGeometry.SWP_NOACTIVATE).Should().NotBe(0);
    }

    [Fact]
    public void Apply_with_only_a_position_sets_NOSIZE_and_leaves_the_size_alone()
    {
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), 100, 100, null, null, false, native);

        var set = native.LastSet!.Value;
        set.Flags.Should().Be(Base | WindowGeometry.SWP_NOSIZE, "'move' keeps the size");
        (set.X, set.Y).Should().Be((100, 100));
        (set.Width, set.Height).Should().Be((Before.Width, Before.Height),
            "the ignored pair is filled from the window's current rect, so a driver that ignores "
            + "the flag still cannot resize the window");
    }

    [Fact]
    public void Apply_with_only_a_size_sets_NOMOVE_and_leaves_the_position_alone()
    {
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), null, null, 800, 600, false, native);

        var set = native.LastSet!.Value;
        set.Flags.Should().Be(Base | WindowGeometry.SWP_NOMOVE, "'resize' keeps the position");
        (set.Width, set.Height).Should().Be((800, 600));
        (set.X, set.Y).Should().Be((Before.X, Before.Y));
    }

    [Fact]
    public void Apply_with_all_four_sets_neither_NOMOVE_nor_NOSIZE()
    {
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), 100, 100, 800, 600, false, native);

        var set = native.LastSet!.Value;
        set.Flags.Should().Be(Base, "'set_bounds' asks for both, so neither is suppressed");
        (set.X, set.Y, set.Width, set.Height).Should().Be((100, 100, 800, 600));
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(null, 200)]
    public void Apply_with_half_a_position_fills_the_other_half_from_the_current_rect(int? x, int? y)
    {
        // Resolved ambiguity (see the report): NOMOVE means "no position was asked for at all",
        // so x alone is still a move, with y left where the window already is.
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), x, y, null, null, false, native);

        var set = native.LastSet!.Value;
        (set.Flags & WindowGeometry.SWP_NOMOVE).Should().Be(0, "a position was asked for");
        set.X.Should().Be(x ?? Before.X);
        set.Y.Should().Be(y ?? Before.Y);
    }

    [Fact]
    public void Apply_with_half_a_size_fills_the_other_half_from_the_current_rect()
    {
        var native = new FakeNative(Before, After);

        WindowGeometry.Apply(Match(), null, null, 800, null, false, native);

        var set = native.LastSet!.Value;
        (set.Flags & WindowGeometry.SWP_NOSIZE).Should().Be(0);
        set.Width.Should().Be(800);
        set.Height.Should().Be(Before.Height);
    }

    // ---- the state refusal, and restore_first -------------------------------------------------

    [Fact]
    public void Apply_refuses_a_minimized_window_and_names_its_state()
    {
        // Upstream refuses too: SetWindowPos on an iconic window writes the restored placement
        // invisibly, so the caller thinks it moved a window it cannot see.
        var native = new FakeNative(Before) { Iconic = true };

        var act = () => WindowGeometry.Apply(Match(WindowState.Minimized), 100, 100, 800, 600, false, native);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Minimized").And.Contain("restore_first",
                "the refusal has to name the state and the way out of it");
        native.Calls.Should().NotContain("SetWindowPos");
        native.Calls.Should().NotContain("Restore");
    }

    [Fact]
    public void Apply_refuses_a_maximized_window_and_names_its_state()
    {
        var native = new FakeNative(Before) { Zoomed = true };

        var act = () => WindowGeometry.Apply(Match(WindowState.Maximized), 100, 100, 800, 600, false, native);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Maximized").And.Contain("restore_first");
        native.Calls.Should().NotContain("SetWindowPos");
    }

    [Fact]
    public void Apply_asks_the_window_not_the_inventory_what_state_it_is_in()
    {
        // The inventory entry can be a second old. IsIconic/IsZoomed are the live truth, and a
        // stale WindowInfo must not be what a refusal rests on.
        var native = new FakeNative(Before) { Iconic = true };

        var act = () => WindowGeometry.Apply(Match(WindowState.Normal), 100, 100, 800, 600, false, native);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Apply_with_restore_first_restores_a_minimized_window_before_moving_it()
    {
        var native = new FakeNative(Before, After) { Iconic = true };

        var result = WindowGeometry.Apply(Match(WindowState.Minimized), 100, 100, 800, 600, true, native);

        result.Restored.Should().BeTrue();
        native.Calls.Should().ContainInOrder(new[] { "Restore", "SetWindowPos" },
            "restoring after the move would put the window back where it was");
        result.After.Should().Be(After);
    }

    [Fact]
    public void Apply_with_restore_first_restores_a_maximized_window_too()
    {
        var native = new FakeNative(Before, After) { Zoomed = true };

        var result = WindowGeometry.Apply(Match(WindowState.Maximized), 100, 100, 800, 600, true, native);

        result.Restored.Should().BeTrue();
        native.Calls.Should().ContainInOrder("Restore", "SetWindowPos");
    }

    [Fact]
    public void Apply_with_restore_first_leaves_a_normal_window_alone()
    {
        var native = new FakeNative(Before, After);

        var result = WindowGeometry.Apply(Match(), 100, 100, 800, 600, restoreFirst: true, native);

        result.Restored.Should().BeFalse("nothing had to be restored, so nothing was");
        native.Calls.Should().NotContain("Restore");
    }

    // ---- the outcome, not the request ---------------------------------------------------------

    [Fact]
    public void Apply_reads_the_rect_before_and_after_and_reports_both()
    {
        var native = new FakeNative(Before, After);

        var result = WindowGeometry.Apply(Match(), 100, 100, 800, 600, false, native);

        result.Before.Should().Be(Before);
        result.After.Should().Be(After);
        native.Calls.Should().ContainInOrder("GetRect", "SetWindowPos", "GetRect");
        native.Calls.Count(c => c == "GetRect").Should().Be(2);
    }

    [Fact]
    public void Apply_reports_the_rect_the_window_ended_up_with_not_the_one_that_was_asked_for()
    {
        // Windows clamps to the minimum tracking size and snaps to work areas; the whole point of
        // re-reading (roadmap C11) is that the agent is told what really happened.
        var clamped = new Bounds(100, 100, 132, 39);
        var native = new FakeNative(Before, clamped);

        var result = WindowGeometry.Apply(Match(), 100, 100, 10, 10, false, native);

        result.After.Should().Be(clamped);
    }

    [Fact]
    public void Apply_does_not_trust_the_SetWindowPos_return_value()
    {
        // B-10's rule for SetForegroundWindow applies here: the return says "request accepted",
        // the re-read says what the window is. A false return with a moved window is still a move.
        var native = new FakeNative(Before, After) { SetWindowPosResult = false };

        var result = WindowGeometry.Apply(Match(), 100, 100, 800, 600, false, native);

        result.After.Should().Be(After);
    }

    [Fact]
    public void Apply_carries_the_matcher_verdict_and_the_window_through()
    {
        var native = new FakeNative(Before, After);

        var result = WindowGeometry.Apply(Match(strategy: "fuzzy", score: 86), 100, 100, 800, 600, false, native);

        result.Window.Title.Should().Be("Untitled - Notepad", "the window that was moved, not the string sent");
        result.Window.Hwnd.Should().Be(0x1234);
        result.MatchStrategy.Should().Be("fuzzy");
        result.Score.Should().Be(86);
    }

    [Fact]
    public void The_flag_constants_are_the_user32_values()
    {
        // These are hand-declared numbers; one transposed digit turns "do not resize" into
        // "do not move" and the bug looks like a Windows quirk.
        WindowGeometry.SWP_NOSIZE.Should().Be(0x0001);
        WindowGeometry.SWP_NOMOVE.Should().Be(0x0002);
        WindowGeometry.SWP_NOZORDER.Should().Be(0x0004);
        WindowGeometry.SWP_NOACTIVATE.Should().Be(0x0010);
    }

    [Fact]
    public void Apply_lets_a_window_that_vanished_between_the_match_and_the_move_surface_as_a_miss()
    {
        // GetWindowRect is where a window closed since the inventory read is noticed. The adapter
        // turns that into KeyNotFoundException (the same answer a stale hwnd gets from the
        // matcher) and Apply must let it through rather than moving a recycled handle.
        var native = new ThrowingRectNative();

        var act = () => WindowGeometry.Apply(Match(), 100, 100, 800, 600, false, native);

        act.Should().Throw<KeyNotFoundException>();
        native.Calls.Should().NotContain("SetWindowPos", "nothing is moved once the window is gone");
    }

    /// <summary>A window that is destroyed between the state check and the first rect read.</summary>
    private sealed class ThrowingRectNative : IWindowGeometryNative
    {
        public List<string> Calls { get; } = [];
        public bool IsIconic(long hwnd) { Calls.Add("IsIconic"); return false; }
        public bool IsZoomed(long hwnd) { Calls.Add("IsZoomed"); return false; }
        public bool Restore(long hwnd) { Calls.Add("Restore"); return true; }
        public bool SetWindowPos(long hwnd, int x, int y, int w, int h, uint flags) { Calls.Add("SetWindowPos"); return true; }
        public Bounds GetRect(long hwnd)
        {
            Calls.Add("GetRect");
            throw new KeyNotFoundException($"Window {hwnd} (0x{hwnd:X}) no longer exists.");
        }
    }
}
