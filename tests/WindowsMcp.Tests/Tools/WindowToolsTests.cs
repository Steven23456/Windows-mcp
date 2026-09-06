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

[Trait("Category", "Unit")]
public class WindowToolsTests
{
    [Fact]
    public async Task Window_dispatches_to_service_with_correct_action_and_title()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("minimize", "Notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("minimize", "Notepad", true));
        var tools = NewTools(mock.Object);

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
        var tools = NewTools(mock.Object);

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

    /// <summary>
    /// A-12 gave the tool a second collaborator. Every pre-A-12 test passes an untouched loose
    /// mock for it: none of those actions may reach the virtual-desktop service, and the A-12
    /// tests assert the reverse with <c>VerifyNoOtherCalls</c> on the window service.
    /// </summary>
    private static WindowTools NewTools(IWindowService window, IVirtualDesktopService? desktops = null)
        => new(window, desktops ?? new Mock<IVirtualDesktopService>().Object);

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
        var tools = NewTools(mock.Object);

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
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var root = Parse(await NewTools(mock.Object).Window("list"));

        root[0].GetProperty("State").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Window_list_forwards_both_flags_to_the_service()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tools = NewTools(mock.Object);

        var json = await tools.Window("list", include_minimized: false, include_hidden: true);

        json.Should().Be("[]", "an empty desktop is an empty array, not null and not an error");
        mock.Verify(s => s.ListAsync(false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("active")]
    [InlineData("desktops")]
    public async Task Window_ignores_an_hwnd_on_the_reading_actions(string action)
    {
        // The description says list/active/desktops ignore 'title' and 'hwnd'. An hwnd that
        // quietly turned a read into an act would be the worst kind of surprise.
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Info()]);
        mock.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Info());
        var desktops = new Mock<IVirtualDesktopService>();
        desktops.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var tools = new WindowTools(mock.Object, desktops.Object);

