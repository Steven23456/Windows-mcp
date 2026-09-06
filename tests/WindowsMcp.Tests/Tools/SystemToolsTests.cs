using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
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
        IReliabilityService? reliability = null,
        IDriverService? drivers = null)
    {
        return new SystemTools(
            wmi          ?? new Mock<IWmiService>().Object,
            env          ?? new Mock<IEnvService>().Object,
            power        ?? new Mock<IPowerService>().Object,
            notification ?? new Mock<INotificationService>().Object,
            audio        ?? new Mock<IAudioService>().Object,
            security     ?? new Mock<ISecurityService>().Object,
            reliability  ?? new Mock<IReliabilityService>().Object,
            drivers      ?? new Mock<IDriverService>().Object);
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

    // ---- C-4: the notification tool ----------------------------------------------------------

    /// <summary>C-4 (R3): the advertised default is the id the server registers for itself.</summary>
    [Fact]
    public void Notification_defaults_app_id_to_the_servers_own_aumid()
    {
        typeof(SystemTools).GetMethod(nameof(SystemTools.Notification))!
            .GetParameters().Single(p => p.Name == "app_id")
            .DefaultValue.Should().Be("Windows-MCP");
    }

    /// <summary>C-4 (R3): the default reaches the service, and the result is the JSON contract.</summary>
    [Fact]
    public async Task Notification_shows_under_the_default_id_and_returns_the_result()
    {
        var mock = new Mock<INotificationService>();
        mock.Setup(s => s.ShowAsync("hi", "there", "Windows-MCP", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationResult(true, "Windows-MCP", true, null));
        var tools = MakeTools(notification: mock.Object);

        var json = JsonDocument.Parse(await tools.Notification("hi", "there")).RootElement;

        json.GetProperty("shown").GetBoolean().Should().BeTrue();
        json.GetProperty("appId").GetString().Should().Be("Windows-MCP");
        json.GetProperty("registered").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.ShowAsync("hi", "there", "Windows-MCP", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// C-4 (R3): a caller's id is forwarded as given, and a dropped toast comes back as a result
    /// with the reason - not as a success and not as an exception.
    /// </summary>
    [Fact]
    public async Task Notification_forwards_a_custom_id_and_carries_the_note_back()
    {
        var mock = new Mock<INotificationService>();
        mock.Setup(s => s.ShowAsync("t", "m", "Contoso.App", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationResult(false, "Contoso.App", false,
                "Contoso.App is not a registered AppUserModelId (0x80070490)"));
        var tools = MakeTools(notification: mock.Object);

        var json = JsonDocument.Parse(await tools.Notification("t", "m", "Contoso.App")).RootElement;

        json.GetProperty("shown").GetBoolean().Should().BeFalse();
        json.GetProperty("appId").GetString().Should().Be("Contoso.App");
        json.GetProperty("registered").GetBoolean().Should().BeFalse();
        json.GetProperty("note").GetString().Should().Contain("Contoso.App");
        mock.Verify(s => s.ShowAsync("t", "m", "Contoso.App", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>C-4 (R3): a blank id never reaches the platform.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Notification_refuses_a_blank_app_id(string appId)
    {
        var mock = new Mock<INotificationService>();
        var tools = MakeTools(notification: mock.Object);

        Func<Task> act = () => tools.Notification("t", "m", appId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*app*");
        mock.Verify(s => s.ShowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
