using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

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
}
