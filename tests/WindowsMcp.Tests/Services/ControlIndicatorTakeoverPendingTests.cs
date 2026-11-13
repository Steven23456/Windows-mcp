using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-6: <see cref="ControlIndicator.TakeoverPending"/> in the states that need no window - a fresh
/// indicator that has never shown, one switched off, one disposed. "Like every member it is false,
/// never throwing, when the indicator is off or disposed." None of these starts the overlay thread
/// or creates a window, so they are Unit. The takeover itself (a posted <c>WM_HOTKEY</c> to the real
/// banner) is <see cref="ControlIndicatorTests.TakeoverPending_follows_a_real_takeover_until_it_is_consumed"/>.
/// </summary>
[Trait("Category", "Unit")]
public class ControlIndicatorTakeoverPendingTests
{
    [Fact]
    public void A_new_indicator_has_no_takeover_pending_and_creates_nothing_to_say_so()
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options());

        ci.TakeoverPending.Should().BeFalse();
        ci.RingHandle.Should().Be(0, "reading the flag never starts the overlay");
    }

    [Fact]
    public void An_indicator_that_is_off_never_has_a_takeover_pending()
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(enabled: false));
        ci.BeginAction();

        var read = () => ci.TakeoverPending;

        read.Should().NotThrow().Which.Should().BeFalse("--control-indicator off: every member is a silent no-op");
        ci.EndAction();
    }

    [Fact]
    public void A_disposed_indicator_never_has_a_takeover_pending_and_does_not_throw()
    {
        var ci = new ControlIndicator(ControlIndicatorTests.Options());
        ci.Dispose();

        var read = () => ci.TakeoverPending;

        read.Should().NotThrow("a sequence racing the host's shutdown must not fail on the indicator")
            .Which.Should().BeFalse();
    }

    // ---- TakeoverCount: inert in the same states (0, never throwing) ------------------------------

    [Fact]
    public void A_new_indicator_has_a_takeover_count_of_0_and_creates_nothing_to_say_so()
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options());

        var read = () => ci.TakeoverCount;

        read.Should().NotThrow().Which.Should().Be(0);
        ci.RingHandle.Should().Be(0, "reading the count never starts the overlay");
    }

    [Fact]
    public void An_indicator_that_is_off_has_a_takeover_count_of_0()
    {
        using var ci = new ControlIndicator(ControlIndicatorTests.Options(enabled: false));
        ci.BeginAction();

        var read = () => ci.TakeoverCount;

        read.Should().NotThrow().Which.Should().Be(0, "--control-indicator off: every member is a silent no-op");
        ci.EndAction();
    }

    [Fact]
    public void A_disposed_indicator_has_a_takeover_count_of_0_and_does_not_throw()
    {
        var ci = new ControlIndicator(ControlIndicatorTests.Options());
        ci.Dispose();

        var read = () => ci.TakeoverCount;

        read.Should().NotThrow().Which.Should().Be(0);
    }
}
