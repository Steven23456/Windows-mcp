using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10: the bring-to-foreground ladder, driven through the <see cref="IForegroundNative"/> seam
/// with a recording fake. Everything that makes the ladder a ladder — the order of the rungs, the
/// restore, the refused <c>AttachThreadInput</c>, the ALT nudge, and above all roadmap C11's rule
/// that success is <c>GetForegroundWindow</c>'s word and never <c>SetForegroundWindow</c>'s
/// return value — is decided here, with no desktop and no injected input, so it is
/// <c>Category=Unit</c>.
/// <para>
/// The live half is <see cref="WindowServiceForegroundTests"/> (Integration: the current
/// foreground window) and the Notepad <c>UIAutomation</c> tests in the same file: a fake cannot
/// prove that user32 was called at all, which is exactly the failure mode CLAUDE.md records for
/// <c>disk_inspect mode:reclaimable</c>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ForegroundLadderTests
{
    private const long Target = 0x1234;
    private const long SomeoneElse = 0x7777;

    /// <summary>
    /// Records every call in order and decides the outcome by counting
    /// <c>SetForegroundWindow</c>s: <c>succeedOnSetCall</c> 1 = the plain call works, 2 = the
    /// second one does (the attach rung, or the nudge rung when attach was refused), 3 = only the
    /// nudge does, 0 = Windows never gives up the foreground.
    /// </summary>
    private sealed class FakeNative(int succeedOnSetCall, bool minimized = false, bool attachAllowed = true)
        : IForegroundNative
    {
        private int _setCalls;

        public List<string> Calls { get; } = [];
        public long Foreground { get; private set; } = SomeoneElse;
        public bool Minimized { get; private set; } = minimized;

        /// <summary>C11's trap: user32 says "yes" and the foreground does not move.</summary>
        public bool SetAlwaysReturnsTrue { get; init; }

        public bool IsIconic(long hwnd)
        {
            Calls.Add("IsIconic");
            hwnd.Should().Be(Target, "every call is aimed at the matched window's handle");
            return Minimized;
        }

        public bool Restore(long hwnd)
        {
            Calls.Add("Restore");
            hwnd.Should().Be(Target);
            Minimized = false;
            return true;
        }

        public bool SetForegroundWindow(long hwnd)
        {
            Calls.Add("SetForegroundWindow");
            hwnd.Should().Be(Target);
            if (++_setCalls == succeedOnSetCall) Foreground = hwnd;
            return SetAlwaysReturnsTrue || Foreground == hwnd;
        }

        public long GetForegroundWindow()
        {
            Calls.Add("GetForegroundWindow");
            return Foreground;
        }

        public bool AttachThreadInput(long hwnd, bool attach)
        {
            Calls.Add(attach ? "Attach" : "Detach");
            hwnd.Should().Be(Target);
            return attachAllowed;
        }

        public bool BringWindowToTop(long hwnd)
        {
            Calls.Add("BringWindowToTop");
            hwnd.Should().Be(Target);
            return true;
        }

        public void AltNudge() => Calls.Add("AltNudge");
    }

    private static WindowMatch Match(WindowState state = WindowState.Normal) =>
        new(new WindowInfo("Untitled - Notepad", Target, 4242, "notepad", state,
                new Bounds(0, 0, 800, 600), 2, IsActive: false, IsBrowser: false, MonitorIndex: 0),
            "fuzzy", 86);

    // ---- which rung worked -------------------------------------------------------------------

    [Fact]
    public void The_plain_call_working_is_reported_as_SetForegroundWindow_and_stops_the_ladder()
    {
        var native = new FakeNative(succeedOnSetCall: 1);

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeTrue();
        result.Strategy.Should().Be("SetForegroundWindow");
        result.Restored.Should().BeFalse();
        native.Calls.Should().Equal("IsIconic", "SetForegroundWindow", "GetForegroundWindow");
        native.Calls.Should().NotContain("Attach").And.NotContain("AltNudge",
            "a rung that was not needed is a rung that was not climbed");
    }

    [Fact]
    public void A_refused_first_attempt_climbs_to_AttachThreadInput_and_detaches_afterwards()
    {
        var native = new FakeNative(succeedOnSetCall: 2);

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeTrue();
        result.Strategy.Should().Be("AttachThreadInput");
        native.Calls.Should().ContainInOrder(
            "SetForegroundWindow", "GetForegroundWindow",
            "Attach", "BringWindowToTop", "SetForegroundWindow", "Detach");
        native.Calls.Count(c => c == "Detach").Should().Be(1,
            "the input queues are attached once and detached once, whatever the outcome");
        native.Calls.Should().NotContain("AltNudge");
    }

    [Fact]
    public void A_refused_AttachThreadInput_skips_that_rung_entirely_and_goes_to_the_nudge()
    {
        // The elevated-target case: Windows denies the attach, so there is nothing to detach and
        // BringWindowToTop is pointless. The ladder must not pretend it climbed that rung.
        var native = new FakeNative(succeedOnSetCall: 2, attachAllowed: false);

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeTrue();
        result.Strategy.Should().Be("AltNudge", "the attach rung never ran, so it cannot be the answer");
        native.Calls.Should().Contain("Attach");
        native.Calls.Should().NotContain("Detach", "nothing was attached, so nothing is detached");
        native.Calls.Should().NotContain("BringWindowToTop");
        native.Calls.Should().ContainInOrder("Attach", "AltNudge", "SetForegroundWindow");
    }

    [Fact]
    public void The_alt_nudge_is_the_last_rung_and_is_reported_by_name()
    {
        var native = new FakeNative(succeedOnSetCall: 3);

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeTrue();
        result.Strategy.Should().Be("AltNudge");
        native.Calls.Should().ContainInOrder("Detach", "AltNudge", "SetForegroundWindow", "GetForegroundWindow");
    }

    [Fact]
    public void Every_rung_refused_is_reported_as_a_failure_with_no_strategy()
    {
        var native = new FakeNative(succeedOnSetCall: 0);

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeFalse("GetForegroundWindow never named our window");
        result.Strategy.Should().BeNull("no step worked, so no step is named");
        native.Calls.Count(c => c == "SetForegroundWindow").Should().Be(3, "all three rungs were tried");
        native.Calls.Count(c => c == "AltNudge").Should().Be(1);
        native.Calls.Count(c => c == "GetForegroundWindow").Should().Be(3,
            "the foreground is re-read after each attempt, not once at the end");
    }

    // ---- C11: the outcome is read, never assumed --------------------------------------------

    [Fact]
    public void A_SetForegroundWindow_that_returns_true_without_moving_the_foreground_is_a_failure()
    {
        // The bug this whole design exists to prevent: user32's return value is a request being
        // accepted, not the foreground actually changing. Only GetForegroundWindow decides.
        var native = new FakeNative(succeedOnSetCall: 0) { SetAlwaysReturnsTrue = true };

        var result = ForegroundLadder.Bring(Match(), native);

        result.Success.Should().BeFalse();
        result.Strategy.Should().BeNull();
    }

    [Fact]
    public void Success_is_the_foreground_window_matching_the_target_handle()
    {
        var native = new FakeNative(succeedOnSetCall: 1);

        var result = ForegroundLadder.Bring(Match(), native);

        native.Foreground.Should().Be(Target);
        result.Success.Should().BeTrue();
    }

    // ---- restore -----------------------------------------------------------------------------

    [Fact]
    public void A_minimized_window_is_restored_before_anything_else_and_Restored_says_so()
    {
        var native = new FakeNative(succeedOnSetCall: 1, minimized: true);

        var result = ForegroundLadder.Bring(Match(WindowState.Minimized), native);

        result.Restored.Should().BeTrue();
        result.Success.Should().BeTrue();
        native.Calls.Should().Equal("IsIconic", "Restore", "SetForegroundWindow", "GetForegroundWindow");
    }

    [Fact]
    public void A_window_that_is_not_minimized_is_never_restored()
    {
        // SW_RESTORE on a maximized window un-maximizes it: an unasked-for side effect.
        var native = new FakeNative(succeedOnSetCall: 1);

        var result = ForegroundLadder.Bring(Match(WindowState.Maximized), native);

        result.Restored.Should().BeFalse();
        native.Calls.Should().NotContain("Restore");
    }

    [Fact]
    public void Restored_is_reported_even_when_the_foreground_change_then_fails()
    {
        // Two independent facts: the window was un-minimized, and it did not come forward.
        var native = new FakeNative(succeedOnSetCall: 0, minimized: true);

        var result = ForegroundLadder.Bring(Match(WindowState.Minimized), native);

        result.Restored.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.Strategy.Should().BeNull();
    }

    [Fact]
    public void The_state_that_decides_a_restore_is_IsIconic_not_the_inventory_snapshot()
    {
        // The inventory can be a few milliseconds stale; IsIconic is asked at the moment of the
        // attempt, and it is what Restored reports.
        var native = new FakeNative(succeedOnSetCall: 1, minimized: true);

        var result = ForegroundLadder.Bring(Match(WindowState.Normal), native);

        result.Restored.Should().BeTrue();
        native.Calls.Should().ContainInOrder("IsIconic", "Restore");
    }

    // ---- what the ladder carries through -----------------------------------------------------

    [Fact]
    public void The_matchers_verdict_is_passed_through_untouched()
    {
        var native = new FakeNative(succeedOnSetCall: 1);
        var match = Match();

        var result = ForegroundLadder.Bring(match, native);

        result.Window.Should().BeSameAs(match.Window, "the ladder reports the window, it does not re-find it");
        result.MatchStrategy.Should().Be("fuzzy");
        result.Score.Should().Be(86);
    }
}
