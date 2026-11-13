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
/// E-5 round 2 (R1-R3), through the REAL <see cref="WindowService"/> and the REAL UIA service - the
/// review found the round-1 Integration class mocked <c>IWindowService.ListAsync</c>, which is
/// exactly where these bugs lived: the filtered inventory drops untitled windows, tool windows and
/// open popups, and a miss there was read as "the window closed". The windows are this process's
/// own (<see cref="OwnedWindowFamily"/>), shown without activation; nothing is clicked, typed or
/// focused (the input mock is strict except the snapshot header's cursor read), and the one tool
/// verb used, <c>scroll_into_view</c> on a control already on screen, does nothing.
/// <para>
/// The popup is an unowned, untitled, non-activating WS_POPUP tool window of this process holding
/// two buttons. A real menu (#32768 through TrackPopupMenu) is not built: it enters a modal menu
/// loop that takes the mouse capture. A real WinForms ContextMenuStrip is in
/// <c>ObserveRealContextMenuIntegrationTests</c>. Found while building this fixture: UIA lists an
/// OWNED top-level window as a child of its owner, so an owned popup is already inside its owner's
/// walk - and an owned dialog's controls appear in a snapshot of its owner, under the owner's title.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceObserveWindowsIntegrationTests : IDisposable
{
    private readonly OwnedWindowFamily _family = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly WindowService _windows = new();
    private readonly UIAutomationService _svc;

    public UIAutomationServiceObserveWindowsIntegrationTests()
    {
        _input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(5, 5));
        _svc = new UIAutomationService(_input.Object, _windows);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _family.Dispose();
    }

    private async Task<ElementInfo> FindAsync(string name)
    {
        ElementInfo? hit = null;
        for (int attempt = 0; attempt < 5 && hit is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _svc.FindElementAsync(name, FindKind.Any, FindScope.Desktop, null, includeOffscreen: true);
            hit = found.Matches.FirstOrDefault(m => m.Name == name);
        }
        hit.Should().NotBeNull($"precondition: UIA sees the fixture control '{name}'");
        return hit!;
    }

    private async Task NotInTheInventory(IntPtr hwnd, string what)
    {
        var inventory = await _windows.ListAsync();
        inventory.Should().NotContain(w => w.Hwnd == (long)hwnd,
            $"precondition: the filtered inventory drops the {what}, which is what this row is about");
    }

    // ---- R1: the window an element belongs to ----------------------------------------------------

    [Fact]
    public void RootWindowOf_is_GA_ROOT_which_stops_at_an_owned_window()
    {
        _svc.RootWindowOf(_family.OwnedButtonHandle).Should().Be(_family.Owned, "the owned dialog, not its owner");
        _svc.RootWindowOf(_family.Owned).Should().Be(_family.Owned);
        _svc.RootWindowOf(_family.HostButtonHandle).Should().Be(_family.Host);
    }

    [Fact]
    public async Task An_element_in_an_owned_window_answers_the_owned_window_not_its_owner()
    {
        var button = await FindAsync(_family.OwnedButton);

        (await _svc.GetElementWindowAsync(button.ElementId)).Should().Be((long)_family.Owned);
    }

    [Fact]
    public async Task A_snapshot_by_the_owned_windows_handle_lists_its_controls_under_its_title()
    {
        var button = await FindAsync(_family.OwnedButton);
        var hwnd = await _svc.GetElementWindowAsync(button.ElementId);

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: hwnd));

        snap.Interactive.Should().Contain(e => e.Name == _family.OwnedButton && e.Window == _family.OwnedTitle);
        snap.Interactive.Should().NotContain(e => e.Name == _family.HostButton, "the owner is another window");
    }

    // ---- R2: windows the inventory does not list ---------------------------------------------------

    [Fact]
    public async Task An_untitled_windows_control_is_observed_by_handle_not_reported_gone()
    {
        await NotInTheInventory(_family.Untitled, "untitled window");
        var button = await FindAsync(_family.UntitledButton);

        var hwnd = await _svc.GetElementWindowAsync(button.ElementId);
        hwnd.Should().Be((long)_family.Untitled);
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: hwnd));

        snap.Interactive.Should().Contain(e => e.Name == _family.UntitledButton);
    }

    [Fact]
    public async Task A_tool_windows_control_is_observed_under_the_tool_windows_title()
    {
        await NotInTheInventory(_family.Tool, "tool window");
        var button = await FindAsync(_family.ToolButton);

        var hwnd = await _svc.GetElementWindowAsync(button.ElementId);
        hwnd.Should().Be((long)_family.Tool);
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: hwnd));

        snap.Interactive.Should().Contain(e => e.Name == _family.ToolButton && e.Window == _family.ToolTitle,
            "a window the inventory does not list is labelled with its UIA name");
    }

    [Fact]
    public async Task A_window_that_really_closed_is_a_KeyNotFoundException_naming_its_handle()
    {
        var hwnd = (long)_family.Untitled;
        _family.Close(_family.Untitled);
        OwnedWindowFamily.Exists(_family.Untitled).Should().BeFalse("precondition: it is gone");

        Func<Task> act = () => _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: hwnd));

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message
            .Should().MatchRegex($"(?i){hwnd}|0x0*{hwnd:X}");
    }

    // ---- R3: popups ----------------------------------------------------------------------------

    [Fact]
    public async Task With_popups_the_host_windows_snapshot_lists_its_open_popups_items_after_its_own()
    {
        await NotInTheInventory(_family.Popup, "popup");

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Host, IncludePopups: true));

        var names = snap.Interactive.Select(e => e.Name).ToList();
        names.Should().Contain([_family.HostButton, _family.PopupItemA, _family.PopupItemB]);
        names.IndexOf(_family.PopupItemA).Should().BeGreaterThan(names.IndexOf(_family.HostButton), "the window first, then its popups");
        snap.Interactive.Where(e => e.Name == _family.PopupItemA).Should().OnlyContain(e => e.Window == "popup",
            "an untitled popup is labelled 'popup'");
    }

    [Fact]
    public async Task Without_popups_the_host_windows_snapshot_is_the_window_alone()
    {
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Host));

        snap.Interactive.Select(e => e.Name).Should().Contain(_family.HostButton).And.NotContain(_family.PopupItemA);
    }

    // ---- PR #43 P2: owned windows are their own windows ------------------------------------------

    private static IEnumerable<SnapshotElement> Named(SnapshotResult snap, string name) => snap.Interactive.Where(e => e.Name == name);

    [Fact]
    public async Task With_popups_an_owned_window_reaching_outside_its_owner_is_observed_whole_under_its_own_title_once()
    {
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Owner2, IncludePopups: true));

        Named(snap, _family.Owned2OutsideButton).Should().ContainSingle(
            "the owned window is walked as its own root, clipped to ITS rectangle, so the part outside its owner is there")
            .Which.Window.Should().Be(_family.Owned2Title);
        Named(snap, _family.Owned2InsideButton).Should().ContainSingle("no row twice: not under the owner as well")
            .Which.Window.Should().Be(_family.Owned2Title);
        Named(snap, _family.Owner2Button).Should().ContainSingle().Which.Window.Should().Be(_family.Owner2Title);
    }

    [Fact]
    public async Task Without_popups_the_owned_window_stays_clipped_inside_its_owners_walk_as_today()
    {
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Owner2));

        Named(snap, _family.Owned2OutsideButton).Should().BeEmpty("clipped to the owner's rectangle, as the plain snapshot always was");
        Named(snap, _family.Owned2InsideButton).Should().ContainSingle().Which.Window.Should().Be(_family.Owner2Title,
            "UI Automation nests the owned window under its owner");
    }

    [Fact]
    public async Task With_popups_an_owned_dialog_beside_its_owner_is_observed_under_its_own_title()
    {
        // The family's first owned dialog lies wholly outside its owner (see the unit row
        // An_owned_window_entirely_outside_its_owner_is_still_found_and_walked).
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Host, IncludePopups: true));

        Named(snap, _family.OwnedButton).Should().ContainSingle().Which.Window.Should().Be(_family.OwnedTitle);
    }

    // ---- PR #43 T2: the real owner relation ------------------------------------------------------

    [Fact]
    public void The_default_owner_relation_returns_the_visible_owned_windows_transitively_nearest_first_and_nothing_else()
    {
        var owned = _svc.OwnedWindowsOf(_family.Owner2).ToList();

        owned.Should().Contain([_family.Owned2, _family.Grand2], "Owned2 is owned by Owner2, Grand2 by Owned2");
        owned.IndexOf(_family.Owned2).Should().BeLessThan(owned.IndexOf(_family.Grand2), "nearest first");
        owned.Should().NotContain(_family.HiddenOwned2, "a hidden owned window is not returned");
        owned.Should().NotContain([_family.Owner2, _family.Host, _family.Owned, _family.Untitled, _family.Tool, _family.Popup],
            "nothing that Owner2 does not own");
    }

    [Fact]
    public void The_default_owner_relation_for_the_host_returns_its_owned_dialog_and_not_the_unowned_popup()
    {
        var owned = _svc.OwnedWindowsOf(_family.Host);

        owned.Should().Contain(_family.Owned);
        owned.Should().NotContain([_family.Popup, _family.Owned2, _family.Grand2]);
    }

    [Fact]
    public async Task With_popups_a_window_owned_by_an_owned_window_is_observed_once_under_its_own_title()
    {
        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_family.Owner2, IncludePopups: true));

        Named(snap, _family.Grand2Button).Should().ContainSingle().Which.Window.Should().Be(_family.Grand2Title);
        Named(snap, _family.Owned2OutsideButton).Should().ContainSingle().Which.Window.Should().Be(_family.Owned2Title);
    }

    // ---- end to end through the tool -------------------------------------------------------------

    [Fact]
    public async Task Interact_element_with_observe_window_on_an_untitled_window_observes_it()
    {
        var button = await FindAsync(_family.UntitledButton);

        var text = await new UIAutomationTools(_svc).InteractElement(button.ElementId, "scroll_into_view", observe: "window", settle_ms: 0);

        var lines = text.Split('\n');
        lines[2].Should().StartWith("Observed").And.NotContainEquivalentOf("gone", "the window did not close: the inventory just does not list it");
        text.Should().Contain(_family.UntitledButton);
    }

    [Fact]
    public async Task Interact_element_with_observe_window_on_the_host_shows_its_open_popup()
    {
        // The checklist's "done when", without a click: the observation of the host includes the
        // popup that is open over it.
        var button = await FindAsync(_family.HostButton);

        var text = await new UIAutomationTools(_svc).InteractElement(button.ElementId, "scroll_into_view", observe: "window", settle_ms: 0);

        text.Should().Contain(_family.PopupItemA).And.Contain(_family.PopupItemB);
    }
}
