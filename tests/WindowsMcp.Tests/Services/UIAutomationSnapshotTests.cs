using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-2 cycle B: <c>SnapshotAsync</c> and the traversal behind it, in three brackets.
/// <list type="bullet">
/// <item><see cref="UIAutomationSnapshotArgumentTests"/> — Unit. Argument rules and the header,
/// with <see cref="IWindowService"/>/<see cref="IInputService"/> mocked and an EMPTY window list,
/// so nothing walks and no desktop is needed.</item>
/// <item><see cref="UIAutomationSnapshotIntegrationTests"/> — Integration. The same call through
/// the real <c>WindowService</c>/<c>InputService</c> on a desktop session. This is the mandatory
/// non-mocked sibling: every assertion in the Unit class above would stay green if the walk never
/// happened, which is exactly how the <c>disk_inspect mode:reclaimable</c> bug survived its
/// tests.</item>
/// <item><see cref="UIAutomationSnapshotDesktopTests"/> — UIAutomation. The real element facts on
/// the Notepad fixture: the editor, the budget, the ids, the tree, the traverser.</item>
/// </list>
/// Separate file from <c>UIAutomationServiceTests</c> because the snapshot surface is its own
/// contract (and that file is already the D-2…D-7 record); the type's other tests stay there.
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationSnapshotArgumentTests
{
    private static readonly MonitorInfo[] TwoMonitors =
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, 0, 1920, 1080, false),
    ];

    private static Mock<IWindowService> WindowsMock(params WindowInfo[] list)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(list);
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoMonitors);
        return mock;
    }

    private static Mock<IInputService> CursorMock(int x = 2000, int y = 10)
    {
        var mock = new Mock<IInputService>();
        mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(x, y));
        return mock;
    }

    private static UIAutomationService NewService(
        Mock<IInputService>? input = null, Mock<IWindowService>? windows = null, UiTreeOptions? options = null)
        => new((input ?? CursorMock()).Object, (windows ?? WindowsMock()).Object, options);

    // ---- R3.1 validation, before any UIA work ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SnapshotAsync_window_scope_requires_a_window_title(string? title)
    {
        var windows = WindowsMock();
        using var svc = NewService(windows: windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, title));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*windowTitle*");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never, "the argument rules are decided before the desktop is touched");
    }

    [Theory]
    [InlineData(SnapshotScope.Desktop)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task SnapshotAsync_rejects_a_window_title_with_another_scope(SnapshotScope scope)
    {
        var windows = WindowsMock();
        using var svc = NewService(windows: windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(scope, "Notepad"));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The service is stricter than the tool on purpose, and identically to FindElementAsync
    // (D-5): the tool turns a blank 'window' into null, so a request that still carries one was
    // built in code and the mistake is worth naming rather than ignoring.
    [Fact]
    public async Task SnapshotAsync_rejects_even_a_blank_window_title_with_another_scope()
    {
        using var svc = NewService();

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, "   "));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task SnapshotAsync_rejects_a_negative_max_elements(int max)
    {
        var windows = WindowsMock();
        using var svc = NewService(windows: windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, MaxElements: max));

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().MatchRegex("[Mm]ax[_ ]?[Ee]lements", "the message names the argument that was wrong");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SnapshotAsync_propagates_a_cancelled_token()
    {
        using var svc = NewService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SnapshotAsync_throws_after_dispose()
    {
        var svc = NewService();
        svc.Dispose();

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest());

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ---- R3.2 the header: one read per collaborator, per call --------------------------------

    [Fact]
    public async Task SnapshotAsync_header_comes_from_the_cursor_the_monitors_and_the_window_list()
    {
        var input = CursorMock(2000, 10);
        var windows = WindowsMock();   // no windows: header only, nothing to walk
        using var svc = new UIAutomationService(input.Object, windows.Object, new UiTreeOptions(42));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Cursor.Should().Be(new CursorPosition(2000, 10));
        snap.CursorMonitorIndex.Should().Be(1, "(2000,10) is on the second monitor of this inventory");
        snap.Windows.Should().BeEmpty();
        snap.ActiveWindow.Should().BeNull();
        snap.Interactive.Should().BeEmpty();
        snap.Scrollable.Should().BeEmpty();
        snap.ElementCount.Should().Be(0);
        snap.Truncated.Should().BeFalse();
        snap.ElementLimit.Should().Be(42, "max_elements 0 means the budget the server was started with");
        snap.Tree.Should().BeNull("no tree was asked for");
        snap.CaptureMs.Should().BeGreaterThanOrEqualTo(0);

        input.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
        windows.Verify(w => w.ListAsync(true, false, It.IsAny<CancellationToken>()),
            Times.Once, "the header lists minimized windows too - only the WALK skips them");
    }

    [Fact]
    public async Task SnapshotAsync_cursor_monitor_index_is_minus_one_when_it_is_on_no_display()
    {
        using var svc = NewService(input: CursorMock(-5000, -5000));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.CursorMonitorIndex.Should().Be(-1);
    }

    [Fact]
    public async Task SnapshotAsync_active_window_is_the_flagged_entry_not_the_first()
    {
        // Both handles are invalid, so nothing walks and the header is all that is under test.
        var windows = WindowsMock(
            Window("Topmost window", hwnd: 0, zOrder: 0),
            Window("Focused window", hwnd: 0, zOrder: 1, isActive: true));
        using var svc = NewService(windows: windows);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Windows.Should().HaveCount(2);
        snap.ActiveWindow.Should().NotBeNull();
        snap.ActiveWindow!.Title.Should().Be("Focused window");
    }

    // A window that dies (or was never real) must cost that window, never the snapshot - the
    // desktop always has something closing while the walk is running (D-5's rule).
    [Fact]
    public async Task SnapshotAsync_skips_a_window_whose_walk_fails_instead_of_failing_the_call()
    {
        var windows = WindowsMock(
            Window("Ghost one", hwnd: 0, zOrder: 0),
            Window("Ghost two", hwnd: 0x7FFFFFF0, zOrder: 1, isActive: true));
        using var svc = NewService(windows: windows);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Windows.Should().HaveCount(2, "the header reports what the inventory reported");
        snap.Interactive.Should().BeEmpty();
        snap.ElementCount.Should().Be(0);
    }

    // ---- R3.3 window scope resolution --------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_window_scope_names_the_open_windows_when_nothing_matches()
    {
        var windows = WindowsMock(
            Window("Untitled - Notepad", hwnd: 0, zOrder: 0),
            Window("Google Chrome", hwnd: 0, zOrder: 1));
        using var svc = NewService(windows: windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "no-such-window-xyz"));

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain("no-such-window-xyz");
        message.Should().Contain("Untitled - Notepad").And.Contain("Google Chrome",
            "an agent that guessed the title wrong can only recover if it is told what IS open");
    }

    [Fact]
    public async Task SnapshotAsync_window_scope_lists_at_most_fifteen_open_titles()
    {
        var many = Enumerable.Range(0, 20)
            .Select(i => Window($"Window {i:00}", hwnd: 0, zOrder: i)).ToArray();
        using var svc = NewService(windows: WindowsMock(many));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "nothing-matches-this"));

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        var listed = message.Split("Window ").Length - 1;
        listed.Should().BeInRange(1, 15, "the same cap the find path uses - the message is for a model to read");
    }

    [Fact]
    public async Task SnapshotAsync_window_scope_says_so_when_no_open_window_has_a_title()
    {
        // Tool windows and cloaked shells have empty titles: the message must still be a sentence,
        // not "Open windows: " trailing into nothing.
        var windows = WindowsMock(Window("", hwnd: 0, zOrder: 0), Window("", hwnd: 0, zOrder: 1));
        using var svc = NewService(windows: windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Notepad"));

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain("Notepad").And.Contain("(none with a title)");
    }

    [Theory]
    [InlineData("Untitled - Notepad")]   // exact
    [InlineData("Notepad")]              // substring
    [InlineData("notepad")]              // and case-insensitive, like FindScope.Window
    public async Task SnapshotAsync_window_scope_matches_exact_then_substring_case_insensitively(string title)
    {
        var windows = WindowsMock(Window("Untitled - Notepad", hwnd: 0, zOrder: 0));
        using var svc = NewService(windows: windows);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, title));

        snap.Windows.Should().HaveCount(1, "a matched title resolves to a root instead of throwing");
    }

    // ---- R3.4 the tree block -----------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_include_tree_returns_a_synthetic_desktop_root()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludeTree: true));

        snap.Tree.Should().NotBeNull();
        snap.Tree!.Root.ElementId.Should().Be("desktop");
        snap.Tree.Root.ControlType.Should().Be("Desktop");
        snap.Tree.Root.Name.Should().BeEmpty();
        snap.Tree.Children.Should().BeEmpty("no window was walked, so the desktop has no child trees");
    }

    [Fact]
    public async Task SnapshotAsync_per_call_max_elements_overrides_the_server_budget()
    {
        using var svc = NewService(options: new UiTreeOptions(500));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, MaxElements: 7));

        snap.ElementLimit.Should().Be(7, "the per-call cap wins over --max-tree-elements");
    }
}

