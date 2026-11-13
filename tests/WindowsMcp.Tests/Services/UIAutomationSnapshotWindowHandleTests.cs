using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// Fake top-level windows for a headless snapshot: each is a strict
/// <c>Mock&lt;FrameworkAutomationElementBase&gt;</c> behind a real <see cref="AutomationElement"/>
/// answering only the properties the walk and the popup filter read (name, control type, bounds,
/// offscreen, process id, native handle). Every other member throws, and the traverser guards
/// every read, so a fake root walks to exactly one node - itself. A root that is a Button is
/// therefore one interactive row whose <c>Window</c> is the label the walk was given: which roots
/// were walked, in which order and under which label is visible in <c>SnapshotResult.Interactive</c>.
/// <para>
/// <c>UIAutomationSnapshotWindowHandleTests.Harness_a_fake_window_root_walks_to_one_interactive_row_under_its_label</c> proves
/// that on today's traverser.
/// </para>
/// </summary>
internal sealed class FakeUiWindows : IDisposable
{
    private const int ButtonType = 50000;

    public UIA3Automation Automation { get; } = new();

    public AutomationElement Window(string name, long handle, int pid = 4242,
        (int X, int Y, int W, int H)? bounds = null, bool offscreen = false, int controlType = ButtonType,
        bool handleSupported = true, bool pidSupported = true, bool broken = false,
        AutomationElement[]? children = null, Exception? pidFailure = null)
    {
        var lib = Automation.PropertyLibrary.Element;
        var b = bounds ?? (100, 100, 300, 200);
        var framework = new Mock<FrameworkAutomationElementBase>(MockBehavior.Strict, Automation);
        object Read(int id)
        {
            // A window that dies while the popup filter reads it.
            if (broken) throw new FlaUI.Core.Exceptions.ElementNotAvailableException();
            if (id == lib.Name.Id) return name;
            if (id == lib.ControlType.Id) return controlType;
            if (id == lib.BoundingRectangle.Id) return new double[] { b.X, b.Y, b.W, b.H };
            if (id == lib.IsOffscreen.Id) return offscreen;
            if (id == lib.ProcessId.Id) return pidFailure is not null ? throw pidFailure : pidSupported ? pid : Automation.NotSupportedValue;
            if (id == lib.NativeWindowHandle.Id) return handleSupported ? (int)handle : Automation.NotSupportedValue;
            if (id == lib.IsEnabled.Id) return true;
            return Automation.NotSupportedValue;
        }
        framework.Protected()
            .Setup<object>("InternalGetPropertyValue", ItExpr.IsAny<int>(), ItExpr.IsAny<bool>(), ItExpr.IsAny<bool>())
            .Returns((int id, bool _, bool _) => Read(id));
        // E-5 P2: a fake with children walks to its subtree (the traverser reads CachedChildren).
        if (children is not null)
            framework.Setup(f => f.GetCachedChildren()).Returns(children);
        return new AutomationElement(framework.Object);
    }

    public void Dispose() => Automation.Dispose();
}

