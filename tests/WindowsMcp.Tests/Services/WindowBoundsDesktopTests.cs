using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-9's "done when" on a live desktop: <c>window(action:"set_bounds", …)</c> on a real
/// application window, and the A-1 inventory reporting exactly the rect that was asked for.
/// <see cref="WindowServiceBoundsTests"/> proves the same thing against a window this process
/// owns, which is a plain <c>STATIC</c> window with no DWM decoration of its own; this class is
/// the one that fails if a real app's frame makes the numbers come back different.
/// <para>
/// <c>Category=UIAutomation</c> and <see cref="DesktopCollection"/>: it moves a real Notepad
/// window and, in the last test, changes the foreground window. Never run unattended.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class WindowBoundsDesktopTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    public WindowBoundsDesktopTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    private static WindowTools NewTools()
        => new(new WindowService(), new Mock<IVirtualDesktopService>().Object);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<WindowInfo> ListedAsync(long hwnd)
        => (await new WindowService().ListAsync(includeMinimized: true)).Single(w => w.Hwnd == hwnd);

    private static Bounds RectOf(JsonElement rect)
        => new(rect.GetProperty("X").GetInt32(), rect.GetProperty("Y").GetInt32(),
               rect.GetProperty("Width").GetInt32(), rect.GetProperty("Height").GetInt32());

    [Fact]
    public async Task Set_bounds_puts_the_notepad_window_exactly_where_it_was_asked_to()
    {
        // The bar from the roadmap: window(action:"set_bounds", x:100, y:100, width:800,
        // height:600) and the inventory reports that rect. A-1's Bounds is GetWindowRect and
        // SetWindowPos writes the same rectangle, so this is an exact comparison, not a tolerance.
        var root = Parse(await NewTools().Window(
            "set_bounds", hwnd: _np.Hwnd, x: 100, y: 100, width: 800, height: 600));

        RectOf(root.GetProperty("After")).Should().Be(new Bounds(100, 100, 800, 600));
        root.GetProperty("Restored").GetBoolean().Should().BeFalse();
        (await ListedAsync(_np.Hwnd)).Bounds.Should().Be(new Bounds(100, 100, 800, 600),
            "window list is what an agent reads back, so it has to agree with what set_bounds said");
    }

    [Fact]
    public async Task Move_keeps_the_size_and_resize_keeps_the_position()
    {
        var tools = NewTools();
        await tools.Window("set_bounds", hwnd: _np.Hwnd, x: 150, y: 150, width: 700, height: 500);

        var moved = RectOf(Parse(await tools.Window("move", hwnd: _np.Hwnd, x: 260, y: 210)).GetProperty("After"));
        moved.Should().Be(new Bounds(260, 210, 700, 500), "'move' is SWP_NOSIZE");

        var resized = RectOf(Parse(await tools.Window("resize", hwnd: _np.Hwnd, width: 640, height: 480)).GetProperty("After"));
        resized.Should().Be(new Bounds(260, 210, 640, 480), "'resize' is SWP_NOMOVE");
    }

    [Fact]
    public async Task A_maximized_window_is_refused_and_then_accepted_with_restore_first()
    {
        var tools = NewTools();
        await tools.Window("maximize", hwnd: _np.Hwnd);
        for (int i = 0; i < 40 && (await ListedAsync(_np.Hwnd)).State != WindowState.Maximized; i++)
            await Task.Delay(50);

        var refused = () => tools.Window("set_bounds", hwnd: _np.Hwnd, x: 120, y: 120, width: 820, height: 620);
        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("Maximized", "the refusal names the state so the model knows what to do next");

        var root = Parse(await tools.Window(
            "set_bounds", hwnd: _np.Hwnd, x: 120, y: 120, width: 820, height: 620, restore_first: true));

        root.GetProperty("Restored").GetBoolean().Should().BeTrue();
        RectOf(root.GetProperty("After")).Should().Be(new Bounds(120, 120, 820, 620));
        (await ListedAsync(_np.Hwnd)).State.Should().Be(WindowState.Normal,
            "restore_first really un-maximized the window, it did not just report that it had");
    }

    [Fact]
    public async Task A_minimized_window_is_refused_and_then_accepted_with_restore_first()
    {
        var tools = NewTools();
        await tools.Window("minimize", hwnd: _np.Hwnd);
        for (int i = 0; i < 40 && (await ListedAsync(_np.Hwnd)).State != WindowState.Minimized; i++)
            await Task.Delay(50);

        var refused = () => tools.Window("move", hwnd: _np.Hwnd, x: 140, y: 140);
        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("Minimized");

        var root = Parse(await tools.Window(
            "set_bounds", hwnd: _np.Hwnd, x: 140, y: 140, width: 760, height: 560, restore_first: true));

        root.GetProperty("Restored").GetBoolean().Should().BeTrue();
        RectOf(root.GetProperty("After")).Should().Be(new Bounds(140, 140, 760, 560));
        _np.BringToForeground();
    }

    [Fact]
    public async Task Set_bounds_with_no_target_moves_the_foreground_window()
    {
        // The documented default: name nothing and the foreground window is the target. The
        // fixture's window is brought to the front by the constructor, so "the foreground window"
        // is a window this class owns and is allowed to move.
        _np.BringToForeground();
        var active = await new WindowService().GetActiveAsync();
        if (active?.Hwnd != _np.Hwnd)
            return;   // the desktop would not give the fixture the foreground; nothing to assert on safely

        var root = Parse(await NewTools().Window("set_bounds", x: 180, y: 160, width: 720, height: 540));

        root.GetProperty("Window").GetProperty("Hwnd").GetInt64().Should().Be(_np.Hwnd);
        RectOf(root.GetProperty("After")).Should().Be(new Bounds(180, 160, 720, 540));
    }
}