/// <summary>
/// A-2 (R8) through the REAL collaborators. Read-only and headless-safe in the same bracket as
/// <see cref="WindowServiceTests"/>: it needs a window station and whatever windows the session
/// has, no foreground app, no input, no capture. Its job is to prove the walk actually happens -
/// the mocked class above cannot tell "walked the desktop" from "returned an empty header".
/// </summary>
[Trait("Category", "Integration")]
public class UIAutomationSnapshotIntegrationTests
{
    [Fact]
    public async Task SnapshotAsync_desktop_walks_this_session_and_renders_the_fixed_layout()
    {
        using var svc = new UIAutomationService(new InputService(), new WindowService());
        var before = (await new WindowService().ListAsync()).Length;

        var sw = Stopwatch.StartNew();
        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
        sw.Stop();

        var after = (await new WindowService().ListAsync()).Length;
        snap.Windows.Length.Should().BeInRange(Math.Min(before, after), Math.Max(before, after),
            "the header is IWindowService.ListAsync - equal on a quiet desktop, +/-1 if a window came or went");

        // Non-vacuity: an implementation that never walked anything would satisfy every
        // invariant below on an empty result.
        snap.ElementCount.Should().BeGreaterThanOrEqualTo(1, "this session has at least one window with elements");
        snap.ElementCount.Should().BeLessThanOrEqualTo(UiTreeOptions.Default.MaxElements);
        snap.ElementLimit.Should().Be(UiTreeOptions.Default.MaxElements);
        if (snap.Truncated) snap.ElementCount.Should().Be(snap.ElementLimit);
        snap.CaptureMs.Should().BeGreaterThanOrEqualTo(0);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the budget is what bounds this walk");

        snap.Interactive.Should().OnlyContain(
            e => e.CenterX >= e.Bounds.X && e.CenterX <= e.Bounds.X + e.Bounds.Width
              && e.CenterY >= e.Bounds.Y && e.CenterY <= e.Bounds.Y + e.Bounds.Height,
            "a centre outside its own bounds would send click at the wrong place");
        snap.Interactive.Should().OnlyContain(e => snap.Windows.Any(w => w.Title == e.Window),
            "every element belongs to a window the header listed");
        snap.Scrollable.Should().OnlyContain(
            s => s.Scroll.VerticalPercent >= 0 && s.Scroll.VerticalPercent <= 100
              && s.Scroll.HorizontalPercent >= 0 && s.Scroll.HorizontalPercent <= 100,
            "UIA reports -1 for a non-scrollable axis; the traverser normalises it");

        var text = SnapshotRenderer.Render(snap);
        text.Should().StartWith("Cursor:");
        text.Should().Contain("Interactive (").And.Contain("Scrollable (");
    }