/// <summary>
/// E-5 S1 as re-pinned by round 2 (R2): <c>SnapshotAsync</c> with <c>Scope == Window</c> and a
/// non-zero <c>WindowHwnd</c> resolves the window FROM THE HANDLE (<c>ElementFromHandle</c>, on the
/// STA thread), not from the filtered window inventory, which drops untitled windows, tool windows
/// and open menus. Alive: walked as its own root, labelled with the inventory title when the
/// inventory has it, else its UIA name. Dead or unresolvable - the seam answers null or throws, or
/// the element it answers reports itself gone (ProcessId 0) - a <see cref="KeyNotFoundException"/>
/// naming the handle, which <c>observe:window</c> reads as "the window closed".
/// <para>
/// Round 1 required the handle to be in the inventory; the rows that pinned that are re-pinned
/// here (see the class report). Seam contract: every window root a snapshot resolves goes through
/// <c>ElementFromHandle</c>, and liveness for a by-handle root is decided by the seam and by the
/// element's own probe, not by a Win32 call the test cannot reach.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotWindowHandleTests : IDisposable
{
    private const long DecoyHandle = 0xAB00;
    private const long TargetHandle = 0xAB01;

    private static readonly MonitorInfo[] OneMonitor = [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    private readonly FakeUiWindows _fakes = new();
    private readonly List<nint> _resolved = [];

    public void Dispose() => _fakes.Dispose();

    private static Mock<IWindowService> WindowsMock(params WindowInfo[] list)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(list);
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OneMonitor);
        return mock;
    }

    private UIAutomationService NewService(Mock<IWindowService> windows, Func<nint, AutomationElement?>? fromHandle = null,
        RecordingLogger<UIAutomationService>? log = null)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object, null, log);
        if (fromHandle is not null)
            svc.ElementFromHandle = h => { _resolved.Add(h); return fromHandle(h); };
        return svc;
    }

    private static Mock<IWindowService> Twins() => WindowsMock(
        Window("Save As", hwnd: DecoyHandle, zOrder: 0, isActive: true),
        Window("Save As", hwnd: TargetHandle, zOrder: 1));

    private static bool Mentions(RecordingLogger<UIAutomationService> log, string text) =>
        log.Records.Any(r => r.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task Harness_every_walked_window_with_a_dead_handle_is_logged_by_title()
    {
        // Today's desktop-scope behaviour, unchanged by E-5 (green before and after).
        var log = new RecordingLogger<UIAutomationService>();
        using var svc = NewService(WindowsMock(
            Window("Decoy window", hwnd: 0x7FFFFFF0, zOrder: 0),
            Window("Target window", hwnd: 0x7FFFFFF2, zOrder: 1)), log: log);

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        Mentions(log, "'Decoy window'").Should().BeTrue();
        Mentions(log, "'Target window'").Should().BeTrue();
    }

    [Fact]
    public void Harness_a_fake_window_root_walks_to_one_interactive_row_under_its_label()
    {
        using var fakes = new FakeUiWindows();
        var root = fakes.Window("Lonely button", 0xAB01);

        var entries = new WindowsMcp.Services.UiTree.UiTraverser(fakes.Automation)
            .Walk(root, "the label", new WindowsMcp.Services.UiTree.ElementBudget(10));

        entries.Should().ContainSingle();
        var (row, _) = UIAutomationService.Project(entries[0].Node, "el_1");
        row.Should().NotBeNull("a Button root with bounds is an interactive row");
        row!.Window.Should().Be("the label");
        row.Name.Should().Be("Lonely button");
    }

    // ---- R2: resolved from the handle --------------------------------------------------------------

    [Fact]
    public async Task By_handle_only_that_window_is_resolved_and_walked_under_its_inventory_title()
    {
        using var svc = NewService(Twins(), h => h == TargetHandle ? _fakes.Window("UIA name of the target", TargetHandle) : null);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        _resolved.Should().Equal([(nint)TargetHandle], "the handle names one window; the decoy shares the title, not the handle");
        snap.Interactive.Should().ContainSingle().Which.Window.Should().Be("Save As",
            "a window the inventory lists is labelled with the inventory title");
        snap.Windows.Should().HaveCount(2, "the header still reports the whole inventory");
    }

    [Fact]
    public async Task By_handle_a_window_the_inventory_does_not_list_is_walked_under_its_UIA_name()
    {
        // Re-pinned (round 1: KeyNotFoundException). An untitled window, a tool window and an open
        // menu are all missing from the filtered inventory, and none of them has closed.
        var windows = WindowsMock(Window("Decoy window", hwnd: DecoyHandle, isActive: true));
        using var svc = NewService(windows, h => h == TargetHandle ? _fakes.Window("Tool palette", TargetHandle) : null);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        _resolved.Should().Equal([(nint)TargetHandle]);
        snap.Interactive.Should().ContainSingle().Which.Window.Should().Be("Tool palette");
    }

    public static TheoryData<string> Unresolvable => new() { "null", "com", "not-available", "invalid-operation" };

    [Theory]
    [MemberData(nameof(Unresolvable))]
    public async Task By_handle_a_handle_that_resolves_to_nothing_is_a_KeyNotFoundException_naming_it(string how)
    {
        using var svc = NewService(Twins(), _ => how switch
        {
            "null" => null,
            "com" => throw new COMException("Invalid window handle", unchecked((int)0x80070578)),
            "not-available" => throw new FlaUI.Core.Exceptions.ElementNotAvailableException(),
            _ => throw new InvalidOperationException("no element for the handle"),
        });

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        var ex = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which;
        ex.Message.Should().Be("No open window with handle 43777 (0xAB01): it has closed.",
            "settled: the by-handle miss keeps round 1's wording");
        if (how != "null")
            ex.InnerException.Should().NotBeNull("the resolution failure is kept as the inner exception");
    }

    [Fact]
    public async Task By_handle_a_window_destroyed_after_the_inventory_read_is_a_KeyNotFoundException_not_an_empty_snapshot()
    {
        // Re-pinned (round 1: a logged skip and an empty snapshot). The inventory still lists it;
        // the element the handle resolves to reports itself gone.
        using var svc = NewService(Twins(), h => _fakes.Window("Gone", h, pid: 0));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        await act.Should().ThrowAsync<KeyNotFoundException>("observe:window reads this as 'the window closed' and looks at the foreground");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task By_handle_a_blank_title_beside_the_handle_is_no_title(string blank)
    {
        using var svc = NewService(Twins(), h => _fakes.Window("Target", h));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, blank, WindowHwnd: TargetHandle));

        snap.Interactive.Should().ContainSingle();
    }

    [Fact]
    public async Task A_title_together_with_a_handle_is_refused_before_the_desktop_is_touched()
    {
        var windows = Twins();
        using var svc = NewService(windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Save As", WindowHwnd: TargetHandle));

        await act.Should().ThrowAsync<ArgumentException>("a title and a handle could name different windows: give one");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SnapshotScope.Desktop)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task A_handle_with_a_scope_other_than_window_is_refused_before_the_desktop_is_touched(SnapshotScope scope)
    {
        var windows = Twins();
        using var svc = NewService(windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: TargetHandle));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().ContainEquivalentOf("window");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_title_with_no_handle_is_still_matched_by_title()
    {
        var log = new RecordingLogger<UIAutomationService>();
        using var svc = NewService(WindowsMock(
            Window("Decoy window", hwnd: 0x7FFFFFF0, zOrder: 0),
            Window("Target window", hwnd: 0x7FFFFFF2, zOrder: 1)), log: log);

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Target", WindowHwnd: 0));

        Mentions(log, "'Target window'").Should().BeTrue();
        Mentions(log, "'Decoy window'").Should().BeFalse();
    }

    // ---- PR #43 review P1: a timeout is not "it has closed" ----------------------------------------

    [Fact]
    public async Task By_handle_a_timeout_resolving_the_handle_propagates_as_that_timeout()
    {
        // A hung provider is not a closed window: reading it as one sends observe:window to the
        // foreground and tells the caller the window is gone.
        var hung = new TimeoutException("UIA timed out resolving the window (wmcp-e5)");
        using var svc = NewService(Twins(), _ => throw hung);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task By_handle_a_timeout_probing_the_resolved_root_for_liveness_propagates_as_that_timeout()
    {
        var hung = new TimeoutException("UIA timed out reading the process id (wmcp-e5)");
        using var svc = NewService(Twins(), h => _fakes.Window("Hung window", h, pidFailure: hung));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: TargetHandle));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("foreground")]
    [InlineData("title")]
    public async Task A_timeout_resolving_a_window_not_asked_for_by_handle_is_still_a_logged_skip(string how)
    {
        // Guard: P1 changes the by-handle path only.
        var log = new RecordingLogger<UIAutomationService>();
        using var svc = NewService(Twins(), _ => throw new TimeoutException("UIA timed out (wmcp-e5)"), log);
        var request = how switch
        {
            "desktop" => new SnapshotRequest(SnapshotScope.Desktop),
            "foreground" => new SnapshotRequest(SnapshotScope.Foreground),
            _ => new SnapshotRequest(SnapshotScope.Window, "Save As"),
        };

        var snap = await svc.SnapshotAsync(request);

        snap.Interactive.Should().BeEmpty();
        Mentions(log, "'Save As'").Should().BeTrue("the window that could not be resolved is logged by title, as before");
    }

    [Fact]
    public void SnapshotRequest_window_hwnd_and_include_popups_default_off_and_come_last()
    {
        new SnapshotRequest().WindowHwnd.Should().Be(0, "every existing construction means 'no handle'");
        new SnapshotRequest().IncludePopups.Should().BeFalse("and no popups");
        new SnapshotRequest(SnapshotScope.Window, null, false, 0, false, 0x1234, true)
            .Should().Match<SnapshotRequest>(r => r.WindowHwnd == 0x1234 && r.IncludePopups);
    }
}

