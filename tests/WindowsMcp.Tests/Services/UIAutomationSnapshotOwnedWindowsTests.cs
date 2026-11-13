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
/// PR #43 review P2, re-pinned for round 3 (T2). UI Automation lists an OWNED top-level window (an
/// owned dialog, an owned popup) as a CHILD of its owner, so a plain walk of the owner swallows it -
/// clipped to the owner's rectangle, under the owner's title. With <c>IncludePopups</c> (Foreground or
/// Window scope only) each owned window is lifted out: the owner's walk skips it, and it is walked
/// as its own root, clipped to its own rectangle, labelled with its inventory title, else its UIA
/// name, else <c>popup</c>, after its owner and before the desktop-children popups, never twice.
/// <para>
/// T2: owned windows come from Win32's owner relation through <c>OwnedWindowsOf</c> (here a fake),
/// each handle resolved through <c>ElementFromHandle</c> - the owner's subtree is never SEARCHED for
/// them, and <c>RootWindowOf</c> is not consulted. An MDI child or an in-window XAML "Window" is never
/// returned by the seam, so it stays inside the host. Without <c>IncludePopups</c>, and in Desktop
/// scope, nothing changes and the seam is never called.
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
    private const long Grand = 0xAC04;

    private static readonly MonitorInfo[] OneMonitor = [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    private readonly FakeUiWindows _fakes = new();

    /// <summary>What the fake owner relation answers, by owner handle; unlisted owners own nothing.</summary>
    private readonly Dictionary<nint, nint[]> _owned = new() { [(nint)Host] = [(nint)Owned] };

    /// <summary>Every handle the service resolved with ElementFromHandle besides the target.</summary>
    private readonly Dictionary<nint, AutomationElement> _byHandle = new();

    private readonly List<nint> _ownerQueries = [];
    private int _rootQueries;

    public void Dispose() => _fakes.Dispose();

    private AutomationElement Button(string name, (int X, int Y, int W, int H) bounds) => _fakes.Window(name, 0, bounds: bounds);

    /// <summary>The owned dialog: partly outside the host, one button inside the host, one outside it.</summary>
    private AutomationElement OwnedDialog(string name = "Owned dialog", params AutomationElement[] more)
    {
        var dialog = _fakes.Window(name, Owned, controlType: WindowType, bounds: (450, 150, 300, 300),
            children: [Button("Owned inside button", (460, 160, 30, 20)), Button("Owned outside button", (600, 420, 100, 20)), .. more]);
        _byHandle.Add((nint)Owned, dialog);   // throws if a test builds two fakes with one handle (the seam returns one)
        return dialog;
    }

    /// <summary>
    /// The host window (100,100 400x300) and its children in tree order: a button, the owned dialog,
    /// an MDI child (a Window with a handle the owner relation does not return), a XAML in-window
    /// "Window" with no handle, another button.
    /// </summary>
    private AutomationElement HostWindow(AutomationElement? owned = null, params AutomationElement[] extra) =>
        _fakes.Window("Host", Host, controlType: WindowType, bounds: (100, 100, 400, 300),
        children:
        [
            Button("Host button", (110, 110, 100, 20)),
            owned ?? OwnedDialog(),
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
        svc.ElementFromHandle = h => h == Host ? host : _byHandle.GetValueOrDefault(h);
        svc.OwnedWindowsOf = h => { _ownerQueries.Add(h); return _owned.GetValueOrDefault(h) ?? []; };
        // d: not consulted for discovery. Answering "its own root" for every handle is what made
        // round 2's search lift the MDI child too, so a search that still asks it shows.
        svc.RootWindowOf = h => { _rootQueries++; return h; };
        svc.DesktopWindows = () => desktop;
        return svc;
    }

    private static string[] Rows(SnapshotResult snap) => snap.Interactive.Select(e => $"{e.Window}: {e.Name}").ToArray();

    private static readonly string[] Nested =
        ["Host: Host button", "Host: Owned inside button", "Host: MDI button", "Host: XAML button", "Host: Host button 2"];

    [Fact]
    public void Harness_a_fake_window_with_children_walks_to_its_subtree()
    {
        var entries = new UiTraverser(_fakes.Automation).Walk(HostWindow(), "Host", new ElementBudget(100));

        entries.Select(e => e.Node.Name).Should().Equal(
            ["Host", "Host button", "Owned dialog", "Owned inside button", "MDI child", "MDI button", "XAML window", "XAML button", "Host button 2"],
            "pre-order, and the owned dialog's outside button is clipped away by the host's rectangle");
    }

    // ---- d (P2) / b (T2): without IncludePopups, and in Desktop scope, nothing changes ----------------

    [Theory]
    [InlineData(SnapshotScope.Window)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task Without_popups_the_owned_window_stays_inside_its_owners_walk_as_today(SnapshotScope scope)
    {
        using var svc = NewService(HostWindow());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0));

        Rows(snap).Should().Equal(Nested, "the plain snapshot is unchanged: nested, clipped to the owner, under the owner's title");
        _ownerQueries.Should().BeEmpty("the owner relation is asked only for an observation");
    }

    [Fact]
    public async Task Desktop_scope_is_unchanged_even_with_popups()
    {
        using var svc = NewService(HostWindow());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludePopups: true));

        Rows(snap).Should().Equal(Nested);
        _ownerQueries.Should().BeEmpty();
    }

    // ---- T2 a: discovered natively, never by searching the owner ------------------------------------

    [Theory]
    [InlineData(SnapshotScope.Window)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task With_popups_the_owner_relation_is_asked_once_and_the_owner_is_never_searched(SnapshotScope scope)
    {
        using var svc = NewService(HostWindow());

        await svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0, IncludePopups: true));

        _ownerQueries.Should().ContainSingle(h => h == (nint)Host, "once, with the walked window's handle");
        _ownerQueries.Should().NotContain((nint)0, "a window with no handle asks nothing");
        _fakes.DescendantSearches.Should().BeEmpty("no FindAllDescendants or other subtree search for owned windows");
        _fakes.ChildReads.GetValueOrDefault("Host").Should().Be(1, "the host's children are read once - by its walk, not by a manual descent too");
        _rootQueries.Should().Be(0, "d: RootWindowOf is not consulted for discovery");
    }

    // ---- P2 rules, driven by the seam (T2 b) ------------------------------------------------------

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
        ], "the owned subtree leaves the owner, whose rows keep their order; it is walked as its own root, clipped to its "
           + "own rectangle, after the owner and before the desktop popups; each row once; the MDI child (never returned by "
           + "the owner relation) and the XAML window stay inside the owner");
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
        using var svc = NewService(HostWindow(), null,
            _fakes.Window("Owned dialog reported again", Owned, bounds: (450, 150, 300, 300)));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().NotContain(r => r.Contains("reported again", StringComparison.Ordinal));
        Rows(snap).Count(r => r.EndsWith("Owned inside button", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task An_owned_window_entirely_outside_its_owner_is_still_found_and_walked()
    {
        var far = _fakes.Window("Far dialog", Far, controlType: WindowType, bounds: (900, 100, 200, 100),
            children: [Button("Far button", (910, 110, 50, 20))]);
        _byHandle.Add((nint)Far, far);
        _owned[(nint)Host] = [(nint)Owned, (nint)Far];
        using var svc = NewService(HostWindow(null, far));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Contain("Far dialog: Far button");
    }

    // ---- T2 c: transitive ownership ----------------------------------------------------------------

    [Fact]
    public async Task A_window_owned_by_an_owned_window_is_walked_once_as_its_own_root_and_skipped_inside_both_owners()
    {
        // UIA nests it under the owned dialog, which is nested under the host. The owner relation
        // returns both for the host, nearest first.
        var grand = _fakes.Window("Grand dialog", Grand, controlType: WindowType, bounds: (470, 300, 200, 100),
            children: [Button("Grand button", (480, 310, 50, 20))]);
        _byHandle.Add((nint)Grand, grand);
        _owned[(nint)Host] = [(nint)Owned, (nint)Grand];
        using var svc = NewService(HostWindow(OwnedDialog("Owned dialog", grand)));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Equal(
        [
            "Host: Host button", "Host: MDI button", "Host: XAML button", "Host: Host button 2",
            "Owned dialog: Owned inside button", "Owned dialog: Owned outside button",
            "Grand dialog: Grand button",
        ]);
    }

    // ---- T2: a returned handle that cannot be walked ----------------------------------------------

    [Theory]
    [InlineData("unresolved")]
    [InlineData("gone")]
    public async Task An_owned_handle_that_does_not_resolve_or_is_gone_is_skipped_and_logged(string how)
    {
        var log = new RecordingLogger<UIAutomationService>();
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Window("Host", hwnd: Host, pid: 4242, isActive: true)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OneMonitor);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        using var svc = new UIAutomationService(input.Object, windows.Object, null, log);
        var host = HostWindow();
        var dead = _fakes.Window("Dead dialog", Owned, pid: 0, controlType: WindowType, bounds: (450, 150, 300, 300),
            children: [Button("Dead button", (460, 160, 30, 20))]);
        svc.ElementFromHandle = h => h == Host ? host : how == "gone" && h == Owned ? dead : null;
        svc.OwnedWindowsOf = h => h == Host ? [(nint)Owned] : [];
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => [];

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().NotContain(r => r.StartsWith("Owned dialog:", StringComparison.Ordinal) || r.StartsWith("Dead dialog:", StringComparison.Ordinal),
            "a returned handle that cannot be walked is not walked as a root");
        Rows(snap).Should().Contain("Host: Host button", "the snapshot does not fail");
        log.Records.Should().Contain(r => r.Message.Contains("owned window", StringComparison.OrdinalIgnoreCase), "it is logged");
    }
}

