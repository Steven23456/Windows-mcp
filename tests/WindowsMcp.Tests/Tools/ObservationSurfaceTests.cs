using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-5: what a client is told about <c>observe</c> - the two new parameters on exactly the seven
/// action tools, where they sit, their defaults as the SDK advertises them, and the descriptions
/// that say what an observation returns and what it costs (it replaces the previous snapshot's
/// ids). The behaviour is <see cref="ObservationToolsTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObservationSurfaceTests
{
    public static TheoryData<string> SevenTools => new()
    {
        "click", "type", "key", "shortcut", "scroll", "drag", "interact_element",
    };

    private static MethodInfo Method(string tool) => tool switch
    {
        "click" => typeof(InputTools).GetMethod(nameof(InputTools.Click))!,
        "type" => typeof(InputTools).GetMethod(nameof(InputTools.Type))!,
        "key" => typeof(InputTools).GetMethod(nameof(InputTools.Key))!,
        "shortcut" => typeof(InputTools).GetMethod(nameof(InputTools.Shortcut))!,
        "scroll" => typeof(InputTools).GetMethod(nameof(InputTools.Scroll))!,
        "drag" => typeof(InputTools).GetMethod(nameof(InputTools.Drag))!,
        "interact_element" => typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.InteractElement))!,
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "no such tool"),
    };

    private static string DescriptionOf(string tool) => Method(tool).GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string? ParameterDescription(string tool, string parameter) =>
        Method(tool).GetParameters().Single(p => p.Name == parameter).GetCustomAttribute<DescriptionAttribute>()?.Description;

    /// <summary>Every discovered tool method, the reflection WithToolsFromAssembly uses.</summary>
    private static MethodInfo[] ToolMethods() =>
        typeof(InputTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

    private static Tool ProtocolTool(MethodInfo method) =>
        McpServerTool.Create(method, (RequestContext<CallToolRequestParams> _) =>
            throw new InvalidOperationException("metadata only - the tool is never invoked")).ProtocolTool;

    // ---- parameters ------------------------------------------------------------------------------

    public static TheoryData<string, string[]> ParameterLists => new()
    {
        { "click", ["x", "y", "element_id", "button", "clicks", "modifiers", "observe", "settle_ms", "ct"] },
        { "type", ["text", "x", "y", "element_id", "clear", "caret", "press_enter", "pace_ms", "observe", "settle_ms"] },
        { "key", ["key", "repeat", "hold_ms", "observe", "settle_ms", "ct"] },
        { "shortcut", ["shortcut", "observe", "settle_ms"] },
        { "scroll", ["direction", "amount", "x", "y", "element_id", "shift_wheel", "observe", "settle_ms"] },
        { "drag", ["from_x", "from_y", "to_x", "to_y", "element_id", "from_element_id", "button", "duration_ms", "steps", "observe", "settle_ms"] },
        { "interact_element", ["element_id", "action", "value", "observe", "settle_ms"] },
    };

    [Theory]
    [MemberData(nameof(ParameterLists))]
    public void Observe_and_settle_ms_come_after_the_existing_parameters_and_before_any_token(string tool, string[] expected)
    {
        var parameters = Method(tool).GetParameters();

        parameters.Select(p => p.Name).Should().Equal(expected,
            "positional callers keep working: the two new parameters are appended, before a trailing CancellationToken");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public void Observe_defaults_to_none_and_settle_ms_to_250(string tool)
    {
        var parameters = Method(tool).GetParameters();
        var observe = parameters.Single(p => p.Name == "observe");
        var settle = parameters.Single(p => p.Name == "settle_ms");

        observe.ParameterType.Should().Be(typeof(string));
        observe.HasDefaultValue.Should().BeTrue();
        observe.DefaultValue.Should().Be("none", "observe:none is today's tool, so it is the default");
        settle.ParameterType.Should().Be(typeof(int));
        settle.HasDefaultValue.Should().BeTrue();
        settle.DefaultValue.Should().Be(250);
    }

    [Fact]
    public void Only_the_seven_action_tools_take_observe_and_settle_ms()
    {
        var observing = ToolMethods()
            .Where(m => m.GetParameters().Any(p => p.Name == "observe"))
            .Select(m => m.Name)
            .ToArray();
        var settling = ToolMethods()
            .Where(m => m.GetParameters().Any(p => p.Name == "settle_ms"))
            .Select(m => m.Name)
            .ToArray();

        var seven = new[] { "Click", "Type", "Key", "Shortcut", "Scroll", "Drag", "InteractElement" };
        observing.Should().BeEquivalentTo(seven, "hover, multi_select and multi_edit do not observe (E-5 scope)");
        settling.Should().BeEquivalentTo(seven, "settle_ms only means something next to observe");
    }

    [Theory]
    [InlineData(nameof(InputTools.Hover))]
    [InlineData(nameof(InputTools.MultiSelect))]
    [InlineData(nameof(InputTools.MultiEdit))]
    public void Hover_and_the_batch_tools_do_not_take_observe(string method)
    {
        typeof(InputTools).GetMethod(method)!.GetParameters().Select(p => p.Name)
            .Should().NotContain("observe").And.NotContain("settle_ms");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public void The_sdk_advertises_observe_as_an_optional_string_and_settle_ms_as_an_optional_integer(string tool)
    {
        var schema = ProtocolTool(Method(tool)).InputSchema;
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];

        properties.GetProperty("observe").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("observe").GetProperty("default").GetString().Should().Be("none");
        properties.GetProperty("settle_ms").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("settle_ms").GetProperty("default").GetInt32().Should().Be(250);
        required.Should().NotContain("observe").And.NotContain("settle_ms", "every existing call leaves them out");
    }

    // ---- descriptions ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SevenTools))]
    public void Each_action_tools_description_says_what_observe_returns_and_what_it_costs(string tool)
    {
        var description = DescriptionOf(tool);

        description.Should().Contain("observe");
        description.Should().ContainEquivalentOf("foreground").And.ContainEquivalentOf("window",
            "the two scopes an observation can take");
        description.Should().ContainEquivalentOf("snapshot", "what is appended is a fresh snapshot");
        description.Should().MatchRegex(@"(?i)\bids?\b[^.]*(replac|no longer valid|invalid)|(replac\w*|invalidat\w*)[^.]*\bids?\b",
            "an observation is a real snapshot: it replaces the previous snapshot's element ids, in one sentence");
        description.Should().MatchRegex(@"(?i)\bnote\b", "a failed observation comes back as a note");
        description.Should().MatchRegex(@"(?i)not (as )?an error", "and is not an error: the action happened");
    }

    /// <summary>The five tools that can name an element, so observe:window is available to them.</summary>
    public static TheoryData<string> ElementTools => new() { "click", "type", "scroll", "drag", "interact_element" };

    [Theory]
    [MemberData(nameof(ElementTools))]
    public void The_observe_parameter_description_gives_its_three_values(string tool)
    {
        // Re-pinned by round 2 R5: the three values on the five tools that can have an element.
        var description = ParameterDescription(tool, "observe");

        description.Should().NotBeNullOrWhiteSpace();
        description.Should().Contain("none").And.Contain("foreground").And.Contain("window");
    }

    private static string[] Sentences(string text) => Regex.Split(text, @"(?<=[.;])\s+");

    private static bool AnySentence(string text, params string[] patterns) =>
        Sentences(text).Any(s => patterns.All(p => Regex.IsMatch(s, p, RegexOptions.IgnoreCase)));

    [Theory]
    [MemberData(nameof(ElementTools))]
    public void R4_the_description_says_window_is_resolved_before_the_action_and_a_failed_lookup_is_refused_without_acting(string tool)
    {
        // Distinct from a snapshot that fails afterwards (a note): the call has not acted yet.
        var description = DescriptionOf(tool);

        AnySentence(description, @"\bwindow\b", @"before the action").Should().BeTrue(
            "the element's window is resolved before the action: {0}", description);
        AnySentence(description, @"refuse", @"without acting|without (performing|sending|doing) (the action|anything)").Should().BeTrue(
            "a window that cannot be determined, or an unknown/expired/gone id, is refused without acting");
        AnySentence(description, @"refuse", @"unknown|expired|gone|cannot be determined|could not be determined").Should().BeTrue(
            "the refusal names the cases");
    }

    [Theory]
    [InlineData("key")]
    [InlineData("shortcut")]
    public void R5_key_and_shortcut_offer_none_and_foreground_and_say_window_is_refused_for_want_of_an_element(string tool)
    {
        var parameter = ParameterDescription(tool, "observe");
        var description = DescriptionOf(tool);

        parameter.Should().NotBeNullOrWhiteSpace();
        parameter.Should().Contain("none").And.Contain("foreground");
        parameter.Should().NotMatchRegex(@"(?i)none[^.;]*\|\s*foreground\s*\|\s*window",
            "window is not one of the values this tool can use");
        if (parameter!.Contains("window", StringComparison.OrdinalIgnoreCase))
            AnySentence(parameter, @"\bwindow\b", @"refus|not available|cannot").Should().BeTrue(
                "where the parameter mentions window it says it is refused: {0}", parameter);
        AnySentence(description, @"\bwindow\b", @"refus|not available|cannot be used", @"element|target").Should().BeTrue(
            "the description says window is refused because the call has no target element: {0}", description);
        AnySentence(description, @"observe", @"\bnone\b|foreground").Should().BeTrue();
    }

    [Theory]
    [InlineData("type")]
    [InlineData("shortcut")]
    [InlineData("scroll")]
    [InlineData("drag")]
    [InlineData("interact_element")]
    public void R8_the_tools_without_a_token_do_not_claim_a_cancel_drops_the_result(string tool)
    {
        // Their calls cannot be cancelled (no request token reaches the observation), so the
        // sentence would describe something that cannot happen.
        AnySentence(DescriptionOf(tool), @"cancel", @"settle|snapshot|observ").Should().BeFalse();
    }

    [Theory]
    [InlineData("click")]
    [InlineData("key")]
    public void R8_the_tools_with_a_token_say_a_cancel_during_the_observation_drops_the_result_but_not_the_action(string tool)
    {
        // Behaviour unchanged and pinned in ObservationToolsTests (A_cancelled_settle_wait_propagates...,
        // A_cancelled_snapshot_propagates); this is the sentence that warns the caller.
        AnySentence(DescriptionOf(tool), @"cancel", @"settle|snapshot|observ", @"performed|happened|was sent|already|still")
            .Should().BeTrue("cancelling during the settle wait or the snapshot drops the result though the action was performed");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public void The_settle_ms_parameter_description_gives_the_0_to_5000_range(string tool)
    {
        var description = ParameterDescription(tool, "settle_ms");

        description.Should().NotBeNullOrWhiteSpace();
        description.Should().MatchRegex(@"\b0\s*(-|to|and)\s*5000\b");
    }

    [Fact]
    public void Snapshot_description_names_observe_among_the_calls_that_take_a_snapshot()
    {
        // The description lists what else takes a snapshot (and so replaces its ids): an action
        // with observe is now one of them.
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("observe");
    }
}