/// <summary>
/// E-5 round 2 R3 (the checklist's "done when": clicking a menu returns the opened menu's items).
/// An open menu, a combo dropdown or a tooltip is its own untitled top-level window of the same
/// process, outside the window inventory. With <c>IncludePopups</c>, after the target windows,
/// every top-level desktop child (the <c>DesktopWindows</c> seam) of a walked window's PROCESS that
/// is neither an inventory window nor an already-walked root (by native handle) and is on screen
/// with a non-empty rectangle is walked too, as its own root, under the same element budget,
/// labelled with its UIA name or <c>popup</c>. Default off: nothing changes. Desktop scope is not
/// pinned.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotPopupsTests : IDisposable
{
    private const long Host = 0xAB10;
    private const long SameProcessInventoryWindow = 0xAB20;

    private static readonly MonitorInfo[] OneMonitor = [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    private readonly FakeUiWindows _fakes = new();
    private readonly RecordingLogger<UIAutomationService> _log = new();

    public void Dispose() => _fakes.Dispose();

    private UIAutomationService NewService(params AutomationElement[] desktopChildren)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Window("Host", hwnd: Host, pid: 4242, zOrder: 0, isActive: true),
            Window("Second window of the same app", hwnd: SameProcessInventoryWindow, pid: 4242, zOrder: 1),
        ]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OneMonitor);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object, null, _log);
        svc.ElementFromHandle = h => h == Host ? _fakes.Window("Host button", Host) : null;
        svc.DesktopWindows = () => desktopChildren;
        return svc;
    }

    /// <summary>The desktop as the popup filter sees it: one row of each kind, the two real popups among them.</summary>
    private AutomationElement[] Desktop() =>
    [
        _fakes.Window("Host button (the host again, another element for the same handle)", Host),
        _fakes.Window("Foreign popup", 0xAB40, pid: 999, bounds: (150, 150, 100, 20)),
        _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20)),
        _fakes.Window("Offscreen popup", 0xAB50, bounds: (150, 150, 100, 20), offscreen: true),
        _fakes.Window("Empty popup", 0xAB60, bounds: (150, 150, 0, 0)),
        _fakes.Window("Second window of the same app", SameProcessInventoryWindow),
        _fakes.Window("", 0xAB31, bounds: (150, 180, 100, 20)),
    ];

    [Fact]
    public async Task Window_scope_with_popups_walks_the_same_processes_open_popups_after_the_window()
    {
        using var svc = NewService(Desktop());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => (e.Window, e.Name)).Should().Equal(
            [("Host", "Host button"), ("Menu item A", "Menu item A"), ("popup", "")],
            "the window first, then each on-screen, non-empty top-level of its process that is not an inventory window or "
            + "already walked, labelled with its UIA name or 'popup' - not the foreign, off-screen or empty ones");
    }

    [Fact]
    public async Task Foreground_scope_with_popups_does_the_same_for_the_foreground_window()
    {
        using var svc = NewService(Desktop());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A", ""]);
    }

    [Theory]
    [InlineData(SnapshotScope.Window)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task Without_popups_nothing_but_the_window_is_walked(SnapshotScope scope)
    {
        using var svc = NewService(Desktop());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(scope, WindowHwnd: scope == SnapshotScope.Window ? Host : 0));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button"], "IncludePopups defaults to false and the snapshot tool sends false");
    }

    [Fact]
    public async Task Popups_share_the_element_budget()
    {
        using var svc = NewService(Desktop());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, MaxElements: 2, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"]);
        snap.Truncated.Should().BeTrue("the budget stopped the walk of the second popup");
        snap.ElementCount.Should().Be(2);
    }

    [Fact]
    public async Task A_popup_with_no_native_handle_is_still_walked_when_its_process_matches()
    {
        // Settled (GREEN round 2): the handle only keeps listed and already-walked windows out.
        using var svc = NewService(
            _fakes.Window("Handleless popup", 0, bounds: (150, 150, 100, 20)),
            _fakes.Window("Handle not supported popup", 0xAB80, bounds: (150, 180, 100, 20), handleSupported: false));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Handleless popup", "Handle not supported popup"]);
    }

    [Fact]
    public async Task Once_the_budget_is_spent_no_further_popup_is_even_read()
    {
        // Each popup read is a cross-process UIA call on the one STA thread: a spent budget stops
        // the loop, it does not read every remaining top-level window and walk it to nothing.
        using var svc = NewService(
            _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20)),
            _fakes.Window("Dying popup", 0xAB82, broken: true));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, MaxElements: 1, WindowHwnd: Host, IncludePopups: true));

        snap.Truncated.Should().BeTrue("the host took the only element and the first popup was refused");
        _log.Records.Should().NotContain(r => r.Message.Contains("popup", StringComparison.Ordinal),
            "the second popup was never read, so it could not fail");
    }

    [Fact]
    public async Task A_popup_whose_process_cannot_be_read_is_not_walked()
    {
        using var svc = NewService(_fakes.Window("Pid not supported popup", 0xAB81, bounds: (150, 150, 100, 20), pidSupported: false));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button"], "a popup is matched to the window by its process");
    }

    [Fact]
    public async Task A_popup_that_dies_while_it_is_read_is_skipped_and_logged_and_the_rest_still_come()
    {
        var log = _log;
        using var svc = NewService(
            _fakes.Window("Dying popup", 0xAB82, broken: true),
            _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20)));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button", "Menu item A"]);
        log.Records.Should().Contain(r => r.Message.Contains("popup", StringComparison.Ordinal), "a skipped popup is logged like a window");
    }

    [Fact]
    public async Task A_desktop_list_that_cannot_be_read_costs_the_popups_not_the_snapshot()
    {
        var log = _log;
        using var svc = NewService();
        svc.DesktopWindows = () => throw new System.Runtime.InteropServices.COMException("desktop enumeration failed (wmcp-e5)");

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button"]);
        log.Records.Should().Contain(r => r.Message.Contains("desktop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Desktop_scope_walks_no_popups_even_when_asked()
    {
        using var svc = NewService(Desktop());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Host button"],
            "settled: popups are for Foreground and Window; the desktop scope already walks every listed window");
    }

    [Fact]
    public async Task No_popups_are_walked_when_no_window_was_walked()
    {
        // Settled: no walked window, no process to match.
        using var svc = NewService(Desktop());
        svc.ElementFromHandle = _ => null;

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        snap.Interactive.Should().BeEmpty();
    }

    [Fact]
    public async Task A_window_outside_the_inventory_finds_its_popups_by_its_own_process_id()
    {
        // The untitled host is not in the inventory, so its pid comes from UIA.
        const long untitled = 0xAB70;
        using var svc = NewService(
            _fakes.Window("Untitled host button", untitled, pid: 5151),
            _fakes.Window("Dropdown row", 0xAB71, pid: 5151, bounds: (150, 150, 100, 20)),
            _fakes.Window("Menu item A", 0xAB30, bounds: (150, 150, 100, 20)));
        svc.ElementFromHandle = h => h == untitled ? _fakes.Window("Untitled host button", untitled, pid: 5151) : null;

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: untitled, IncludePopups: true));

        snap.Interactive.Select(e => e.Name).Should().Equal(["Untitled host button", "Dropdown row"],
            "the 4242 popup belongs to another process than the walked window");
    }
}
