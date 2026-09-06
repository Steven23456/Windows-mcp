using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-11: <c>start_process</c>'s <c>args_json</c>, parsed. Pure, so every accepted and rejected
/// shape is a <c>Category=Unit</c> row rather than a spawned process.
/// </summary>
[Trait("Category", "Unit")]
public class ArgvJsonTests
{
    [Fact]
    public void A_json_array_of_strings_becomes_the_argv_list_in_order()
    {
        ArgvJson.Parse("""["/c","echo","a \"quoted\" b"]""")
            .Should().Equal("/c", "echo", "a \"quoted\" b");
    }

    [Fact]
    public void Items_are_taken_verbatim_with_no_quoting_or_splitting()
    {
        // The entire point of argv: an argument containing spaces, quotes and backslashes is one
        // argument, and nothing in this layer tries to escape it.
        ArgvJson.Parse("""["C:\\path with space\\a.txt","--flag=\"x y\"","tail\\"]""")
            .Should().Equal("C:\\path with space\\a.txt", "--flag=\"x y\"", "tail\\");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_given_means_no_argv_list_at_all(string? argsJson)
    {
        // null, not an empty array: the difference decides whether `command` is a whole command
        // line (today's behaviour) or an executable path.
        ArgvJson.Parse(argsJson).Should().BeNull();
    }

    [Fact]
    public void An_empty_array_is_an_empty_argv_list_not_null()
    {
        ArgvJson.Parse("[]").Should().NotBeNull().And.BeEmpty(
            "an explicit empty array still means argv mode: the command is an executable path");
    }

    [Theory]
    [InlineData("\"notastring\"", "a JSON string is not an array")]
    [InlineData("notastring", "unquoted text is not JSON at all")]
    [InlineData("{}", "an object is not an array")]
    [InlineData("""{"0":"a"}""", "an object with numeric keys is still not an array")]
    [InlineData("[1,2]", "numbers are not arguments")]
    [InlineData("""["ok",null]""", "null is not an argument")]
    [InlineData("""["ok",["nested"]]""", "a nested array is not an argument")]
    [InlineData("""["ok",{"a":1}]""", "an object is not an argument")]
    [InlineData("[true]", "a boolean is not an argument")]
    [InlineData("""["unterminated""", "malformed JSON is refused, not half-parsed")]
    [InlineData("42", "a bare number is not an array")]
    public void Anything_that_is_not_an_array_of_strings_is_refused_by_name(string argsJson, string why)
    {
        var act = () => ArgvJson.Parse(argsJson);

        act.Should().Throw<ArgumentException>(why)
            .Which.Message.Should().Contain("args_json",
                "the model sent a parameter by that name and has to be told which one was wrong");
    }

    [Fact]
    public void Whitespace_around_the_array_is_tolerated()
    {
        // The Claude Desktop quirk arrives as a JSON-stringified array, sometimes padded.
        ArgvJson.Parse("  [\"a\", \"b\"]  ").Should().Equal("a", "b");
    }

    [Fact]
    public void An_empty_string_argument_is_kept()
    {
        // "" is a legitimate argv entry (many CLIs treat it as an explicit empty value) and must
        // not be confused with "no arguments".
        ArgvJson.Parse("""["a","","b"]""").Should().Equal("a", "", "b");
    }

    [Fact]
    public void Unicode_and_newlines_inside_an_argument_survive()
    {
        ArgvJson.Parse("""["caf\u00e9","line1\r\nline2"]""")
            .Should().Equal("café", "line1\r\nline2");
    }
}
