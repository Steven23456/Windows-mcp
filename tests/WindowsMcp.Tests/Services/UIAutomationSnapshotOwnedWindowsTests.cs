using System.Reflection;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// PR #43 review P2: UI Automation lists an OWNED top-level window (an owned dialog, an owned popup)
/// as a CHILD of its owner, so a walk of the owner swallows it - clipped to the owner's rectangle and
/// labelled with the owner's title - and the desktop-children popup pass never sees it. With
/// <c>IncludePopups</c> (Foreground or Window scope only), a descendant whose control type is Window,
/// with a native handle that is a top-level window in its own right (<c>RootWindowOf(h) == h</c>),
/// is cut out of the owner's rows (the owner's other rows keep their order and parent links) and
/// walked as its own root, clipped to its own rectangle, labelled with its inventory title, else its
/// UIA name, else <c>popup</c>, after its owner and before the desktop-children popups, never twice.
/// A Window-typed descendant that is not its own root (an MDI child, a XAML in-window "Window") stays.
/// Without <c>IncludePopups</c> nothing changes.
/// <para>
/// Fakes from <see cref="FakeUiWindows"/>, now with children (the traverser reads
/// <c>CachedChildren</c>); <see cref="Harness_a_fake_window_with_children_walks_to_its_subtree"/> proves it.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotOwnedWindowsTests : IDisposable
{
    private const int WindowType = 50032;
    private const long Host = 0xAB10;
    private const long Owned = 0xAC01;
    private const long Mdi = 0xAC02;
    private const long Far = 0xAC03;

    private static readonly MonitorInfo[] OneMonitor = [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    private readonly FakeUiWindows _fakes = new();
    private readonly Dictionary<nint, nint> _roots = new() { [(nint)Mdi] = (nint)Host };

    public void Dispose() => _fakes.Dispose();

    private AutomationElement Button(string name, (int X, int Y, int W, int H) bounds) => _fakes.Window(name, 0, bounds: bounds);

    /// <summary>The owned dialog: partly outside the host, one button inside the host, one outside it.</summary>
    private AutomationElement OwnedDialog(string name = "Owned dialog") => _fakes.Window(name, Owned, controlType: WindowType,
        bounds: (450, 150, 300, 300),
        children: [Button("Owned inside button", (460, 160, 30, 20)), Button("Owned outside button", (600, 420, 100, 20))]);

    /// <summary>
    /// The host window (100,100 400x300) and its children in tree order: a button, the owned dialog,
    /// an MDI child (a Window with a handle whose root is the host), a XAML in-window "Window" with
    /// no handle, another button.
    /// </summary>
    private AutomationElement HostWindow(params AutomationElement[] extra) => _fakes.Window("Host", Host, controlType: WindowType,
        bounds: (100, 100, 400, 300),
        children:
        [
            Button("Host button", (110, 110, 100, 20)),
            OwnedDialog(),
            _fakes.Window("MDI child", Mdi, controlType: WindowType, bounds: (120, 190, 150, 50),
                children: [Button("MDI button", (125, 200, 50, 20))]),
            _fakes.Window("XAML window", 0, controlType: WindowType, bounds: (120, 245, 150, 50),
                children: [Button("XAML button", (130, 250, 50, 20))]),
            Button("Host button 2", (300, 350, 50, 20)),
            .. extra,
        ]);

    private UIAutomationService NewService(AutomationElement host, WindowInfo[]? inventory = null, params AutomationElement[] desktop)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inventory ?? [Window("Host", hwnd: Host, pid: 4242, isActive: true)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OneMonitor);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object);
        svc.ElementFromHandle = h => h == Host ? host : null;
        svc.RootWindowOf = h => _roots.TryGetValue(h, out var r) ? r : h;
        svc.DesktopWindows = () => desktop;
        return svc;
    }

    private static string[] Rows(SnapshotResult snap) => snap.Interactive.Select(e => $"{e.Window}: {e.Name}").ToArray();

    [Fact]
    public void Harness_a_fake_window_with_children_walks_to_its_subtree()
    {
        var entries = new UiTraverser(_fakes.Automation).Walk(HostWindow(), "Host", new ElementBudget(100));

        entries.Select(e => e.Node.Name).Should().Equal(
            ["Host", "Host button", "Owned dialog", "Owned inside button", "MDI child", "MDI button", "XAML window", "XAML button", "Host button 2"],
            "pre-order, and the owned dialog's outside button is clipped away by the host's rectangle");
    }

    // ---- d: without IncludePopups nothing changes (guard) ----------------------------------------

    [Theory]
    [InlineData(SnapshotScope.Window)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task Without_popups_the_owned_window_stays_inside_its_owners_walk_as_today(SnapshotScope scope)
    {
        using var svc = NewService(HostWindow());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0));

        Rows(snap).Should().Equal(
            ["Host: Host button", "Host: Owned inside button", "Host: MDI button", "Host: XAML button", "Host: Host button 2"],
            "the plain snapshot is unchanged: nested, clipped to the owner, under the owner's title");
    }

    // ---- a, b, c, e ------------------------------------------------------------------------------

    [Theory]
    [InlineData(SnapshotScope.Window)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task With_popups_an_owned_window_is_cut_out_of_its_owner_and_walked_whole_under_its_own_label(SnapshotScope scope)
    {
        using var svc = NewService(HostWindow(), null, _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20)));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0, IncludePopups: true));

        Rows(snap).Should().Equal(
        [
            "Host: Host button", "Host: MDI button", "Host: XAML button", "Host: Host button 2",
            "Owned dialog: Owned inside button", "Owned dialog: Owned outside button",
            "Menu item A: Menu item A",
        ], "a: the owned subtree leaves the owner, whose rows keep their order; b: it is walked as its own root, clipped to "
           + "its own rectangle (the outside button is back), after the owner and before the desktop popups; c: each row once; "
           + "e: the MDI child and the XAML window stay inside the owner");
    }

    [Fact]
    public async Task The_owners_tree_keeps_its_parent_links_and_the_owned_window_gets_its_own()
    {
        using var svc = NewService(HostWindow());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, IncludeTree: true, WindowHwnd: Host, IncludePopups: true));

        var trees = snap.Tree!.Children;
        trees.Select(t => t.Root.Name).Should().Equal(["Host", "Owned dialog"]);
        trees[0].Children.Select(c => c.Root.Name).Should().Equal(["Host button", "MDI child", "XAML window", "Host button 2"]);
        trees[0].Children[1].Children.Select(c => c.Root.Name).Should().Equal(["MDI button"]);
        trees[0].Children[2].Children.Select(c => c.Root.Name).Should().Equal(["XAML button"]);
        trees[1].Children.Select(c => c.Root.Name).Should().Equal(["Owned inside button", "Owned outside button"]);
    }

    [Fact]
    public async Task An_owned_window_the_inventory_lists_is_labelled_with_its_inventory_title()
    {
        using var svc = NewService(HostWindow(),
            [Window("Host", hwnd: Host, pid: 4242, isActive: true), Window("Find and Replace", hwnd: Owned, pid: 4242, zOrder: 1)]);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Contain(["Find and Replace: Owned inside button", "Find and Replace: Owned outside button"]);
    }

    [Fact]
    public async Task An_unnamed_owned_window_that_the_inventory_does_not_list_is_labelled_popup()
    {
        var host = _fakes.Window("Host", Host, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [Button("Host button", (110, 110, 100, 20)), OwnedDialog(name: "")]);
        using var svc = NewService(host);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Equal(["Host: Host button", "popup: Owned inside button", "popup: Owned outside button"]);
    }

    [Fact]
    public async Task An_owned_window_the_desktop_pass_also_reports_is_not_walked_twice()
    {
        // b/c: whatever lists it, an owned window is one window and its rows appear once.
        using var svc = NewService(HostWindow(), null,
            _fakes.Window("Owned dialog reported again", Owned, bounds: (450, 150, 300, 300)));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().NotContain(r => r.Contains("reported again", StringComparison.Ordinal));
        Rows(snap).Count(r => r.EndsWith("Owned inside button", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task An_owned_window_entirely_outside_its_owner_is_still_found_and_walked()
    {
        // My reading of rule a ("a descendant ... is an owned window"): detection cannot depend on
        // the owner's clipping, or an owned dialog placed BESIDE its owner - the common case, and
        // OwnedWindowFamily's - would never be observed at all.
        var far = _fakes.Window("Far dialog", Far, controlType: WindowType, bounds: (900, 100, 200, 100),
            children: [Button("Far button", (910, 110, 50, 20))]);
        using var svc = NewService(HostWindow(far));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Contain("Far dialog: Far button");
    }

    [Fact]
    public async Task Desktop_scope_is_unchanged_even_with_popups()
    {
        using var svc = NewService(HostWindow());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludePopups: true));

        Rows(snap).Should().Equal(
            ["Host: Host button", "Host: Owned inside button", "Host: MDI button", "Host: XAML button", "Host: Host button 2"]);
    }
}

