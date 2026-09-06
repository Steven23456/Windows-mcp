using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-6 (R45-R63): the <c>wait_for</c> TOOL — the condition vocabulary the model is given, the
/// argument rules that fire before the service is touched, and the fact that a wait ALWAYS
/// answers with a structured result (roadmap C4). What the service then does with the request is
/// <c>WaitForServiceTests</c>; the verdicts themselves are <c>WaitConditionsTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationToolsWaitForTests
{
    private static readonly ElementInfo Saved =
        new("el_9", "Save", "Button", true, false, new Bounds(1, 2, 3, 4), null, null, null);

    /// <summary>A service that answers every wait with <paramref name="result"/> and records the request.</summary>
    private static Mock<IUIAutomationService> Waiting(WaitForResult? result = null, List<WaitRequest>? seen = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()))
            .Callback((WaitRequest r, CancellationToken _) => seen?.Add(r))
            .ReturnsAsync(result ?? new WaitForResult(true, "element_exists", 120, 1, "found 'Save' (el_9)", Saved));
        return mock;
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ---- the old call shape still works, and means element_exists ----------------------------

    [Fact]
    public async Task WaitFor_with_only_a_text_waits_for_that_element_with_the_old_defaults()
    {
        // Roadmap C4: every call written before B-6 keeps its meaning. text stays first, the six
        // filters keep their names, order and defaults, and condition defaults to what the tool
        // has always done.
        var seen = new List<WaitRequest>();
        var mock = Waiting(seen: seen);

        await new UIAutomationTools(mock.Object).WaitFor("Ready");

        var request = seen.Should().ContainSingle().Subject;
        request.Condition.Should().Be(WaitCondition.ElementExists);
        request.Text.Should().Be("Ready");
        request.TimeoutMs.Should().Be(10000);
        request.IntervalMs.Should().Be(500);
        request.Kind.Should().Be(FindKind.Any);
        request.Scope.Should().Be(FindScope.Foreground);
        request.WindowTitle.Should().BeNull();
        request.IncludeOffscreen.Should().BeFalse();
        request.UseDom.Should().BeFalse();
    }

    [Fact]
    public async Task WaitFor_forwards_kind_scope_window_offscreen_and_the_two_budgets()
    {
        var seen = new List<WaitRequest>();
        var mock = Waiting(seen: seen);

        await new UIAutomationTools(mock.Object).WaitFor(
            "Ready", timeout_ms: 2000, interval_ms: 100, kind: "text", scope: "window",
            window: "Notepad", include_offscreen: true);

        var request = seen.Should().ContainSingle().Subject;
        request.TimeoutMs.Should().Be(2000);
        request.IntervalMs.Should().Be(100);
        request.Kind.Should().Be(FindKind.Text);
        request.Scope.Should().Be(FindScope.Window);
        request.WindowTitle.Should().Be("Notepad");
        request.IncludeOffscreen.Should().BeTrue();
        mock.Verify(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitFor_keeps_the_scope_and_window_rules_find_element_uses()
    {
        var mock = Waiting();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> noTitle = () => tools.WaitFor("Ready", scope: "window");
        Func<Task> strayTitle = () => tools.WaitFor("Ready", scope: "desktop", window: "Notepad");

        await noTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
        await strayTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        mock.VerifyNoOtherCalls();
    }

    // ---- the condition vocabulary -------------------------------------------------------------

    [Theory]
    [InlineData("element_exists", WaitCondition.ElementExists)]
    [InlineData("element", WaitCondition.ElementExists)]
    [InlineData("element_enabled", WaitCondition.ElementEnabled)]
    [InlineData("enabled", WaitCondition.ElementEnabled)]
    [InlineData("focused_element", WaitCondition.FocusedElement)]
    [InlineData("focused", WaitCondition.FocusedElement)]
    [InlineData("text_exists", WaitCondition.TextExists)]
    [InlineData("text", WaitCondition.TextExists)]
    [InlineData("active_window", WaitCondition.ActiveWindow)]
    [InlineData("window", WaitCondition.ActiveWindow)]
    [InlineData("ACTIVE_WINDOW", WaitCondition.ActiveWindow)]
    [InlineData("Text", WaitCondition.TextExists)]
    public async Task WaitFor_accepts_every_condition_name_and_upstream_alias(string condition, WaitCondition expected)
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor("Ready", condition: condition);

        seen.Should().ContainSingle().Which.Condition.Should().Be(expected);
    }

    [Fact]
    public async Task WaitFor_rejects_an_unknown_condition_by_listing_the_ones_it_has()
    {
        // A rejection that does not enumerate the vocabulary leaves the model guessing a second time.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", condition: "appears");

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("appears")
            .And.Contain("element_exists").And.Contain("element_enabled").And.Contain("focused_element")
            .And.Contain("text_exists").And.Contain("active_window");
        mock.VerifyNoOtherCalls();   // an unparseable condition is refused before the service is asked to wait
    }

    [Fact]
    public async Task WaitFor_forwards_use_dom_for_the_conditions_that_read_the_page()
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object)
            .WaitFor("Probe heading", condition: "text_exists", use_dom: true);

        var request = seen.Should().ContainSingle().Subject;
        request.UseDom.Should().BeTrue();
        request.Condition.Should().Be(WaitCondition.TextExists);
    }

    [Fact]
    public async Task WaitFor_accepts_use_dom_with_a_condition_that_ignores_it()
    {
        // Accepted and ignored, not refused: the flag is cheap and refusing it would make the
        // model choose between two spellings of the same wait.
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object)
            .WaitFor("Notepad", condition: "active_window", use_dom: true);

        seen.Should().ContainSingle().Which.UseDom.Should().BeTrue();
    }

    // ---- the result (roadmap C4) --------------------------------------------------------------

    [Fact]
    public async Task WaitFor_returns_the_whole_result_when_the_condition_is_satisfied()
    {
        var mock = Waiting(new WaitForResult(true, "element_exists", 1234, 3, "found 'Save' (el_9)", Saved));

        var json = Json(await new UIAutomationTools(mock.Object).WaitFor("Save"));

        json.GetProperty("Satisfied").GetBoolean().Should().BeTrue();
        json.GetProperty("Condition").GetString().Should().Be("element_exists");
        json.GetProperty("ElapsedMs").GetInt64().Should().Be(1234);
        json.GetProperty("Attempts").GetInt32().Should().Be(3);
        json.GetProperty("Detail").GetString().Should().Be("found 'Save' (el_9)");
        json.GetProperty("Element").GetProperty("ElementId").GetString().Should().Be("el_9",
            "the id is what the next call clicks with");
    }

    [Fact]
    public async Task WaitFor_returns_a_result_on_timeout_instead_of_the_string_null()
    {
        // THE contract break of section B (roadmap C4 / decision 2): "null" told the model nothing
        // about how long it waited or what was on screen instead, and read as an error.
        var mock = Waiting(new WaitForResult(false, "text_exists", 10000, 20, "no text matching 'Ready'"));

        var text = await new UIAutomationTools(mock.Object).WaitFor("Ready", condition: "text");

        text.Should().NotBe("null");
        var json = Json(text);
        json.GetProperty("Satisfied").GetBoolean().Should().BeFalse();
        json.GetProperty("Detail").GetString().Should().Be("no text matching 'Ready'");
        json.GetProperty("Attempts").GetInt32().Should().Be(20);
        json.TryGetProperty("Element", out var element).Should().BeFalse(
            "there is no element to report; a null Element would be noise on every timeout");
        _ = element;
    }

    // ---- the ranges, mirrored at the tool -----------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(120001)]
    public async Task WaitFor_refuses_a_timeout_outside_the_range_before_asking_the_service(int timeoutMs)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", timeout_ms: timeoutMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("timeout_ms").And.Contain("120000");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task WaitFor_refuses_an_interval_outside_the_range_before_asking_the_service(int intervalMs)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", interval_ms: intervalMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("interval_ms").And.Contain("5000");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WaitFor_refuses_a_blank_text_naming_the_condition(string text)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor(text, condition: "active_window");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("active_window");
        mock.VerifyNoOtherCalls();   // a wait on nothing would be satisfied by anything
    }

    [Theory]
    [InlineData("window", "active_window")]
    [InlineData("text", "text_exists")]
    [InlineData("focused", "focused_element")]
    [InlineData("enabled", "element_enabled")]
    [InlineData("element", "element_exists")]
    public async Task WaitFor_names_the_canonical_condition_when_an_alias_asked_for_nothing(
        string alias, string canonical)
    {
        // The alias is accepted, but the message teaches the canonical name - otherwise the model
        // learns "window" from its own mistake and carries the short form into the next call.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("  ", condition: alias);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain(canonical).And.Contain("needs text",
                "the alias was understood - the refusal is about the missing text, not the condition");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitFor_refuses_an_unknown_condition_before_it_looks_at_the_text()
    {
        // Order matters for the diagnosis: with both wrong, the condition is the one that has to
        // be reported, because there is no condition to name a text requirement for.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("", condition: "appears");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("appears").And.NotContain("needs text");
        mock.VerifyNoOtherCalls();
    }

    // ---- the description IS the spec the model reads -------------------------------------------

    [Fact]
    public void WaitFor_describes_the_five_conditions_their_aliases_and_the_result_shape()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_exists").And.Contain("element_enabled").And.Contain("focused_element")
            .And.Contain("text_exists").And.Contain("active_window")
            .And.Contain("use_dom")
            .And.ContainEquivalentOf("satisfied", "the result shape is what the model keys on")
            .And.ContainEquivalentOf("detail");
        description.Should().NotContain("'null'",
            "the null-on-timeout contract is gone; a description that still promises it is a lie");
    }

    [Fact]
    public void WaitFor_describes_a_timeout_as_an_outcome_rather_than_an_error()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().ContainEquivalentOf("timeout")
            .And.ContainEquivalentOf("not an error",
                "a model that reads a timeout as a failure retries instead of acting on the detail");
    }

    [Fact]
    public void WaitFor_describes_its_two_new_parameters()
    {
        var parameters = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!.GetParameters();

        var condition = parameters.Single(p => p.Name == "condition");
        var useDom = parameters.Single(p => p.Name == "use_dom");

        condition.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().Contain("element_exists").And.Contain("active_window");
        useDom.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().ContainEquivalentOf("page", "use_dom is about the browser page, not the window");
        condition.HasDefaultValue.Should().BeTrue();
        condition.DefaultValue.Should().Be("element_exists");
        useDom.DefaultValue.Should().Be(false);
    }

    [Fact]
    public void WaitFor_keeps_the_seven_parameters_it_had_in_the_order_it_had_them()
    {
        // Callers pass text positionally and the schema order is what the model copies; the two
        // new parameters go on the END.
        var names = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetParameters().Select(p => p.Name).ToArray();

        names.Should().Equal(
            "text", "timeout_ms", "interval_ms", "kind", "scope", "window", "include_offscreen",
            "condition", "use_dom");
    }
}
