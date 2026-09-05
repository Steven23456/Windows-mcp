using System.Runtime.InteropServices;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Tests that INJECT input (click, type, move the pointer) carry Category=UIAutomation: they act on
// whatever window has focus, so a headless or background run must never execute them. The
// read-only ones (a cursor read, argument validation) stay Integration/Unit.
// The class moves the real pointer and asserts where it landed, so it is serialised against every
// other pointer/pixel class (see PointerAndPixelCollection).
[Collection(PointerAndPixelCollection.Name)]
public class InputServiceTests
{
    // Recategorized to Integration: SendInput / SetCursorPos fail under the test runner (UIPI
    // elevation mismatch). The service logic is correct; a real desktop session is required.
    [Fact]
    [Trait("Category", "UIAutomation")]
    public async Task ClickAsync_returns_result_with_correct_coordinates_and_button()
    {
        var service = new InputService();
        var result = await service.ClickAsync(100, 200, MouseButton.Left);
        result.Should().BeEquivalentTo(new ClickResult(100, 200, MouseButton.Left, 1));
    }

    [Fact]
    [Trait("Category", "UIAutomation")]
    public async Task TypeAsync_reports_character_count_for_unicode_input()
    {
        var service = new InputService();
        var result = await service.TypeAsync("héllo");
        result.CharsTyped.Should().Be(5);
    }

    // D-3: the cursor must land exactly where asked on EVERY monitor, including ones left of /
    // above the primary (negative coordinates). On a one-monitor box this still checks the primary.
    [Fact]
    [Trait("Category", "UIAutomation")]
    public async Task HoverAsync_lands_exactly_on_every_monitor()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        monitors.Should().NotBeEmpty();
        var service = new InputService();

        foreach (var m in monitors)
        {
            int cx = m.X + m.Width / 2;
            int cy = m.Y + m.Height / 2;

            await service.HoverAsync(cx, cy);

            GetCursorPos(out var p).Should().BeTrue();
            (p.X, p.Y).Should().Be((cx, cy), $"monitor {m.Index} ({m.DeviceName}) centre");
        }
    }

    // D-3: SetCursorPos silently clamps an off-screen point to the nearest edge; the service must
    // notice and fail loudly rather than click somewhere else.
    [Fact]
    [Trait("Category", "UIAutomation")]
    public async Task HoverAsync_rejects_a_point_outside_the_virtual_screen()
    {
        var service = new InputService();
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top  = GetSystemMetrics(SM_YVIRTUALSCREEN);

        Func<Task> act = () => service.HoverAsync(left - 1000, top - 1000);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithMessage("*landed at*");
    }

    // D-1: a bad token must be named in the error.
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PressShortcutAsync_names_the_offending_token()
    {
        var service = new InputService();
        Func<Task> act = () => service.PressShortcutAsync("not+a+real+key");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*'not'*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TypeAsync_throws_when_cancellation_already_requested()
    {
        var service = new InputService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => service.TypeAsync("hello", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- A-11 (R1) — the cursor position the screenshot metadata reports --------------------

    /// <summary>
    /// The live read, through the real <c>GetCursorPos</c>: every tool test mocks
    /// <see cref="IInputService"/>, so without this nothing proves the service returns the actual
    /// cursor rather than a constant (the <c>disk_inspect mode:reclaimable</c> failure mode in
    /// CLAUDE.md). Read-only — it moves nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetCursorPositionAsync_returns_a_point_inside_the_virtual_screen()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        monitors.Should().NotBeEmpty();
        var virtualScreen = RegionMath.VirtualScreen(monitors);

        var cursor = await new InputService().GetCursorPositionAsync();

        cursor.X.Should().BeInRange(virtualScreen.X, virtualScreen.X + virtualScreen.Width - 1);
        cursor.Y.Should().BeInRange(virtualScreen.Y, virtualScreen.Y + virtualScreen.Height - 1);
    }

    /// <summary>
    /// The same coordinate space in and out (roadmap C1): what <c>HoverAsync</c> takes is what
    /// <c>GetCursorPositionAsync</c> reports — the property the screenshot metadata rests on.
    /// </summary>
    [Fact]
    [Trait("Category", "UIAutomation")]
    public async Task GetCursorPositionAsync_reports_where_the_cursor_was_just_moved()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        var primary = RegionMath.Primary(monitors);
        int x = primary.X + primary.Width / 3;
        int y = primary.Y + primary.Height / 3;
        var service = new InputService();

        await service.HoverAsync(x, y);
        var cursor = await service.GetCursorPositionAsync();

        (cursor.X, cursor.Y).Should().Be((x, y));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCursorPositionAsync_throws_when_cancellation_already_requested()
    {
        // The convention every other InputService method follows: check the token before the API.
        var service = new InputService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.GetCursorPositionAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