    [Fact]
    public async Task SnapshotAsync_desktop_honours_a_small_budget_on_the_real_desktop()
    {
        using var svc = new UIAutomationService(new InputService(), new WindowService(), new UiTreeOptions(5));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.ElementCount.Should().Be(5, "the walk stops the moment the budget refuses");
        snap.Truncated.Should().BeTrue();
        snap.ElementLimit.Should().Be(5);
        SnapshotRenderer.Render(snap).Should().EndWith(TruncationNote(5));
    }

    // ---- the target rules, driven through a CONTROLLED inventory over REAL windows -------------
    // The walk, the ids and the tree are real UIA; only IWindowService is a mock, because "which
    // window is active" and "which window is minimized" cannot be arranged on a shared desktop
    // without disturbing it. Every assertion below still fails if the traversal does nothing.

    /// <summary>The windows of this session the walk can actually do something with.</summary>
    /// <summary>
    /// The topmost listed window whose automation element still resolves. Other tests in the same
    /// run open and close windows; a window that vanished between ListAsync and FromHandle is the
    /// desktop changing under us, not a traverser failure.
    /// </summary>
    private static async Task<(WindowInfo Window, AutomationElement Root)?> FirstResolvableWindowAsync(UIA3Automation automation)
    {
        foreach (var w in await WalkableWindowsAsync())
        {
            try
            {
                var root = automation.FromHandle((nint)w.Hwnd);
                if (root is not null) return (w, root);
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.ElementNotAvailableException or System.Runtime.InteropServices.COMException)
            {
                // gone; try the next one
            }
        }
        return null;
    }

