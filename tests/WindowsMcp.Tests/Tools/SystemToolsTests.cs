using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class SystemToolsTests
{
    private static SystemTools MakeTools(
        IWmiService? wmi = null,
        IEnvService? env = null,
        IPowerService? power = null,
        INotificationService? notification = null,
        IAudioService? audio = null,
        ISecurityService? security = null,
        IReliabilityService? reliability = null)
    {
        return new SystemTools(
            wmi          ?? new Mock<IWmiService>().Object,
            env          ?? new Mock<IEnvService>().Object,
            power        ?? new Mock<IPowerService>().Object,
            notification ?? new Mock<INotificationService>().Object,
            audio        ?? new Mock<IAudioService>().Object,
            security     ?? new Mock<ISecurityService>().Object,
            reliability  ?? new Mock<IReliabilityService>().Object);
    }

    [Fact]
    public async Task PowerAction_requires_confirm()
    {
        var mockPower = new Mock<IPowerService>();
        var tools = MakeTools(power: mockPower.Object);

        Func<Task> act = () => tools.PowerAction("shutdown", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockPower.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SystemInfo_dispatches_to_wmi_with_correct_class()
    {
        var mockWmi = new Mock<IWmiService>();
        mockWmi.Setup(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<object>());

        var tools = MakeTools(wmi: mockWmi.Object);
        var result = await tools.SystemInfo("os");

        result.Should().NotBeNull();
        mockWmi.Verify(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Env_set_requires_confirm()
    {
        var mockEnv = new Mock<IEnvService>();
        var tools = MakeTools(env: mockEnv.Object);

        Func<Task> act = () => tools.Env("set", name: "MY_VAR", value: "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockEnv.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<EnvironmentVariableTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
