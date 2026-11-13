using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-6: what a client is told about <c>sequence</c> - its signature as the SDK advertises it and the
/// description, which is the only spec the model reads: the step format, the allowlist, the cap, the
/// stop rules and the takeover rule. The behaviour is <see cref="SequenceToolsTests"/>; the count and
/// the annotations are <see cref="ToolInventoryTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SequenceSurfaceTests
{
    private static readonly string[] Allowlist =
        ["click", "type", "key", "shortcut", "scroll", "drag", "interact_element", "wait", "wait_for", "assert_element"];

    private static MethodInfo Method => typeof(SequenceTools).GetMethod(nameof(SequenceTools.Sequence))!;

    private static string Description => Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static Tool ProtocolTool() =>
        McpServerTool.Create(Method, (RequestContext<CallToolRequestParams> _) =>
            throw new InvalidOperationException("metadata only - the tool is never invoked")).ProtocolTool;

    [Fact]
    public void The_signature_is_steps_json_observe_settle_ms_and_the_token()
    {
        Method.GetParameters().Select(p => p.Name).Should().Equal("steps_json", "observe", "settle_ms", "ct");
        var observe = Method.GetParameters().Single(p => p.Name == "observe");
        var settle = Method.GetParameters().Single(p => p.Name == "settle_ms");
        observe.DefaultValue.Should().Be("none");
        settle.DefaultValue.Should().Be(250);
    }

    [Fact]
    public void The_wire_name_is_sequence_and_steps_json_is_a_required_string()
    {
        var tool = ProtocolTool();
        var properties = tool.InputSchema.GetProperty("properties");
        var required = tool.InputSchema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];

        tool.Name.Should().Be("sequence");
        properties.GetProperty("steps_json").GetProperty("type").GetString().Should().Be("string",
            "the B-7 convention: the array arrives as a string (or a string holding it)");
        required.Should().Equal(["steps_json"], "observe and settle_ms are optional");
        properties.TryGetProperty("ct", out _).Should().BeFalse("the token is not a parameter a client passes");
    }

    [Fact]
    public void The_description_names_every_allowlisted_tool()
    {
        foreach (var tool in Allowlist)
            Description.Should().Contain(tool, "the model can only compose steps from tools it is told about");
    }

    [Fact]
    public void The_description_gives_the_step_format_and_the_cap()
    {
        Description.Should().Contain("tool").And.Contain("args", "a step is {tool, args}");
        Description.Should().Contain("20", "the step cap");
    }

    [Fact]
    public void The_description_says_what_stops_the_batch_and_what_comes_back()
    {
        Description.Should().Contain("failedIndex").And.Contain("stoppedBy").And.Contain("completed");
        Description.Should().Contain("wait_for").And.Contain("assert_element");
    }

    [Fact]
    public void The_description_states_the_takeover_rule()
    {
        Description.Should().MatchRegex(@"(?i)take[sn]?\s*over|takeover",
            "a takeover between steps stops the remaining steps - the model must expect stoppedBy takeover");
    }

    [Fact]
    public void The_description_says_steps_are_validated_before_anything_runs()
    {
        Description.Should().MatchRegex(@"(?i)before (any|the first) step|nothing (is )?(done|run|sent)",
            "a refused batch did nothing, so the caller can fix it and resend the whole batch");
    }

    [Fact]
    public void The_observe_parameter_offers_none_and_foreground_only()
    {
        var observe = Method.GetParameters().Single(p => p.Name == "observe").GetCustomAttribute<DescriptionAttribute>()!.Description;

        observe.Should().Contain("none").And.Contain("foreground");
    }

    [Fact]
    public void The_description_says_a_snapshotting_wait_for_invalidates_earlier_element_ids()
    {
        // E-6 round 2 (F3): "a wait_for step with text_exists or focused_element takes a snapshot
        // itself, which invalidates the ids of every earlier snapshot, so an el_N step after it fails".
        Description.Should().Contain("text_exists").And.Contain("focused_element",
            "the two wait_for conditions that snapshot are named");
        Description.Should().MatchRegex(@"(?is)(text_exists|focused_element).{0,300}(el_N|element ids?)",
            "and tied to the element ids they invalidate");
        Description.Should().MatchRegex(@"(?is)(text_exists|focused_element).{0,300}(invalidat|replac|no longer|stale|expire)",
            "the model must not address an earlier el_N after such a wait");
    }

    [Fact]
    public void The_description_says_cancelling_stops_before_the_next_step()
    {
        Description.Should().MatchRegex(@"(?is)cancel.{0,250}(next step|further step|no (more |other )?steps?|remaining steps|stops)",
            "a cancelled call starts no further step (E-6 round 2, F1)");
    }

    [Fact]
    public void The_description_mentions_the_popup_text_the_observation_appends()
    {
        // observe:foreground appends what Observation appends, which ends with a 'Popup text' section
        // (Observation.ToolNote); a description listing the appended parts must not leave it out.
        Description.Should().MatchRegex(@"(?i)popup text|what (those|the|its) popups say",
            "the observation's last section is the popups' text");
    }

    [Fact]
    public void The_description_does_not_still_say_it_is_not_implemented()
    {
        Description.Should().NotContainEquivalentOf("not implemented", "the test-agent stub description must be replaced");
    }
}
