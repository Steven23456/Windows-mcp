using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-5, the real-UIA sibling of <see cref="UIAutomationServiceElementWindowTests"/> and
/// <see cref="UIAutomationSnapshotWindowHandleTests"/>: the two service members
/// <c>observe:window</c> stands on, through real UI Automation, against Win32 windows this process
/// owns (<see cref="OwnedControlsWindow"/>). The fake-element rows prove the walk's rules; these
/// prove the real control-view parent walk reaches the real top-level window and that a real
/// snapshot by that handle lists that window's controls and no other's.
/// <para>
/// Read-only: nothing is clicked, typed or focused (the input mock is strict for everything but
/// the snapshot header's cursor read). The window inventory is mocked so the snapshot's targets are
/// exactly the fixture windows; the walk itself is real. In <see cref="DesktopCollection"/> because
/// fixture windows appear and close, as the other OwnedControlsWindow classes are.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceElementWindowIntegrationTests : IDisposable
{
    private readonly OwnedControlsWindow _first = new();
    private readonly OwnedControlsWindow _second = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly Mock<IWindowService> _windows = new();
    private readonly UIAutomationService _svc;

    public UIAutomationServiceElementWindowIntegrationTests()
    {
        _input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(5, 5));
        _windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        // Both fixture windows under ONE title: only the handle can tell them apart.
        _windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            [
                Window("wmcp-e5 twin", hwnd: (long)_first.Handle, zOrder: 0),
                Window("wmcp-e5 twin", hwnd: (long)_second.Handle, zOrder: 1),
            ]);
        _svc = new UIAutomationService(_input.Object, _windows.Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _first.Dispose();
        _second.Dispose();
    }

    private async Task<ElementInfo> FindAsync(OwnedControlsWindow window, string name)
    {
        window.Exists.Should().BeTrue("precondition: the fixture window was created");
        ElementInfo? hit = null;
        for (int attempt = 0; attempt < 5 && hit is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _svc.FindElementAsync(name, FindKind.Any, FindScope.Window, window.Title, includeOffscreen: true);
            hit = found.Matches.FirstOrDefault(m => m.Name == name);
        }
        hit.Should().NotBeNull($"precondition: UIA sees the fixture control '{name}'");
        return hit!;
    }

    [Fact]
    public async Task A_child_controls_window_is_the_fixture_windows_handle()
    {
        var button = await FindAsync(_first, _first.EnabledButton);
        var box = await FindAsync(_first, _first.DisabledCheckBox);

        (await _svc.GetElementWindowAsync(button.ElementId)).Should().Be((long)_first.Handle,
            "the BUTTON has an HWND of its own; the answer is its top-level window's");
        (await _svc.GetElementWindowAsync(box.ElementId)).Should().Be((long)_first.Handle,
            "a disabled control is read, not acted on, so it is not refused");
    }

    [Fact]
    public async Task A_control_of_the_second_window_answers_the_second_windows_handle()
    {
        var button = await FindAsync(_second, _second.EnabledButton);

        (await _svc.GetElementWindowAsync(button.ElementId)).Should().Be((long)_second.Handle);
    }

    [Fact]
    public async Task A_snapshot_by_that_handle_lists_that_windows_controls_and_not_its_twins()
    {
        // The path observe:window takes: the element's window, then a snapshot of exactly it.
        var button = await FindAsync(_second, _second.EnabledButton);
        var hwnd = await _svc.GetElementWindowAsync(button.ElementId);

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: hwnd));

        var names = snap.Interactive.Select(e => e.Name).ToArray();
        names.Should().Contain(_second.EnabledButton, "the walked window's enabled button is a row");
        names.Should().NotContain(n => n.StartsWith(_first.Token, StringComparison.Ordinal),
            "the other window shares the title, not the handle, and is not walked");
        snap.Interactive.Should().OnlyContain(e => e.Window == "wmcp-e5 twin");
    }

    [Fact]
    public async Task Interact_element_with_observe_window_appends_a_real_snapshot_of_the_elements_window()
    {
        // End to end through the tool and the real service: the one verb that touches nothing
        // (scroll_into_view on a control already on screen succeeds with nothing done), so no
        // input, no focus change. Every Unit row mocks IUIAutomationService; this one does not.
        var button = await FindAsync(_second, _second.EnabledButton);
        var tools = new WindowsMcp.Tools.UIAutomationTools(_svc);

        var text = await tools.InteractElement(button.ElementId, "scroll_into_view", observe: "window", settle_ms: 0);

        var lines = text.Split('\n');
        lines[0].Should().StartWith("{\"ElementId\":\"" + button.ElementId + "\"", "the action's own result comes first");
        lines[1].Should().BeEmpty();
        lines[2].Should().StartWith("Observed").And.Contain("window").And.NotContainEquivalentOf("foreground");
        lines.Skip(3).Should().Contain(l => l.Contains(_second.EnabledButton, StringComparison.Ordinal),
            "the appended snapshot is the walk of the element's own window");
        text.Should().NotContain(_first.Token, "and not of its twin, which shares the title");
    }

    [Fact]
    public async Task A_control_whose_window_closed_is_refused_as_no_longer_available()
    {
        var button = await FindAsync(_first, _first.EnabledButton);
        _first.Close();
        _first.Exists.Should().BeFalse("precondition: the fixture window is gone");

        Func<Task> act = () => _svc.GetElementWindowAsync(button.ElementId);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("no longer available", "the existing refusal for a destroyed element");
    }

    [Fact]
    public async Task A_snapshot_by_the_handle_of_a_window_that_closed_is_a_KeyNotFoundException()
    {
        // With the live inventory this is the window that closed under observe:window (W4). The
        // mocked inventory here still lists it, so this row uses a handle it does not list.
        Func<Task> act = () => _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: 0x00ABCDEF));

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
