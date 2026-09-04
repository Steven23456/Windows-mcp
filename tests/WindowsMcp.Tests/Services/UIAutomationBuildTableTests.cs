using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-13 R3.3: the projection <c>get_table</c> uses to turn the raw strings read off a UIA grid
/// into <see cref="TableData"/>. <c>BuildTable</c> is <c>internal static</c> precisely so this can
/// run headless - a GridPattern cannot be faked (<c>AutomationElement</c> is sealed and COM-backed),
/// so <c>GetTableAsync</c> keeps the pattern reads and every rule that acts on the strings lives
/// here.
/// </summary>
/// <remarks>
/// Non-ASCII inputs are written as numeric code points (as in <see cref="UiTextTests"/>) so the
/// file stays pure ASCII and no editor or source-encoding step can quietly change the thing being
/// asserted.
/// </remarks>
[Trait("Category", "Unit")]
public class UIAutomationBuildTableTests
{
    /// <summary>One UTF-16 code unit - the only way to write a LONE surrogate.</summary>
    private static string Ch(int codeUnit) => ((char)codeUnit).ToString();

    /// <summary>One code point, encoded as a surrogate pair when it is supplementary.</summary>
    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string Pua = Ch(0xE0B0);          // a codicon / powerline glyph
    private static readonly string Emoji = Cp(0x1F600);       // grinning face, a valid pair
    private static readonly string Replacement = Ch(0xFFFD);

    private static readonly string?[][] NoRows = [];

    [Fact]
    public void BuildTable_sanitises_every_header()
    {
        var table = Build(
            [Pua + " Name", "Size" + Ch(0xD83D), "  Type  ", "a" + Ch(0x0001) + "b"],
            NoRows);

        table.Headers.Should().Equal("Name", "Size" + Replacement, "Type", "ab");
    }

    [Fact]
    public void BuildTable_sanitises_every_cell()
    {
        var table = Build(
            ["h0", "h1"],
            [
                [Pua + " left", "right" + Ch(0xDC00)],
                ["  padded  ", "ctl" + Ch(0x007F)],
            ]);

        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("left", "right" + Replacement);
        table.Rows[1].Should().Equal("padded", "ctl");
    }

    // A column whose header element the TablePattern did not supply arrives as null. Before A-13
    // that null was serialised as JSON null in a string[] the DTO declares non-nullable; the
    // projection makes it "".
    [Fact]
    public void BuildTable_turns_a_missing_header_into_an_empty_string()
    {
        var table = Build([null, "Size", null], NoRows);

        table.Headers.Should().Equal("", "Size", "");
        table.Headers.Should().NotContainNulls("a column with no header element must be \"\", never null");
    }

    [Fact]
    public void BuildTable_turns_a_null_cell_into_an_empty_string()
    {
        var table = Build(["h"], [[null], ["v"]]);

        table.Rows[0].Should().Equal("");
        table.Rows[1].Should().Equal("v");
    }

    [Fact]
    public void BuildTable_preserves_the_grid_shape_and_the_cell_order()
    {
        var table = Build(
            ["c0", "c1", "c2"],
            [
                ["r0c0", "r0c1", "r0c2"],
                ["r1c0", "r1c1", "r1c2"],
            ]);

        table.Headers.Should().Equal("c0", "c1", "c2");
        table.Rows.Should().HaveCount(2);
        table.Rows[0].Should().Equal("r0c0", "r0c1", "r0c2");
        table.Rows[1].Should().Equal("r1c0", "r1c1", "r1c2");
    }

    [Fact]
    public void BuildTable_on_an_empty_grid_returns_empty_arrays_not_null()
    {
        var table = Build([], NoRows);

        table.Headers.Should().NotBeNull().And.BeEmpty();
        table.Rows.Should().NotBeNull().And.BeEmpty();
    }

    // GetTableAsync builds rectangular input, but the projection must not silently reshape: each
    // row keeps its own length so a shape bug upstream stays visible instead of being padded away.
    [Fact]
    public void BuildTable_keeps_each_rows_own_length()
    {
        var table = Build(["h0", "h1"], [["a"], ["b", "c"], []]);

        table.Rows[0].Should().HaveCount(1);
        table.Rows[1].Should().HaveCount(2);
        table.Rows[2].Should().BeEmpty();
    }

    [Fact]
    public void BuildTable_keeps_valid_text_intact()
    {
        var cjk = Cp(0x65E5) + Cp(0x672C);

        var table = Build([Emoji + " col"], [[cjk], ["tab\there"]]);

        table.Headers.Should().Equal(Emoji + " col");
        table.Rows[0].Should().Equal(cjk);
        table.Rows[1].Should().ContainSingle().Which.Should().Be("tab\there",
            "tab is exempt from the control-character rule");
    }

    // R4 for get_table: what the tool serialises must survive the encoder unchanged.
    [Fact]
    public void BuildTable_result_round_trips_through_the_json_encoder()
    {
        var table = Build([Pua + " Name"], [[Emoji + " cell" + Ch(0xD83D)]]);

        var serialize = () => JsonSerializer.Serialize(table);
        serialize.Should().NotThrow();

        var json = JsonSerializer.Serialize(table);
        json.Should().NotContain("\\uE0B0", "the codicon must be gone before the encoder ever sees it");

        var back = JsonSerializer.Deserialize<TableData>(json);
        back.Should().NotBeNull();
        back!.Headers.Should().ContainSingle().Which.Should().Be("Name",
            "the codicon and the space it left are gone from the header");
        back.Headers.Should().Equal(table.Headers);
        back.Rows[0].Should().Equal(table.Rows[0]);
        back.Rows[0][0].Should().Be(Emoji + " cell" + Replacement,
            "the lone surrogate was repaired before serialisation, not by it");
    }

    private static TableData Build(string?[] headers, string?[][] cells)
        => UIAutomationService.BuildTable(headers, cells);
}
