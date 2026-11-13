using System.Reflection;
using FluentAssertions;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-6: <see cref="SequenceSteps.Parse"/>, the pure half of <c>sequence</c> - the "everything
/// checkable is checked before step 1 runs" list of docs/design/E-6-sequence.md, one row per
/// refusal, plus the binding that lets a step run "exactly the code the standalone tool runs, with
/// the same defaults". No service, no input: <see cref="SequenceToolsTests"/> proves the tool runs
/// nothing when this refuses.
/// <para>
/// Every refusal is an <see cref="ArgumentException"/> for <c>steps_json</c>; a step-level one names
/// the step as <c>steps_json[i]</c> (the B-7 <c>targets_json[i]</c> convention, chosen in the RED
/// report because the note only says "naming the step index").
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SequenceStepsTests
{
    private static readonly string[] Allowlist =
        ["click", "type", "key", "shortcut", "scroll", "drag", "interact_element", "wait", "wait_for", "assert_element"];

    private static MethodInfo InputMethod(string name) => typeof(InputTools).GetMethod(name)!;
    private static MethodInfo UiaMethod(string name) => typeof(UIAutomationTools).GetMethod(name)!;

    /// <summary>A step-level refusal: ArgumentException for steps_json, naming steps_json[index] and every fragment.</summary>
    private static ArgumentException RefusedAt(string json, int index, params string[] fragments)
    {
        var act = () => SequenceSteps.Parse(json);
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("steps_json", "the refusal is about the sequence's steps_json argument");
        ex.Message.Should().Contain($"steps_json[{index}]", "the caller fixes one step instead of guessing which");
        foreach (var fragment in fragments)
            ex.Message.Should().Contain(fragment);
        return ex;
    }

    /// <summary>A whole-array refusal: ArgumentException for steps_json.</summary>
    private static ArgumentException Refused(string json)
    {
        var act = () => SequenceSteps.Parse(json);
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("steps_json");
        ex.Message.Should().Contain("steps_json");
        return ex;
    }

    private static string Steps(int count, string step = """{"tool":"key","args":{"key":"a"}}""") =>
        "[" + string.Join(",", Enumerable.Repeat(step, count)) + "]";

    private const string DesignExample = """
        [
          {"tool": "shortcut", "args": {"shortcut": "ctrl+shift+s"}},
          {"tool": "wait_for", "args": {"text": "File name", "condition": "element_exists"}},
          {"tool": "type",     "args": {"text": "report.txt", "press_enter": true}}
        ]
        """;

    // ---- shape: array, or a string holding the array ---------------------------------------------

    [Fact]
    public void Parse_reads_the_design_notes_example_into_three_steps_in_order()
    {
        var steps = SequenceSteps.Parse(DesignExample);

        steps.Select(s => s.Index).Should().Equal(0, 1, 2);
        steps.Select(s => s.Tool).Should().Equal("shortcut", "wait_for", "type");
        steps[0].Method.Should().BeSameAs(InputMethod(nameof(InputTools.Shortcut)), "a step runs the standalone tool's own method");
        steps[1].Method.Should().BeSameAs(UiaMethod(nameof(UIAutomationTools.WaitFor)));
        steps[2].Method.Should().BeSameAs(InputMethod(nameof(InputTools.Type)));
        steps[0].Arguments["shortcut"].Should().Be("ctrl+shift+s");
        steps[1].Arguments["text"].Should().Be("File name");
        steps[1].Arguments["condition"].Should().Be("element_exists");
        steps[2].Arguments["text"].Should().Be("report.txt");
        steps[2].Arguments["press_enter"].Should().Be(true);
    }

    [Fact]
    public void Parse_accepts_a_json_string_holding_the_array()
    {
        // The B-7 convention: some clients stringify an array argument a second time.
        var stringified = System.Text.Json.JsonSerializer.Serialize(DesignExample);

        var steps = SequenceSteps.Parse(stringified);

        steps.Select(s => s.Tool).Should().Equal("shortcut", "wait_for", "type");
        steps[2].Arguments["press_enter"].Should().Be(true);
    }

    [Fact]
    public void Parse_accepts_whitespace_and_crlf_around_and_inside_the_array()
    {
        var steps = SequenceSteps.Parse("\r\n  [ {\"tool\" : \"key\",\r\n \"args\" : { \"key\" : \"tab\" } } ]\r\n ");

        steps.Should().ContainSingle().Which.Arguments["key"].Should().Be("tab");
    }

    [Fact]
    public void Args_may_be_omitted_and_mean_an_empty_object()
    {
        var steps = SequenceSteps.Parse("""[{"tool":"click"}]""");

        steps.Should().ContainSingle();
        steps[0].Method.Should().BeSameAs(InputMethod(nameof(InputTools.Click)));
        steps[0].Arguments["button"].Should().Be("left", "every omitted argument takes the tool's own default");
    }

    public static TheoryData<string, string> NotAnArray => new()
    {
        { "{}", "an object" },
        { """{"tool":"key","args":{"key":"a"}}""", "a lone step, not wrapped in an array" },
        { "5", "a number" },
        { "true", "a boolean" },
        { "null", "JSON null" },
        { "\"just text\"", "a string that does not hold JSON" },
        { "\"{}\"", "a string holding an object" },
        { "not json at all", "text that does not parse" },
        { "[{\"tool\":\"key\"", "a truncated array" },
    };

    [Theory]
    [MemberData(nameof(NotAnArray))]
    public void Steps_json_that_is_not_an_array_is_refused(string json, string what)
    {
        Refused(json).Should().NotBeNull(what);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("[]")]
    [InlineData(" [ ] ")]
    [InlineData("\"[]\"")]
    [InlineData("\"\"")]
    public void An_empty_steps_json_is_refused(string json)
    {
        Refused(json);
    }

    // ---- the 20-step cap ---------------------------------------------------------------------------

    [Fact]
    public void Twenty_steps_are_accepted()
    {
        var steps = SequenceSteps.Parse(Steps(20));

        steps.Should().HaveCount(20);
        steps.Select(s => s.Index).Should().Equal(Enumerable.Range(0, 20));
    }

    [Fact]
    public void Twenty_one_steps_are_refused_naming_the_cap()
    {
        var ex = Refused(Steps(21));

        ex.Message.Should().Contain("20", "the refusal says what the cap is");
    }

    [Fact]
    public void A_bad_twentieth_step_is_refused_by_its_index()
    {
        // Off-by-one at the cap: the last allowed step is validated like every other.
        var json = "[" + string.Join(",", Enumerable.Repeat("""{"tool":"key","args":{"key":"a"}}""", 19)
                       .Append("""{"tool":"powershell"}""")) + "]";

        RefusedAt(json, 19, "powershell");
    }

    // ---- a step must be an object -----------------------------------------------------------------

    [Theory]
    [InlineData("""[{"tool":"key","args":{"key":"a"}}, 5]""", 1)]
    [InlineData("""[{"tool":"key","args":{"key":"a"}}, "click"]""", 1)]
    [InlineData("""[null]""", 0)]
    [InlineData("""[[{"tool":"key","args":{"key":"a"}}]]""", 0)]
    [InlineData("""[true]""", 0)]
    public void A_step_that_is_not_an_object_is_refused_by_index(string json, int index)
    {
        RefusedAt(json, index);
    }

    [Theory]
    [InlineData("""[{"tool":"key","args":{"key":"a"},"note":"x"}]""", "note")]
    [InlineData("""[{"tool":"key","arguments":{"key":"a"}}]""", "arguments")]
    [InlineData("""[{"tool":"key","args":{"key":"a"},"observe":"foreground"}]""", "observe")]
    [InlineData("""[{"tool":"key","args":{"key":"a"},"id":1}]""", "id")]
    public void Any_step_property_other_than_tool_and_args_is_refused_by_name(string json, string property)
    {
        RefusedAt(json, 0, property);
    }

    // ---- tool -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"args":{"key":"a"}}]""", 1)]
    [InlineData("""[{}]""", 0)]
    public void A_step_with_no_tool_is_refused(string json, int index)
    {
        RefusedAt(json, index, "tool");
    }

    [Theory]
    [InlineData("""[{"tool":5}]""")]
    [InlineData("""[{"tool":null}]""")]
    [InlineData("""[{"tool":true}]""")]
    [InlineData("""[{"tool":["click"]}]""")]
    [InlineData("""[{"tool":{"name":"click"}}]""")]
    public void A_tool_that_is_not_a_string_is_refused(string json)
    {
        RefusedAt(json, 0, "tool");
    }

    [Theory]
    [InlineData("powershell")]        // the note's first "never"
    [InlineData("sequence")]          // not itself
    [InlineData("hover")]             // a driving tool, but not on the list
    [InlineData("multi_select")]
    [InlineData("multi_edit")]
    [InlineData("snapshot")]
    [InlineData("screenshot")]
    [InlineData("file_read")]
    [InlineData("file_write")]
    [InlineData("registry_set")]
    [InlineData("process")]
    [InlineData("start_process")]
    [InlineData("window")]
    [InlineData("focus")]
    [InlineData("switch_to_window")]
    [InlineData("launch")]
    [InlineData("clipboard")]
    [InlineData("Click")]             // ordinal: wrong case is another name
    [InlineData("CLICK")]
    [InlineData("click ")]            // and so is padding
    [InlineData(" click")]
    [InlineData("Wait_For")]
    [InlineData("waitfor")]
    [InlineData("")]
    public void A_tool_outside_the_allowlist_is_refused_naming_it_and_listing_the_allowlist(string tool)
    {
        var json = $$$"""[{"tool":"key","args":{"key":"a"}},{"tool":"{{{tool}}}","args":{}}]""";

        // Quoted, so the name is asserted exactly - padding included - and the empty name ("''") is
        // asserted too: FluentAssertions refuses Contain("") outright.
        var ex = RefusedAt(json, 1, $"'{tool}'");

        foreach (var allowed in Allowlist)
            ex.Message.Should().Contain(allowed, "the refusal lists the allowlist, so the caller can pick a tool that is on it");
    }

    public static TheoryData<string, string> EveryAllowedTool => new()
    {
        { "click", """{"x":1,"y":2}""" },
        { "type", """{"text":"a"}""" },
        { "key", """{"key":"a"}""" },
        { "shortcut", """{"shortcut":"ctrl+c"}""" },
        { "scroll", """{"direction":"down"}""" },
        { "drag", """{"to_x":1,"to_y":2}""" },
        { "interact_element", """{"element_id":"el_1","action":"click"}""" },
        { "wait", """{"seconds":1}""" },
        { "wait_for", """{"text":"OK"}""" },
        { "assert_element", """{"element_id":"el_1","state":"exists"}""" },
    };

    [Theory]
    [MemberData(nameof(EveryAllowedTool))]
    public void Every_allowlisted_tool_binds_to_the_standalone_tools_own_method(string tool, string args)
    {
        var expected = tool switch
        {
            "click" => InputMethod(nameof(InputTools.Click)),
            "type" => InputMethod(nameof(InputTools.Type)),
            "key" => InputMethod(nameof(InputTools.Key)),
            "shortcut" => InputMethod(nameof(InputTools.Shortcut)),
            "scroll" => InputMethod(nameof(InputTools.Scroll)),
            "drag" => InputMethod(nameof(InputTools.Drag)),
            "wait" => InputMethod(nameof(InputTools.Wait)),
            "interact_element" => UiaMethod(nameof(UIAutomationTools.InteractElement)),
            "wait_for" => UiaMethod(nameof(UIAutomationTools.WaitFor)),
            "assert_element" => UiaMethod(nameof(UIAutomationTools.AssertElement)),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

        var step = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single();

        step.Tool.Should().Be(tool);
        step.Method.Should().BeSameAs(expected);
    }

    // ---- args -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("""[{"tool":"key","args":[]}]""")]
    [InlineData("""[{"tool":"key","args":["a"]}]""")]
    [InlineData("""[{"tool":"key","args":"key=a"}]""")]
    [InlineData("""[{"tool":"key","args":5}]""")]
    [InlineData("""[{"tool":"key","args":true}]""")]
    [InlineData("""[{"tool":"key","args":null}]""")]   // "may be omitted", not "may be null": null is not an object
    public void Args_that_are_not_an_object_are_refused(string json)
    {
        RefusedAt(json, 0, "args");
    }

    [Fact]
    public void An_unknown_argument_is_refused_listing_the_arguments_the_tool_has()
    {
        var ex = RefusedAt("""[{"tool":"type","args":{"txt":"hi"}}]""", 0, "txt", "type");

        foreach (var name in new[] { "text", "x", "y", "element_id", "clear", "caret", "press_enter", "pace_ms" })
            ex.Message.Should().Contain(name, "the refusal lists the arguments type takes");
        ex.Message.Should().NotContain("settle_ms", "a step may not carry settle_ms, so it is not offered as a valid name");
    }

    [Theory]
    [InlineData("""[{"tool":"type","args":{"Text":"hi"}}]""", "Text")]                 // argument names are exact
    [InlineData("""[{"tool":"type","args":{"text":"hi","pressEnter":true}}]""", "pressEnter")]
    [InlineData("""[{"tool":"click","args":{"x":1,"y":2,"duration_ms":5}}]""", "duration_ms")]   // hover's, not click's
    [InlineData("""[{"tool":"key","args":{"key":"a","ct":1}}]""", "ct")]              // the token is not an argument
    [InlineData("""[{"tool":"wait","args":{"seconds":1,"timeout_ms":5}}]""", "timeout_ms")]
    public void An_argument_the_tool_does_not_have_is_refused_by_name(string json, string name)
    {
        RefusedAt(json, 0, name);
    }

    [Theory]
    [InlineData("click", """{"x":1,"y":2,"observe":"foreground"}""", "observe")]
    [InlineData("click", """{"x":1,"y":2,"observe":"none"}""", "observe")]           // refused by name, whatever the value
    [InlineData("key", """{"key":"a","settle_ms":0}""", "settle_ms")]
    [InlineData("shortcut", """{"shortcut":"ctrl+s","settle_ms":250}""", "settle_ms")]
    [InlineData("interact_element", """{"element_id":"el_1","action":"click","observe":"window"}""", "observe")]
    [InlineData("wait", """{"seconds":1,"observe":"foreground"}""", "observe")]       // a tool that never had it
    public void Observe_and_settle_ms_in_a_step_are_refused_by_name(string tool, string args, string name)
    {
        RefusedAt($$$"""[{"tool":"key","args":{"key":"a"}},{"tool":"{{{tool}}}","args":{{{args}}}}]""", 1, name);
    }

    [Theory]
    [InlineData("type", """{}""", "text")]
    [InlineData("type", """{"press_enter":true}""", "text")]
    [InlineData("wait", """{}""", "seconds")]
    [InlineData("key", """{"repeat":2}""", "key")]
    [InlineData("shortcut", """{}""", "shortcut")]
    [InlineData("scroll", """{"amount":2}""", "direction")]
    [InlineData("interact_element", """{"element_id":"el_1"}""", "action")]
    [InlineData("interact_element", """{"action":"click"}""", "element_id")]
    [InlineData("assert_element", """{"element_id":"el_1"}""", "state")]
    [InlineData("assert_element", """{"state":"exists"}""", "element_id")]
    public void A_missing_required_argument_is_refused_by_name(string tool, string args, string name)
    {
        RefusedAt($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""", 0, name);
    }

    [Theory]
    [InlineData("click", """{}""")]
    [InlineData("drag", """{}""")]
    [InlineData("wait_for", """{}""")]
    public void A_tool_with_no_required_argument_parses_with_empty_args(string tool, string args)
    {
        // What only the tool can decide (no target, no text) is decided when the step runs.
        SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Should().ContainSingle();
    }

    // ---- JSON types: no coercion ----------------------------------------------------------------

    [Theory]
    [InlineData("click", """{"x":"10","y":20}""", "x")]                  // a string where a number is expected
    [InlineData("click", """{"x":10.5,"y":20}""", "x")]                  // fractional for an int
    [InlineData("click", """{"x":true,"y":20}""", "x")]
    [InlineData("click", """{"clicks":"2"}""", "clicks")]
    [InlineData("click", """{"clicks":1.5}""", "clicks")]
    [InlineData("click", """{"clicks":2147483648}""", "clicks")]         // one past int.MaxValue
    [InlineData("click", """{"clicks":-2147483649}""", "clicks")]        // one below int.MinValue
    [InlineData("click", """{"clicks":null}""", "clicks")]               // null for a non-nullable int
    [InlineData("click", """{"button":null}""", "button")]               // null for a non-nullable string with a default
    [InlineData("click", """{"button":1}""", "button")]                  // a number where a string is expected
    [InlineData("click", """{"element_id":5}""", "element_id")]          // a number for a nullable string
    [InlineData("type", """{"text":5}""", "text")]
    [InlineData("type", """{"text":null}""", "text")]
    [InlineData("type", """{"text":["a"]}""", "text")]
    [InlineData("type", """{"text":{"v":"a"}}""", "text")]
    [InlineData("type", """{"text":"a","press_enter":"true"}""", "press_enter")]   // a string for a bool
    [InlineData("type", """{"text":"a","press_enter":1}""", "press_enter")]        // a number for a bool
    [InlineData("type", """{"text":"a","press_enter":null}""", "press_enter")]
    [InlineData("type", """{"text":"a","pace_ms":null}""", "pace_ms")]
    [InlineData("type", """{"text":"a","caret":null}""", "caret")]
    [InlineData("wait", """{"seconds":"2"}""", "seconds")]
    [InlineData("wait", """{"seconds":null}""", "seconds")]
    [InlineData("wait", """{"seconds":true}""", "seconds")]
    [InlineData("key", """{"key":"a","repeat":"3"}""", "repeat")]
    [InlineData("key", """{"key":5}""", "key")]
    [InlineData("scroll", """{"direction":"left","shift_wheel":0}""", "shift_wheel")]
    [InlineData("interact_element", """{"element_id":"el_1","action":"type","value":5}""", "value")]
    [InlineData("wait_for", """{"text":"a","timeout_ms":1000.5}""", "timeout_ms")]
    [InlineData("wait_for", """{"text":"a","include_offscreen":"yes"}""", "include_offscreen")]
    public void An_argument_of_the_wrong_json_type_is_refused_by_name(string tool, string args, string name)
    {
        RefusedAt($$$"""[{"tool":"key","args":{"key":"a"}},{"tool":"{{{tool}}}","args":{{{args}}}}]""", 1, name);
    }

    [Fact]
    public void Int_arguments_take_the_full_int_range()
    {
        var step = SequenceSteps.Parse("""[{"tool":"click","args":{"x":2147483647,"y":-2147483648}}]""").Single();

        step.Arguments["x"].Should().Be(int.MaxValue);
        step.Arguments["y"].Should().Be(int.MinValue);
    }

    [Fact]
    public void Null_is_accepted_for_a_nullable_parameter()
    {
        var click = SequenceSteps.Parse("""[{"tool":"click","args":{"x":null,"y":null,"element_id":null,"modifiers":null}}]""").Single();
        var interact = SequenceSteps.Parse("""[{"tool":"interact_element","args":{"element_id":"el_1","action":"click","value":null}}]""").Single();
        var assert = SequenceSteps.Parse("""[{"tool":"assert_element","args":{"element_id":"el_1","state":"exists","expected":null}}]""").Single();
        var waitFor = SequenceSteps.Parse("""[{"tool":"wait_for","args":{"text":"a","window":null,"within":null}}]""").Single();

        click.Arguments["x"].Should().BeNull();
        click.Arguments["y"].Should().BeNull();
        click.Arguments["element_id"].Should().BeNull();
        click.Arguments["modifiers"].Should().BeNull();
        interact.Arguments["value"].Should().BeNull();
        assert.Arguments["expected"].Should().BeNull();
        waitFor.Arguments["window"].Should().BeNull();
        waitFor.Arguments["within"].Should().BeNull();
    }

    [Fact]
    public void A_whole_json_number_binds_to_a_double_parameter_and_a_fraction_is_kept()
    {
        var whole = SequenceSteps.Parse("""[{"tool":"wait","args":{"seconds":2}}]""").Single();
        var fraction = SequenceSteps.Parse("""[{"tool":"wait","args":{"seconds":0.25}}]""").Single();

        whole.Arguments["seconds"].Should().BeOfType<double>().Which.Should().Be(2.0);
        fraction.Arguments["seconds"].Should().BeOfType<double>().Which.Should().Be(0.25);
    }

    [Fact]
    public void Values_are_bound_with_the_parameters_clr_types()
    {
        var step = SequenceSteps.Parse("""[{"tool":"click","args":{"x":10,"y":20,"button":"right","clicks":2}}]""").Single();

        step.Arguments["x"].Should().BeOfType<int>().Which.Should().Be(10);
        step.Arguments["y"].Should().BeOfType<int>().Which.Should().Be(20);
        step.Arguments["button"].Should().BeOfType<string>().Which.Should().Be("right");
        step.Arguments["clicks"].Should().BeOfType<int>().Which.Should().Be(2);
    }

    [Fact]
    public void Text_is_bound_verbatim_unicode_crlf_and_padding_included()
    {
        const string text = "  héllo — 日本語 😀\r\nline two\t ";
        var json = "[{\"tool\":\"type\",\"args\":{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}}]";

        SequenceSteps.Parse(json).Single().Arguments["text"].Should().Be(text);
    }

    // ---- defaults ---------------------------------------------------------------------------------

    [Fact]
    public void Omitted_arguments_take_the_tool_methods_own_defaults()
    {
        var step = SequenceSteps.Parse("""[{"tool":"type","args":{"text":"hi"}}]""").Single();

        step.Arguments.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["text"] = "hi",
            ["x"] = null,
            ["y"] = null,
            ["element_id"] = null,
            ["clear"] = false,
            ["caret"] = "idle",
            ["press_enter"] = false,
            ["pace_ms"] = 5,
            ["observe"] = "none",     // a step never observes: the observation belongs to the sequence
            ["settle_ms"] = 250,
        });
    }

    [Fact]
    public void Every_default_is_read_from_the_method_not_restated()
    {
        // Pinned against ParameterInfo, so a default changed on the standalone tool is the default in
        // a sequence too: "with the same defaults".
        foreach (var (tool, args) in EveryAllowedTool.Select(r => ((string)r[0], (string)r[1])))
        {
            var step = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single();
            foreach (var p in step.Method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken) && p.HasDefaultValue))
            {
                if (args.Contains($"\"{p.Name}\"", StringComparison.Ordinal)) continue;
                step.Arguments.Should().ContainKey(p.Name!, "{0} has a default for {1}", tool, p.Name);
                step.Arguments[p.Name!].Should().Be(p.DefaultValue, "{0}.{1} defaults to the tool's own default", tool, p.Name);
            }
        }
    }

    [Theory]
    [InlineData("click")]
    [InlineData("key")]
    [InlineData("wait")]
    public void The_cancellation_token_is_never_a_bound_argument(string tool)
    {
        var args = tool switch { "key" => """{"key":"a"}""", "wait" => """{"seconds":1}""", _ => "{}" };

        var step = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single();

        var expected = step.Method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => p.Name!);
        step.Arguments.Keys.Should().BeEquivalentTo(expected, "every parameter but the token is bound; the token is the sequence's own");
    }

    // ---- what only the tool can decide is left to the step ----------------------------------------

    [Theory]
    [InlineData("click", """{"x":1,"y":2,"button":"bogus"}""")]       // an unknown button
    [InlineData("click", """{"element_id":"el_999"}""")]              // an expired id
    [InlineData("wait", """{"seconds":0}""")]                         // seconds out of range
    [InlineData("wait", """{"seconds":-1}""")]
    [InlineData("key", """{"key":"a","repeat":1000}""")]
    [InlineData("wait_for", """{"text":"a","timeout_ms":999999}""")]
    [InlineData("interact_element", """{"element_id":"el_1","action":"bogus"}""")]
    [InlineData("assert_element", """{"element_id":"el_1","state":"bogus"}""")]
    [InlineData("click", """{"x":1}""")]                              // x without y: the tool's own rule
    public void A_value_only_the_tool_can_judge_parses(string tool, string args)
    {
        SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Should().ContainSingle(
            "the note: what only the tool itself can decide is decided when that step runs, and is that step's failure");
    }

    // ---- GREEN close-out: the edges the implementation revealed ------------------------------------

    [Theory]
    [InlineData("""[{"tool":"key","tool":"click","args":{"key":"a"}}]""", "tool")]
    [InlineData("""[{"tool":"key","args":{"key":"a"},"args":{"key":"b"}}]""", "args")]
    public void A_step_property_given_twice_is_refused_by_name(string json, string property)
    {
        // JsonDocument keeps duplicate properties; taking the first or the last silently would run a
        // step the caller did not mean.
        RefusedAt(json, 0, $"'{property}'", "twice");
    }

    [Theory]
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"tool":"key","args":{"key":"a","key":"b"}}]""", "key")]
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"tool":"click","args":{"x":1,"y":2,"x":3}}]""", "x")]
    public void An_argument_given_twice_is_refused_by_name(string json, string name)
    {
        RefusedAt(json, 1, $"'{name}'", "twice");
    }

    [Theory]
    [InlineData("""{"x":10.0,"y":20}""", "x")]       // a whole number written with a fraction: System.Text.Json refuses it for int too
    [InlineData("""{"x":1e1,"y":20}""", "x")]        // exponent form
    [InlineData("""{"x":-0.5,"y":20}""", "x")]
    public void An_int_argument_takes_only_an_integer_literal(string args, string name)
    {
        // Ambiguity resolved: "a fractional number" is read as the literal, which is also what the
        // SDK's own binding of a standalone click does with 10.0, so a sequence step and the
        // standalone call refuse the same input.
        RefusedAt($$$"""[{"tool":"click","args":{{{args}}}}]""", 0, name);
    }

    [Theory]
    [InlineData("[{\"tool\":\"key\",\"args\":{\"key\":\"a\"}},]")]          // trailing comma
    [InlineData("[{\"tool\":\"key\",\"args\":{\"key\":\"a\"}} /* c */]")]   // a comment
    [InlineData("[{\"tool\":\"key\",\"args\":{\"key\":\"a\"}}] x")]         // trailing text
    public void Lenient_json_is_refused(string json)
    {
        Refused(json);
    }

    [Fact]
    public void Only_one_level_of_string_wrapping_is_unwrapped()
    {
        var once = System.Text.Json.JsonSerializer.Serialize("""[{"tool":"key","args":{"key":"a"}}]""");
        var twice = System.Text.Json.JsonSerializer.Serialize(once);

        SequenceSteps.Parse(once).Should().ContainSingle("precondition: one level is the B-7 convention");
        Refused(twice).Message.Should().Contain("array", "a string holding a string is not an array of steps");
    }

    [Fact]
    public void A_refusal_names_the_first_bad_step_when_several_are_bad()
    {
        RefusedAt("""[{"tool":"key","args":{"key":"a"}},{"tool":"bogus1"},{"tool":"bogus2"}]""", 1, "bogus1")
            .Message.Should().NotContain("bogus2", "validation stops at the first refusal, in order");
    }

    /// <summary>A JSON literal of the parameter's CLR type.</summary>
    private static string LiteralFor(ParameterInfo p)
    {
        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (type == typeof(string)) return "\"v\"";
        if (type == typeof(bool)) return "true";
        if (type == typeof(int)) return "1";
        if (type == typeof(double)) return "0.5";
        throw new InvalidOperationException($"{p.Member.DeclaringType!.Name}.{p.Member.Name} parameter '{p.Name}' has type {p.ParameterType}, which a sequence step cannot set");
    }

    [Theory]
    [MemberData(nameof(EveryAllowedTool))]
    public void Every_argument_of_every_allowlisted_tool_can_be_set_in_a_step(string tool, string minimalArgs)
    {
        // "args carries the step tool's own parameters under their wire names": every one of them,
        // not just the common ones. If a standalone tool gains a parameter of a type the binder does
        // not convert (a long, an enum, an array), LiteralFor throws here and names it - before a
        // caller finds the step refusing "cannot be set in a sequence".
        var method = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{minimalArgs}}}}]""").Single().Method;
        var settable = method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken) && p.Name is not ("observe" or "settle_ms"))
            .ToArray();
        var args = "{" + string.Join(",", settable.Select(p => $"\"{p.Name}\":{LiteralFor(p)}")) + "}";

        var step = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single();

        foreach (var p in settable)
        {
            var expected = LiteralFor(p) switch { "\"v\"" => (object)"v", "true" => true, "1" => 1, _ => 0.5 };
            step.Arguments[p.Name!].Should().Be(expected, "{0}.{1} is bound from its own JSON type", tool, p.Name);
        }
    }

    // ---- E-6 round 2 (F6): the dispatcher's assumptions about the allowlisted methods --------------

    [Fact]
    public void The_allowlist_the_code_uses_is_exactly_the_design_notes_ten_in_order()
    {
        SequenceSteps.AllowedNames.Split(", ").Should().Equal(Allowlist,
            "the rows of this class (EveryAllowedTool) cover exactly what the dispatcher allows");
    }

    [Theory]
    [MemberData(nameof(EveryAllowedTool))]
    public void Every_allowlisted_method_lives_on_InputTools_or_UIAutomationTools_and_has_no_overload(string tool, string args)
    {
        // SequenceTools picks the instance by DeclaringType (InputTools, else UIAutomationTools) and
        // SequenceSteps finds the method by name with Type.GetMethod(name): a method moved to a third
        // class would be invoked on the wrong instance, and a second public overload would make
        // GetMethod throw AmbiguousMatchException in the static initialiser - every sequence call
        // failing, not just this tool's.
        var method = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single().Method;

        method.DeclaringType.Should().Match<Type>(t => t == typeof(InputTools) || t == typeof(UIAutomationTools),
            "{0} must be a method of one of the two tool instances the sequence builds", tool);
        method.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Count(m => m.Name == method.Name).Should().Be(1, "{0}.{1} must have exactly one public overload", method.DeclaringType.Name, method.Name);
        method.ReturnType.Should().Be(typeof(Task<string>), "the dispatcher casts the invocation to Task<string>");
        method.IsStatic.Should().BeFalse();
    }

    // ---- E-6 round 2 (F5): a lone surrogate System.Text.Json cannot materialise --------------------

    [Fact]
    public void Probe_System_Text_Json_parses_an_escaped_lone_surrogate_but_cannot_read_it_as_a_string()
    {
        // Pins the platform behaviour the rows below rely on: JsonDocument.Parse accepts the six
        // characters \ud800 (the reader validates only that the escape is hex), and the failure comes
        // later, as an InvalidOperationException - which ToolErrors.IsCallerFacing would pass through
        // as if it were a deliberate refusal, without naming the step.
        using var doc = System.Text.Json.JsonDocument.Parse("""{"\ud800":"\ud800"}""");
        var property = doc.RootElement.EnumerateObject().Single();

        var value = () => property.Value.GetString();
        var name = () => property.Name;

        value.Should().Throw<InvalidOperationException>("a value that is not valid UTF-16 cannot become a .NET string");
        name.Should().Throw<InvalidOperationException>("nor can a property name");
    }

    [Theory]
    [InlineData("""[{"tool":"\ud800"}]""", 0)]                                                    // tool name
    [InlineData("""[{"tool":"key","args":{"key":"a"},"\ud800":1}]""", 0)]                         // step property name
    [InlineData("""[{"tool":"key","args":{"\ud800":"a"}}]""", 0)]                                 // argument name
    [InlineData("""[{"tool":"key","args":{"key":"\ud800"}}]""", 0)]                               // argument value
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"tool":"type","args":{"text":"x\udc00y"}}]""", 1)]   // lone low half, later step
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"tool":"type","args":{"text":"x\ude00\ud83dy"}}]""", 1)]   // halves in the wrong order
    [InlineData("""[{"tool":"key","args":{"key":"a"}},{"tool":"key","args":{"key":"b","\ud800":1}}]""", 1)]
    public void A_lone_surrogate_anywhere_in_a_step_is_refused_naming_the_step(string json, int index)
    {
        RefusedAt(json, index);
    }

    [Fact]
    public void A_lone_surrogate_inside_the_stringified_arrays_inner_text_is_refused_naming_the_step()
    {
        // The outer string is valid (an escaped backslash); the inner array it holds has the escape.
        RefusedAt(""" "[{\"tool\":\"key\",\"args\":{\"key\":\"\\ud800\"}}]" """, 0);
    }

    [Fact]
    public void A_lone_surrogate_in_the_outer_string_of_the_stringified_form_is_refused()
    {
        // The outer JSON string itself cannot become a .NET string: no step to name, but still a
        // refusal of steps_json, never an InvalidOperationException.
        Refused(""" "[{\"tool\":\"key\",\"args\":{\"key\":\"\ud800\"}}]" """);
    }

    [Fact]
    public void A_valid_surrogate_pair_escape_is_accepted()
    {
        var step = SequenceSteps.Parse("""[{"tool":"type","args":{"text":"x\ud83d\ude00y"}}]""").Single();

        step.Arguments["text"].Should().Be("x\U0001F600y", "a well-formed pair is an ordinary character (an emoji)");
    }

    [Theory]
    [MemberData(nameof(EveryAllowedTool))]
    public void A_step_never_observes_on_its_own(string tool, string args)
    {
        // The observation belongs to the sequence (one, at the end): a step that has observe is bound
        // with "none" whatever it was given - it cannot be given - so it takes no snapshot of its own.
        // Pinned here because the binder takes the standalone default: if that default ever became
        // "foreground", every step would observe and this row goes red.
        var step = SequenceSteps.Parse($$$"""[{"tool":"{{{tool}}}","args":{{{args}}}}]""").Single();

        if (step.Arguments.TryGetValue("observe", out var observe))
            observe.Should().Be("none", "{0} in a sequence never observes", tool);
    }
}
