using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class RegistryToolsTests
{
    [Fact]
    public async Task RegistryGet_dispatches_to_service()
    {
        var dto = new RegistryValueDto(@"HKCU\Software\Test", "MyValue", "hello", "String");
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.GetAsync("HKCU", @"Software\Test", "MyValue", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var tools = new RegistryTools(mock.Object);
        var result = await tools.RegistryGet("HKCU", @"Software\Test", "MyValue");

        result.Should().Contain("hello");
        mock.VerifyAll();
    }

    [Fact]
    public async Task RegistrySet_requires_confirm()
    {
        var mock = new Mock<IRegistryService>();
        var tools = new RegistryTools(mock.Object);

        Func<Task> act = () => tools.RegistrySet("HKCU", @"Software\Test", "MyValue", "data", "String", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