    private static async Task<WindowInfo[]> WalkableWindowsAsync()
    {
        var windows = await new WindowService().ListAsync();
        var walkable = windows
            .Where(w => w.State != WindowState.Minimized && w.Hwnd != 0 && w.Title.Length > 0)
            .ToArray();
        walkable.Should().NotBeEmpty("this session must have at least one visible titled window to walk");
        return walkable;
    }

    /// <summary>The real service over a fixed inventory and a fixed cursor; the UIA side is live.</summary>
    private static UIAutomationService ServiceOver(params WindowInfo[] inventory)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(0, 0));
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inventory);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        return new UIAutomationService(input.Object, windows.Object);
    }

    [Fact]
    public async Task SnapshotAsync_foreground_walks_the_entry_the_inventory_flagged_active()
    {
        var walkable = await WalkableWindowsAsync();
        var active = walkable[^1] with { IsActive = true };
        // A second walkable window when the session has one, an unwalkable placeholder otherwise:
        // either way "walk the first entry" and "walk them all" are distinguishable from "walk the
        // active one".
        var other = walkable.Length > 1
            ? walkable[0] with { IsActive = false }
            : Window("Not the active window", hwnd: 0, zOrder: 99);
        using var svc = ServiceOver(other, active);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludeTree: true));

        snap.ElementCount.Should().BeGreaterThan(0, "the active entry carries a real handle, so the walk has work to do");
        snap.Tree!.Children.Should().HaveCount(1, "scope=foreground walks exactly one window, not the whole inventory");
        snap.Interactive.Should().OnlyContain(e => e.Window == active.Title,
            "every element in a foreground snapshot belongs to the active window");
    }

    [Fact]
    public async Task SnapshotAsync_foreground_falls_back_to_the_ui_automation_foreground_when_no_entry_is_active()
    {
        // The inventory flags nothing active when the desktop itself has focus, or when the
        // foreground window is cloaked. Returning an empty snapshot there would leave the agent
        // with no way to see the screen at all, so the walk falls back to UIA's own idea of front.
        var walkable = await WalkableWindowsAsync();
        using var svc = ServiceOver(walkable[0] with { IsActive = false });

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        snap.ActiveWindow.Should().BeNull("nothing in this inventory is flagged active");
        snap.ElementCount.Should().BeGreaterThan(0, "the fallback root is walked instead of returning nothing");
    }

    [Fact]
    public async Task SnapshotAsync_desktop_walks_a_normal_window_and_skips_the_same_one_minimized()
    {
        var walkable = await WalkableWindowsAsync();
        var normal = walkable[0] with { IsActive = true };

        using var walked = ServiceOver(normal);
        using var skipped = ServiceOver(normal with { State = WindowState.Minimized });

        var withNormal = await walked.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
        var withMinimized = await skipped.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        withNormal.ElementCount.Should().BeGreaterThan(0);
        withMinimized.Windows.Should().HaveCount(1, "the header reports the whole inventory");
        withMinimized.ElementCount.Should().Be(0, "a minimized window has nothing on screen to walk");
        withMinimized.Interactive.Should().BeEmpty();
    }

    // ---- roadmap C5: an id lives until the next snapshot, and no longer --------------------------

    [Fact]
    public async Task Snapshot_ids_resolve_and_are_evicted_by_the_next_snapshot()
    {
        using var svc = new UIAutomationService(new InputService(), new WindowService(), new UiTreeOptions(50));

        var first = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludeTree: true));
        var ids = TreeIds(first.Tree!).Skip(1).ToArray();   // skip the synthetic "desktop" root
        ids.Should().NotBeEmpty("the walk issues one id per element it admitted");

        (await svc.GetElementAsync(ids[0])).Should().NotBeNull("a fresh snapshot id resolves to its live element");

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        Func<Task> stale = () => svc.GetElementAsync(ids[0]);
        await stale.Should().ThrowAsync<KeyNotFoundException>(
            "the ids the previous snapshot issued are evicted when the next one starts (roadmap C5)");
    }

    // ---- the semantic tree ----------------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_include_tree_reproduces_every_walked_element_under_the_desktop_root()
    {
        using var svc = new UIAutomationService(new InputService(), new WindowService(), new UiTreeOptions(60));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludeTree: true));

        snap.Tree.Should().NotBeNull();
        snap.Tree!.Root.ElementId.Should().Be("desktop");
        snap.Tree.Children.Should().NotBeEmpty("one child tree per walked window");

        var ids = TreeIds(snap.Tree);
        ids.Skip(1).Should().HaveCount(snap.ElementCount,
            "the tree carries every element the walk admitted - a dropped subtree is a silently smaller tree");
        ids.Should().OnlyHaveUniqueItems();
        ids.Skip(1).Should().OnlyContain(id => id.StartsWith("el_", StringComparison.Ordinal));

        // The ids in the tree are the same live handles the interactive list points at.
        foreach (var id in ids.Skip(1).Take(10))
            (await svc.GetElementAsync(id)).Should().NotBeNull($"tree id {id} must resolve");
        foreach (var element in snap.Interactive.Take(10))
            ids.Should().Contain(element.ElementId, "the lists and the tree are one numbering");
    }

    private static List<string> TreeIds(ElementTree tree)
    {
        var ids = new List<string>();
        Collect(tree);
        return ids;

        void Collect(ElementTree node)
        {
            ids.Add(node.Root.ElementId);
            foreach (var child in node.Children) Collect(child);
        }
    }

    // ---- A-4 back-port: the same budget bounds get_state ----------------------------------------

    /// <summary>
    /// <c>get_state</c> keeps its depth-3 shape and gains the cap. Two walks of the same live
    /// foreground tree, one budgeted and one not: the sanity assertion on the unbounded walk is
    /// what stops this passing on a tree too small to truncate.
    /// </summary>
    [Fact]
    public async Task GetStateAsync_stops_at_the_budget_and_reports_it_on_the_root()
    {
        using var bounded = new UIAutomationService(new InputService(), new WindowService(), new UiTreeOptions(2));
        using var whole = new UIAutomationService(new InputService(), new WindowService());

        var small = await bounded.GetStateAsync();
        var full = await whole.GetStateAsync();

        CountNodes(full).Should().BeGreaterThan(2, "sanity: this session's foreground tree has more than two nodes");
        full.Truncated.Should().BeFalse("500 elements is far more than three levels of one window");
        full.ElementLimit.Should().Be(0, "an untruncated tree serialises exactly as it did before A-4");

        CountNodes(small).Should().BeLessThanOrEqualTo(2, "the walk stops the moment the budget refuses");
        small.Truncated.Should().BeTrue();
        small.ElementLimit.Should().Be(2);
        small.Children.Should().OnlyContain(c => !c.Truncated && c.ElementLimit == 0,
            "the verdict belongs to the walk, so it is on the ROOT only");
    }

    private static int CountNodes(ElementTree tree) => 1 + tree.Children.Sum(CountNodes);

    // ---- A-2/A-3 metadata: the CacheRequest must not lose the pattern-backed facts ---------------

    /// <summary>
    /// The facts a snapshot is *for* — the legacy role (the D-6 fallback for Chromium/Qt Custom
    /// elements), the value, the toggle/expand state, the range and the scroll percentages — all
    /// come off UIA <i>patterns</i>, and every one of them is read inside
    /// <c>UiTraverser</c>'s <c>CacheRequest</c> behind a <c>Try(read, fallback)</c> guard. This is
    /// the only test that checks the guard is not swallowing them: it reads the same fact live,
    /// outside any cache request, and demands the walk report it too.
    /// </summary>
    [Fact]
    public async Task Walk_reports_the_legacy_role_that_a_live_read_returns()
    {
        using var automation = new UIA3Automation();
        var resolved = await FirstResolvableWindowAsync(automation);
        if (resolved is null) return;   // every listed window died between the list and the read
        var (window, root) = resolved.Value;

        var live = root!.Patterns.LegacyIAccessible.PatternOrDefault?.Role.ValueOrDefault;
        if (live is null) return;   // no LegacyIAccessible at all on this window: nothing to compare

        var entries = new UiTraverser(automation).Walk(root, window.Title, new ElementBudget(50));

        entries.Should().NotBeEmpty();
        entries[0].Node.LegacyRole.Should().Be(
            live.ToString()!.Replace("ROLE_SYSTEM_", "", StringComparison.Ordinal).ToLowerInvariant(),
            "the walk must report the role the element actually has - a property the CacheRequest "
            + "did not ask for throws PropertyNotCachedException, which Try() turns into a silent null");
    }

    /// <summary>A-3's whole output block: a region that really scrolls has to reach the list.</summary>
    [Fact]
    public async Task Walk_reports_the_scroll_pattern_that_a_live_read_finds()
    {
        using var automation = new UIA3Automation();
        var resolved = await FirstResolvableWindowAsync(automation);
        if (resolved is null) return;   // every listed window died between the list and the read
        var (window, root) = resolved.Value;

        var entries = new UiTraverser(automation).Walk(root!, window.Title, new ElementBudget(200));

        var live = entries.Count(e => LiveScrolls(e.Element));
        if (live == 0) return;   // nothing in this window scrolls: nothing to prove here

        entries.Count(e => e.Node.Scroll is { } s && (s.VerticallyScrollable || s.HorizontallyScrollable))
            .Should().BeGreaterThan(0,
                "A-3's scrollable list stays empty forever unless the walk reads the ScrollPattern's properties");
    }

    private static bool LiveScrolls(FlaUI.Core.AutomationElements.AutomationElement el)
    {
        try
        {
            var scroll = el.Patterns.Scroll.PatternOrDefault;
            return scroll is not null
                && (scroll.VerticallyScrollable.ValueOrDefault || scroll.HorizontallyScrollable.ValueOrDefault);
        }
        catch { return false; }
    }
}

