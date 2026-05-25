using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class WindowToolsTests
{
    [Fact]
    public async Task Window_dispatches_to_service_with_correct_action_and_title()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("minimize", "Notepad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("minimize", "Notepad", true));
        var tools = new WindowTools(mock.Object);

        var result = await tools.Window("minimize", "Notepad");

        result.Should().Contain("minimize").And.Contain("Notepad");
        mock.VerifyAll();
    }

    [Fact]
    public async Task MultiMonitor_returns_serialized_array_with_both_monitors()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new MonitorInfo(0, "DISPLAY1", 0,    0, 1920, 1080, true),
                new MonitorInfo(1, "DISPLAY2", 1920, 0, 2560, 1440, false)
            });
        var tools = new WindowTools(mock.Object);

        var result = await tools.MultiMonitor();

        result.Should().Contain("DISPLAY1").And.Contain("DISPLAY2");
        mock.VerifyAll();
    }
}
