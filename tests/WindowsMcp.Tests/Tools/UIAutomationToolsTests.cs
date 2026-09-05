using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class UIAutomationToolsTests
{
    [Fact]
    public async Task FindElement_passes_interactive_kind_to_service()
    {
        var element = new ElementInfo("el-1", "Submit", "Button", true, false,
            new Bounds(10, 20, 100, 30), null, null, null);
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync("Submit", FindKind.Interactive, FindScope.Foreground, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FindElementResult(new[] { element }));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.FindElement("Submit", "interactive");

        result.Should().Contain("Submit").And.Contain("el-1");
        mock.VerifyAll();
    }

    [Fact]
    public async Task FindElement_rejects_unknown_kind_with_clear_message()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", "unknown_kind");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*kind*");
    }

    // D-2: the tool forwards the action and value untouched and reports what actually fired.
    [Fact]
    public async Task InteractElement_forwards_arguments_and_reports_what_fired()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.InteractAsync("el-1", "type", "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InteractResult("el-1", "type", "Keyboard", "typed at the caret"));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.InteractElement("el-1", "type", "hi");

        result.Should().Contain("\"Method\":\"Keyboard\"").And.Contain("el-1");
        mock.VerifyAll();
    }

    // D-4: the tool forwards `expected` untouched and renders the observed state on FAIL.
    [Fact]
    public async Task AssertElement_forwards_expected_and_renders_the_observed_state()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.AssertElementAsync("el-1", "value", "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssertResult("el-1", "value", false, "value is 'ho' (from ValuePattern)"));
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.AssertElement("el-1", "value", "hi");

        result.Should().Be("FAIL: value — observed value is 'ho' (from ValuePattern)");
        mock.VerifyAll();
    }

    [Fact]
    public async Task AssertElement_renders_a_bare_PASS()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.AssertElementAsync("el-1", "enabled", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssertResult("el-1", "enabled", true, "enabled"));
        var tools = new UIAutomationTools(mock.Object);

        (await tools.AssertElement("el-1", "enabled")).Should().Be("PASS");
        mock.VerifyAll();
    }

    // ---- D-5 / D-7: scope, window target, and the off-screen flag reach the service ------------

    [Fact]
    public async Task FindElement_defaults_to_the_foreground_window_and_drops_offscreen()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync("Save", FindKind.Any, FindScope.Foreground, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("Save");

        mock.VerifyAll();
    }

    [Fact]
    public async Task FindElement_forwards_window_scope_with_its_title()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync("Save", FindKind.Any, FindScope.Window, "Notepad", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("Save", scope: "window", window: "Notepad");

        mock.VerifyAll();
    }

    [Fact]
    public async Task FindElement_forwards_desktop_scope_and_include_offscreen()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync("", FindKind.Text, FindScope.Desktop, null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FindElementResult(Array.Empty<ElementInfo>()));
        var tools = new UIAutomationTools(mock.Object);

        await tools.FindElement("", "text", scope: "desktop", include_offscreen: true);

        mock.VerifyAll();
    }

    [Fact]
    public async Task FindElement_rejects_unknown_scope_with_clear_message()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "everywhere");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*everywhere*");
    }

    // A window title only means something with scope=window. Rejecting the mismatch rather than
    // ignoring the argument is the D-4 `expected` rule.
    [Fact]
    public async Task FindElement_rejects_window_scope_without_a_title()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "window");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
    }

    [Fact]
    public async Task FindElement_rejects_a_window_title_with_another_scope()
    {
        var tools = new UIAutomationTools(new Mock<IUIAutomationService>().Object);
        Func<Task> act = () => tools.FindElement("text", scope: "desktop", window: "Notepad");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
    }

    [Fact]
    public async Task WaitFor_forwards_kind_scope_window_and_offscreen()
    {
        var element = new ElementInfo("el-9", "Ready", "Text", true, false, new Bounds(0, 0, 5, 5), null, null, null);
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.WaitForAsync("Ready", 2000, 100, FindKind.Text, FindScope.Window, "Notepad", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(element);
        var tools = new UIAutomationTools(mock.Object);

        var result = await tools.WaitFor("Ready", 2000, 100, "text", "window", "Notepad");

        result.Should().Contain("el-9");
        mock.VerifyAll();
    }

    [Fact]
    public async Task WaitFor_renders_null_on_timeout()
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.WaitForAsync("Ready", 10000, 500, FindKind.Any, FindScope.Foreground, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementInfo?)null);
        var tools = new UIAutomationTools(mock.Object);

        (await tools.WaitFor("Ready")).Should().Be("null");
        mock.VerifyAll();
    }

    // ---- A-2 (R5): the snapshot tool ---------------------------------------------------------
    // The tool is the whole contract the model sees: what it may pass, what comes back, and what
    // an id is good for. Every argument rule is decided HERE, before the service is touched, and
    // the two output formats are exactly "the renderer" and "the DTOs" - no third shape.

    /// <summary>A service that answers every snapshot with <paramref name="result"/>.</summary>
    private static Mock<IUIAutomationService> SnapshotService(SnapshotResult? result = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? Result());
        return mock;
    }

    /// <summary>A snapshot with one of everything, so a dropped block shows up in the output.</summary>
    private static SnapshotResult PopulatedResult(ElementTree? tree = null) => Result(
        windows: [Window()],
        active: Window(isActive: true),
        interactive: [Element(shortcut: "Ctrl+S")],
        scrollable: [Scrollable()],
        tree: tree,
        elementCount: 57);

    [Theory]
    [InlineData("desktop", null, SnapshotScope.Desktop)]
    [InlineData("foreground", null, SnapshotScope.Foreground)]
    [InlineData("window", "Notepad", SnapshotScope.Window)]
    [InlineData("DESKTOP", null, SnapshotScope.Desktop)]      // scope is case-insensitive, as in find_element
    public async Task Snapshot_maps_the_scope_and_calls_the_service_once(string scope, string? window, SnapshotScope expected)
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        await tools.Snapshot(scope, window);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == expected
                                     && r.WindowTitle == window
                                     && !r.IncludeTree
                                     && r.MaxElements == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_forwards_include_tree_and_max_elements()
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        await tools.Snapshot("foreground", include_tree: true, max_elements: 25);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Foreground && r.IncludeTree && r.MaxElements == 25),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Snapshot_rejects_an_unknown_scope_naming_the_three_choices()
    {
        var mock = SnapshotService();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> act = () => tools.Snapshot("everywhere");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("everywhere");
        foreach (var choice in new[] { "desktop", "foreground", "window" })
            message.Should().Contain(choice);
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_rejects_window_scope_without_a_window()
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot("window");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("foreground")]
    public async Task Snapshot_rejects_a_window_with_another_scope(string scope)
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(scope, "Notepad");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Snapshot_rejects_a_negative_max_elements(int max)
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(max_elements: max);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("max_elements", "the message names the argument the caller got wrong");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_passes_max_elements_zero_through_as_the_server_default()
    {
        // 0 is not "no elements": the service turns it into the --max-tree-elements budget.
        var mock = SnapshotService();

        await new UIAutomationTools(mock.Object).Snapshot(max_elements: 0);

        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.MaxElements == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("TEXT")]
    [InlineData("Json")]
    public async Task Snapshot_accepts_both_formats_case_insensitively(string format)
    {
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult()).Object);

        var output = await tools.Snapshot(format: format);

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            output.Should().StartWith("{", "the json form is the serialised SnapshotResult");
        else
            output.Should().StartWith("Cursor:", "the text form starts with the renderer's header");
    }

    // ---- A-14 (R4): a profiled snapshot's timings reach the caller in BOTH formats -----------
    // SnapshotRendererTests pins the text line and StageTimingDtosTests the JSON shape; both call
    // their target directly. This is the tool: it must not strip, re-order or re-serialise the
    // stages on the way out - json is the DTO, text is the renderer, and there is no third shape.

    [Fact]
    public async Task Snapshot_json_carries_the_stage_timings_the_service_reported()
    {
        var profiled = PopulatedResult() with { CaptureMs = 142, Stages = [new("header", 12), new("walk", 130)] };
        var tools = new UIAutomationTools(SnapshotService(profiled).Object);

        var output = await tools.Snapshot(format: "json");

        using var doc = JsonDocument.Parse(output);
        var stages = doc.RootElement.GetProperty("Stages");
        stages.GetArrayLength().Should().Be(2);
        stages[0].GetProperty("Stage").GetString().Should().Be("header");
        stages[1].GetProperty("Ms").GetInt64().Should().Be(130);
        doc.RootElement.GetProperty("CaptureMs").GetInt64().Should().Be(142);
    }

    [Fact]
    public async Task Snapshot_text_ends_with_the_timing_line_when_the_snapshot_was_profiled()
    {
        var profiled = PopulatedResult() with { CaptureMs = 142, Stages = [new("header", 12), new("walk", 130)] };
        var tools = new UIAutomationTools(SnapshotService(profiled).Object);

        var output = await tools.Snapshot(format: "text");

        output.Split('\n').Last().Should().Be("Timing: header 12 ms, walk 130 ms (total 142 ms)");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task Snapshot_says_nothing_about_timings_when_profiling_is_off(string format)
    {
        // The default server: neither output form mentions a stage, so no existing caller's
        // parsing changes because A-14 shipped.
        var tools = new UIAutomationTools(SnapshotService(PopulatedResult()).Object);

        var output = await tools.Snapshot(format: format);

        output.Should().NotContain("Stages").And.NotContain("Timing:");
    }

    [Fact]
    public async Task Snapshot_rejects_an_unknown_format_naming_both_choices()
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(format: "yaml");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("yaml").And.Contain("text").And.Contain("json");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A-5 is not implemented; the PARAMETER exists so the shape is stable, and passing it is a
    // refusal rather than a silently ignored argument (the D-2 `select` failure mode).
    [Fact]
    public async Task Snapshot_use_dom_is_reserved_for_A5_and_refuses_before_the_service_is_called()
    {
        var mock = SnapshotService();

        Func<Task> act = () => new UIAutomationTools(mock.Object).Snapshot(use_dom: true);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("A-5").And.ContainEquivalentOf("not implemented");
        mock.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_text_is_exactly_the_rendered_layout()
    {
        var result = PopulatedResult();
        var tools = new UIAutomationTools(SnapshotService(result).Object);

        var output = await tools.Snapshot();

        output.Should().Be(SnapshotRenderer.Render(result),
            "text is the default format and the renderer is the one place the layout lives");
    }

    [Fact]
    public async Task Snapshot_json_is_the_serialised_result()
    {
        var result = PopulatedResult();
        var tools = new UIAutomationTools(SnapshotService(result).Object);

        var output = await tools.Snapshot(format: "json");

        output.Should().Be(JsonSerializer.Serialize(result));
        output.Should().Contain("\"ElementId\":\"el_12\"").And.Contain("\"CenterX\":612");
        output.Should().Contain("\"Interactive\"").And.Contain("\"Scrollable\"").And.Contain("\"ElementCount\":57");
    }

    [Fact]
    public async Task Snapshot_json_carries_the_tree_only_when_it_was_asked_for()
    {
        var treeRoot = new ElementInfo("desktop", "", "Desktop", true, false, null, null, null, null);
        var withTree = PopulatedResult(new ElementTree(treeRoot, []));
        var withoutTree = PopulatedResult();

        var asked = await new UIAutomationTools(SnapshotService(withTree).Object)
            .Snapshot(include_tree: true, format: "json");
        var notAsked = await new UIAutomationTools(SnapshotService(withoutTree).Object)
            .Snapshot(format: "json");

        using var askedDoc = JsonDocument.Parse(asked);
        askedDoc.RootElement.GetProperty("Tree").GetProperty("Root").GetProperty("ElementId")
            .GetString().Should().Be("desktop");

        using var notAskedDoc = JsonDocument.Parse(notAsked);
        notAskedDoc.RootElement.GetProperty("Tree").ValueKind.Should().Be(JsonValueKind.Null,
            "the tree is the expensive block; it is absent unless include_tree asked for it");
    }

    // The description is the only spec the model reads before it calls the tool: what one call
    // returns, how long an id lives, and which tools accept it.
    [Fact]
    public void Snapshot_description_tells_the_model_what_it_gets_and_how_long_the_ids_live()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.Snapshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var fragment in new[]
                 {
                     "one call", "desktop",                        // what it is
                     "text", "json",                               // the formats, text being default
                     "scope", "window", "max_elements",            // the arguments that change the walk
                     "--max-tree-elements",                        // where the 0 default comes from
                     "next snapshot",                              // roadmap C5: id lifetime
                     "click", "interact_element", "get_element",   // what an id is good for
                     "use_dom", "A-5",                             // reserved, and why it refuses
                 })
            description.Should().ContainEquivalentOf(fragment);

        description.Should().MatchRegex("cent(re|er)", "the model is told the coordinates are the element's centre");
        description.Should().MatchRegex("[Tt]runcat", "the truncation note is advertised, not a surprise");
    }
}
