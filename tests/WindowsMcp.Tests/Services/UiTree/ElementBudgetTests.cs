using FluentAssertions;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-4 (R4): the count budget, on its own. The guarantee it makes is the one thing standing
/// between a 5 000-row grid and a snapshot that never returns, so the off-by-one at the cap and
/// the behaviour of the call that goes over it are pinned exactly.
/// </summary>
[Trait("Category", "Unit")]
public class ElementBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Ctor_rejects_a_limit_below_one(int limit)
    {
        // A budget of 0 would report "truncated" for an empty desktop and walk nothing at all;
        // that is a caller bug (max_elements: 0 means "server default" upstream of here).
        var act = () => new ElementBudget(limit);
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*limit*");
    }

    [Fact]
    public void A_new_budget_has_spent_nothing_and_is_not_truncated()
    {
        var budget = new ElementBudget(500);
        budget.Limit.Should().Be(500);
        budget.Count.Should().Be(0);
        budget.Truncated.Should().BeFalse();
    }

    [Fact]
    public void TryTake_allows_exactly_the_limit()
    {
        var budget = new ElementBudget(3);

        budget.TryTake().Should().BeTrue();
        budget.TryTake().Should().BeTrue();
        budget.TryTake().Should().BeTrue();

        budget.Count.Should().Be(3);
        budget.Truncated.Should().BeFalse("spending the whole budget is not the same as running out of it");
    }

    [Fact]
    public void TryTake_refuses_the_first_element_past_the_limit_and_records_the_truncation()
    {
        var budget = new ElementBudget(3);
        for (int i = 0; i < 3; i++) budget.TryTake();

        budget.TryTake().Should().BeFalse();

        budget.Truncated.Should().BeTrue();
        budget.Count.Should().Be(3, "a refused element was never taken, so it must not be counted");
    }

    [Fact]
    public void TryTake_keeps_refusing_and_the_count_stops_growing()
    {
        // The traverser keeps offering elements until it unwinds; the budget must not drift up
        // with every rejected one or ElementCount becomes a lie.
        var budget = new ElementBudget(2);
        budget.TryTake();
        budget.TryTake();

        for (int i = 0; i < 50; i++) budget.TryTake().Should().BeFalse();

        budget.Count.Should().Be(2);
        budget.Truncated.Should().BeTrue();
    }

    [Fact]
    public void A_budget_of_one_allows_one_element()
    {
        var budget = new ElementBudget(1);
        budget.TryTake().Should().BeTrue();
        budget.Truncated.Should().BeFalse();
        budget.TryTake().Should().BeFalse();
        budget.Truncated.Should().BeTrue();
        budget.Count.Should().Be(1);
    }

    [Fact]
    public void Note_is_the_truncation_sentence_the_agent_reads()
        => new ElementBudget(500).Note().Should().Be(SnapshotFixtures.TruncationNote(500));

    [Fact]
    public void Note_names_the_limit_that_was_actually_in_force()
        => new ElementBudget(7).Note().Should().Be(
            "Truncated at 7 elements. Narrow the view (scope=foreground, or window=<title>) or raise max_elements.");

    [Fact]
    public void Note_reads_the_same_before_and_after_the_budget_runs_out()
    {
        // The renderer asks for the note only when Truncated, but the text is a pure function of
        // the limit - it must not depend on how much of the budget was spent.
        var budget = new ElementBudget(4);
        var before = budget.Note();
        for (int i = 0; i < 6; i++) budget.TryTake();
        budget.Note().Should().Be(before);
    }
}
