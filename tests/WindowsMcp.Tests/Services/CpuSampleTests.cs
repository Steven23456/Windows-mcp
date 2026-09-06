using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-3 R1: the pure half of the CPU column. The normalisation is the part a reader has to trust
/// ("12.5 means one of my eight cores"), and the ordering is what <c>sort_by</c> promises — both
/// decided here, with no process table in the way.
/// </summary>
[Trait("Category", "Unit")]
public class CpuSampleTests
{
    private static double Percent(double deltaSeconds, double elapsedSeconds, int cores) =>
        CpuSample.Percent(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(deltaSeconds),
            TimeSpan.FromSeconds(elapsedSeconds),
            cores);

    [Fact]
    public void Percent_of_one_saturated_core_out_of_eight_is_twelve_point_five()
    {
        // The headline case: Task Manager shows 12.5 for a single-threaded spinner on 8 cores.
        Percent(1.0, 1.0, 8).Should().Be(12.5);
    }

    [Fact]
    public void Percent_of_half_a_core_out_of_four_is_twelve_point_five()
    {
        Percent(0.5, 1.0, 4).Should().Be(12.5);
    }

    [Fact]
    public void Percent_of_every_core_is_a_hundred()
    {
        Percent(8.0, 1.0, 8).Should().Be(100);
    }

    [Fact]
    public void Percent_is_normalised_over_the_sample_window_not_a_fixed_second()
    {
        // 0.5 s of CPU across a 250 ms window on 8 cores = 25 %.
        Percent(0.5, 0.25, 8).Should().Be(25);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Percent_is_zero_when_no_time_elapsed(double elapsedSeconds)
    {
        Percent(1.0, elapsedSeconds, 8).Should().Be(0, "a zero or negative window cannot be divided by");
    }

    [Fact]
    public void Percent_is_zero_when_the_delta_is_negative()
    {
        // A process that exits between the samples (or a counter that goes backwards) reads 0.
        CpuSample.Percent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 8)
            .Should().Be(0);
    }

    [Fact]
    public void Percent_is_zero_when_nothing_was_used()
    {
        Percent(0.0, 1.0, 8).Should().Be(0);
    }

    [Fact]
    public void Percent_is_clamped_to_a_hundred()
    {
        Percent(20.0, 1.0, 8).Should().Be(100, "250 % is a sampling artefact, not a number to report");
    }

    [Fact]
    public void Percent_is_zero_when_the_core_count_is_not_positive()
    {
        Percent(1.0, 1.0, 0).Should().Be(0, "a divide by zero must not reach the caller as NaN or Infinity");
    }

    [Fact]
    public void Percent_is_rounded_to_one_decimal()
    {
        // 1 s over 1 s on 3 cores = 33.3333...
        Percent(1.0, 1.0, 3).Should().Be(33.3);
    }

    // ---- SortAndLimit --------------------------------------------------------------------------

    private static ProcessDto[] Rows() =>
    [
        new(3, "beta", null, 50, 10),
        new(1, "Alpha", null, 200, 5),
        new(2, "gamma", null, 100, 30),
        new(4, "alpha", null, 10, 0),
    ];

    private static int[] Pids(ProcessDto[] rows) => rows.Select(r => r.Pid).ToArray();

    [Fact]
    public void SortAndLimit_orders_memory_descending()
    {
        var sorted = CpuSample.SortAndLimit(Rows(), ProcessSort.Memory, 0);

        Pids(sorted).Should().Equal(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void SortAndLimit_orders_cpu_descending()
    {
        var sorted = CpuSample.SortAndLimit(Rows(), ProcessSort.Cpu, 0);

        Pids(sorted).Should().Equal(new[] { 2, 3, 1, 4 });
    }

    [Fact]
    public void SortAndLimit_orders_name_ascending_ignoring_case_then_by_pid()
    {
        var sorted = CpuSample.SortAndLimit(Rows(), ProcessSort.Name, 0);

        Pids(sorted).Should().Equal(new[] { 1, 4, 3, 2 },
            "'Alpha' and 'alpha' sort together ordinal-ignore-case, and the lower pid comes first");
    }

    [Fact]
    public void SortAndLimit_orders_pid_ascending()
    {
        var sorted = CpuSample.SortAndLimit(Rows(), ProcessSort.Pid, 0);

        Pids(sorted).Should().Equal(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void SortAndLimit_with_limit_zero_returns_every_row()
    {
        CpuSample.SortAndLimit(Rows(), ProcessSort.Memory, 0).Should().HaveCount(4,
            "0 means all, so no existing caller loses rows");
    }

    [Fact]
    public void SortAndLimit_applies_the_limit_after_the_sort()
    {
        var top = CpuSample.SortAndLimit(Rows(), ProcessSort.Cpu, 2);

        Pids(top).Should().Equal(new[] { 2, 3 }, "the cap keeps the busiest, not the first two seen");
    }

    [Fact]
    public void SortAndLimit_with_a_limit_past_the_end_returns_every_row()
    {
        CpuSample.SortAndLimit(Rows(), ProcessSort.Pid, 99).Should().HaveCount(4);
    }

    [Fact]
    public void SortAndLimit_of_nothing_is_nothing()
    {
        CpuSample.SortAndLimit(Array.Empty<ProcessDto>(), ProcessSort.Cpu, 5).Should().BeEmpty();
    }
}
