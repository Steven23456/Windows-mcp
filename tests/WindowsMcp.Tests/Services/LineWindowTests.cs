using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R1: the pure line window <c>file_read(offset_lines, limit_lines)</c> cuts after decoding.
/// The whole point of extracting it is that CRLF-vs-LF, the 1-based offset and the "lines remain"
/// flag are decided here, with no file and no encoding in the way.
/// </summary>
[Trait("Category", "Unit")]
public class LineWindowTests
{
    private const string FiveLf = "alpha\nbravo\ncharlie\ndelta\necho";
    private const string FiveCrLf = "alpha\r\nbravo\r\ncharlie\r\ndelta\r\necho";

    [Fact]
    public void Slice_with_no_window_returns_the_whole_text_untruncated()
    {
        var window = LineWindow.Slice(FiveLf, 0, 0);

        window.TotalLines.Should().Be(5);
        window.Offset.Should().Be(1, "0 and 1 both mean the first line, and the result reports the 1-based start");
        window.Returned.Should().Be(5);
        window.Truncated.Should().BeFalse();
        window.Content.Should().Be(FiveLf);
    }

    [Fact]
    public void Slice_counts_a_crlf_file_the_same_as_an_lf_one_and_strips_the_carriage_returns()
    {
        var lf = LineWindow.Slice(FiveLf, 0, 0);
        var crlf = LineWindow.Slice(FiveCrLf, 0, 0);

        crlf.TotalLines.Should().Be(lf.TotalLines, "a trailing \\r is stripped, so line 3 is line 3 either way");
        crlf.Content.Should().Be(lf.Content).And.NotContain("\r", "the window joins with \\n");
    }

    [Theory]
    [InlineData("alpha\nbravo\n")]
    [InlineData("alpha\r\nbravo\r\n")]
    public void Slice_does_not_count_a_final_newline_as_an_extra_empty_line(string text)
    {
        var window = LineWindow.Slice(text, 0, 0);

        window.TotalLines.Should().Be(2);
        window.Returned.Should().Be(2);
        window.Content.Should().Be("alpha\nbravo");
    }

    [Fact]
    public void Slice_keeps_a_blank_line_inside_the_text()
    {
        // Only a TRAILING \r is stripped: the empty middle line is a line, not an artefact.
        var window = LineWindow.Slice("alpha\r\n\r\nbravo", 0, 0);

        window.TotalLines.Should().Be(3);
        window.Content.Should().Be("alpha\n\nbravo");
    }

    [Fact]
    public void Slice_treats_offset_zero_and_offset_one_as_the_same_first_line()
    {
        var fromZero = LineWindow.Slice(FiveLf, 0, 2);
        var fromOne = LineWindow.Slice(FiveLf, 1, 2);

        fromOne.Should().Be(fromZero, "upstream's offset is 1-based and 0 means 'from the top'");
        fromOne.Content.Should().Be("alpha\nbravo");
    }

    [Fact]
    public void Slice_with_limit_zero_reads_from_the_offset_to_the_end()
    {
        var window = LineWindow.Slice(FiveLf, 3, 0);

        window.Offset.Should().Be(3);
        window.Returned.Should().Be(3);
        window.Content.Should().Be("charlie\ndelta\necho");
        window.Truncated.Should().BeFalse("nothing remains past the end");
    }

    [Fact]
    public void Slice_of_a_window_in_the_middle_says_lines_remain()
    {
        var window = LineWindow.Slice(FiveLf, 2, 2);

        window.TotalLines.Should().Be(5);
        window.Offset.Should().Be(2);
        window.Returned.Should().Be(2);
        window.Content.Should().Be("bravo\ncharlie");
        window.Truncated.Should().BeTrue("delta and echo are past the window");
    }

    [Fact]
    public void Slice_of_the_last_window_does_not_say_lines_remain()
    {
        var window = LineWindow.Slice(FiveLf, 4, 2);

        window.Returned.Should().Be(2);
        window.Content.Should().Be("delta\necho");
        window.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Slice_with_a_limit_past_the_end_returns_what_is_there()
    {
        var window = LineWindow.Slice(FiveLf, 4, 500);

        window.Returned.Should().Be(2);
        window.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Slice_with_an_offset_past_the_end_returns_no_lines_and_is_not_truncated()
    {
        var window = LineWindow.Slice(FiveLf, 99, 10);

        window.TotalLines.Should().Be(5, "the file's size is still reported");
        window.Returned.Should().Be(0);
        window.Content.Should().BeEmpty();
        window.Truncated.Should().BeFalse("there is nothing past a window that starts past the end");
    }

    [Fact]
    public void Slice_of_an_empty_file_is_zero_lines()
    {
        var window = LineWindow.Slice(string.Empty, 0, 0);

        window.TotalLines.Should().Be(0);
        window.Returned.Should().Be(0);
        window.Content.Should().BeEmpty();
        window.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Slice_of_one_line_without_a_newline_is_one_line()
    {
        var window = LineWindow.Slice("only", 0, 0);

        window.TotalLines.Should().Be(1);
        window.Content.Should().Be("only");
    }

    [Fact]
    public void Slice_refuses_a_negative_offset()
    {
        var act = () => LineWindow.Slice(FiveLf, -1, 0);

        act.Should().Throw<ArgumentException>().WithMessage("*offset*");
    }

    [Fact]
    public void Slice_refuses_a_negative_limit()
    {
        var act = () => LineWindow.Slice(FiveLf, 0, -1);

        act.Should().Throw<ArgumentException>().WithMessage("*limit*");
    }
}
