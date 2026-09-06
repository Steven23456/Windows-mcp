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
/// B-9 at the tool layer: the three new actions on <c>window</c>, which arguments each of them
/// needs, what is forwarded to the service, and what the model gets back. The geometry itself is
/// <c>WindowGeometryTests</c>'s and the real move is <c>WindowServiceBoundsTests</c>'s.
/// </summary>
[Trait("Category", "Unit")]
public class WindowToolsBoundsTests
{
    private static WindowTools NewTools(IWindowService window)
        => new(window, new Mock<IVirtualDesktopService>().Object);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static WindowInfo Info()
        => new("Untitled - Notepad", 0x1234, 9001, "notepad", WindowState.Normal,
               new Bounds(10, 20, 300, 200), 0, true, false, 1);

    private static WindowBoundsResult BoundsResult(
        Bounds? before = null, Bounds? after = null, string matchStrategy = "substring",
        int score = 100, bool restored = false)
        => new(Info(), before ?? new Bounds(10, 20, 300, 200), after ?? new Bounds(100, 100, 800, 600),
               matchStrategy, score, restored);

    private static Mock<IWindowService> Service(WindowBoundsResult? result = null)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.SetBoundsAsync(
                It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? BoundsResult());
        return mock;
    }

    // ---- what each action forwards -------------------------------------------------------------

    [Fact]
    public async Task Window_set_bounds_forwards_all_four_numbers()
    {
        var mock = Service();

        await NewTools(mock.Object).Window("set_bounds", "notepad", x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.SetBoundsAsync("notepad", null, 100, 100, 800, 600, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Window_move_forwards_only_the_position_even_when_a_size_was_given()
    {
        // The description says move keeps the size. Passing width/height through anyway would
        // make "move" silently resize, which is exactly the surprise the action names avoid.
        var mock = Service();

        await NewTools(mock.Object).Window("move", "notepad", x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.SetBoundsAsync("notepad", null, 100, 100, null, null, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Window_resize_forwards_only_the_size_even_when_a_position_was_given()
    {
        var mock = Service();

        await NewTools(mock.Object).Window("resize", "notepad", x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.SetBoundsAsync("notepad", null, null, null, 800, 600, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("set_bounds")]
    public async Task Window_forwards_restore_first(string action)
    {
        var mock = Service();

        await NewTools(mock.Object).Window(action, "notepad", x: 1, y: 2, width: 3, height: 4, restore_first: true);

        mock.Verify(s => s.SetBoundsAsync("notepad", null, It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Window_set_bounds_accepts_an_hwnd_instead_of_a_title()
    {
        var mock = Service(BoundsResult(matchStrategy: "hwnd"));

        var root = Parse(await NewTools(mock.Object).Window("set_bounds", hwnd: 0x99L, x: 1, y: 2, width: 3, height: 4));

        root.GetProperty("MatchStrategy").GetString().Should().Be("hwnd");
        mock.Verify(s => s.SetBoundsAsync(null, 0x99L, 1, 2, 3, 4, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("set_bounds")]
    public async Task Window_lets_the_service_default_to_the_foreground_window(string action)
    {
        // Unlike minimize/close, the geometry actions have a sane default target (upstream's
        // "name? or the active window"), so naming nothing is a valid call - the service resolves it.
        var mock = Service();

        await NewTools(mock.Object).Window(action, x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.SetBoundsAsync(null, null, It.IsAny<int?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("MOVE")]
    [InlineData("Set_Bounds")]
    [InlineData("RESIZE")]
    public async Task Window_matches_the_new_actions_case_insensitively(string action)
    {
        var mock = Service();

        await NewTools(mock.Object).Window(action, "notepad", x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.SetBoundsAsync(It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---- the response --------------------------------------------------------------------------

    [Fact]
    public async Task Window_set_bounds_serialises_the_window_both_rects_and_the_verdict()
    {
        var mock = Service(BoundsResult(
            before: new Bounds(10, 20, 300, 200), after: new Bounds(100, 100, 800, 600),
            matchStrategy: "fuzzy", score: 86, restored: true));

        var root = Parse(await NewTools(mock.Object).Window("set_bounds", "notepad", x: 100, y: 100, width: 800, height: 600));

        root.GetProperty("Window").GetProperty("Title").GetString().Should().Be("Untitled - Notepad",
            "the window that moved, not the string the caller sent");
        root.GetProperty("Window").GetProperty("Hwnd").GetInt64().Should().Be(0x1234);
        var before = root.GetProperty("Before");
        (before.GetProperty("X").GetInt32(), before.GetProperty("Y").GetInt32(),
         before.GetProperty("Width").GetInt32(), before.GetProperty("Height").GetInt32())
            .Should().Be((10, 20, 300, 200));
        var after = root.GetProperty("After");
        (after.GetProperty("X").GetInt32(), after.GetProperty("Y").GetInt32(),
         after.GetProperty("Width").GetInt32(), after.GetProperty("Height").GetInt32())
            .Should().Be((100, 100, 800, 600), "After is re-read from the window - the outcome, not the request");
        root.GetProperty("MatchStrategy").GetString().Should().Be("fuzzy");
        root.GetProperty("Score").GetInt32().Should().Be(86);
        root.GetProperty("Restored").GetBoolean().Should().BeTrue();
    }

    // ---- the per-action argument rules ----------------------------------------------------------

    [Theory]
    [InlineData(null, 100)]
    [InlineData(100, null)]
    [InlineData(null, null)]
    public async Task Window_move_needs_both_x_and_y(int? x, int? y)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Window("move", "notepad", x: x, y: y);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("move").And.Contain("x").And.Contain("y");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(800, null)]
    [InlineData(null, null)]
    public async Task Window_resize_needs_both_width_and_height(int? width, int? height)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Window("resize", "notepad", width: width, height: height);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("resize").And.Contain("width").And.Contain("height");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, 100, 800, 600)]
    [InlineData(100, null, 800, 600)]
    [InlineData(100, 100, null, 600)]
    [InlineData(100, 100, 800, null)]
    public async Task Window_set_bounds_needs_all_four(int? x, int? y, int? width, int? height)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Window("set_bounds", "notepad", x: x, y: y, width: width, height: height);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("set_bounds").And.Contain("x").And.Contain("y")
            .And.Contain("width").And.Contain("height");
        mock.VerifyNoOtherCalls();
    }

    // ---- the actions that must not notice the new parameters -------------------------------------

    [Theory]
    [InlineData("list")]
    [InlineData("active")]
    [InlineData("desktops")]
    public async Task Window_reading_actions_ignore_the_geometry_parameters(string action)
    {
        var window = new Mock<IWindowService>();
        window.Setup(s => s.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Info()]);
        window.Setup(s => s.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Info());
        var desktops = new Mock<IVirtualDesktopService>();
        desktops.Setup(d => d.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await new WindowTools(window.Object, desktops.Object)
            .Window(action, x: 100, y: 100, width: 800, height: 600, restore_first: true);

        window.Verify(s => s.SetBoundsAsync(It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never, "a stray x on a read must never turn it into a move");
    }

    [Theory]
    [InlineData("minimize")]
    [InlineData("close")]
    public async Task Window_state_actions_ignore_the_geometry_parameters(string action)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(s => s.ExecuteAsync(action, "notepad", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowAction(action, "Untitled - Notepad", true, "substring", 100, 0x1234));

        await NewTools(mock.Object).Window(action, "notepad", x: 100, y: 100, width: 800, height: 600);

        mock.Verify(s => s.ExecuteAsync(action, "notepad", null, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.SetBoundsAsync(It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<int?>(),
            It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- the menu the model reads ---------------------------------------------------------------

    [Theory]
    [InlineData("bogus")]
    [InlineData("setbounds")]
    [InlineData("set-bounds")]
    public async Task Window_unknown_action_now_lists_all_ten_actions(string action)
    {
        var mock = new Mock<IWindowService>();

        var act = () => NewTools(mock.Object).Window(action);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should()
            .Contain(action)
            .And.Contain("list|active|desktops|minimize|maximize|restore|close|move|resize|set_bounds",
                "B-9 adds three actions and the model only ever sees the menu in this message");
    }

    [Fact]
    public void Window_description_advertises_the_three_geometry_actions()
    {
        var method = typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("move").And.Contain("resize").And.Contain("set_bounds")
            .And.ContainEquivalentOf("restore_first",
                "the way past a minimised or maximised refusal has to be discoverable");
        method.GetParameters().Single(p => p.Name == "action")
            .GetCustomAttribute<DescriptionAttribute>()!.Description.Should()
            .Contain("move").And.Contain("resize").And.Contain("set_bounds");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("restore_first")]
    public void Window_describes_each_new_parameter(string parameter)
    {
        var info = typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!
            .GetParameters().Single(p => p.Name == parameter);

        info.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Window_keeps_the_A1_parameters_in_front_of_the_new_ones()
    {
        // Positional callers exist (window("list") and window("minimize", "Notepad") are both in
        // the tests and the docs); the new parameters are appended, never inserted.
        typeof(WindowTools).GetMethod(nameof(WindowTools.Window))!.GetParameters()
            .Select(p => p.Name).Should().StartWith(
                ["action", "title", "include_minimized", "include_hidden", "hwnd"]);
    }
}
