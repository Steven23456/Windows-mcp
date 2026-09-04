using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class BoundedTextBufferTests
{
    [Fact]
    public void Append_under_capacity_round_trips_with_nothing_trimmed()
    {
        var buf = new BoundedTextBuffer(100);
        buf.Append("hello ");
        buf.Append("world");

        buf.Snapshot().Should().Be("hello world");
        buf.Length.Should().Be(11);
        buf.TrimmedChars.Should().Be(0);
    }

    [Fact]
    public void Append_over_capacity_keeps_the_most_recent_tail_and_counts_trimmed()
    {
        var buf = new BoundedTextBuffer(10);
        buf.Append("01234");
        buf.Append("56789AB");   // total 12 -> retain last 10

        buf.Snapshot().Should().Be("23456789AB");
        buf.TrimmedChars.Should().Be(2);
    }

    [Fact]
    public void Append_single_write_larger_than_capacity_keeps_only_its_tail()
    {
        var buf = new BoundedTextBuffer(4);
        buf.Append("0123456789");

        buf.Snapshot().Should().Be("6789");
        buf.TrimmedChars.Should().Be(6);
    }

    [Fact]
    public void Tail_returns_last_n_chars_and_everything_for_zero_or_oversized_requests()
    {
        var buf = new BoundedTextBuffer(100);
        buf.Append("abcdef");

        buf.Tail(3).Should().Be("def");
        buf.Tail(0).Should().Be("abcdef");
        buf.Tail(100).Should().Be("abcdef");
    }

    [Fact]
    public void Capacity_clamps_to_at_least_one_char()
    {
        var buf = new BoundedTextBuffer(0);
        buf.Append("ab");

        buf.Snapshot().Should().Be("b");
        buf.Length.Should().Be(1);
    }

    // D-9: a finished job's stderr buffer is rewritten in place with its decoded text.
    [Fact]
    public void ReplaceAll_swaps_the_retained_text_and_keeps_the_trim_count()
    {
        var buf = new BoundedTextBuffer(10);
        buf.Append("0123456789ABCDE");        // 5 chars trimmed from the front
        var trimmedBefore = buf.TrimmedChars;
        trimmedBefore.Should().Be(5);

        buf.ReplaceAll("decoded");

        buf.Snapshot().Should().Be("decoded");
        buf.Length.Should().Be(7);
        buf.TrimmedChars.Should().Be(trimmedBefore, "TrimmedChars counts what was lost from the RAW stream");
    }

    [Fact]
    public void ReplaceAll_still_honours_the_capacity()
    {
        var buf = new BoundedTextBuffer(4);
        buf.ReplaceAll("abcdefgh");

        buf.Snapshot().Should().Be("efgh");
        buf.Length.Should().Be(4);
        buf.TrimmedChars.Should().Be(4);
    }
}
