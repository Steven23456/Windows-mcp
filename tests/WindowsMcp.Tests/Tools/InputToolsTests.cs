using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class InputToolsTests
{
    [Fact]
    public async Task Click_dispatches_to_service_with_correct_args()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.ClickAsync(100, 200, MouseButton.Left, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClickResult(100, 200, MouseButton.Left, 2));
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Click(100, 200, "left", 2);

        result.Should().Contain("100").And.Contain("200");
        mock.VerifyAll();
    }

    [Fact]
    public async Task Click_rejects_unknown_button_with_clear_message()
    {
        var tools = new InputTools(new Mock<IInputService>().Object, new Mock<IClipboardService>().Object);
        Func<Task> act = () => tools.Click(0, 0, "fourth", 1);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*button*");
    }
}