/// <summary>
/// A-2 (R7) / A-4 (R4) on a live desktop with Notepad in the foreground. Everything here needs
/// real UIA elements: the editor's action hint, the ids, the tree, and the traverser itself.
/// </summary>
[Trait("Category", "UIAutomation")]
public class UIAutomationSnapshotDesktopTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    public UIAutomationSnapshotDesktopTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    private static UIAutomationService NewService(UiTreeOptions? options = null)
        => new(new InputService(), new WindowService(), options);

    private long NotepadHwnd()
    {
        var window = _np.App.GetMainWindow(_np.Automation, TimeSpan.FromSeconds(2));
        if (window is not null)
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd != IntPtr.Zero) return hwnd.ToInt64();
        }

        // Modern (XAML) Notepad's window belongs to a different process than the one
        // Application.Launch started, so GetMainWindow can come back empty. The A-1 inventory
        // finds it by title - which is also how an agent would.
        var listed = new WindowService().ListAsync().GetAwaiter().GetResult()
            .FirstOrDefault(w => w.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException("Notepad has no main window");
        return listed.Hwnd;
    }

    private static bool IsEditor(SnapshotElement e) => e.ControlType is "Document" or "Edit";

    // ---- R7 (a) the headline: the editor is listed, with a usable centre ----------------------

    [Fact]
    public async Task SnapshotAsync_foreground_lists_the_notepad_editor_with_a_fill_action()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        snap.Windows.Should().NotBeEmpty();
        snap.ActiveWindow.Should().NotBeNull();
        snap.ElementCount.Should().BeGreaterThan(0);
        snap.CaptureMs.Should().BeGreaterThanOrEqualTo(0);

        var editor = snap.Interactive.FirstOrDefault(IsEditor);
        editor.Should().NotBeNull("the editor - a Document on modern Notepad, an Edit on the classic one - is interactive");
        editor!.Action.Should().Be("fill");
        editor.CenterX.Should().BeInRange(editor.Bounds.X, editor.Bounds.X + editor.Bounds.Width);
        editor.CenterY.Should().BeInRange(editor.Bounds.Y, editor.Bounds.Y + editor.Bounds.Height);
        editor.Window.Should().ContainEquivalentOf("notepad");
    }

    // ---- R7 (b) the budget ------------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_stops_at_max_elements_and_says_so_in_the_text()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, MaxElements: 5));

        snap.ElementCount.Should().Be(5);
        snap.Truncated.Should().BeTrue();
        snap.ElementLimit.Should().Be(5);
        SnapshotRenderer.Render(snap).Should().EndWith(TruncationNote(5),
            "the note is the last thing the model reads, so it knows the list is partial");
    }

    [Fact]
    public async Task SnapshotAsync_max_elements_zero_uses_the_budget_the_server_was_started_with()
    {
        using var svc = NewService(new UiTreeOptions(6));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        snap.ElementLimit.Should().Be(6);
        snap.ElementCount.Should().BeLessThanOrEqualTo(6);
    }

    // ---- R7 (c) the ids are real element handles ---------------------------------------------

    [Fact]
    public async Task Snapshot_ids_work_with_get_element_and_interact_element()
    {
        using var svc = NewService();
        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));
        var editor = snap.Interactive.FirstOrDefault(IsEditor);
        editor.Should().NotBeNull();

        var info = await svc.GetElementAsync(editor!.ElementId);
        info.ControlType.Should().Be(editor.ControlType);

        var interaction = await svc.InteractAsync(editor.ElementId, "focus", null);
        interaction.Method.Should().Be("Focus");
    }

    // ---- R7 (d) roadmap C5: id lifetime ------------------------------------------------------

    [Fact]
    public async Task A_second_snapshot_evicts_the_first_snapshots_ids_but_not_a_find_element_id()
    {
        using var svc = NewService();
        var first = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));
        first.Interactive.Should().NotBeEmpty();
        var snapshotId = first.Interactive[0].ElementId;

        var found = await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        found.Matches.Should().NotBeEmpty();
        var findId = found.Matches[0].ElementId;

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        Func<Task> stale = () => svc.GetElementAsync(snapshotId);
        await stale.Should().ThrowAsync<KeyNotFoundException>(
            "a snapshot id is documented as valid only until the next snapshot");

        var survivor = await svc.GetElementAsync(findId);
        survivor.Should().NotBeNull("an unrelated snapshot must not break a find_element -> interact_element workflow");
    }

    // ---- R7 (e) scope=window ------------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_window_scope_finds_notepad_by_substring()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Notepad"));

        snap.ElementCount.Should().BeGreaterThan(0);
        snap.Interactive.Should().Contain(e => IsEditor(e));
    }

    [Fact]
    public async Task SnapshotAsync_window_scope_names_the_open_windows_when_nothing_matches()
    {
        using var svc = NewService();

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "no-such-window-xyz"));

        (await act.Should().ThrowAsync<KeyNotFoundException>()).WithMessage("*no-such-window-xyz*");
    }

    // ---- R7 (f) scope=desktop -----------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_desktop_includes_the_foreground_notepad_editor()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Windows.Should().NotBeEmpty();
        // Roots are walked in z-order, topmost first, and Notepad is in the foreground, so the
        // budget cannot run out before it is reached.
        snap.Interactive.Should().Contain(e => IsEditor(e) && e.Window.Contains("Notepad", StringComparison.OrdinalIgnoreCase));
    }

    // A minimized window has nothing on screen to click, so it is in the header but NOT walked.
    // Driven through a mocked inventory carrying Notepad's REAL handle, which is the only way to
    // change one window's reported state without minimizing anything on the desktop.
    [Fact]
    public async Task SnapshotAsync_walks_a_normal_window_and_skips_the_same_window_when_minimized()
    {
        var hwnd = NotepadHwnd();
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(0, 0));

        static Mock<IWindowService> Inventory(WindowInfo window)
        {
            var mock = new Mock<IWindowService>();
            mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([window]);
            mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
            return mock;
        }

        var normal = Window("Untitled - Notepad", hwnd: hwnd, state: WindowState.Normal, isActive: true);
        var minimized = normal with { State = WindowState.Minimized };

        using var walked = new UIAutomationService(input.Object, Inventory(normal).Object);
        using var skipped = new UIAutomationService(input.Object, Inventory(minimized).Object);

        var withNormal = await walked.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
        var withMinimized = await skipped.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        withNormal.ElementCount.Should().BeGreaterThan(0, "the real handle must resolve to a real window");
        withMinimized.Windows.Should().HaveCount(1, "the header still lists it");
        withMinimized.ElementCount.Should().Be(0, "a minimized window is not walked");
    }

    // ---- R7 (g) the semantic tree -------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_include_tree_hangs_one_window_under_the_desktop_root_with_resolvable_ids()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludeTree: true));

        snap.Tree.Should().NotBeNull();
        snap.Tree!.Root.ElementId.Should().Be("desktop");
        snap.Tree.Children.Should().HaveCount(1, "one child tree per walked window, and foreground walks one");

        var ids = new List<string>();
        Collect(snap.Tree, ids);
        ids.Should().HaveCountGreaterThan(1);
        ids[0].Should().Be("desktop");

        // The point of issuing an id per WALKED node (not per interactive node) is that a tree id
        // is usable with get_element and interact_element too. Twenty is enough to prove it.
        foreach (var id in ids.Skip(1).Take(20))
            (await svc.GetElementAsync(id)).Should().NotBeNull($"tree id {id} must resolve");

        static void Collect(ElementTree tree, List<string> into)
        {
            into.Add(tree.Root.ElementId);
            foreach (var child in tree.Children) Collect(child, into);
        }
    }

    // ---- R7 (h) A-3 scroll percentages --------------------------------------------------------

    [Fact]
    public async Task Scrollable_entries_report_percentages_inside_zero_to_a_hundred()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        // NOTE: vacuous when nothing on the desktop exposes a moving ScrollPattern (xunit v2 has
        // no dynamic skip - see the report). The desktop scope makes that unlikely rather than
        // impossible; the strict assertion is the invariant when there IS one.
        snap.Scrollable.Should().OnlyContain(
            s => s.Scroll.VerticalPercent >= 0 && s.Scroll.VerticalPercent <= 100
              && s.Scroll.HorizontalPercent >= 0 && s.Scroll.HorizontalPercent <= 100);
        snap.Scrollable.Should().OnlyContain(
            s => s.Scroll.VerticallyScrollable || s.Scroll.HorizontallyScrollable,
            "a region that cannot move on either axis is not a scrollable region");
    }

    // ---- R7 (i) it has to be fast enough to be part of an agent loop ---------------------------

    [Fact]
    public async Task SnapshotAsync_foreground_completes_well_inside_ten_seconds()
    {
        using var svc = NewService();
        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));   // warm the UIA cache

        var sw = Stopwatch.StartNew();
        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a snapshot the agent runs every turn cannot cost more than the action it precedes");
    }

    // ---- R4: the same budget, back-ported to get_state -----------------------------------------

    [Fact]
    public async Task GetStateAsync_stops_at_the_budget_and_marks_the_root_truncated()
    {
        using var svc = NewService(new UiTreeOptions(3));

        var tree = await svc.GetStateAsync();

        CountNodes(tree).Should().BeInRange(1, 3, "the walk stops when the budget refuses");
        tree.Truncated.Should().BeTrue();
        tree.ElementLimit.Should().Be(3);
        tree.Children.Should().OnlyContain(c => !c.Truncated && c.ElementLimit == 0,
            "the verdict belongs to the walk, so it is on the ROOT only");
    }

    [Fact]
    public async Task GetStateAsync_is_not_truncated_under_the_default_budget()
    {
        using var svc = NewService();

        var tree = await svc.GetStateAsync();

        tree.Truncated.Should().BeFalse("Notepad three levels deep is far short of 500 elements");
        tree.ElementLimit.Should().Be(0, "an untruncated tree serialises exactly as it did before A-4");
        CountNodes(tree).Should().BeGreaterThan(1);
    }

    private static int CountNodes(ElementTree tree) => 1 + tree.Children.Sum(CountNodes);

    // ---- the traverser itself -----------------------------------------------------------------

    [Fact]
    public void Walk_returns_the_window_first_then_its_subtree_in_pre_order()
    {
        var root = _np.Automation.FromHandle(new IntPtr(NotepadHwnd()));
        var budget = new ElementBudget(500);

        var entries = new UiTraverser(_np.Automation).Walk(root, "Untitled - Notepad", budget);

        entries.Should().NotBeEmpty();
        entries[0].ParentIndex.Should().Be(-1, "entry 0 is the window itself");
        entries[0].Node.Depth.Should().Be(0);
        for (var i = 1; i < entries.Count; i++)
            entries[i].ParentIndex.Should().BeInRange(0, i - 1,
                "the list is pre-order, so a node's parent always precedes it");

        entries.Should().OnlyContain(e => e.Node.Window == "Untitled - Notepad");
        entries.Should().OnlyContain(e => e.Element != null, "every entry keeps the live element an id is issued for");
        budget.Count.Should().Be(entries.Count, "the budget is spent exactly once per admitted node");
        budget.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Walk_admits_only_on_screen_nodes_clipped_to_the_window()
    {
        var root = _np.Automation.FromHandle(new IntPtr(NotepadHwnd()));

        var entries = new UiTraverser(_np.Automation).Walk(root, "Untitled - Notepad", new ElementBudget(500));

        var window = entries[0].Node.Bounds;
        window.Should().NotBeNull();
        entries.Should().OnlyContain(e => e.Node.Bounds != null && e.Node.Bounds.Width > 0 && e.Node.Bounds.Height > 0,
            "a zero-area node is nothing the model can click");
        entries.Skip(1).Should().OnlyContain(e =>
            e.Node.Bounds!.X >= window!.X && e.Node.Bounds.Y >= window.Y
            && e.Node.Bounds.X + e.Node.Bounds.Width <= window.X + window.Width
            && e.Node.Bounds.Y + e.Node.Bounds.Height <= window.Y + window.Height,
            "every node is clipped to its window's rectangle (upstream's iou_bounding_box)");
        entries.Should().OnlyContain(e => !e.Node.IsOffscreen || e.Node.ControlType == "Edit",
            "off-screen nodes are dropped, with D-7's Edit exception");
    }

    [Fact]
    public void Walk_stops_the_moment_the_budget_refuses()
    {
        var root = _np.Automation.FromHandle(new IntPtr(NotepadHwnd()));
        var budget = new ElementBudget(3);

        var entries = new UiTraverser(_np.Automation).Walk(root, "Untitled - Notepad", budget);

        entries.Should().HaveCount(3);
        budget.Truncated.Should().BeTrue();
    }
}