/// <summary>
/// PR #43 review P1 end to end in-process: <c>Observation</c> over the REAL
/// <see cref="UIAutomationService"/>, the window resolved through fakes. A by-handle snapshot that
/// TIMES OUT must reach the observation as the timeout - the note, with the timeout's message - and
/// never as "the window is gone", which would take a foreground snapshot instead.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObserveWindowTimeoutTests : IDisposable
{
    private const long Handle = 0xAB01;

    private readonly FakeUiWindows _fakes = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly UIAutomationService _svc;

    public ObserveWindowTimeoutTests()
    {
        _windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Window("Some app", hwnd: 0xAB00, isActive: true)]);
        _windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        _svc = new UIAutomationService(input.Object, _windows.Object);
        _svc.RootWindowOf = h => h;
    }

    public void Dispose()
    {
        _svc.Dispose();
        _fakes.Dispose();
    }

    private string Issue(AutomationElement element) =>
        ((ElementCache<AutomationElement>)typeof(UIAutomationService)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_svc)!).Add(element);

    [Fact]
    public async Task Observe_window_whose_snapshot_times_out_returns_the_note_with_the_timeout_and_takes_no_fallback()
    {
        var id = Issue(_fakes.Window("OK", Handle));
        _svc.ElementFromHandle = _ => throw new TimeoutException("UIA timed out resolving the window (wmcp-e5)");

        var text = await new Observation(_svc).AroundAsync("window", 0, id, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().StartWith("acted\n\nObservation failed: ");
        text.Should().Contain("UIA timed out resolving the window (wmcp-e5)", "TimeoutException is caller-facing, so its message is in the note");
        text.Should().NotContainEquivalentOf("gone");
        _windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once,
            "one snapshot: no foreground fallback after a timeout");
    }

    [Fact]
    public async Task Observe_window_whose_window_really_closed_still_falls_back_to_the_foreground()
    {
        // Guard: KeyNotFound (null / gone) keeps the fallback.
        var id = Issue(_fakes.Window("OK", Handle));
        _svc.ElementFromHandle = _ => null;

        var text = await new Observation(_svc).AroundAsync("window", 0, id, CancellationToken.None, () => Task.FromResult("acted"));

        text.Split('\n')[2].Should().ContainEquivalentOf("gone").And.Contain("foreground");
        _windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
