using System.Text.Json;
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

    // ---- A-1: window(action:"list" | "active") ----------------------------------------------

    /// <summary>One inventory entry; every field distinct so a mis-mapped field shows up.</summary>
    private static WindowInfo Info(
        string title = "Untitled - Notepad",
        long hwnd = 0x1234,
        int pid = 9001,
        string process = "notepad",
        WindowState state = WindowState.Maximized,
        int zOrder = 0,
        bool isActive = true,
        bool isBrowser = false,
        int monitorIndex = 1)
        => new(title, hwnd, pid, process, state, new Bounds(300, 40, 1024, 768), zOrder, isActive, isBrowser, monitorIndex);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Window_list_returns_every_field_of_every_window_as_a_json_array()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Info(),
                Info("Docs - Google Chrome", hwnd: 0x99, pid: 7, process: "chrome",
                     state: WindowState.Normal, zOrder: 1, isActive: false, isBrowser: true, monitorIndex: -1),
            ]);
        var tools = new WindowTools(mock.Object);

        var root = Parse(await tools.Window("list"));

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(2);
        var first = root[0];
        first.GetProperty("Title").GetString().Should().Be("Untitled - Notepad");
        first.GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
        first.GetProperty("Pid").GetInt32().Should().Be(9001);
        first.GetProperty("ProcessName").GetString().Should().Be("notepad");
        first.GetProperty("ZOrder").GetInt32().Should().Be(0);
        first.GetProperty("IsActive").GetBoolean().Should().BeTrue();
        first.GetProperty("IsBrowser").GetBoolean().Should().BeFalse();
        first.GetProperty("MonitorIndex").GetInt32().Should().Be(1);
        first.GetProperty("DesktopId").ValueKind.Should().Be(JsonValueKind.Null);
        var bounds = first.GetProperty("Bounds");
        (bounds.GetProperty("X").GetInt32(), bounds.GetProperty("Y").GetInt32(),
         bounds.GetProperty("Width").GetInt32(), bounds.GetProperty("Height").GetInt32())
            .Should().Be((300, 40, 1024, 768));

        var second = root[1];
        second.GetProperty("IsBrowser").GetBoolean().Should().BeTrue();
        second.GetProperty("IsActive").GetBoolean().Should().BeFalse();
        second.GetProperty("MonitorIndex").GetInt32().Should().Be(-1, "-1 means the window is on no monitor");
        second.GetProperty("ZOrder").GetInt32().Should().Be(1);

        mock.Verify(s => s.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(WindowState.Normal, "Normal")]
    [InlineData(WindowState.Minimized, "Minimized")]
    [InlineData(WindowState.Maximized, "Maximized")]
    public async Task Window_list_writes_the_state_as_its_name(WindowState state, string expected)
    {
        // The model reads this JSON: "Minimized" is information, 1 is a riddle. (Note that plain
        // DTO enums elsewhere — ClickResult.Button — serialise numerically; this one is pinned to
        // the name, which needs a JsonStringEnumConverter on WindowState or on the serialisation.)
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Info(state: state)]);

        var root = Parse(await new WindowTools(mock.Object).Window("list"));

        root[0].GetProperty("State").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Window_list_forwards_both_flags_to_the_service()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tools = new WindowTools(mock.Object);

        var json = await tools.Window("list", include_minimized: false, include_hidden: true);

        json.Should().Be("[]", "an empty desktop is an empty array, not null and not an error");
        mock.Verify(s => s.ListAsync(false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("LIST")]
    [InlineData("List")]
    public async Task Window_list_is_case_insensitive_and_ignores_a_title(string action)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Info()]);
        var tools = new WindowTools(mock.Object);

        var root = Parse(await tools.Window(action, "Notepad"));

        root.GetArrayLength().Should().Be(1);
        mock.Verify(s => s.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "list needs no title, so a title given with it is ignored, not acted on");
    }

    [Theory]
    [InlineData("active")]
    [InlineData("ACTIVE")]
    public async Task Window_active_returns_the_foreground_window_as_one_object(string action)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Info(title: "Docs - Google Chrome", hwnd: 0x99, process: "chrome", isBrowser: true));
        var tools = new WindowTools(mock.Object);

        var root = Parse(await tools.Window(action, "ignored"));

        root.ValueKind.Should().Be(JsonValueKind.Object, "'active' is one window, not a list");
        root.GetProperty("Title").GetString().Should().Be("Docs - Google Chrome");
        root.GetProperty("Hwnd").GetInt64().Should().Be(0x99);
        root.GetProperty("IsActive").GetBoolean().Should().BeTrue();
        root.GetProperty("IsBrowser").GetBoolean().Should().BeTrue();
        root.GetProperty("State").GetString().Should().Be("Maximized");
        mock.Verify(s => s.GetActiveAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Window_active_reports_found_false_when_there_is_no_foreground_window()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((WindowInfo?)null);
        var tools = new WindowTools(mock.Object);

        var json = await tools.Window("active");

        json.Should().Contain("\"found\":false");
        var root = Parse(json);
        root.TryGetProperty("Title", out _).Should().BeFalse("there is no window to describe");
        root.TryGetProperty("Hwnd", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    [InlineData("close")]
    public async Task Window_still_requires_a_title_for_the_acting_actions(string action)
    {
        var mock = new Mock<IWindowService>();
        var tools = new WindowTools(mock.Object);

        var act = () => tools.Window(action);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("title");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "the missing argument is caught before the service is asked to act on nothing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Window_treats_a_blank_title_as_missing(string title)
    {
        var mock = new Mock<IWindowService>();
        var tools = new WindowTools(mock.Object);

        var act = () => tools.Window("minimize", title);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("title");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("bogus", null)]
    [InlineData("bogus", "Notepad")]
    [InlineData("lst", null)]
    // Actions are matched with ToLowerInvariant() and nothing else, the way every other tool in
    // the repo matches its action/mode, so a padded action is a wrong action, not a trimmed one.
    [InlineData(" list ", null)]
    public async Task Window_rejects_an_unknown_action_and_names_every_valid_one(string action, string? title)
    {
        var mock = new Mock<IWindowService>();
        var tools = new WindowTools(mock.Object);

        var act = () => tools.Window(action, title);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain(action, "the model needs to see what it sent")
            .And.Contain("list|active|minimize|maximize|restore|close",
                "the two new actions belong in the error the model reads when it guesses wrong");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unknown action is rejected by the tool, not turned into a no-op success by the service");
    }

    [Fact]
    public async Task Window_rejects_an_empty_action_without_touching_the_service()
    {
        var mock = new Mock<IWindowService>();

        var act = () => new WindowTools(mock.Object).Window("");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("list|active|minimize|maximize|restore|close",
                "an empty action is as unknown as a misspelt one, and the model is told the whole menu");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Window_passes_the_acting_action_through_as_written()
    {
        // The response echoes the action the caller sent, and ExecuteAsync lowercases internally.
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("MINIMIZE", "Notepad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("MINIMIZE", "Notepad", true));
        var tools = new WindowTools(mock.Object);

        var json = await tools.Window("MINIMIZE", "Notepad");

        Parse(json).GetProperty("Action").GetString().Should().Be("MINIMIZE");
        mock.Verify(s => s.ExecuteAsync("MINIMIZE", "Notepad", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- the rest of the tool surface (pre-existing, unchanged by A-1) -----------------------
    // These three had no test at all; WindowTools.cs is in A-1's diff, so they are covered here
    // rather than left as the only uncovered methods in a changed file. Behaviour is pinned as
    // it stands today — nothing about them was altered by A-1.

    [Theory]
    [InlineData(true, "switched to 'Notepad'")]
    [InlineData(false, "window 'Notepad' not found")]
    public async Task SwitchToWindow_reports_whether_the_window_was_found(bool found, string expected)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.SwitchToAsync("Notepad", It.IsAny<CancellationToken>())).ReturnsAsync(found);

        var text = await new WindowTools(mock.Object).SwitchToWindow("Notepad");

        text.Should().Be(expected);
        mock.Verify(s => s.SwitchToAsync("Notepad", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, "focused 'Notepad'")]
    [InlineData(false, "window 'Notepad' not found")]
    public async Task Focus_is_switch_to_window_with_its_own_wording(bool found, string expected)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.SwitchToAsync("Notepad", It.IsAny<CancellationToken>())).ReturnsAsync(found);

        var text = await new WindowTools(mock.Object).Focus("Notepad");

        text.Should().Be(expected, "the alias reports 'focused', not 'switched to'");
        mock.Verify(s => s.SwitchToAsync("Notepad", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Launch_returns_the_pid_the_service_reports()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.LaunchAsync("notepad.exe", It.IsAny<CancellationToken>())).ReturnsAsync(4242);

        var text = await new WindowTools(mock.Object).Launch("notepad.exe");

        text.Should().Be("launched (pid=4242)");
        mock.Verify(s => s.LaunchAsync("notepad.exe", It.IsAny<CancellationToken>()), Times.Once);
    }
}
