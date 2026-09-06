using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10's acting half — <c>ExecuteAsync</c> after the matcher has chosen a window — against a
/// real top-level window <b>this test process owns</b>. <see cref="WindowMatcherTests"/> proves
/// which window would be chosen and would stay green if <c>ShowWindow</c> and the WM_CLOSE were
/// deleted; <c>WindowToolsTests</c> mocks the service away entirely. This is the class that fails
/// when the four arms of the switch do nothing, when the response reports the string the caller
/// sent instead of the window that matched, or when the wrong handle is acted on.
/// <para>
/// <c>Category=Integration</c>, not <c>UIAutomation</c>: the window is created with
/// <c>SW_SHOWNOACTIVATE</c>, so it never takes the foreground, never receives injected input, and
/// nothing the user has open is touched. It is destroyed when the test finishes.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public class WindowServiceExecuteTests
{
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

    private static async Task<WindowInfo?> WaitForState(WindowService svc, IntPtr handle, WindowState state)
    {
        WindowInfo? listed = null;
        for (int i = 0; i < 40; i++)
        {
            listed = (await svc.ListAsync(includeMinimized: true)).FirstOrDefault(w => w.Hwnd == handle);
            if (listed?.State == state) return listed;
            await Task.Delay(50);
        }
        return listed;
    }

    /// <summary>A title no other window can have, and a request that is only part of it.</summary>
    private static string Marker() => "wmcp-owned-" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("minimize", WindowState.Minimized)]
    [InlineData("maximize", WindowState.Maximized)]
    public async Task ExecuteAsync_acts_on_the_window_the_title_matched(string action, WindowState expected)
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        window.Exists.Should().BeTrue("the test's own window has to exist before it can be acted on");
        var svc = new WindowService();
        var listed = await WaitForWindow(svc, window.Handle);

        var result = await svc.ExecuteAsync(action, marker);

        result.Success.Should().BeTrue();
        result.Action.Should().Be(action);
        result.Hwnd.Should().Be(window.Handle.ToInt64(), "the matched window's handle, not the caller's guess");
        result.Title.Should().Be(listed.Title,
            "B-10 reports the window that was acted on - the request was only part of that title");
        result.Title.Should().NotBe(marker, "the response must not echo the request back as the title");
        result.MatchStrategy.Should().Be("substring");
        result.Score.Should().Be(100);

        (await WaitForState(svc, window.Handle, expected))!.State.Should().Be(expected,
            $"'{action}' has to actually move the window, not just report that it did");
    }

    // SW_RESTORE on a minimized window TAKES THE FOREGROUND, so every parallel headless test that
    // reads "the foreground window" saw this STATIC window for an instant (the intermittent
    // failures across VirtualDesktop, snapshot and GetActive tests). Desktop bracket only.
    [Fact, Trait("Category", "UIAutomation")]
    public async Task ExecuteAsync_restore_brings_a_minimized_window_back()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        await WaitForWindow(svc, window.Handle);
        await svc.ExecuteAsync("minimize", marker);
        (await WaitForState(svc, window.Handle, WindowState.Minimized))!.State
            .Should().Be(WindowState.Minimized, "the arrangement has to have taken effect");

        var result = await svc.ExecuteAsync("restore", marker);

        result.Success.Should().BeTrue();
        (await WaitForState(svc, window.Handle, WindowState.Normal))!.State.Should().Be(WindowState.Normal,
            "SW_RESTORE un-minimizes the window it was sent to");
    }

    [Fact]
    public async Task ExecuteAsync_close_posts_WM_CLOSE_to_the_matched_window()
    {
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        var listed = await WaitForWindow(svc, window.Handle);

        var result = await svc.ExecuteAsync("close", marker);

        result.Success.Should().BeTrue();
        result.Hwnd.Should().Be(window.Handle.ToInt64());
        result.Title.Should().Be(listed.Title);
        for (int i = 0; i < 40 && window.Exists; i++) await Task.Delay(50);
        window.Exists.Should().BeFalse(
            "close is a real WM_CLOSE to the matched window, not a minimize and not a no-op");
    }

    [Fact]
    public async Task ExecuteAsync_by_hwnd_acts_on_that_window_even_when_the_title_says_otherwise()
    {
        // The precedence rule where it matters most: a caller with a handle gets that handle's
        // window, whatever a stale title argument says.
        var marker = Marker();
        using var window = new OwnedWindow(marker + " test window");
        var svc = new WindowService();
        var listed = await WaitForWindow(svc, window.Handle);

        var result = await svc.ExecuteAsync("minimize", "wmcp-not-this-window", window.Handle.ToInt64());

        result.MatchStrategy.Should().Be("hwnd");
        result.Hwnd.Should().Be(window.Handle.ToInt64());
        result.Title.Should().Be(listed.Title);
        (await WaitForState(svc, window.Handle, WindowState.Minimized))!.State.Should().Be(WindowState.Minimized);
    }
}
