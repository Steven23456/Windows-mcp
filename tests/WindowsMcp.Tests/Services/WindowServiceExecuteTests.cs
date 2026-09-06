using System.Runtime.InteropServices;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
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
public class WindowServiceExecuteTests
{
    /// <summary>
    /// A real top-level window owned by this process: created on its own message-pumping thread
    /// (cross-thread <c>GetWindowText</c> and <c>PostMessage</c> both need one) from the built-in
    /// STATIC class, so no window class has to be registered and DefWindowProc handles WM_CLOSE.
    /// </summary>
    private sealed class OwnedWindow : IDisposable
    {
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint WM_CLOSE = 0x0010;
        private static readonly IntPtr HWND_BOTTOM = new(1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly ManualResetEventSlim _ready = new(false);

        public IntPtr Handle { get; private set; }
        public string Title { get; }

        public OwnedWindow(string title)
        {
            Title = title;
            var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-owned-window" };
            thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(10));
        }

        public bool Exists => Handle != IntPtr.Zero && IsWindow(Handle);

        private void Pump()
        {
            Handle = CreateWindowExW(0, "STATIC", Title, WS_OVERLAPPEDWINDOW,
                120, 120, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle != IntPtr.Zero)
            {
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
                // Bottom of the z-order, never activated: parallel test classes that walk "the
                // first window on the desktop" (UIAutomationDomSnapshotTests) must not pick a
                // window that is about to be destroyed.
                SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            _ready.Set();

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        public void Dispose()
        {
            if (Exists)
            {
                // DestroyWindow only works from the owning thread; WM_CLOSE reaches it through
                // the pump and DefWindowProc destroys the window for us.
                PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20 && Exists; i++) Thread.Sleep(50);
                if (Exists) DestroyWindow(Handle);   // last resort; fails cross-thread, but harmless
            }
            _ready.Dispose();
        }
    }

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

    [Fact]
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
