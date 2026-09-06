using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-7 (R64-R84): the pure parser behind <c>multi_select</c> and <c>multi_edit</c>. Both tools
/// take their batch as a STRING, so this is the only place a malformed batch can be caught, and
/// it has to be caught before a single click is injected — a batch that fails halfway through has
/// already changed the desktop. Every refusal names the parameter and the entry index, because
/// "invalid JSON" against a ten-entry batch is not a diagnosis.
/// </summary>
[Trait("Category", "Unit")]
public class BatchTargetsTests
{
    // ---- the two target shapes ----------------------------------------------------------------

    [Fact]
    public void ParseTargets_reads_points_and_element_ids_in_order()
    {
        var targets = BatchTargets.ParseTargets("""[{"x":10,"y":20},{"element_id":"el_3"},{"x":-5,"y":-6}]""");

        targets.Should().HaveCount(3);
        targets[0].X.Should().Be(10);
        targets[0].Y.Should().Be(20);
        targets[0].ElementId.Should().BeNull();
        targets[1].ElementId.Should().Be("el_3");
        targets[1].X.Should().BeNull();
        targets[1].Y.Should().BeNull();
        targets[2].X.Should().Be(-5, "a monitor left of the primary has negative coordinates (roadmap C2)");
        targets[2].Y.Should().Be(-6);
    }

    [Fact]
    public void ParseTargets_leaves_the_typing_options_alone()
    {
        var target = BatchTargets.ParseTargets("""[{"x":1,"y":2}]""").Should().ContainSingle().Subject;

        target.Text.Should().BeNull("multi_select clicks, it does not type");
        target.Clear.Should().BeFalse();
        target.PressEnter.Should().BeFalse();
    }

    [Fact]
    public void ParseTargets_ignores_whitespace_and_CRLF_around_the_array()
    {
        // A client that pretty-prints its argument, or a here-doc that arrives with CRLF, is not a
        // malformed batch.
        var targets = BatchTargets.ParseTargets("\r\n  [\r\n    {\"x\":1, \"y\":2}\r\n  ]  \r\n");

        targets.Should().ContainSingle().Which.X.Should().Be(1);
    }

    [Fact]
    public void ParseTargets_accepts_an_array_that_was_stringified_twice()
    {
        // The Claude Desktop quirk the checklist records: the client JSON-encodes the argument it
        // was already given as JSON, so the parameter arrives as a QUOTED array.
        var targets = BatchTargets.ParseTargets("\"[{\\\"x\\\":7,\\\"y\\\":8}]\"");

        targets.Should().ContainSingle().Subject.X.Should().Be(7);
    }

    [Fact]
    public void ParseTargets_unwraps_a_stringified_array_however_many_times_it_was_wrapped()
    {
        // Decision pinned: the unwrap is recursive, not one level. A client that double-encodes
        // (its own wrapper plus the transport's) still gets a batch rather than
        // "must be a JSON array of targets, got String" - and the cost is one extra Parse.
        var once = System.Text.Json.JsonSerializer.Serialize("""[{"x":7,"y":8}]""");
        var twice = System.Text.Json.JsonSerializer.Serialize(once);

        BatchTargets.ParseTargets(once).Should().ContainSingle().Which.X.Should().Be(7);
        BatchTargets.ParseTargets(twice).Should().ContainSingle().Which.X.Should().Be(7);
    }

    [Fact]
    public void ParseTargets_ignores_a_property_it_does_not_know()
    {
        // Upstream's payloads carry extra keys (a label, a button); an unknown key is not a reason
        // to refuse a batch whose target is perfectly well formed.
        var targets = BatchTargets.ParseTargets("""[{"x":1,"y":2,"button":"right","label":"Row 1"}]""");

        var target = targets.Should().ContainSingle().Subject;
        target.X.Should().Be(1);
        target.Y.Should().Be(2);
        target.ElementId.Should().BeNull();
    }

