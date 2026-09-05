using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-1 (R3) through the <b>real</b> enumerator. <see cref="WindowFilterTests"/> proves the rules on
/// hand-written probes and would stay green if <c>EnumWindows</c> were never called, if the probe
/// were filled from the wrong Win32 call, or if the z-order were reversed — the failure mode
/// recorded in CLAUDE.md for <c>disk_inspect mode:reclaimable</c>. These tests assert the
/// invariants the real desktop must satisfy whatever happens to be open.
/// <para>
/// Read-only and headless-safe in the same bracket as <c>ScreenToolsMonitorInventoryTests</c>:
/// <c>EnumWindows</c>/<c>GetForegroundWindow</c> need a window station but no foreground app, no
/// input and no capture, so this is <c>Category=Integration</c>, not <c>UIAutomation</c>. Every
/// assertion is written as an invariant that holds whatever is open, with one deliberate
/// exception — <c>ListAsync_finds_the_windows_this_session_actually_has</c>, the non-vacuity
/// guard that requires an interactive session to have at least one window.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceTests
{
    private static bool HasPrivateUse(string s)
    {
        foreach (char c in s)
            if (c is >= '' and <= '') return true;
        return false;
    }

    [Fact]
    public async Task ListAsync_finds_the_windows_this_session_actually_has()
    {
        // Non-vacuity guard. Every other test in this class is written as an invariant
        // ("OnlyContain", "NotContain") so it cannot flake on whatever happens to be open — but
        // those are all trivially true on an empty array, so an enumerator that returned nothing
        // would keep them green. This is the one test that fails when EnumWindows is never called
        // or the filter drops everything.
        //
        // Environmental precondition, same bracket as ScreenToolsMonitorInventoryTests' "monitors
        // .Should().NotBeEmpty()": an interactive window station with at least one visible titled
        // top-level window — which is any desktop session, including the one that runs the suite
        // from a terminal. Under Session 0 / no interactive desktop there are no windows at all
        // and this fails; that is the environment, not a regression (cf. ClipboardServiceTests).
        var list = await new WindowService().ListAsync();

        list.Should().NotBeEmpty("the session running this test has windows open, so the inventory cannot be empty");
    }

    [Fact]
    public async Task ListAsync_returns_windows_whose_every_field_is_sane()
    {
        var list = await new WindowService().ListAsync();

        list.Should().NotBeNull();
        list.Should().OnlyContain(w => w.Title.Length > 0,
            "the default (include_hidden:false) drops untitled windows");
        list.Should().OnlyContain(w => w.Bounds.Width > 0 && w.Bounds.Height > 0,
            "zero-area windows are filtered out");
        list.Should().OnlyContain(w => w.Hwnd != 0);
        list.Should().OnlyContain(w => w.Pid > 0, "every real window belongs to a live process");
        list.Should().OnlyContain(w => w.DesktopId == null, "DesktopId is reserved for A-12");
        list.Select(w => w.Hwnd).Should().OnlyHaveUniqueItems("EnumWindows visits each window once");
    }

    [Fact]
    public async Task ListAsync_numbers_z_order_from_zero_in_the_order_returned()
    {
        var list = await new WindowService().ListAsync();

        list.Select(w => w.ZOrder).Should().Equal(Enumerable.Range(0, list.Length),
            "ZOrder is the position in the returned list, topmost first, with no gaps");
    }

    [Fact]
    public async Task ListAsync_flags_at_most_one_window_active()
    {
        var list = await new WindowService().ListAsync();

        list.Count(w => w.IsActive).Should().BeLessThanOrEqualTo(1,
            "there is one foreground window, and it may itself have been filtered out");
    }

    [Fact]
    public async Task ListAsync_reports_a_monitor_index_that_exists_in_the_real_inventory()
    {
        var service = new WindowService();
        var monitors = await service.EnumerateMonitorsAsync();
        var indices = monitors.Select(m => m.Index).ToArray();

        var list = await service.ListAsync();

        foreach (var w in list)
            (w.MonitorIndex == -1 || indices.Contains(w.MonitorIndex)).Should().BeTrue(
                "'{0}' reports monitor {1}, which is neither -1 (off-screen) nor one of {2}",
                w.Title, w.MonitorIndex, string.Join(",", indices));
    }

    [Fact]
    public async Task ListAsync_excludes_the_shell_chrome()
    {
        // The class name is not on the DTO, so the desktop window is identified by the only thing
        // the model would see: Progman's caption.
        //
        // MEASURED (2026-09-05, Win11 build 28000): this box's Progman does not reach the class
        // rule at all — with WindowFilter's ShellChromeClasses check deliberately disabled the
        // list still has no "Program Manager", because an earlier rule (visible/cloaked) already
        // dropped it. So on THIS shell the assertion is a backstop, not the proof; the rule's
        // proof is WindowFilterTests.Keep_drops_shell_chrome_by_class_name, which is exhaustive
        // and deterministic. Kept because it does bite on a shell that exposes Progman, and
        // because a chrome window appearing in the inventory is worth failing on wherever it
        // comes from.
        var list = await new WindowService().ListAsync(includeMinimized: true, includeHidden: true);

        list.Should().NotContain(w => w.Title == "Program Manager",
            "Progman/WorkerW/Shell_TrayWnd are chrome, not windows the agent can act on");
    }

    [Fact]
    public async Task ListAsync_titles_are_sanitised()
    {
        var list = await new WindowService().ListAsync();

        list.Should().OnlyContain(w => w.Title == w.Title.Trim(),
            "titles go through UiText.Sanitize, which trims");
        list.Should().NotContain(w => HasPrivateUse(w.Title),
            "Private Use glyphs are stripped before the model sees a title (A-13)");
    }

    [Fact]
    public async Task GetActiveAsync_agrees_with_the_entry_the_list_flags_active()
    {
        var service = new WindowService();

        var active = await service.GetActiveAsync();
        var list = await service.ListAsync();

        if (active is null)
            return; // no foreground window, or it was filtered out (the desktop) — allowed by R3.

        active.Title.Should().NotBeEmpty();
        active.IsActive.Should().BeTrue("the foreground window is by definition the active one");
        active.Bounds.Width.Should().BePositive();

        // Focus can move between the two calls, so the cross-check is conditional on the window
        // still being there — what must never happen is the list carrying it as NOT active.
        var same = list.FirstOrDefault(w => w.Hwnd == active.Hwnd);
        if (same is not null)
        {
            same.IsActive.Should().BeTrue("the same window cannot be the foreground one in one call and not the other");

            // GetActiveAsync is the list route, so every field on it is the inventory's field —
            // ZOrder above all. A GetActiveAsync that read GetForegroundWindow on its own would
            // have to invent a depth (0), and this is what would catch it.
            active.ZOrder.Should().Be(same.ZOrder,
                "the active window's depth is its position in the inventory, not a synthesised 0");
            active.ZOrder.Should().BeInRange(0, list.Length - 1);
            list[active.ZOrder].Hwnd.Should().Be(active.Hwnd, "ZOrder indexes the list it was numbered in");
            active.Should().BeEquivalentTo(same, o => o.Excluding(w => w.Bounds),
                "the two calls describe the same window (bounds excluded: it may have been moved between them)");
        }
    }

    [Fact]
    public async Task ListAsync_without_minimized_windows_is_a_subset_with_no_minimized_state()
    {
        var service = new WindowService();

        var full = await service.ListAsync(includeMinimized: true);
        var lean = await service.ListAsync(includeMinimized: false);

        lean.Should().OnlyContain(w => w.State != WindowState.Minimized);
        lean.Length.Should().BeLessThanOrEqualTo(full.Length);
        var minimized = full.Where(w => w.State == WindowState.Minimized).Select(w => w.Hwnd).ToArray();
        if (minimized.Length > 0)   // nothing minimized right now: the claim has no witness to make
            lean.Select(w => w.Hwnd).Should().NotIntersectWith(minimized,
                "a window the first call reported minimized must not come back when they are excluded");
        lean.Select(w => w.ZOrder).Should().Equal(Enumerable.Range(0, lean.Length),
            "z-order is renumbered over what survives the filter");
    }

    [Fact]
    public async Task ListAsync_with_hidden_windows_is_a_superset()
    {
        var service = new WindowService();

        var visible = await service.ListAsync(includeHidden: false);
        var withHidden = await service.ListAsync(includeHidden: true);

        withHidden.Length.Should().BeGreaterThanOrEqualTo(visible.Length,
            "include_hidden only ever adds untitled windows");
    }

    // ---- the acting path, through the real service ------------------------------------------
    // WindowTools' minimize/maximize/restore/close route reaches ExecuteAsync, which every tool
    // test mocks away. These two are the non-mocked sibling: read-only, because FindWindow on a
    // title that cannot exist returns null and no window is ever touched.

    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    [InlineData("close")]
    public async Task ExecuteAsync_on_a_title_no_window_has_reports_not_found_and_touches_nothing(string action)
    {
        var title = "wmcp-window-" + Guid.NewGuid().ToString("N");

        var result = await new WindowService().ExecuteAsync(action, title);

        result.Success.Should().BeFalse("no window has that title, so nothing was acted on");
        result.Action.Should().Be(action, "the response echoes the action the caller sent");
        result.Title.Should().Be(title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_without_a_title_throws_naming_the_argument(string? title)
    {
        var act = () => new WindowService().ExecuteAsync("minimize", title);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("title");
    }

    [Fact]
    public async Task ListAsync_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => new WindowService().ListAsync(true, false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetActiveAsync_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => new WindowService().GetActiveAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
