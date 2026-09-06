using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-9 through the <b>real</b> user32, against a top-level window <b>this test process owns</b>.
/// <see cref="WindowGeometryTests"/> proves the flags and the refusals on a fake and would stay
/// green if <c>SetWindowPos</c> were never called or the wrong handle were passed;
/// <c>WindowToolsBoundsTests</c> mocks the service away entirely. This is the class that fails
/// when the move does not happen, when the re-read reports the request instead of the window, or
/// when the flags are composed the wrong way round.
/// <para>
/// <c>Category=Integration</c>, not <c>UIAutomation</c>, for the same reason
/// <c>WindowServiceExecuteTests</c> is: the window is created with <c>SW_SHOWNOACTIVATE</c> at
/// the bottom of the z-order, never takes the foreground, receives no injected input, and is
/// destroyed when the test finishes. Nothing the user has open is moved.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceBoundsTests
{
    private static string Marker() => "wmcp-bounds-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<WindowInfo> WaitForWindow(WindowService svc, IntPtr handle)
    {
        for (int i = 0; i < 40; i++)
        {
            var listed = (await svc.ListAsync(includeMinimized: true)).FirstOrDefault(w => w.Hwnd == handle);
            if (listed is not null) return listed;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("the window this test created never appeared in the inventory");
    }

    [Fact]
    public async Task SetBoundsAsync_moves_and_resizes_the_window_the_title_matched()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        var listed = await WaitForWindow(svc, window.Handle);

        var result = await svc.SetBoundsAsync(marker, null, 220, 180, 640, 480, false);

        result.Window.Hwnd.Should().Be(window.Handle.ToInt64(), "the matched window, not the caller's guess");
        result.Window.Title.Should().Be(listed.Title);
        result.MatchStrategy.Should().Be("substring");
        result.Score.Should().Be(100);
        result.Restored.Should().BeFalse();
        result.After.Should().Be(new Bounds(220, 180, 640, 480),
            "SetWindowPos and GetWindowRect share one coordinate space, so the rect comes back exactly");
        result.Before.Should().NotBe(result.After, "the window really was somewhere else before");

        // And the inventory - the same GetWindowRect A-1 reads - agrees, which is the "done when".
        var relisted = (await svc.ListAsync()).Single(w => w.Hwnd == window.Handle.ToInt64());
        relisted.Bounds.Should().Be(new Bounds(220, 180, 640, 480));
    }

    [Fact]
    public async Task SetBoundsAsync_move_keeps_the_size()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);
        var start = await svc.SetBoundsAsync(marker, null, 200, 200, 500, 400, false);

        var moved = await svc.SetBoundsAsync(marker, null, 320, 260, null, null, false);

        (moved.After.X, moved.After.Y).Should().Be((320, 260));
        (moved.After.Width, moved.After.Height).Should().Be((start.After.Width, start.After.Height),
            "'move' is SWP_NOSIZE: the size is not the caller's business here");
    }

    [Fact]
    public async Task SetBoundsAsync_resize_keeps_the_position()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);
        var start = await svc.SetBoundsAsync(marker, null, 200, 200, 500, 400, false);

        var resized = await svc.SetBoundsAsync(marker, null, null, null, 700, 520, false);

        (resized.After.Width, resized.After.Height).Should().Be((700, 520));
        (resized.After.X, resized.After.Y).Should().Be((start.After.X, start.After.Y),
            "'resize' is SWP_NOMOVE");
    }

    [Fact]
    public async Task SetBoundsAsync_by_hwnd_acts_on_that_window_even_when_the_title_says_otherwise()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);

        var result = await svc.SetBoundsAsync(
            "wmcp-not-this-window", window.Handle.ToInt64(), 240, 190, 560, 420, false);

        result.MatchStrategy.Should().Be("hwnd");
        result.Window.Hwnd.Should().Be(window.Handle.ToInt64());
        result.After.Should().Be(new Bounds(240, 190, 560, 420));
    }

    [Fact]
    public async Task SetBoundsAsync_does_not_take_the_foreground()
    {
        // SWP_NOACTIVATE, for real: an agent tidying windows in the background must not have the
        // user's focus stolen out from under them.
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);

        await svc.SetBoundsAsync(marker, null, 260, 210, 520, 400, false);

        var listed = (await svc.ListAsync()).Single(w => w.Hwnd == window.Handle.ToInt64());
        listed.IsActive.Should().BeFalse("the moved window was never activated");
    }

    [Fact]
    public async Task SetBoundsAsync_refuses_a_minimized_window_and_names_its_state()
    {
        // The live IsIconic reading, on a real window - WindowGeometryTests only proves the
        // refusal against a fake. The restore_first half is deliberately NOT here: SW_RESTORE on
        // a minimized window TAKES THE FOREGROUND, and this class promises it never does that
        // (parallel classes read "the foreground window" and would fail on a bare STATIC window).
        // That half is WindowGeometryTests' on the fake and WindowBoundsDesktopTests' for real.
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);
        await svc.ExecuteAsync("minimize", marker);
        for (int i = 0; i < 40; i++)
        {
            var state = (await svc.ListAsync()).FirstOrDefault(w => w.Hwnd == window.Handle.ToInt64())?.State;
            if (state == WindowState.Minimized) break;
            await Task.Delay(50);
        }

        var refused = () => svc.SetBoundsAsync(marker, null, 280, 220, 600, 460, false);

        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("Minimized").And.Contain("restore_first",
                "the refusal names the state it read from the window and the way past it");
        (await svc.ListAsync()).Single(w => w.Hwnd == window.Handle.ToInt64())
            .State.Should().Be(WindowState.Minimized, "the refused call left the window as it found it");
    }

    [Fact]
    public async Task SetBoundsAsync_refuses_a_call_that_asks_for_nothing()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        var before = await WaitForWindow(svc, window.Handle);

        var act = () => svc.SetBoundsAsync(marker, null, null, null, null, null, false);

        await act.Should().ThrowAsync<ArgumentException>();
        var after = (await svc.ListAsync()).Single(w => w.Hwnd == window.Handle.ToInt64());
        after.Bounds.Should().Be(before.Bounds, "a refused call moves nothing");
    }

    [Fact]
    public async Task SetBoundsAsync_reports_a_title_that_matches_nothing_as_a_key_not_found()
    {
        // The B-10 contract, inherited: a miss lists the open windows rather than answering false.
        var svc = new WindowService();

        var act = () => svc.SetBoundsAsync("wmcp-no-such-window-" + Guid.NewGuid().ToString("N"), null, 10, 10, null, null, false);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task SetBoundsAsync_reports_an_hwnd_that_no_longer_exists()
    {
        var svc = new WindowService();

        var act = () => svc.SetBoundsAsync(null, 0x7FFFFFF0L, 10, 10, null, null, false);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- the foreground default, without moving anything the user has open -----------------------

    /// <summary>
    /// Records what user32 was asked to do and answers <c>GetRect</c> from a fixed pair, so the
    /// service's <b>targeting</b> can be driven against the real inventory while nothing on this
    /// desktop moves. Only the seven lines of <c>SetBoundsAsync</c> that choose a window are
    /// under test here; the move itself is the rest of this class.
    /// </summary>
    private sealed class RecordingGeometry : IWindowGeometryNative
    {
        public long? MovedHwnd { get; private set; }
        public bool IsIconic(long hwnd) => false;
        public bool IsZoomed(long hwnd) => false;
        public bool Restore(long hwnd) => true;
        public bool SetWindowPos(long hwnd, int x, int y, int width, int height, uint flags)
        {
            MovedHwnd = hwnd;
            return true;
        }
        public Bounds GetRect(long hwnd) => new(10, 20, 300, 200);
    }

    [Fact]
    public async Task SetBoundsAsync_with_neither_a_title_nor_an_hwnd_targets_the_foreground_window()
    {
        // Upstream's "name? or the active window". The foreground read is real - only user32's
        // move is faked - so this fails if the default target were the first listed window, the
        // last one, or a throw. WindowBoundsDesktopTests does the same thing for real.
        var geometry = new RecordingGeometry();
        var svc = new WindowService(null, null, null, null, geometry);

        var result = await svc.SetBoundsAsync(null, null, 100, 120, 800, 600, false);

        result.MatchStrategy.Should().Be("foreground", "no title and no hwnd names the active window");
        result.Score.Should().Be(100);
        result.Window.IsActive.Should().BeTrue("the entry the inventory flags active is the one that was taken");
        geometry.MovedHwnd.Should().Be(result.Window.Hwnd, "user32 was pointed at the window the result reports");
    }

    [Fact]
    public async Task SetBoundsAsync_honours_a_cancelled_token_before_it_reads_or_moves_anything()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var geometry = new RecordingGeometry();
        var svc = new WindowService(null, null, null, null, geometry);

        var act = () => svc.SetBoundsAsync(null, null, 100, 120, 800, 600, false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        geometry.MovedHwnd.Should().BeNull("a cancelled call must not end with a window somewhere new");
    }

    [Fact]
    public void The_user32_adapter_reports_a_handle_no_window_owns_as_a_miss()
    {
        // Win32WindowGeometryNative.GetRect is the only place a window destroyed between the
        // match and the move is noticed; GetWindowRect returning false has to become the same
        // KeyNotFoundException a stale hwnd gets, not a rect of zeros.
        var act = () => Win32WindowGeometryNative.Instance.GetRect(0x7FFFFFF0L);

        act.Should().Throw<KeyNotFoundException>().Which.Message.Should().Contain("no longer exists");
    }
}