    [Fact]
    public void ParseTargets_reads_a_hundred_targets_in_order()
    {
        // Nothing caps the batch, and the index in every message has to stay right at the far end
        // of a long one.
        var json = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"x\":{i},\"y\":{i * 2}}}")) + "]";

        var targets = BatchTargets.ParseTargets(json);

        targets.Should().HaveCount(100);
        targets[0].X.Should().Be(0);
        targets[99].X.Should().Be(99);
        targets[99].Y.Should().Be(198);
        targets.Select(t => t.X).Should().BeInAscendingOrder("the batch runs in the order it was written");
    }

    [Fact]
    public void ParseTargets_accepts_an_element_id_that_is_an_empty_string()
    {
        // Pinned as it stands: the parser only checks the SHAPE, so "" is a target and the id is
        // resolved (and refused) by the element lookup the tool runs before any click - see
        // InputToolsBatchTests.Multi_select_refuses_an_empty_element_id_before_any_input. The
        // refusal therefore does not name the entry index; that is a known rough edge, not a
        // desktop-touching one.
        var targets = BatchTargets.ParseTargets("""[{"element_id":""}]""");

        targets.Should().ContainSingle().Which.ElementId.Should().Be("");
    }

    // ---- the refusals, each naming targets_json and the index ---------------------------------

    [Theory]
    [InlineData("""[{"x":3.5,"y":2}]""", "x")]
    [InlineData("""[{"x":1,"y":2.5}]""", "y")]
    [InlineData("""[{"x":"10","y":2}]""", "x")]
    [InlineData("""[{"x":true,"y":2}]""", "x")]
    public void ParseTargets_refuses_a_coordinate_that_is_not_a_whole_number(string json, string name)
    {
        // Coordinates are physical pixels; silently truncating 3.5 to 3 would click a pixel the
        // caller did not name, and a click one pixel out lands on the wrong control often enough.
        var act = () => BatchTargets.ParseTargets(json);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain($"targets_json[0].{name}").And.ContainEquivalentOf("integer");
    }


    [Fact]
    public void ParseTargets_refuses_an_entry_that_gives_both_a_point_and_an_id()
    {
        // Roadmap C1's exclusivity rule, one entry at a time: two targets in one entry means the
        // caller believes something that is not true about where the click will land.
        var act = () => BatchTargets.ParseTargets("""[{"x":1,"y":2},{"x":3,"y":4,"element_id":"el_3"}]""");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("targets_json").And.Contain("1");
    }

    [Fact]
    public void ParseTargets_refuses_an_entry_with_no_target_at_all()
    {
        var act = () => BatchTargets.ParseTargets("""[{"x":1,"y":2},{}]""");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("targets_json").And.Contain("1");
    }

    [Theory]
    [InlineData("""[{"x":1}]""")]
    [InlineData("""[{"y":2}]""")]
    public void ParseTargets_refuses_half_a_coordinate_pair(string json)
    {
        var act = () => BatchTargets.ParseTargets(json);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("targets_json").And.Contain("0");
    }

    [Theory]
    [InlineData("""[{"x":1,"y":2},"el_3"]""")]
    [InlineData("""[{"x":1,"y":2},42]""")]
    [InlineData("""[{"x":1,"y":2},[3,4]]""")]
    [InlineData("""[{"x":1,"y":2},null]""")]
    public void ParseTargets_refuses_an_entry_that_is_not_an_object(string json)
    {
        var act = () => BatchTargets.ParseTargets(json);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("targets_json").And.Contain("1");
    }

    [Theory]
    [InlineData("""{"x":1,"y":2}""")]
    [InlineData("\"el_3\"")]
    [InlineData("42")]
    public void ParseTargets_refuses_a_root_that_is_not_an_array(string json)
    {
        var act = () => BatchTargets.ParseTargets(json);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("targets_json");
    }

    [Theory]
    [InlineData("[{")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void ParseTargets_refuses_malformed_json_without_leaking_a_parser_exception(string json)
    {
        // A JsonException reaches the model as an unhandled tool error with no parameter name in
        // it; an ArgumentException naming targets_json is something it can act on.
        var act = () => BatchTargets.ParseTargets(json);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("targets_json");
    }

    [Fact]
    public void ParseTargets_refuses_an_empty_batch()
    {
        // A batch of nothing is a caller mistake, not a no-op: something built the array and lost
        // its contents on the way.
        var act = () => BatchTargets.ParseTargets("[]");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("targets_json");
    }

    // ---- multi_edit entries: the same targets plus the B-1 options ----------------------------

    [Fact]
    public void ParseEntries_reads_the_text_and_the_typing_options()
    {
        var entries = BatchTargets.ParseEntries(
            """[{"x":1,"y":2,"text":"alpha"},{"element_id":"el_9","text":"beta","clear":true,"press_enter":true}]""");

        entries.Should().HaveCount(2);
        entries[0].Text.Should().Be("alpha");
        entries[0].Clear.Should().BeFalse("clear defaults off - a batch must not wipe fields it was not told to");
        entries[0].PressEnter.Should().BeFalse();
        entries[1].ElementId.Should().Be("el_9");
        entries[1].Text.Should().Be("beta");
        entries[1].Clear.Should().BeTrue();
        entries[1].PressEnter.Should().BeTrue();
    }

    [Fact]
    public void ParseEntries_accepts_an_array_that_was_stringified_twice()
    {
        var entries = BatchTargets.ParseEntries("\"[{\\\"element_id\\\":\\\"el_1\\\",\\\"text\\\":\\\"a\\\"}]\"");

        entries.Should().ContainSingle().Subject.Text.Should().Be("a");
    }

    [Fact]
    public void ParseEntries_reads_the_typing_options_written_out_as_false()
    {
        // A client that always emits both flags must get the same batch as one that omits them:
        // "clear": false has to mean "do not clear", not "unreadable".
        var entries = BatchTargets.ParseEntries(
            """[{"x":1,"y":2,"text":"alpha","clear":false,"press_enter":false},{"x":3,"y":4,"text":"beta","clear":null}]""");

        entries.Should().HaveCount(2);
        entries[0].Clear.Should().BeFalse();
        entries[0].PressEnter.Should().BeFalse();
        entries[1].Clear.Should().BeFalse("an explicit null is the same as not saying it");
    }

    [Theory]
    [InlineData("""[{"x":1,"y":2,"text":"a","clear":"yes"}]""", "clear")]
    [InlineData("""[{"x":1,"y":2,"text":"a","press_enter":1}]""", "press_enter")]
    public void ParseEntries_refuses_a_flag_that_is_not_a_boolean(string json, string name)
    {
        // "clear":"yes" read as false would leave the old text in the field and append to it -
        // a wrong value typed into a form, silently.
        var act = () => BatchTargets.ParseEntries(json);

        act.Should().Throw<ArgumentException>().Which.Message
            .Should().Contain($"entries_json[0].{name}").And.Contain("true or false");
    }

    [Fact]
    public void ParseEntries_refuses_an_entry_with_no_text()
    {
        var act = () => BatchTargets.ParseEntries("""[{"x":1,"y":2,"text":"a"},{"x":3,"y":4}]""");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("entries_json").And.Contain("1");
    }

    [Theory]
    [InlineData("""[{"x":1,"y":2,"text":42}]""")]
    [InlineData("""[{"x":1,"y":2,"text":null}]""")]
    [InlineData("""[{"x":1,"y":2,"text":["a"]}]""")]
    public void ParseEntries_refuses_a_text_that_is_not_a_string(string json)
    {
        var act = () => BatchTargets.ParseEntries(json);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("entries_json").And.Contain("0");
    }

    [Fact]
    public void ParseEntries_refuses_the_same_broken_targets_naming_its_own_parameter()
    {
        // The rules are shared; the parameter name in the message is not - a message that says
        // targets_json to a multi_edit caller sends them looking for an argument they never passed.
        var both = () => BatchTargets.ParseEntries("""[{"x":1,"y":2,"element_id":"el_3","text":"a"}]""");
        var neither = () => BatchTargets.ParseEntries("""[{"text":"a"}]""");
        var empty = () => BatchTargets.ParseEntries("[]");

        both.Should().Throw<ArgumentException>().Which.Message.Should().Contain("entries_json");
        neither.Should().Throw<ArgumentException>().Which.Message.Should().Contain("entries_json");
        empty.Should().Throw<ArgumentException>().Which.Message.Should().Contain("entries_json");
    }
}
