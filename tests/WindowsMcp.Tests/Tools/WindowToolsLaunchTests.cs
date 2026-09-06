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
/// B-8 at the tool layer: what <c>launch</c> advertises, what it forwards, what it refuses, and
/// what the model gets back. The old <c>"launched (pid=N)"</c> string is gone — a boolean the
/// agent can branch on replaces "launched" versus "sent, window not detected".
/// </summary>
[Trait("Category", "Unit")]
public class WindowToolsLaunchTests
{
    private static WindowTools NewTools(IWindowService window)
        => new(window, new Mock<IVirtualDesktopService>().Object);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static LaunchResult Result(
        string matchedName = "Calculator",
        string kind = "packaged",
        int score = 100,
        int pid = 4242,
        long? hwnd = 0x1234,
        string? title = "Calculator",
        bool windowDetected = true,
        string strategy = "prefix")
        => new(matchedName, kind, score, pid, hwnd, title, windowDetected, strategy);

    private static Mock<IWindowService> Service(LaunchResult result, string appName = "calc",
        bool waitForWindow = true, int timeoutMs = 10_000)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.LaunchAsync(appName, waitForWindow, timeoutMs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return mock;
    }

    // ---- the response -------------------------------------------------------------------------

    [Fact]
    public async Task Launch_serialises_every_field_of_the_result()
    {
        var mock = Service(Result());

        var root = Parse(await NewTools(mock.Object).Launch("calc"));

        root.GetProperty("MatchedName").GetString().Should().Be("Calculator",
            "the model asked for 'calc' and needs to be told what actually opened");
        root.GetProperty("Kind").GetString().Should().Be("packaged");
        root.GetProperty("Strategy").GetString().Should().Be("prefix");
        root.GetProperty("Score").GetInt32().Should().Be(100);
        root.GetProperty("Pid").GetInt32().Should().Be(4242);
        root.GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
        root.GetProperty("Title").GetString().Should().Be("Calculator");
        root.GetProperty("WindowDetected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Launch_reports_a_window_that_never_appeared_as_data_not_as_an_error()
    {
        var mock = Service(Result(hwnd: null, title: null, windowDetected: false));

        var root = Parse(await NewTools(mock.Object).Launch("calc"));

        root.GetProperty("WindowDetected").GetBoolean().Should().BeFalse();
        root.GetProperty("Hwnd").ValueKind.Should().Be(JsonValueKind.Null,
            "null says 'not known'; omitting the field would make the model guess");
        root.GetProperty("Title").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("Pid").GetInt32().Should().Be(4242, "the pid is still actionable");
    }

    [Fact]
    public async Task Launch_no_longer_returns_the_old_launched_pid_string()
    {
        // Behaviour change, CHANGELOG Changed: callers that parsed "launched (pid=N)" get JSON.
        var mock = Service(Result());

        var text = await NewTools(mock.Object).Launch("calc");

        text.Should().NotContain("launched (pid=");
        text.Should().StartWith("{", "the response is the serialised LaunchResult");
    }

    // ---- what is forwarded --------------------------------------------------------------------

    [Fact]
    public async Task Launch_defaults_to_waiting_ten_seconds_for_the_window()
    {
        var mock = Service(Result());

        await NewTools(mock.Object).Launch("calc");

        mock.Verify(s => s.LaunchAsync("calc", true, 10_000, It.IsAny<CancellationToken>()), Times.Once,
            "the defaults the description advertises are the defaults the service is given");
    }

    [Fact]
    public async Task Launch_forwards_a_caller_that_does_not_want_to_wait()
    {
        var mock = Service(Result(hwnd: null, title: null, windowDetected: false), waitForWindow: false);

        await NewTools(mock.Object).Launch("calc", wait_for_window: false);

        mock.Verify(s => s.LaunchAsync("calc", false, 10_000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2500)]
    [InlineData(60_000)]
    public async Task Launch_forwards_a_timeout_inside_the_range(int timeoutMs)
    {
        var mock = Service(Result(), timeoutMs: timeoutMs);

        await NewTools(mock.Object).Launch("calc", timeout_ms: timeoutMs);

        mock.Verify(s => s.LaunchAsync("calc", true, timeoutMs, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Launch_passes_a_path_through_untouched()
    {
        var path = @"C:\Program Files\Thing\thing.exe";
        var mock = Service(Result(matchedName: path, kind: "path", strategy: "path"), appName: path);

        var root = Parse(await NewTools(mock.Object).Launch(path));

        root.GetProperty("Strategy").GetString().Should().Be("path");
        root.GetProperty("Kind").GetString().Should().Be("path");
        mock.Verify(s => s.LaunchAsync(path, true, 10_000, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Launch_refuses_a_blank_app_name_without_touching_the_service(string? appName)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Launch(appName!);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("app_name", "the model is told which parameter it left empty");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60_001)]
    [InlineData(int.MaxValue)]
    public async Task Launch_refuses_a_timeout_outside_the_range_and_names_it(int timeoutMs)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Launch("calc", timeout_ms: timeoutMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("timeout_ms").And.Contain("60000",
                "naming the parameter and the ceiling is what lets the model fix the call itself");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Launch_lets_a_catalog_miss_reach_the_caller_with_its_suggestions()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.LaunchAsync("zzqq", true, 10_000, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("No app matching 'zzqq'. Nearest: 'Calculator' (12), 'Notepad' (10)"));

        var act = () => NewTools(mock.Object).Launch("zzqq");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message
            .Should().Contain("Nearest", "the five nearest names are the whole value of a miss");
    }

    // ---- the description is the spec the model reads -------------------------------------------

    [Fact]
    public void Launch_description_advertises_the_catalog_the_matching_and_the_wait()
    {
        var description = typeof(WindowTools).GetMethod(nameof(WindowTools.Launch))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .ContainEquivalentOf("Start Menu", "the catalog's main source is how the model knows what names work")
            .And.ContainEquivalentOf("fuzzy", "a name is matched, not looked up")
            .And.ContainEquivalentOf("packaged", "Store apps are launched by AUMID and the model should know they are covered")
            .And.ContainEquivalentOf("windowDetected", "the field the agent branches on has to be advertised")
            .And.ContainEquivalentOf("timeout_ms");
        description.Should().NotContain("Uses ShellExecute so Start Menu shortcuts and PATH are resolved",
            "that was the whole pre-B-8 description and it is no longer what the tool does");
    }

    [Theory]
    [InlineData("app_name")]
    [InlineData("wait_for_window")]
    [InlineData("timeout_ms")]
    public void Launch_describes_each_of_its_parameters(string parameter)
    {
        var info = typeof(WindowTools).GetMethod(nameof(WindowTools.Launch))!
            .GetParameters().Single(p => p.Name == parameter);

        info.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Launch_parameter_defaults_match_what_the_description_promises()
    {
        var parameters = typeof(WindowTools).GetMethod(nameof(WindowTools.Launch))!.GetParameters();

        parameters.Single(p => p.Name == "wait_for_window").DefaultValue.Should().Be(true);
        parameters.Single(p => p.Name == "timeout_ms").DefaultValue.Should().Be(10_000);
    }
}
