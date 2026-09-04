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
        mock.Setup(s => s.FindElementAsync("Submit", FindKind.Interactive, It.IsAny<CancellationToken>()))
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
}