/// <summary>
/// PR #43 review P1 and round 3 T1 end to end in-process: <c>Observation</c> over the REAL
/// <see cref="UIAutomationService"/>, the windows given by fakes. A snapshot that TIMES OUT must
/// reach the observation as the timeout - the note, with the timeout's message, no rows - and never
/// as "the window is gone", which would take a foreground snapshot instead.
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
        _svc.OwnedWindowsOf = _ => [];
        _svc.DesktopWindows = () => [];
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

    private void ShouldBeTheTimeoutNote(string text, string message)
    {
        text.Should().StartWith("acted\n\nObservation failed: ");
        text.Should().Contain(message, "TimeoutException is caller-facing, so its message is in the note");
        text.Should().NotContainEquivalentOf("gone");
        text.Should().NotContain("Cursor: (", "no rows: the observed window was not walked");
        _windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once,
            "one snapshot: no foreground fallback after a timeout");
    }

    [Fact]
    public async Task Observe_window_whose_snapshot_times_out_returns_the_note_with_the_timeout_and_takes_no_fallback()
    {
        var id = Issue(_fakes.Window("OK", Handle));
        _svc.ElementFromHandle = _ => throw new TimeoutException("UIA timed out resolving the window (wmcp-e5)");

        var text = await new Observation(_svc).AroundAsync("window", 0, id, CancellationToken.None, () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "UIA timed out resolving the window (wmcp-e5)");
    }

    [Fact]
    public async Task Observe_window_whose_owned_window_discovery_times_out_returns_the_note()
    {
        // T1: after the handle resolved. Today the per-window catch logs a skip and the
        // observation comes back empty under a header that says the window was observed.
        var id = Issue(_fakes.Window("OK", Handle));
        _svc.ElementFromHandle = h => _fakes.Window("OK", h);
        _svc.OwnedWindowsOf = _ => throw new TimeoutException("owner relation timed out (wmcp-e5)");

        var text = await new Observation(_svc).AroundAsync("window", 0, id, CancellationToken.None, () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "owner relation timed out (wmcp-e5)");
    }

    [Fact]
    public async Task Observe_foreground_whose_owned_window_discovery_times_out_returns_the_note()
    {
        _svc.ElementFromHandle = h => _fakes.Window("Some app button", h);
        _svc.OwnedWindowsOf = _ => throw new TimeoutException("owner relation timed out (wmcp-e5)");

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "owner relation timed out (wmcp-e5)");
    }

    [Fact]
    public async Task Observe_foreground_whose_foreground_window_times_out_on_resolution_returns_the_note()
    {
        // Round 4 U1 at the tool: no "Observed the foreground window" header over no controls.
        _svc.ElementFromHandle = _ => throw new TimeoutException("resolving the foreground window timed out (wmcp-e5)");

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "resolving the foreground window timed out (wmcp-e5)");
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("window")]
    public async Task Observe_whose_open_popup_times_out_while_it_is_walked_returns_the_note(string observe)
    {
        // Round 4 U2c at the tool: the menu the click opened hangs.
        var id = Issue(_fakes.Window("OK", Handle));
        _svc.ElementFromHandle = h => _fakes.Window("OK", h);
        _svc.DesktopWindows = () =>
        [
            _fakes.Window("Context menu", 0xAB30, controlType: 50009, bounds: (150, 150, 120, 80),
                childrenFailure: new TimeoutException("popup children read timed out (wmcp-e5)")),
        ];

        var text = await new Observation(_svc).AroundAsync(observe, 0, observe == "window" ? id : null, CancellationToken.None,
            () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "popup children read timed out (wmcp-e5)");
    }

    [Fact]
    public async Task Observe_foreground_whose_windows_subtree_cache_fetch_times_out_returns_the_note()
    {
        // Round 5 V1 at the tool: the reviewer's regression - the fetch hangs while the fallback root
        // would have been readable, and the observation used to come back "Observed".
        _svc.ElementFromHandle = h => _fakes.Window("Some app", h, controlType: 50032, bounds: (100, 100, 400, 300),
            cacheFetchFailure: new TimeoutException("subtree cache fetch timed out (wmcp-e5)"),
            children: [_fakes.Window("Some app button", 0, bounds: (110, 110, 50, 20))]);

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        ShouldBeTheTimeoutNote(text, "subtree cache fetch timed out (wmcp-e5)");
    }

    [Fact]
    public async Task X1_observe_window_whose_lookup_liveness_read_times_out_is_refused_before_the_action()
    {
        // Through the real service: the element's process-id read hangs, its handle would answer.
        var hung = new TimeoutException("process id read timed out (wmcp-e5)");
        var id = Issue(_fakes.Window("OK", Handle, pidFailure: hung));
        _svc.ElementFromHandle = _ => null;
        bool acted = false;

        Func<Task> act = () => new Observation(_svc).AroundAsync("window", 0, id, CancellationToken.None,
            () => { acted = true; return Task.FromResult("acted"); });

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
        acted.Should().BeFalse("the window is resolved before the action, so the action is never sent to a hung provider");
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("process")]
    public async Task Y4_observe_foreground_with_a_hung_unrelated_window_returns_the_observation_with_the_popup(string read)
    {
        // User-approved: a hung window of another application does not turn a healthy observation
        // into the failure note. Through the real service; the hung window is listed first.
        _svc.ElementFromHandle = h => _fakes.Window("Some app button", h);
        var timeout = new TimeoutException($"unrelated {read} read timed out (wmcp-e5)");
        var hung = read == "handle"
            ? _fakes.Window("Hung window", 0xAB30, pid: 999, bounds: (150, 150, 100, 20),
                propertyFailures: new Dictionary<string, Exception> { ["NativeWindowHandle"] = timeout })
            : _fakes.Window("Hung window", 0xAB30, pid: 999, bounds: (150, 150, 100, 20), pidFailure: timeout);
        _svc.DesktopWindows = () =>
        [
            hung,
            _fakes.Window("Context menu", 0xAB31, controlType: 50009, bounds: (150, 180, 120, 80),
                children: [_fakes.Window("Menu item A", 0, bounds: (155, 185, 100, 20))]),
        ];

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().StartWith("acted\n\nObserved", "an observation, not the failure note");
        text.Should().Contain("Menu item A", "the healthy popup of the observed process is in it");
        text.Should().NotContain("Observation failed");
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

/// <summary>
/// PR #43 round 3 T1 at the service: in an observation (<c>IncludePopups</c>), a
/// <see cref="TimeoutException"/> raised while a TARGET window is processed after its handle
/// resolved - discovering its owned windows, reading its process, or out of its walk - propagates as
/// that same exception. In a plain snapshot a timeout in one window stays a logged skip.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotObservationTimeoutTests : IDisposable
{
    private const long Host = 0xAB10;

    private readonly FakeUiWindows _fakes = new();
    private readonly RecordingLogger<UIAutomationService> _log = new();

    public void Dispose() => _fakes.Dispose();

    /// <param name="pid">The inventory's pid for the host; 0 makes the snapshot read the root's own.</param>
    private UIAutomationService NewService(AutomationElement host, int pid = 4242)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Window("Host", hwnd: Host, pid: pid, isActive: true)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object, null, _log);
        svc.ElementFromHandle = h => h == Host ? host : null;
        svc.OwnedWindowsOf = _ => [];
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => [];
        return svc;
    }

    public static TheoryData<SnapshotScope> ObservedScopes => new() { SnapshotScope.Window, SnapshotScope.Foreground };

    private static SnapshotRequest Observation(SnapshotScope scope) =>
        new(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0, IncludePopups: true);

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task A_timeout_discovering_the_owned_windows_propagates(SnapshotScope scope)
    {
        var hung = new TimeoutException("owner relation timed out (wmcp-e5)");
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.OwnedWindowsOf = _ => throw hung;

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task A_timeout_reading_the_foreground_windows_process_propagates()
    {
        // Foreground is not by handle, so the by-handle liveness rule (P1) does not cover it.
        var hung = new TimeoutException("process id read timed out (wmcp-e5)");
        using var svc = NewService(_fakes.Window("Host button", Host, pidFailure: hung), pid: 0);

        Func<Task> act = () => svc.SnapshotAsync(Observation(SnapshotScope.Foreground));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task A_timeout_out_of_the_walk_itself_propagates(SnapshotScope scope)
    {
        // Today the traverser swallows every failure (D-5: a dead element is skipped); for an
        // observation a hung provider must reach the caller instead.
        var hung = new TimeoutException("children read timed out (wmcp-e5)");
        using var svc = NewService(_fakes.Window("Host", Host, controlType: 50032, childrenFailure: hung));

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    // ---- PR #43 round 4 U1: resolving a target not asked for by handle -----------------------------

    public static TheoryData<string> NotByHandle => new() { "foreground", "title" };

    [Theory]
    [MemberData(nameof(NotByHandle))]
    public async Task U1_a_timeout_resolving_a_target_not_asked_for_by_handle_propagates_in_an_observation(string how)
    {
        // Today the window is skipped and observe:foreground answers "Observed the foreground
        // window" over no controls. The no-popups guard is
        // UIAutomationSnapshotWindowHandleTests.A_timeout_resolving_a_window_not_asked_for_by_handle_is_still_a_logged_skip.
        var hung = new TimeoutException("resolving the window timed out (wmcp-e5)");
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.ElementFromHandle = _ => throw hung;
        var request = how == "foreground"
            ? new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true)
            : new SnapshotRequest(SnapshotScope.Window, "Host", IncludePopups: true);

        Func<Task> act = () => svc.SnapshotAsync(request);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    // ---- PR #43 round 4 U2: the desktop-level popups pass ------------------------------------------

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task U2a_a_timeout_listing_the_desktops_windows_propagates(SnapshotScope scope)
    {
        var hung = new TimeoutException("listing the desktop timed out (wmcp-e5)");
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () => throw hung;

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    public static TheoryData<SnapshotScope, string> ScopesTimesFilterRead()
    {
        var data = new TheoryData<SnapshotScope, string>();
        foreach (var scope in new[] { SnapshotScope.Window, SnapshotScope.Foreground })
            foreach (var read in new[] { "handle", "process" })
                data.Add(scope, read);
        return data;
    }

    // ---- Y1 (user-approved, reverses round 4's U2b for these two reads) --------------------------
    // A candidate is "ours" only once its handle AND its process id have answered and the pid is a
    // walked one. Until then a hung window may belong to any application: a timeout on either read
    // skips that candidate (logged) and the pass goes on - one hung unrelated window must not turn a
    // healthy observation into the failure note. Once a candidate IS ours, a timeout still
    // propagates (Y2: offscreen, bounds, label, walk - W3, W2d and U2c rows).

    private AutomationElement HealthyPopup() =>
        _fakes.Window("Menu item A", 0xAB31, bounds: (150, 180, 100, 20));

    private AutomationElement HungCandidate(string read, Exception failure) => read == "handle"
        ? _fakes.Window("Hung window", 0xAB30, pid: 999, bounds: (150, 150, 100, 20),
            propertyFailures: new Dictionary<string, Exception> { ["NativeWindowHandle"] = failure })
        : _fakes.Window("Hung window", 0xAB30, pid: 999, bounds: (150, 150, 100, 20), pidFailure: failure);

    [Theory]
    [MemberData(nameof(ScopesTimesFilterRead))]
    public async Task Y1_a_timeout_reading_a_candidates_handle_or_process_skips_it_and_the_pass_goes_on(SnapshotScope scope, string read)
    {
        // Re-pinned from U2b_a_timeout_reading_a_popup_in_the_filter_propagates.
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () => [HungCandidate(read, new TimeoutException($"{read} read timed out (wmcp-e5)")), HealthyPopup()];

        var snap = await svc.SnapshotAsync(Observation(scope));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"],
            "the hung candidate is skipped and the healthy popup of the observed process after it is still walked");
        snap.Truncated.Should().BeFalse();
        _log.Records.Should().Contain(r => r.Message.Contains("popup window", StringComparison.Ordinal), "the skipped candidate is logged");
    }

    [Theory]
    [MemberData(nameof(ScopesTimesFilterRead))]
    public async Task Y4_a_hung_unrelated_window_before_a_healthy_popup_leaves_the_observation_whole(SnapshotScope scope, string read)
    {
        // The scenario the user cares about, as its own row: the hung window is listed FIRST.
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () => [HungCandidate(read, new TimeoutException($"unrelated {read} read timed out (wmcp-e5)")), HealthyPopup()];

        Func<Task<SnapshotResult>> act = () => svc.SnapshotAsync(Observation(scope));

        var snap = (await act.Should().NotThrowAsync()).Subject;
        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"]);
        snap.Truncated.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ScopesTimesFilterRead))]
    public async Task Y5_guard_a_non_timeout_failure_reading_a_candidates_handle_or_process_is_skipped_and_logged(SnapshotScope scope, string read)
    {
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () =>
            [HungCandidate(read, new System.Runtime.InteropServices.COMException($"{read} read failed (wmcp-e5)", unchecked((int)0x80004005))), HealthyPopup()];

        var snap = await svc.SnapshotAsync(Observation(scope));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"]);
        _log.Records.Should().Contain(r => r.Message.Contains("popup window", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task U2c_a_same_process_unowned_popup_whose_children_read_times_out_propagates(SnapshotScope scope)
    {
        // The reviewer's regression: the menu a click opened hangs while it is walked.
        var hung = new TimeoutException("popup children read timed out (wmcp-e5)");
        var popup = _fakes.Window("Context menu", 0xAB30, controlType: 50009, bounds: (150, 150, 120, 80), childrenFailure: hung);
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () => [popup];

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task U2_guard_a_popup_whose_children_read_fails_another_way_is_still_walked_as_far_as_it_goes()
    {
        // Non-timeout failures keep today's behaviour (the D-5 traverser skips what it cannot read).
        var popup = _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20),
            childrenFailure: new InvalidOperationException("children read failed (wmcp-e5)"));
        using var svc = NewService(_fakes.Window("Host button", Host));
        svc.DesktopWindows = () => [popup];

        var snap = await svc.SnapshotAsync(Observation(SnapshotScope.Window));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"]);
    }

    // ---- PR #43 round 5 V: the walk itself (UiTraverserTimeoutTests pins every read at the source) ----

    private AutomationElement HostWindow(Exception? cacheFetch = null, IReadOnlyDictionary<string, Exception>? childFails = null) =>
        _fakes.Window("Host", Host, controlType: 50032, bounds: (100, 100, 400, 300), cacheFetchFailure: cacheFetch,
            children: [_fakes.Window("Host button", 0, bounds: (110, 110, 50, 20), propertyFailures: childFails)]);

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task V_a_target_whose_subtree_cache_fetch_times_out_propagates_in_an_observation(SnapshotScope scope)
    {
        var hung = new TimeoutException("subtree cache fetch timed out (wmcp-e5)");
        using var svc = NewService(HostWindow(cacheFetch: hung));

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Theory]
    [MemberData(nameof(ObservedScopes))]
    public async Task V_a_target_whose_descendant_property_read_times_out_propagates_in_an_observation(SnapshotScope scope)
    {
        var hung = new TimeoutException("descendant Name read timed out (wmcp-e5)");
        using var svc = NewService(HostWindow(childFails: new Dictionary<string, Exception> { ["Name"] = hung }));

        Func<Task> act = () => svc.SnapshotAsync(Observation(scope));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task V_guard_without_popups_neither_timeout_fails_the_snapshot()
    {
        using var fetch = NewService(HostWindow(cacheFetch: new TimeoutException("timed out (wmcp-e5)")));
        using var read = NewService(HostWindow(childFails: new Dictionary<string, Exception> { ["Name"] = new TimeoutException("timed out (wmcp-e5)") }));

        var fetched = await fetch.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));
        var readSnap = await read.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        fetched.Interactive.Select(e => e.Name).Should().Equal(["Host button"], "the fetch falls back to the root, as D-5 always did");
        readSnap.Interactive.Should().ContainSingle("the unreadable Name takes its default");
    }

    // The other non-timeout guards of this pass are UIAutomationSnapshotPopupsTests
    // .A_popup_that_dies_while_it_is_read_is_skipped_and_logged_and_the_rest_still_come and
    // .A_desktop_list_that_cannot_be_read_costs_the_popups_not_the_snapshot.

    // ---- guards: the plain snapshot ----------------------------------------------------------------

    [Fact]
    public async Task Without_popups_a_timeout_reading_a_windows_process_stays_a_logged_skip()
    {
        using var svc = NewService(_fakes.Window("Host button", Host, pidFailure: new TimeoutException("timed out (wmcp-e5)")), pid: 0);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        snap.Interactive.Should().BeEmpty();
        _log.Records.Should().Contain(r => r.Message.Contains("'Host'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_popups_a_timeout_out_of_the_walk_does_not_fail_the_snapshot()
    {
        using var svc = NewService(_fakes.Window("Host", Host, controlType: 50032, childrenFailure: new TimeoutException("timed out (wmcp-e5)")));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        await act.Should().NotThrowAsync("the plain snapshot keeps D-5: one window's failure never fails the call");
    }
}

/// <summary>
/// PR #43 second review round, re-pinned for round 3 (T2: the owned window comes from
/// <c>OwnedWindowsOf</c> + <c>ElementFromHandle</c>). Q1: an owned window's elements are charged
/// to the element budget ONCE; truncation falls where a single pass puts it, the owner's own rows
/// first (including those AFTER the owned window in document order), then the owned window's;
/// <c>ElementCount</c> counts what the result holds. Without <c>IncludePopups</c> nothing changes and
/// no handle is read during the walk. Q2: the Foreground fallback (no inventory window flagged
/// active) gives its root the same treatment, through the <c>ForegroundRoot</c> seam.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotOwnedWindowBudgetTests : IDisposable
{
    private const int WindowType = 50032;
    private const long Host = 0xAB10;
    private const long Owned = 0xAC01;
    private const long Palette = 0xAD00;

    private static readonly MonitorInfo[] OneMonitor = [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    private readonly FakeUiWindows _fakes = new();
    private readonly Dictionary<nint, AutomationElement> _byHandle = new();
    private readonly List<nint> _ownerQueries = [];

    public void Dispose() => _fakes.Dispose();

    private AutomationElement Button(string name, (int X, int Y, int W, int H) bounds) => _fakes.Window(name, 0, bounds: bounds);

    /// <summary>An owned dialog wholly inside its host.</summary>
    private AutomationElement Dialog(params AutomationElement[] buttons)
    {
        var dialog = _fakes.Window("Owned dialog", Owned, controlType: WindowType, bounds: (150, 150, 200, 120), children: buttons);
        _byHandle.Add((nint)Owned, dialog);   // throws if a test builds two fakes with one handle (the seam returns one)
        return dialog;
    }

    private AutomationElement HostWith(params AutomationElement[] children) =>
        _fakes.Window("Host", Host, controlType: WindowType, bounds: (100, 100, 400, 300), children: children);

    /// <summary>host, Host button 1, [dialog: Dialog button 1, Dialog button 2], Host button 2 - six elements.</summary>
    private AutomationElement SixElementHost() => HostWith(
        Button("Host button 1", (110, 110, 50, 20)),
        Dialog(Button("Dialog button 1", (160, 160, 50, 20)), Button("Dialog button 2", (160, 200, 50, 20))),
        Button("Host button 2", (110, 350, 50, 20)));

    private UIAutomationService NewService(AutomationElement host, bool hostIsActive = true)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Window("Host", hwnd: Host, pid: 4242, isActive: hostIsActive)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OneMonitor);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object);
        svc.ElementFromHandle = h => h == Host ? host : _byHandle.GetValueOrDefault(h);
        svc.OwnedWindowsOf = h => { _ownerQueries.Add(h); return (long)h is Host or Palette ? [(nint)Owned] : []; };
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => [];
        return svc;
    }

    private static string[] Rows(SnapshotResult snap) => snap.Interactive.Select(e => $"{e.Window}: {e.Name}").ToArray();

    private static int Nodes(ElementTree t) => 1 + t.Children.Sum(Nodes);

    // ---- Q1: charged once --------------------------------------------------------------------------

    [Fact]
    public async Task An_exact_budget_holds_the_host_and_its_owned_dialog_with_nothing_truncated()
    {
        using var svc = NewService(HostWith(Button("Host button", (110, 110, 50, 20)), Dialog(Button("Dialog button", (160, 160, 50, 20)))));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, MaxElements: 4, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Equal(["Host: Host button", "Owned dialog: Dialog button"]);
        snap.Truncated.Should().BeFalse("every element fits: the dialog is charged once");
        snap.ElementCount.Should().Be(4);
    }

    [Fact]
    public async Task Element_count_is_what_the_result_holds_with_no_phantom_count_for_removed_rows()
    {
        using var svc = NewService(SixElementHost());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, IncludeTree: true, WindowHwnd: Host, IncludePopups: true));

        snap.Tree!.Children.Sum(Nodes).Should().Be(6, "precondition: host 3 + dialog 3");
        snap.ElementCount.Should().Be(6);
    }

    [Theory]
    [InlineData(3, new[] { "Host: Host button 1", "Host: Host button 2" })]
    [InlineData(4, new[] { "Host: Host button 1", "Host: Host button 2" })]
    [InlineData(5, new[] { "Host: Host button 1", "Host: Host button 2", "Owned dialog: Dialog button 1" })]
    public async Task A_short_budget_truncates_where_one_pass_would_owner_first_and_keeps_the_owners_later_rows(int max, string[] rows)
    {
        using var svc = NewService(SixElementHost());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, MaxElements: max, WindowHwnd: Host, IncludePopups: true));

        Rows(snap).Should().Equal(rows);
        snap.Truncated.Should().BeTrue();
        snap.ElementCount.Should().Be(max);
    }

    // ---- Q1 guards: without IncludePopups nothing changes ------------------------------------------

    [Fact]
    public async Task Without_popups_the_budget_falls_as_today_through_the_nested_dialog()
    {
        using var svc = NewService(SixElementHost());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, MaxElements: 4, WindowHwnd: Host));

        Rows(snap).Should().Equal(["Host: Host button 1", "Host: Dialog button 1"]);
        snap.Truncated.Should().BeTrue();
        snap.ElementCount.Should().Be(4);
    }

    [Fact]
    public async Task Without_popups_no_handle_is_read_during_the_walk_and_the_owner_relation_is_not_asked()
    {
        using var svc = NewService(SixElementHost());

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host));

        _fakes.HandleReads.Should().Be(0);
        _ownerQueries.Should().BeEmpty();
    }

    // ---- Q2: the foreground fallback -----------------------------------------------------------------

    private AutomationElement PaletteWithDialog()
    {
        var dialog = _fakes.Window("Owned dialog", Owned, controlType: WindowType, bounds: (450, 150, 300, 300),
            children: [Button("Owned inside button", (460, 160, 30, 20)), Button("Owned outside button", (600, 420, 100, 20))]);
        _byHandle.Add((nint)Owned, dialog);   // throws if a test builds two fakes with one handle (the seam returns one)
        return _fakes.Window("Tool palette", Palette, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [Button("Palette button", (110, 110, 50, 20)), dialog]);
    }

    [Fact]
    public async Task Foreground_fallback_with_popups_lifts_the_owned_dialog_out_of_the_root_in_front()
    {
        var palette = PaletteWithDialog();
        // The inactive inventory host is never walked here; it carries no dialog, so the only fake
        // with the handle Owned is the palette's (ElementFromHandle resolves owned windows by handle).
        using var svc = NewService(HostWith(Button("Host button 1", (110, 110, 50, 20))), hostIsActive: false);
        svc.ForegroundRoot = () => palette;
        svc.DesktopWindows = () => [_fakes.Window("Owned dialog reported again", Owned, bounds: (450, 150, 300, 300))];

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        Rows(snap).Should().Equal(["Tool palette: Palette button", "Owned dialog: Owned inside button", "Owned dialog: Owned outside button"]);
        _ownerQueries.Should().Equal([(nint)Palette], "the root in front is asked about by its own handle");
    }

    [Fact]
    public async Task Foreground_fallback_whose_root_has_no_handle_asks_the_owner_relation_nothing()
    {
        var palette = _fakes.Window("Tool palette", 0, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [Button("Palette button", (110, 110, 50, 20))]);
        // The inactive inventory host is never walked here; it carries no dialog, so the only fake
        // with the handle Owned is the palette's (ElementFromHandle resolves owned windows by handle).
        using var svc = NewService(HostWith(Button("Host button 1", (110, 110, 50, 20))), hostIsActive: false);
        svc.ForegroundRoot = () => palette;

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        Rows(snap).Should().Equal(["Tool palette: Palette button"]);
        _ownerQueries.Should().BeEmpty("a window with no native handle owns nothing it can name");
    }

    // ---- PR #43 U3 at the service: as far as a seam can honestly reach -------------------------------
    // The live ladder (foreground window, else focused element, else desktop) is pinned on the pure
    // helper, UiRootsTests.With_timeouts_propagating_*; the ForegroundRoot seam replaces the ladder
    // whole, so these rows only say what happens when the root call itself throws.

    [Fact]
    public async Task Foreground_fallback_whose_root_times_out_propagates_in_an_observation()
    {
        var hung = new TimeoutException("foreground root timed out (wmcp-e5)");
        using var svc = NewService(HostWith(Button("Host button 1", (110, 110, 50, 20))), hostIsActive: false);
        svc.ForegroundRoot = () => throw hung;

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task Foreground_fallback_whose_root_call_throws_fails_the_plain_snapshot_today_too()
    {
        // Characterisation, not a rule: the root call sits outside every per-window catch, so a
        // throw from it ends any foreground snapshot, observation or not. In production the
        // lenient ladder (UiRoots.FirstAvailable without propagation) absorbs a failing rung
        // before it gets here; only its LAST rung (the desktop) can throw this far.
        var hung = new TimeoutException("foreground root timed out (wmcp-e5)");
        using var svc = NewService(HostWith(Button("Host button 1", (110, 110, 50, 20))), hostIsActive: false);
        svc.ForegroundRoot = () => throw hung;

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task Foreground_fallback_without_popups_is_unchanged()
    {
        var palette = PaletteWithDialog();
        // The inactive inventory host is never walked here; it carries no dialog, so the only fake
        // with the handle Owned is the palette's (ElementFromHandle resolves owned windows by handle).
        using var svc = NewService(HostWith(Button("Host button 1", (110, 110, 50, 20))), hostIsActive: false);
        svc.ForegroundRoot = () => palette;

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        Rows(snap).Should().Equal(["Tool palette: Palette button", "Tool palette: Owned inside button"]);
        _ownerQueries.Should().BeEmpty();
    }
}
