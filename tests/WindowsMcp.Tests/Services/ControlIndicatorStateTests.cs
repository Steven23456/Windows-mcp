using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// S-11 (R2, R3): the indicator's pure core - the refcount of driving calls in flight, the fade
/// deadline, the dismissal and the pending takeover - under a fake clock, with no window and no
/// timer. <see cref="ControlIndicatorTests"/> drives the same rules through the real windows; every
/// rule about WHEN the indicator is up is decided here, so these are the fast net.
/// <para>
/// The design note's "indicator only" decision is why the refcount matters: upstream's #456 latched
/// a gate forever after a missed key-up. Here an unbalanced <c>End</c> clamps at zero, so the next
/// balanced call always fades out - the tests below pin that.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ControlIndicatorStateTests
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);
    private const string Hotkey = "Ctrl+Alt+Shift+Backspace";
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    /// <summary>
    /// A clock that only moves when told to (the <c>AppCatalogServiceTests</c> precedent). Thread-safe
    /// reads. It drives BOTH of a <see cref="TimeProvider"/>'s clocks (F4): the wall clock
    /// (<c>GetUtcNow</c>, what a takeover is stamped with) and the monotonic one
    /// (<c>GetTimestamp</c>, what the fade is measured on). <see cref="Advance"/> moves both, as real
    /// time does; <see cref="StepWall"/> moves only the wall clock (an NTP correction, a manual
    /// change); <see cref="AdvanceMonotonic"/> moves only the monotonic one.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _wallTicks = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        private long _monotonicTicks = 1_000_000_000;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _wallTicks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref _monotonicTicks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _wallTicks, by.Ticks);
            Interlocked.Add(ref _monotonicTicks, by.Ticks);
        }

        public void StepWall(TimeSpan by) => Interlocked.Add(ref _wallTicks, by.Ticks);
        public void AdvanceMonotonic(TimeSpan by) => Interlocked.Add(ref _monotonicTicks, by.Ticks);
    }

    private static (ControlIndicatorState State, FakeClock Clock) Make(TimeSpan? linger = null)
    {
        var clock = new FakeClock();
        return (new ControlIndicatorState(linger ?? Linger, clock), clock);
    }

    // ---- R2 - lifetime -------------------------------------------------------------------------

    [Fact]
    public void A_new_state_is_hidden_with_nothing_in_flight()
    {
        var (state, _) = Make();

        state.ShouldShow.Should().BeFalse("nothing has driven the desktop yet");
        state.Active.Should().Be(0);
    }

    [Fact]
    public void Begin_makes_it_visible_and_counts_the_call()
    {
        var (state, _) = Make();

        state.Begin();

        state.ShouldShow.Should().BeTrue("the indicator appears when a driving tool starts");
        state.Active.Should().Be(1);
    }

    [Fact]
    public void It_stays_visible_however_long_a_call_is_in_flight()
    {
        // A drag of 10 s or a type of a long text: the indicator is not on a timer while a call runs.
        var (state, clock) = Make();
        state.Begin();

        clock.Advance(TimeSpan.FromHours(1));

        state.ShouldShow.Should().BeTrue("the fade only starts when the last call ends");
    }

    [Fact]
    public void Overlapping_begins_are_refcounted_and_it_stays_up_until_the_last_end()
    {
        // HTTP clients can overlap calls; the first call to finish must not take it down.
        var (state, clock) = Make();
        state.Begin();
        state.Begin();

        state.End();
        clock.Advance(TimeSpan.FromHours(1));

        state.Active.Should().Be(1, "one call is still in flight");
        state.ShouldShow.Should().BeTrue("a call is still in flight, so no fade has started");
    }

    [Fact]
    public void After_the_last_end_it_stays_visible_for_exactly_the_linger_and_is_hidden_after()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();

        state.Active.Should().Be(0);
        state.ShouldShow.Should().BeTrue("it fades Linger after the last call ends, not at once");

        clock.Advance(Linger - OneTick);
        state.ShouldShow.Should().BeTrue("one tick before the deadline it is still up");

        clock.Advance(OneTick);
        state.ShouldShow.Should().BeFalse("at the deadline (now == end + linger) it is gone: visible while now < deadline");
    }

    [Fact]
    public void The_linger_is_measured_from_the_last_end_not_from_the_first_begin()
    {
        var (state, clock) = Make();
        state.Begin();
        clock.Advance(TimeSpan.FromMinutes(10));

        state.End();
        clock.Advance(Linger - OneTick);

        state.ShouldShow.Should().BeTrue("the call ended Linger-1tick ago; how long it ran does not matter");
    }

    [Fact]
    public void The_linger_after_overlapping_calls_starts_at_the_last_end()
    {
        var (state, clock) = Make();
        state.Begin();
        state.Begin();
        state.End();
        clock.Advance(TimeSpan.FromSeconds(4));
        state.End();

        clock.Advance(Linger - OneTick);
        state.ShouldShow.Should().BeTrue("the first End armed nothing: a call was still in flight");

        clock.Advance(OneTick);
        state.ShouldShow.Should().BeFalse();
    }

    [Fact]
    public void A_begin_during_the_fade_cancels_the_fade()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();
        clock.Advance(TimeSpan.FromSeconds(3));

        state.Begin();
        clock.Advance(TimeSpan.FromHours(1));

        state.ShouldShow.Should().BeTrue("a new driving call during the fade keeps it up, for as long as it runs");
        state.Active.Should().Be(1);
    }

    [Fact]
    public void After_a_cancelled_fade_the_next_end_gives_a_full_linger_again()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();
        clock.Advance(TimeSpan.FromSeconds(4));
        state.Begin();
        state.End();

        clock.Advance(Linger - OneTick);
        state.ShouldShow.Should().BeTrue("the old deadline was cleared by the Begin; the new one is end + linger");

        clock.Advance(OneTick);
        state.ShouldShow.Should().BeFalse();
    }

    [Fact]
    public void A_zero_linger_hides_it_the_moment_the_last_call_ends()
    {
        var (state, _) = Make(TimeSpan.Zero);
        state.Begin();

        state.End();

        state.ShouldShow.Should().BeFalse("now < now + 0 is false: no fade at all");
    }

    [Fact]
    public void An_unbalanced_end_clamps_at_zero()
    {
        var (state, _) = Make();

        state.End();
        state.End();

        state.Active.Should().Be(0, "the refcount never goes negative");
    }

    [Fact]
    public void An_unbalanced_end_on_a_hidden_state_does_not_show_it()
    {
        // Ambiguity resolved (see the RED report): an End that decrements nothing is not a call that
        // just finished, so it arms no fade. The indicator appears only when a driving tool starts.
        var (state, _) = Make();

        state.End();

        state.ShouldShow.Should().BeFalse("nothing started, so there is nothing to fade out of");
    }

    [Fact]
    public void After_an_unbalanced_end_the_next_call_still_shows_and_still_fades()
    {
        // Upstream #456 latched forever on a missed key-up. A negative refcount would do the same
        // thing in reverse here: Begin would bring it to 0 and the indicator would never appear,
        // or a later End would never reach 0 and it would never fade.
        var (state, clock) = Make();
        state.End();

        state.Begin();
        state.ShouldShow.Should().BeTrue("the clamp means this Begin counts from zero");
        state.Active.Should().Be(1);

        state.End();
        state.Active.Should().Be(0);
        clock.Advance(Linger);
        state.ShouldShow.Should().BeFalse("and the balanced End still starts the fade");
    }

    // ---- R3 - takeover -------------------------------------------------------------------------

    [Fact]
    public void A_takeover_while_visible_hides_it_at_once_even_with_a_call_in_flight()
    {
        var (state, _) = Make();
        state.Begin();

        var record = state.Takeover(Hotkey);

        record.Should().NotBeNull("the indicator was up, so the combination counts as a takeover");
        state.ShouldShow.Should().BeFalse("takeover hides it immediately, whatever is still in flight");
        state.Active.Should().Be(1, "the call itself is not cancelled - nothing is ever refused or cancelled");
    }

    [Fact]
    public void A_takeover_records_the_moment_and_the_combination()
    {
        var (state, clock) = Make();
        clock.Advance(TimeSpan.FromMinutes(17));
        var now = clock.GetUtcNow();
        state.Begin();

        var record = state.Takeover(Hotkey);

        record.Should().Be(new TakeoverRecord(now, Hotkey));
    }

    [Fact]
    public void A_takeover_during_the_fade_hides_it_and_is_recorded()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();
        clock.Advance(TimeSpan.FromSeconds(2));

        var record = state.Takeover(Hotkey);

        record.Should().NotBeNull("the indicator is still up while it fades");
        state.ShouldShow.Should().BeFalse();
        state.Consume().Should().Be(record);
    }

    [Fact]
    public void A_takeover_on_a_state_that_was_never_shown_records_nothing()
    {
        var (state, _) = Make();

        state.Takeover(Hotkey).Should().BeNull("there was nothing on screen to take over from");
        state.Consume().Should().BeNull("and so there is nothing to tell the AI");
    }

    [Fact]
    public void A_takeover_after_the_fade_records_nothing()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();
        clock.Advance(Linger);

        state.Takeover(Hotkey).Should().BeNull("the indicator had already faded");
        state.Consume().Should().BeNull();
    }

    [Fact]
    public void A_second_takeover_while_already_dismissed_records_nothing_and_keeps_the_first()
    {
        var (state, clock) = Make();
        state.Begin();
        var first = state.Takeover(Hotkey);
        clock.Advance(TimeSpan.FromSeconds(1));

        state.Takeover("Ctrl+Alt+Shift+F13").Should().BeNull("the indicator is hidden now");

        state.Consume().Should().Be(first, "the record of the real takeover is not overwritten");
    }

    [Fact]
    public void The_pending_takeover_is_consumed_exactly_once()
    {
        var (state, _) = Make();
        state.Begin();
        var record = state.Takeover(Hotkey);

        state.Consume().Should().Be(record, "the next tool result carries the note");
        state.Consume().Should().BeNull("and only that one");
        state.Consume().Should().BeNull();
    }

    [Fact]
    public void Consume_with_nothing_pending_is_null()
    {
        var (state, _) = Make();
        state.Begin();
        state.End();

        state.Consume().Should().BeNull();
    }

    [Fact]
    public void The_next_begin_after_a_takeover_shows_it_again()
    {
        var (state, _) = Make();
        state.Begin();
        state.Takeover(Hotkey);
        state.End();

        state.Begin();

        state.ShouldShow.Should().BeTrue("the next driving call shows the indicator again");
    }

    [Fact]
    public void A_call_that_was_in_flight_ending_after_a_takeover_does_not_bring_it_back()
    {
        var (state, clock) = Make();
        state.Begin();
        state.Takeover(Hotkey);

        state.End();

        state.ShouldShow.Should().BeFalse("the End of a call that started before the takeover arms no fade");
        clock.Advance(TimeSpan.FromSeconds(1));
        state.ShouldShow.Should().BeFalse();
    }

    [Fact]
    public void With_two_calls_in_flight_a_takeover_holds_until_a_new_call_starts()
    {
        var (state, _) = Make();
        state.Begin();
        state.Begin();
        state.Takeover(Hotkey);

        state.End();
        state.ShouldShow.Should().BeFalse("one of the old calls ended; the user still has the controls");

        state.Begin();
        state.ShouldShow.Should().BeTrue("a NEW driving call is what shows it again");
        state.Active.Should().Be(2);
    }

    [Fact]
    public void After_a_reshow_a_second_takeover_is_recorded_and_the_latest_is_what_is_consumed()
    {
        // Ambiguity resolved (see the RED report): two takeovers with no tool result in between
        // deliver ONE note, for the most recent one - "the user has the controls since T2".
        var (state, clock) = Make();
        state.Begin();
        state.Takeover(Hotkey);
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Begin();
        var second = state.Takeover(Hotkey);

        second.Should().NotBeNull("the indicator was shown again by the new call");
        state.Consume().Should().Be(second);
        state.Consume().Should().BeNull("one note, not two");
    }

    [Fact]
    public void After_a_consumed_takeover_a_later_cycle_can_be_taken_over_again()
    {
        var (state, clock) = Make();
        state.Begin();
        state.Takeover(Hotkey);
        state.End();
        state.Consume();
        clock.Advance(TimeSpan.FromMinutes(1));

        state.Begin();
        var again = state.Takeover(Hotkey);

        again.Should().Be(new TakeoverRecord(clock.GetUtcNow(), Hotkey));
        state.Consume().Should().Be(again);
    }

    // ---- R2/R3 - concurrency ---------------------------------------------------------------------

    [Fact]
    public void Parallel_begin_end_pairs_leave_nothing_in_flight_and_the_fade_armed()
    {
        var (state, clock) = Make();

        Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            state.Begin();
            state.End();
        });

        state.Active.Should().Be(0, "every Begin was matched by an End; a lost update leaves a call 'in flight' forever");
        state.ShouldShow.Should().BeTrue("the last End armed the fade and the clock has not moved");
        clock.Advance(Linger);
        state.ShouldShow.Should().BeFalse("and the fade completes - it did not latch");
    }

    [Fact]
    public void Parallel_begins_then_parallel_ends_count_exactly()
    {
        var (state, _) = Make();

        Parallel.For(0, 5_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => state.Begin());
        state.Active.Should().Be(5_000);

        Parallel.For(0, 5_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => state.End());
        state.Active.Should().Be(0);
    }

    [Fact]
    public void Concurrent_takeovers_record_exactly_one()
    {
        var (state, _) = Make();
        state.Begin();
        var records = new System.Collections.Concurrent.ConcurrentBag<TakeoverRecord>();

        Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            if (state.Takeover(Hotkey) is { } r) records.Add(r);
        });

        records.Should().ContainSingle("the first takeover dismisses it; every later one sees it hidden");
    }

    [Fact]
    public void Concurrent_consumers_get_the_pending_takeover_exactly_once()
    {
        // Overlapping HTTP calls each finish through the filter; only one of them carries the note.
        var (state, _) = Make();
        state.Begin();
        state.Takeover(Hotkey);
        var delivered = 0;

        Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            if (state.Consume() is not null) Interlocked.Increment(ref delivered);
        });

        delivered.Should().Be(1);
    }

    // ---- the default clock ----------------------------------------------------------------------

    [Fact]
    public void With_no_clock_given_the_fade_runs_on_real_time()
    {
        var state = new ControlIndicatorState(TimeSpan.FromMilliseconds(500));
        state.Begin();
        state.End();

        state.ShouldShow.Should().BeTrue("500 ms have not passed yet");
        Thread.Sleep(1200);
        state.ShouldShow.Should().BeFalse("the default clock is the system clock, not a frozen one");
    }

    // ---- F4 - the fade is measured on the monotonic clock; the takeover is stamped on the wall one --

    [Fact]
    public void The_fade_ends_on_the_monotonic_clock_even_when_the_wall_clock_steps_back()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();

        clock.StepWall(TimeSpan.FromHours(-1));   // an NTP correction or a manual change during the linger
        clock.AdvanceMonotonic(Linger - OneTick);
        state.ShouldShow.Should().BeTrue("the linger has not elapsed yet");
        clock.AdvanceMonotonic(OneTick);

        state.ShouldShow.Should().BeFalse(
            "the linger has elapsed; a wall clock that stepped back an hour must not keep the indicator - "
            + "and the user's combination - up for another hour");
    }

    [Fact]
    public void A_wall_clock_step_forward_does_not_cut_the_linger_short()
    {
        var (state, clock) = Make();
        state.Begin();
        state.End();

        clock.StepWall(TimeSpan.FromHours(1));

        state.ShouldShow.Should().BeTrue("no time has passed: a clock step is not the linger elapsing");
        clock.AdvanceMonotonic(Linger - OneTick);
        state.ShouldShow.Should().BeTrue();
        clock.AdvanceMonotonic(OneTick);
        state.ShouldShow.Should().BeFalse("exactly the linger, measured on the monotonic clock");
    }

    [Fact]
    public void A_takeover_during_the_linger_counts_after_a_wall_clock_step_forward()
    {
        // The takeover check asks the same question as the tick: is it on screen? A wall-clock step
        // must not make a visible indicator un-take-over-able.
        var (state, clock) = Make();
        state.Begin();
        state.End();
        clock.StepWall(TimeSpan.FromHours(1));

        var record = state.Takeover(Hotkey);

        record.Should().NotBeNull("the indicator was still lingering on screen when the user pressed the combination");
        state.ShouldShow.Should().BeFalse("and the takeover hides it");
    }

    [Fact]
    public void A_takeover_is_stamped_with_the_wall_clock_not_the_monotonic_one()
    {
        // At is what the note shows the agent ("at 14:03:07"): wall-clock time, whatever the
        // monotonic clock - which has no calendar meaning - says.
        var (state, clock) = Make();
        state.Begin();
        clock.StepWall(TimeSpan.FromHours(-1));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(2));
        var wallNow = clock.GetUtcNow();

        state.Takeover(Hotkey)!.At.Should().Be(wallNow);
    }
}
