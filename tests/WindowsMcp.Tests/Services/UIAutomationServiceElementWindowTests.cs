using System.Reflection;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-5 S2 as re-pinned by round 2 (R1): <c>UIAutomationService.GetElementWindowAsync</c> - the
/// window <c>observe:window</c> observes, resolved BEFORE the action. The rule: the nearest element
/// with a non-zero NativeWindowHandle, starting with the element itself and walking <c>ParentOf</c>,
/// and then that handle's ROOT window as Win32 reports it (<c>GetAncestor(GA_ROOT)</c>, through the
/// <c>RootWindowOf</c> seam) - which stops at an owned dialog instead of crossing to its owner.
/// <para>
/// Round 1 walked to the desktop root instead. The review found that walk returns an
/// intermediate control when the walk fails half-way, and the owner instead of an owned dialog; the
/// rows that pinned the desktop-root rule are re-pinned here to the new one (see the class report).
/// </para>
/// <para>
/// Driven through the REAL service with FAKE elements (the
/// <see cref="UIAutomationServiceInteractVerbsTests"/> harness: a strict
/// <c>Mock&lt;FrameworkAutomationElementBase&gt;</c> behind a real <see cref="AutomationElement"/>,
/// put in the id cache by reflection), the ancestors given through <c>ParentOf</c> and the Win32
/// answer through <c>RootWindowOf</c>, both recorded. The real GA_ROOT on real owned windows is
/// <c>UIAutomationServiceObserveWindowsIntegrationTests</c>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationServiceElementWindowTests : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private const int ButtonType = 50000;
    private const int PaneType = 50033;
    private const int WindowType = 50032;

    private const int WindowHandle = 0x1234;

    private readonly UIA3Automation _automation = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    /// <summary>Every handle the service asked <c>RootWindowOf</c> about, in order.</summary>
    private readonly List<nint> _rootQueries = [];

    /// <summary>What <c>RootWindowOf</c> answers; unlisted handles are their own root.</summary>
    private readonly Dictionary<nint, nint> _roots = new();

    private int _parentCalls;

    public UIAutomationServiceElementWindowTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
        _svc.RootWindowOf = h =>
        {
            _rootQueries.Add(h);
            return _roots.TryGetValue(h, out var root) ? root : h;
        };
    }

    public void Dispose()
    {
        _svc.Dispose();
        _automation.Dispose();
    }

    private sealed class Fake
    {
        public required AutomationElement Element { get; init; }
        public required string Id { get; init; }
        public int HandleReads { get; set; }
    }

    private object NotSupported => _automation.NotSupportedValue;

    private ElementCache<AutomationElement> Cache => (ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(_svc)!;

    /// <param name="handle">What the NativeWindowHandle read answers; null = not supported.</param>
    /// <param name="processId">What the ProcessId read answers (0 = a destroyed Win32 control).</param>
    /// <param name="issue">Put the element in the service's cache (only the element asked about needs an id).</param>
    private Fake Arrange(string name, int controlType, int? handle, int processId = 4242, bool issue = false, bool handleThrows = false)
    {
        var lib = _automation.PropertyLibrary.Element;
        var framework = new Mock<FrameworkAutomationElementBase>(MockBehavior.Strict, _automation);
        Fake? fake = null;

        object Read(int id)
        {
            if (id == lib.NativeWindowHandle.Id)
            {
                fake!.HandleReads++;
                if (handleThrows) throw new COMException("handle read failed (wmcp-e5)", unchecked((int)0x80004005));
                return handle is { } h ? h : NotSupported;
            }
            if (id == lib.ProcessId.Id) return processId;
            if (id == lib.Name.Id) return name;
            if (id == lib.ControlType.Id) return controlType;
            if (id == lib.IsEnabled.Id) return true;
            return NotSupported;
        }

        framework.Protected()
            .Setup<object>("InternalGetPropertyValue", ItExpr.IsAny<int>(), ItExpr.IsAny<bool>(), ItExpr.IsAny<bool>())
            .Returns((int id, bool _, bool _) => Read(id));

        var element = new AutomationElement(framework.Object);
        fake = new Fake { Element = element, Id = issue ? Cache.Add(element) : "" };
        return fake;
    }

    /// <summary>child -> parent links by reference; anything unlisted has no parent (the walker's null).</summary>
    private void Parents(params (Fake Child, Fake Parent)[] links)
    {
        _svc.ParentOf = e =>
        {
            _parentCalls++;
            foreach (var (child, parent) in links)
                if (ReferenceEquals(child.Element, e)) return parent.Element;
            return null;
        };
    }

    /// <summary>The desktop root as UIA reports it: its handle is the real desktop window's.</summary>
    private Fake Desktop() => Arrange("Desktop 1", PaneType, (int)GetDesktopWindow());

    private async Task ShouldBeUndetermined(Fake fake)
    {
        Func<Task> act = () => _svc.GetElementWindowAsync(fake.Id).WaitAsync(TimeSpan.FromSeconds(20));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().ContainEquivalentOf("could not be determined").And.ContainEquivalentOf("window");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    // ---- the harness itself ----------------------------------------------------------------------

    [Fact]
    public void Harness_a_fake_answers_NativeWindowHandle_the_way_FlaUI_converts_it()
    {
        var window = Arrange("Untitled - Notepad", WindowType, WindowHandle);
        var none = Arrange("No handle", PaneType, null);

        window.Element.Properties.NativeWindowHandle.ValueOrDefault.Should().Be(new IntPtr(WindowHandle));
        none.Element.Properties.NativeWindowHandle.ValueOrDefault.Should().Be(IntPtr.Zero);
    }

    // ---- R1: nearest handle, then its root window ------------------------------------------------

    [Fact]
    public async Task An_element_with_a_handle_of_its_own_answers_the_root_window_of_that_handle()
    {
        // A Win32 button is an HWND: it is its own nearest handle, and GA_ROOT names its window.
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var pane = Arrange("Pane", PaneType, 0x3333);
        var button = Arrange("OK", ButtonType, 0x2222, issue: true);
        Parents((button, pane), (pane, window), (window, desktop));
        _roots[0x2222] = WindowHandle;

        var hwnd = await _svc.GetElementWindowAsync(button.Id);

        hwnd.Should().Be(WindowHandle);
        _rootQueries.Should().Equal([(nint)0x2222], "the element itself is the nearest element with a handle");
        _parentCalls.Should().Be(0, "nothing above it needs reading");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_element_without_a_handle_answers_the_root_of_its_nearest_ancestor_with_one()
    {
        // A WPF / XAML / Chromium control has no HWND of its own: its nearest HWND host is asked.
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var host = Arrange("Host pane", PaneType, 0x3333);
        var middle = Arrange("Group", PaneType, null);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        Parents((leaf, middle), (middle, host), (host, window), (window, desktop));
        _roots[0x3333] = WindowHandle;

        var hwnd = await _svc.GetElementWindowAsync(leaf.Id);

        hwnd.Should().Be(WindowHandle);
        _rootQueries.Should().Equal([(nint)0x3333], "the NEAREST handle, not the window's and not the desktop's");
    }

    [Fact]
    public async Task The_root_window_lookup_decides_so_an_owned_dialog_answers_itself_not_its_owner()
    {
        // GA_ROOT stops at an owned top-level window; the UIA walk is not continued to the owner.
        var desktop = Desktop();
        var dialog = Arrange("Find", WindowType, 0x5500);
        var button = Arrange("Find next", ButtonType, 0x5501, issue: true);
        Parents((button, dialog), (dialog, desktop));
        _roots[0x5501] = 0x5500;

        var hwnd = await _svc.GetElementWindowAsync(button.Id);

        hwnd.Should().Be(0x5500);
    }

    [Fact]
    public async Task A_top_level_window_answers_its_own_root()
    {
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle, issue: true);
        Parents((window, desktop));

        var hwnd = await _svc.GetElementWindowAsync(window.Id);

        hwnd.Should().Be(WindowHandle);
        _rootQueries.Should().Equal([(nint)WindowHandle]);
    }

    [Fact]
    public async Task A_deep_control_without_handles_walks_up_to_the_first_one()
    {
        var desktop = Desktop();
        var window = Arrange("Deep", WindowType, WindowHandle);
        var chain = Enumerable.Range(0, 12).Select(i => Arrange($"level {i}", PaneType, null)).ToList();
        var leaf = Arrange("Leaf", ButtonType, null, issue: true);
        var links = new List<(Fake, Fake)> { (leaf, chain[^1]) };
        for (int i = chain.Count - 1; i > 0; i--) links.Add((chain[i], chain[i - 1]));
        links.Add((chain[0], window));
        links.Add((window, desktop));
        Parents([.. links]);

        var hwnd = await _svc.GetElementWindowAsync(leaf.Id);

        hwnd.Should().Be(WindowHandle);
    }

    [Fact]
    public async Task An_orphan_with_a_handle_of_its_own_answers_its_root()
    {
        // Re-pinned (round 1 refused this): the element's own handle is enough, no parent needed.
        var orphan = Arrange("Orphan", ButtonType, 0x2222, issue: true);
        Parents();
        _roots[0x2222] = 0x2200;

        var hwnd = await _svc.GetElementWindowAsync(orphan.Id);

        hwnd.Should().Be(0x2200);
    }

    [Fact]
    public async Task A_handle_read_that_fails_is_no_handle_and_the_walk_goes_on_to_the_window()
    {
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var leaf = Arrange("OK", ButtonType, 0x2222, issue: true, handleThrows: true);
        Parents((leaf, window), (window, desktop));

        var hwnd = await _svc.GetElementWindowAsync(leaf.Id);

        hwnd.Should().Be(WindowHandle);
        _rootQueries.Should().Equal([(nint)WindowHandle]);
    }

    // ---- R1: no answer -----------------------------------------------------------------------------

    [Fact]
    public async Task No_element_with_a_handle_up_to_the_end_of_the_walk_is_refused()
    {
        var window = Arrange("Windowless host", WindowType, null);
        var zero = Arrange("Zero handle", PaneType, 0);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        Parents((leaf, zero), (zero, window));

        await ShouldBeUndetermined(leaf);
        _rootQueries.Should().BeEmpty("a handle of 0 is no handle, and there was no other");
    }

    [Fact]
    public async Task The_desktop_roots_own_handle_is_never_the_answer()
    {
        // Proposed (round 2, not in the coordinator's list): a window whose NativeWindowHandle read
        // fails leaves only the desktop root with a handle. Answering it would make observe:window
        // snapshot "the window" by the desktop's handle - the whole desktop.
        var desktop = Desktop();
        var window = Arrange("Handle read fails", WindowType, null);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        Parents((leaf, window), (window, desktop));

        await ShouldBeUndetermined(leaf);
    }

    [Fact]
    public async Task A_root_window_lookup_that_answers_zero_is_refused()
    {
        var button = Arrange("OK", ButtonType, 0x2222, issue: true);
        Parents();
        _roots[0x2222] = 0;

        await ShouldBeUndetermined(button);
    }

    [Fact]
    public async Task A_zero_root_for_the_elements_own_handle_is_the_refusal_and_the_walk_does_not_go_on_up()
    {
        // GREEN round 2 (settled): once the element (or the first qualifying ancestor) has a handle,
        // RootWindowOf decides. A 0 there is not "try the parent": the parent's root is another
        // window's answer, not this element's.
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var button = Arrange("OK", ButtonType, 0x2222, issue: true);
        Parents((button, window), (window, desktop));
        _roots[0x2222] = 0;

        await ShouldBeUndetermined(button);
        _rootQueries.Should().Equal([(nint)0x2222], "the window's handle is never asked about");
    }

    [Fact]
    public async Task A_root_lookup_that_throws_for_the_elements_own_handle_is_the_refusal_too()
    {
        // Settled: a throw other than Timeout/OCE from RootWindowOf is a 0 - and so the refusal.
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var button = Arrange("OK", ButtonType, 0x2222, issue: true);
        Parents((button, window), (window, desktop));
        _svc.RootWindowOf = h =>
        {
            _rootQueries.Add(h);
            return h == 0x2222 ? throw new InvalidOperationException("GetAncestor failed (wmcp-e5)") : h;
        };

        await ShouldBeUndetermined(button);
        _rootQueries.Should().Equal([(nint)0x2222]);
    }

    [Fact]
    public async Task A_zero_root_for_the_first_ancestor_with_a_handle_is_the_refusal_not_the_next_ancestor()
    {
        var desktop = Desktop();
        var window = Arrange("Save As", WindowType, WindowHandle);
        var host = Arrange("Host pane", PaneType, 0x3333);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        Parents((leaf, host), (host, window), (window, desktop));
        _roots[0x3333] = 0;

        await ShouldBeUndetermined(leaf);
        _rootQueries.Should().Equal([(nint)0x3333]);
    }

    [Fact]
    public async Task A_hung_root_window_lookup_propagates_as_a_timeout()
    {
        var button = Arrange("OK", ButtonType, 0x2222, issue: true);
        Parents();
        _svc.RootWindowOf = _ => throw new TimeoutException("GetAncestor hung (wmcp-e5)");

        Func<Task> act = () => _svc.GetElementWindowAsync(button.Id);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Be("GetAncestor hung (wmcp-e5)");
    }

    [Fact]
    public async Task An_ancestor_with_a_handle_whose_own_parent_cannot_be_read_is_not_answered()
    {
        // Characterisation (GREEN round 2): the desktop-root rule counts an ancestor only while it
        // has a parent of its own, and a failed parent read looks like "no parent". So the host
        // pane below, whose parent read fails, is treated like the root: refused, not answered.
        // Conservative on purpose - "cannot say" is never "this is the top".
        var host = Arrange("Host pane", PaneType, 0x3333);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        _svc.ParentOf = e => ReferenceEquals(e, leaf.Element) ? host.Element : throw new InvalidOperationException("walker failed (wmcp-e5)");

        await ShouldBeUndetermined(leaf);
        _rootQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parent_walk_that_fails_before_a_handle_is_found_is_refused_not_answered_with_what_was_reached()
    {
        // The walk failed above `middle`: the window above it is unknown, so there is no answer -
        // not "the last element reached" (round 1's bug, review F3).
        var window = Arrange("Save As", WindowType, WindowHandle);
        var middle = Arrange("Group", PaneType, null);
        var leaf = Arrange("OK", ButtonType, null, issue: true);
        _svc.ParentOf = e =>
        {
            _parentCalls++;
            if (ReferenceEquals(e, leaf.Element)) return middle.Element;
            if (ReferenceEquals(e, middle.Element)) throw new InvalidOperationException("walker failed (wmcp-e5)");
            return ReferenceEquals(e, window.Element) ? null : window.Element;
        };

        await ShouldBeUndetermined(leaf);
        _rootQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parent_walk_that_fails_at_the_first_step_is_no_parent_and_so_no_window()
    {
        // Re-pinned: the element has no handle of its own, so the walk is needed and fails at once.
        var button = Arrange("OK", ButtonType, null, issue: true);
        _svc.ParentOf = _ => throw new InvalidOperationException("walker failed (wmcp-e5)");

        await ShouldBeUndetermined(button);
    }

    [Fact]
    public async Task A_hung_provider_on_the_parent_walk_propagates_as_a_timeout()
    {
        var button = Arrange("OK", ButtonType, null, issue: true);
        _svc.ParentOf = _ => throw new TimeoutException("provider hung (wmcp-e5)");

        Func<Task> act = () => _svc.GetElementWindowAsync(button.Id);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Be("provider hung (wmcp-e5)");
    }

    [Fact]
    public async Task A_parent_chain_that_loops_without_a_handle_ends_in_a_refusal_not_a_hang()
    {
        var a = Arrange("A", PaneType, null, issue: true);
        var b = Arrange("B", PaneType, null);
        Parents((a, b), (b, a));

        await ShouldBeUndetermined(a);
    }

    // ---- the existing refusals (it runs inside Refusing) -------------------------------------------

    [Fact]
    public async Task An_id_that_was_never_issued_is_the_unknown_id_refusal()
    {
        Func<Task> act = () => _svc.GetElementWindowAsync("el_999999");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("Unknown element id 'el_999999'");
    }

    [Fact]
    public async Task An_expired_id_is_the_expired_refusal()
    {
        var gone = Cache.AddSnapshot(Arrange("Old row", ButtonType, null).Element);
        Cache.EvictSnapshot();

        Func<Task> act = () => _svc.GetElementWindowAsync(gone);

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().ContainEquivalentOf("expired");
    }

    [Fact]
    public async Task A_destroyed_element_is_the_no_longer_available_refusal()
    {
        var dead = Arrange("OK", ButtonType, null, processId: 0, issue: true);
        Parents();

        Func<Task> act = () => _svc.GetElementWindowAsync(dead.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{dead.Id}' is no longer available");
    }

    [Fact]
    public async Task A_destroyed_element_that_still_reports_a_handle_is_refused_not_given_its_root()
    {
        // A destroyed Win32 control's proxy can still answer a cached handle; liveness comes first.
        var dead = Arrange("OK", ButtonType, 0x2222, processId: 0, issue: true);
        Parents();
        _roots[0x2222] = WindowHandle;

        Func<Task> act = () => _svc.GetElementWindowAsync(dead.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{dead.Id}' is no longer available");
        _rootQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured_before_any_work()
    {
        var window = Arrange("Save As", WindowType, WindowHandle, issue: true);
        Parents();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _svc.GetElementWindowAsync(window.Id, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        window.HandleReads.Should().Be(0, "nothing is read for a call that was cancelled before it started");
        _rootQueries.Should().BeEmpty();
    }
}