        await tools.Window(action, hwnd: 0x99L);

        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never, "an hwnd is a target for the acting actions only");
        mock.Verify(s => s.BringToFrontAsync(It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        var tools = NewTools(mock.Object);

        var root = Parse(await tools.Window(action, "Notepad"));

        root.GetArrayLength().Should().Be(1);
        mock.Verify(s => s.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
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
        var tools = NewTools(mock.Object);

        var root = Parse(await tools.Window(action, "ignored"));

        root.ValueKind.Should().Be(JsonValueKind.Object, "'active' is one window, not a list");
        root.GetProperty("Title").GetString().Should().Be("Docs - Google Chrome");
        root.GetProperty("Hwnd").GetInt64().Should().Be(0x99);
        root.GetProperty("IsActive").GetBoolean().Should().BeTrue();
        root.GetProperty("IsBrowser").GetBoolean().Should().BeTrue();
        root.GetProperty("State").GetString().Should().Be("Maximized");
        mock.Verify(s => s.GetActiveAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Window_active_reports_found_false_when_there_is_no_foreground_window()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((WindowInfo?)null);
        var tools = NewTools(mock.Object);

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
        var tools = NewTools(mock.Object);

        var act = () => tools.Window(action);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("title").And.Contain("hwnd",
                "B-10: either one names the window, so the refusal has to mention both");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
            "the missing argument is caught before the service is asked to act on nothing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Window_treats_a_blank_title_as_missing(string title)
    {
        var mock = new Mock<IWindowService>();
        var tools = NewTools(mock.Object);

        var act = () => tools.Window("minimize", title);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("title");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        var tools = NewTools(mock.Object);

        var act = () => tools.Window(action, title);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain(action, "the model needs to see what it sent")
            .And.Contain("list|active|desktops|minimize|maximize|restore|close",
                "every action belongs in the error the model reads when it guesses wrong — A-12 added 'desktops'");
        mock.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unknown action is rejected by the tool, not turned into a no-op success by the service");
    }

    [Fact]
    public async Task Window_rejects_an_empty_action_without_touching_the_service()
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Window("");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("list|active|desktops|minimize|maximize|restore|close",
                "an empty action is as unknown as a misspelt one, and the model is told the whole menu");
        mock.VerifyNoOtherCalls();
    }

    // ---- A-12 phase 1: window(action:"desktops") ---------------------------------------------

    private static readonly VirtualDesktopInfo[] TwoDesktops =
    [
        new("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa", "Work", 0, false),
        new("96a9d868-feea-4270-bf42-ffcfae7316f5", "Play", 1, true),
    ];

    private static Mock<IVirtualDesktopService> Desktops(params VirtualDesktopInfo[] all)
    {
        var mock = new Mock<IVirtualDesktopService>();
        mock.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(all);
        return mock;
    }

    [Fact]
    public async Task Window_desktops_returns_the_current_desktop_and_all_of_them()
    {
        var window = new Mock<IWindowService>();
        var desktops = Desktops(TwoDesktops);

        var root = Parse(await NewTools(window.Object, desktops.Object).Window("desktops"));

        root.ValueKind.Should().Be(JsonValueKind.Object, "'desktops' is an envelope, not a bare array");
        var all = root.GetProperty("all");
        all.GetArrayLength().Should().Be(2);
        all[0].GetProperty("Id").GetString().Should().Be("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa");
        all[0].GetProperty("Name").GetString().Should().Be("Work");
        all[0].GetProperty("Index").GetInt32().Should().Be(0);
        all[0].GetProperty("IsCurrent").GetBoolean().Should().BeFalse();
        all[1].GetProperty("Name").GetString().Should().Be("Play");
        all[1].GetProperty("IsCurrent").GetBoolean().Should().BeTrue();

        var current = root.GetProperty("current");
        current.ValueKind.Should().Be(JsonValueKind.Object);
        current.GetProperty("Id").GetString().Should().Be("96a9d868-feea-4270-bf42-ffcfae7316f5");
        current.GetProperty("Name").GetString().Should().Be("Play");
        current.GetProperty("Index").GetInt32().Should().Be(1);
        current.GetProperty("IsCurrent").GetBoolean().Should().BeTrue();

        window.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Window_desktops_takes_current_from_the_list_it_just_returned()
    {
        // Resolved ambiguity: one service call, one truth. Reading `current` from a second call
        // costs a second registry read and lets the two halves of one response disagree — so
        // GetCurrentAsync (which is defined as "the IsCurrent entry of ListAsync") is not used
        // here, and a mock that answers it differently must not change the response.
        var desktops = Desktops(TwoDesktops);
        desktops.Setup(d => d.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualDesktopInfo("00000000-0000-0000-0000-000000000000", "Contradiction", 7, true));

        var root = Parse(await NewTools(new Mock<IWindowService>().Object, desktops.Object).Window("desktops"));

        root.GetProperty("current").GetProperty("Name").GetString().Should().Be("Play");
        desktops.Verify(d => d.ListAsync(It.IsAny<CancellationToken>()), Times.Once,
            "one call per tool call: the list is the whole answer");
    }

    [Fact]
    public async Task Window_desktops_reports_current_null_when_no_desktop_is_flagged()
    {
        var desktops = Desktops(new VirtualDesktopInfo("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa", "Desktop 1", 0, false));

        var json = await NewTools(new Mock<IWindowService>().Object, desktops.Object).Window("desktops");

        var root = Parse(json);
        root.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null,
            "null says 'not known', which is honest; omitting the field would make the model guess");
        root.GetProperty("all").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Window_desktops_returns_an_empty_list_when_the_machine_reports_none()
    {
        // The observed 10.0.28000 shape: the VirtualDesktops key has no VirtualDesktopIDs value.
        var desktops = Desktops();

        var root = Parse(await NewTools(new Mock<IWindowService>().Object, desktops.Object).Window("desktops"));

        root.GetProperty("all").GetArrayLength().Should().Be(0, "no desktops is an empty array, not an error");
        root.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("desktops", null)]
    [InlineData("DESKTOPS", null)]
    [InlineData("Desktops", "Notepad")]
    public async Task Window_desktops_is_case_insensitive_and_needs_no_title(string action, string? title)
    {
        var window = new Mock<IWindowService>();
        var desktops = Desktops(TwoDesktops);

        var root = Parse(await NewTools(window.Object, desktops.Object).Window(action, title));

        root.GetProperty("all").GetArrayLength().Should().Be(2);
        desktops.Verify(d => d.ListAsync(It.IsAny<CancellationToken>()), Times.Once);
        window.VerifyNoOtherCalls();
    }

    // The description is the only spec the model reads: an action it does not name is an action
    // that is never called, and a field it calls "reserved, null" is a field that is never used.

    [Fact]
    public void Window_description_advertises_the_desktops_action()
    {
        var description = typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("desktops", "the new action has to be in the menu the model reads")
            .And.NotContain("DesktopId (reserved, null)",
                "A-12 fills DesktopId; the description must stop telling the model the field is always null");
    }

    [Fact]
    public void Window_action_parameter_lists_the_desktops_action_too()
    {
        var info = typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!
            .GetParameters().Single(p => p.Name == "action");

        info.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("desktops");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("active")]
    public async Task Window_list_and_active_never_touch_the_virtual_desktop_service(string action)
    {
        // A-12 fills DesktopId inside WindowService, not in the tool: the tool must not start
        // making a second call per window list.
        var window = new Mock<IWindowService>();
        window.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Info()]);
        window.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Info());
        var desktops = Desktops(TwoDesktops);

        await NewTools(window.Object, desktops.Object).Window(action);

        desktops.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Window_passes_the_acting_action_through_as_written()
    {
        // The response echoes the action the caller sent, and ExecuteAsync lowercases internally.
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("MINIMIZE", "Notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("MINIMIZE", "Notepad", true));
        var tools = NewTools(mock.Object);

        var json = await tools.Window("MINIMIZE", "Notepad");

        Parse(json).GetProperty("Action").GetString().Should().Be("MINIMIZE");
        mock.Verify(s => s.ExecuteAsync("MINIMIZE", "Notepad", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- the rest of the tool surface (pre-existing, unchanged by A-1) -----------------------
    // These three had no test at all; WindowTools.cs is in A-1's diff, so they are covered here
    // rather than left as the only uncovered methods in a changed file. Behaviour is pinned as
    // it stands today — nothing about them was altered by A-1.

    // ---- B-10: switch_to_window / focus return the whole ForegroundResult -------------------
    // Both tools are the same call with a different name (the alias predates B-10), so every
    // requirement below is a [Theory] over the two of them: a fix applied to one and forgotten
    // on the other is a failure, not a coincidence.

    private const string SwitchVerb = "switch_to_window";
    private const string FocusVerb = "focus";

    private static Func<WindowTools, string?, long?, Task<string>> Verb(string verb) => verb switch
    {
        SwitchVerb => (tools, title, hwnd) => tools.SwitchToWindow(title, hwnd),
        FocusVerb => (tools, title, hwnd) => tools.Focus(title, hwnd),
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    private static ForegroundResult Fg(
        WindowInfo? window = null,
        string matchStrategy = "substring",
        int score = 100,
        bool restored = false,
        string? strategy = "SetForegroundWindow",
        bool success = true)
        => new(window ?? Info(), matchStrategy, score, restored, strategy, success);

    [Theory]
    [InlineData(SwitchVerb)]
    [InlineData(FocusVerb)]
    public async Task SwitchToWindow_and_focus_serialise_every_field_of_the_foreground_result(string verb)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.BringToFrontAsync("notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fg(matchStrategy: "fuzzy", score: 86, restored: true, strategy: "AttachThreadInput"));

        var root = Parse(await Verb(verb)(NewTools(mock.Object), "notepad", null));

        root.GetProperty("Window").GetProperty("Title").GetString().Should().Be("Untitled - Notepad",
            "the model needs the title of the window that actually matched, not the string it sent");
        root.GetProperty("Window").GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
        root.GetProperty("MatchStrategy").GetString().Should().Be("fuzzy");
        root.GetProperty("Score").GetInt32().Should().Be(86);
        root.GetProperty("Restored").GetBoolean().Should().BeTrue();
        root.GetProperty("Strategy").GetString().Should().Be("AttachThreadInput");
        root.GetProperty("Success").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.BringToFrontAsync("notepad", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(SwitchVerb)]
    [InlineData(FocusVerb)]
    public async Task SwitchToWindow_and_focus_pass_an_hwnd_through_with_no_title(string verb)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.BringToFrontAsync(null, 0x1234L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fg(matchStrategy: "hwnd"));

        var root = Parse(await Verb(verb)(NewTools(mock.Object), null, 0x1234L));

        root.GetProperty("MatchStrategy").GetString().Should().Be("hwnd");
        mock.Verify(s => s.BringToFrontAsync(null, 0x1234L, It.IsAny<CancellationToken>()), Times.Once,
            "an hwnd is a complete target: no title has to be invented for it");
    }

    [Theory]
    [InlineData(SwitchVerb, null)]
    [InlineData(SwitchVerb, "")]
    [InlineData(SwitchVerb, "   ")]
    [InlineData(FocusVerb, null)]
    [InlineData(FocusVerb, "")]
    [InlineData(FocusVerb, "   ")]
    public async Task SwitchToWindow_and_focus_reject_a_call_with_neither_title_nor_hwnd(string verb, string? title)
    {
        var mock = new Mock<IWindowService>();

        var act = () => Verb(verb)(NewTools(mock.Object), title, null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("title").And.Contain("hwnd",
                "the model is told both ways to name a window, not just the one it left out");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(SwitchVerb)]
    [InlineData(FocusVerb)]
    public async Task SwitchToWindow_and_focus_report_a_refused_foreground_change_instead_of_throwing(string verb)
    {
        // Roadmap C11: Windows refusing every step is an outcome the agent acts on, not an error.
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.BringToFrontAsync("notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fg(strategy: null, success: false));

        var root = Parse(await Verb(verb)(NewTools(mock.Object), "notepad", null));

        root.GetProperty("Success").GetBoolean().Should().BeFalse();
        root.GetProperty("Strategy").ValueKind.Should().Be(JsonValueKind.Null,
            "no step worked, so no step is named");
    }

    [Theory]
    [InlineData(nameof(WindowTools.SwitchToWindow))]
    [InlineData(nameof(WindowTools.Focus))]
    public void SwitchToWindow_and_focus_describe_the_matching_ladder_and_the_result(string method)
    {
        var description = typeof(WindowTools).GetMethod(method)!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("exact", "the model has to know the title is not matched exactly only")
            .And.Contain("substring")
            .And.Contain("fuzzy")
            .And.Contain("hwnd", "the second way to name a window belongs in the description")
            .And.Contain("Strategy", "the field that says which step worked is only useful if it is advertised")
            .And.NotContain("using SetForegroundWindow",
                "SetForegroundWindow is the first rung of a ladder now, not the whole method");
    }

    // ---- B-10: window(action:…) targets through the same matcher ----------------------------

    [Fact]
    public async Task Window_acts_on_an_hwnd_with_no_title()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("close", null, 0x99L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("close", "Untitled - Notepad", true, "hwnd", 100, 0x99));

        var root = Parse(await NewTools(mock.Object).Window("close", hwnd: 0x99L));

        root.GetProperty("Success").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.ExecuteAsync("close", null, 0x99L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Window_reports_the_window_that_actually_matched_not_the_string_it_was_given()
    {
        // window(action:"close", title:"notepad") closes "Untitled - Notepad" — and says so.
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("close", "notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("close", "Untitled - Notepad", true, "fuzzy", 86, 0x1234));

        var root = Parse(await NewTools(mock.Object).Window("close", "notepad"));

        root.GetProperty("Title").GetString().Should().Be("Untitled - Notepad");
        root.GetProperty("MatchStrategy").GetString().Should().Be("fuzzy");
        root.GetProperty("Score").GetInt32().Should().Be(86);
        root.GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
    }

    [Fact]
    public async Task Window_forwards_a_title_and_an_hwnd_together_and_lets_the_matcher_decide()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync("minimize", "notepad", 0x99L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction("minimize", "Untitled - Notepad", true, "hwnd", 100, 0x99));

        await NewTools(mock.Object).Window("minimize", "notepad", hwnd: 0x99L);

        mock.Verify(s => s.ExecuteAsync("minimize", "notepad", 0x99L, It.IsAny<CancellationToken>()), Times.Once,
            "which of the two wins is the matcher's rule (hwnd), not something the tool decides");
    }

    [Fact]
    public void Window_description_advertises_the_hwnd_target()
    {
        var method = typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!;

        method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should()
            .Contain("hwnd", "targeting by handle is new in B-10 and the model only knows what the description says")
            .And.NotContain("no tool takes it yet",
                "the description told the model the handle was useless; B-10 makes it a target");
        method.GetParameters().Single(p => p.Name == "hwnd")
            .GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull();
    }

    // ---- B-12: multi_monitor carries the display detail --------------------------------------

    [Fact]
    public async Task MultiMonitor_serialises_the_work_area_orientation_dpi_and_scale()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new MonitorInfo(0, "DISPLAY1", 0, 0, 1920, 1080, true,
                    WorkArea: new Bounds(0, 0, 1920, 1032), Orientation: 0, EffectiveDpi: 144, Scale: 1.5),
                new MonitorInfo(1, "DISPLAY2", 1920, 0, 1440, 2560, false,
                    WorkArea: new Bounds(1920, 0, 1440, 2560), Orientation: 90, EffectiveDpi: 96, Scale: 1.0),
            });

        var root = Parse(await NewTools(mock.Object).MultiMonitor());

        var first = root[0];
        var work = first.GetProperty("WorkArea");
        (work.GetProperty("X").GetInt32(), work.GetProperty("Y").GetInt32(),
         work.GetProperty("Width").GetInt32(), work.GetProperty("Height").GetInt32())
            .Should().Be((0, 0, 1920, 1032), "the taskbar is the difference between Bounds and WorkArea");
        first.GetProperty("Orientation").GetInt32().Should().Be(0);
        first.GetProperty("EffectiveDpi").GetInt32().Should().Be(144);
        first.GetProperty("Scale").GetDouble().Should().Be(1.5, "a 150% display is 144 dpi");

        var second = root[1];
        second.GetProperty("Orientation").GetInt32().Should().Be(90, "a rotated display reports its rotation");
        second.GetProperty("EffectiveDpi").GetInt32().Should().Be(96);
        second.GetProperty("Scale").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task Launch_returns_the_pid_the_service_reports()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.LaunchAsync("notepad.exe", It.IsAny<CancellationToken>())).ReturnsAsync(4242);

        var text = await NewTools(mock.Object).Launch("notepad.exe");

        text.Should().Be("launched (pid=4242)");
        mock.Verify(s => s.LaunchAsync("notepad.exe", It.IsAny<CancellationToken>()), Times.Once);
    }
}
