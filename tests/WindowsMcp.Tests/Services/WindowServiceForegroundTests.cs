using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10 through the <b>real</b> service: the matcher runs over the real
/// <c>EnumWindows</c> inventory and the ladder runs over real user32 calls.
/// <see cref="ForegroundLadderTests"/> proves the ladder's logic against a fake and would stay
/// green if user32 were never called at all — the failure mode CLAUDE.md records for
/// <c>disk_inspect mode:reclaimable</c>. This is the class that fails when the seam is wired to
/// nothing.
/// <para>
/// Headless-safe and <c>Category=Integration</c>, not <c>UIAutomation</c>, because every test
/// here targets <b>the window that is already in front</b>: bringing the foreground window to the
/// foreground changes nothing on screen, steals no focus from a parallel test class, and injects
/// no input. The tests that actually move a window forward live in
/// <c>WindowForegroundDesktopTests</c>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceForegroundTests
{
    /// <summary>
    /// Reads the foreground window and climbs the ladder at it inside one short retry loop.
    /// Both halves have to hold at the same moment, and a parallel test class spawning a console
    /// (<c>ProcessServiceStartSpecIntegrationTests</c> starts cmd.exe) owns the foreground for a
    /// few milliseconds while it does — which makes "bring the window that is already in front
    /// forward" briefly untrue through no fault of the code. The loop re-reads instead of
    /// asserting on a stale pair; after the last attempt it returns what it saw so the caller's
    /// own assertions produce the failure message.
    /// </summary>
    private static async Task<(WindowInfo Active, ForegroundResult Result)> ForegroundForward(
        WindowService svc,
        Func<WindowInfo, Task<ForegroundResult>> bring,
        Func<WindowInfo, ForegroundResult, bool>? settled = null)
    {
        settled ??= (active, result) => result.Success && result.Window.Hwnd == active.Hwnd;

        WindowInfo? lastActive = null;
        ForegroundResult? lastResult = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var active = await svc.GetActiveAsync();
            active.Should().NotBeNull("this session has a foreground window");
            var result = await bring(active!);
            if (settled(active!, result)) return (active!, result);
            (lastActive, lastResult) = (active, result);
        }
        return (lastActive!, lastResult!);
    }

    [Fact]
    public async Task BringToFrontAsync_by_hwnd_on_the_foreground_window_reports_success_at_the_first_rung()
    {
        // Environmental precondition, the same one WindowServiceTests' non-vacuity guard needs:
        // an interactive window station with a foreground window.
        var svc = new WindowService();

        var (active, result) = await ForegroundForward(svc, a => svc.BringToFrontAsync(null, a.Hwnd),
            (a, r) => r.Success && r.Window.Hwnd == a.Hwnd && r.Strategy == "SetForegroundWindow");

        result.Window.Hwnd.Should().Be(active.Hwnd);
        result.MatchStrategy.Should().Be("hwnd");
        result.Score.Should().Be(100);
        result.Restored.Should().BeFalse("the foreground window is not minimized");
        result.Success.Should().BeTrue("GetForegroundWindow must agree that our target is in front");
        result.Strategy.Should().Be("SetForegroundWindow",
            "SetForegroundWindow succeeds for the process that already owns the foreground, so the "
            + "ladder must stop at the first rung rather than nudging ALT on a desktop it did not need to");
    }

    [Fact]
    public async Task BringToFrontAsync_by_exact_title_finds_the_same_window_the_inventory_reports()
    {
        var svc = new WindowService();

        var (active, result) = await ForegroundForward(svc, a => svc.BringToFrontAsync(a.Title, null));

        result.Window.Hwnd.Should().Be(active.Hwnd,
            "the real inventory is what the matcher matched over, not a second FindWindow call");
        result.MatchStrategy.Should().BeOneOf("exact", "substring",
            "an exact title can also be a substring of another window's title; either way it is not fuzzy");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task BringToFrontAsync_lists_the_open_windows_when_nothing_matches()
    {
        var svc = new WindowService();

        var act = () => svc.BringToFrontAsync("no window is called this — zzqqxx", null);

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().StartWith("No top-level window matching ");
        message.Should().Contain("Open windows: ");
    }

    [Fact]
    public async Task BringToFrontAsync_refuses_a_call_that_names_no_window()
    {
        var svc = new WindowService();

        var act = () => svc.BringToFrontAsync(null, null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("title").And.Contain("hwnd");
    }

    [Fact]
    public async Task BringToFrontAsync_honours_a_cancelled_token()
    {
        // Checked before the inventory read and before any user32 call: a cancelled request must
        // not end with the desktop rearranged.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var svc = new WindowService();
        var active = await svc.GetActiveAsync();

        var act = () => svc.BringToFrontAsync(null, active!.Hwnd, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BringToFrontAsync_refuses_a_handle_that_is_not_a_listed_window()
    {
        // A stale hwnd from an earlier window list is the common case; it must not silently
        // become "the foreground window" or a success.
        var svc = new WindowService();

        var act = () => svc.BringToFrontAsync(null, 0x7FFFFFFF);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task BringToFrontAsync_reports_the_matched_window_from_the_live_inventory()
    {
        // The result's Window is an inventory entry, not a hand-built stub: its ZOrder, State and
        // ProcessName have to be the ones ListAsync reported for that handle.
        var svc = new WindowService();

        var (active, result) = await ForegroundForward(svc, a => svc.BringToFrontAsync(null, a.Hwnd));

        // Field by field rather than record equality: ZOrder can legitimately shift between two
        // enumerations on a live desktop, the identity of the window cannot.
        result.Window.Hwnd.Should().Be(active.Hwnd);
        result.Window.Pid.Should().Be(active.Pid);
        result.Window.ProcessName.Should().Be(active.ProcessName);
        result.Window.IsActive.Should().BeTrue("the window we brought forward is the foreground one");
        result.Window.Bounds.Width.Should().BeGreaterThan(0, "this is a real inventory entry, not a stub");
    }
}
