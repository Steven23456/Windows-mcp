using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-7 through the <b>real</b> A-1 window inventory and the real monitor inventory. Every
/// <c>ScreenToolsTargetTests</c> row hands the tool a hand-written <c>ListAsync</c>; all of them
/// would stay green if the real inventory reported bounds in another space than the virtual desktop
/// the capture takes, or if the tool asked it for a different window set (the
/// <c>disk_inspect mode:reclaimable</c> lesson in CLAUDE.md).
/// <para>
/// Read-only and headless-safe in the same sense as <see cref="ScreenToolsMonitorInventoryTests"/>:
/// <c>EnumWindows</c>/<c>GetWindowRect</c>/<c>EnumDisplayMonitors</c> need a desktop session but no
/// foreground window, no capture and no input. <see cref="IScreenshotService"/> stays mocked — the
/// real capture is UIAutomation-category.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ScreenToolsTargetIntegrationTests
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static (Mock<IScreenshotService> Shot, ScreenTools Tools) Make(IWindowService windows)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var shot = new Mock<IScreenshotService>();
        shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 100, 100, ImageFormat.Png, 100, 100, 1.0));
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(0, 0));
        var tools = new ScreenTools(shot.Object, new Mock<IOcrService>().Object, windows, input.Object,
            new Mock<IUIAutomationService>(MockBehavior.Strict).Object, new Mock<IFlashOverlay>().Object,
            new ScreenshotOptions(1.0, Flash: false));
        return (shot, tools);
    }

    /// <summary>The window's rect intersected with Windows' own idea of the virtual screen, computed here, not by RegionMath.</summary>
    private static ScreenRegion? ExpectedClip(Bounds b)
    {
        long vl = GetSystemMetrics(SM_XVIRTUALSCREEN), vt = GetSystemMetrics(SM_YVIRTUALSCREEN);
        long vr = vl + GetSystemMetrics(SM_CXVIRTUALSCREEN), vb = vt + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        long l = Math.Max(b.X, vl), t = Math.Max(b.Y, vt);
        long r = Math.Min((long)b.X + b.Width, vr), btm = Math.Min((long)b.Y + b.Height, vb);
        return r > l && btm > t ? new ScreenRegion((int)l, (int)t, (int)(r - l), (int)(btm - t)) : null;
    }

    [Fact]
    public async Task An_hwnd_from_the_real_inventory_captures_its_real_bounds_clipped_to_the_real_virtual_screen()
    {
        var windows = new WindowService();
        var inventory = await windows.ListAsync(includeMinimized: true, includeHidden: false);
        var candidate = inventory.FirstOrDefault(w => w.State != WindowState.Minimized && ExpectedClip(w.Bounds) is not null);
        if (candidate is null)
            return;   // a session with no on-screen top-level window (a bare service desktop): nothing to target
        var (shot, tools) = Make(windows);

        await tools.Screenshot(format: "png", output: "inline", hwnd: candidate.Hwnd);

        // The window may have moved between the two reads; a still desktop is the norm in a test run.
        shot.Verify(s => s.CaptureAsync(ExpectedClip(candidate.Bounds), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, $"'{candidate.Title}' reports {candidate.Bounds} and the capture must be that rect on the virtual screen");
    }

    [Fact]
    public async Task A_minimised_window_in_the_real_inventory_is_refused_by_name()
    {
        var windows = new WindowService();
        var inventory = await windows.ListAsync(includeMinimized: true, includeHidden: false);
        var minimised = inventory.FirstOrDefault(w => w.State == WindowState.Minimized && w.Title.Length > 0);
        if (minimised is null)
            return;   // nothing minimised on this desk; ScreenToolsTargetTests covers the rule itself
        var (shot, tools) = Make(windows);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline", hwnd: minimised.Hwnd);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(minimised.Title);
        shot.Invocations.Should().BeEmpty();
    }
}

/// <summary>
/// E-7 GREEN: the window and element targets through the REAL window inventory and the REAL UI
/// Automation element cache, against a Win32 window this process owns
/// (<see cref="OwnedControlsWindow"/>: shown without activation at the bottom of the z-order, never
/// clicked or typed into). The class above depends on whatever happens to be open on the desk (its
/// minimised row is vacuous when nothing is minimised); this one brings its own window, so every row
/// runs everywhere a desktop session exists. Every Unit row mocks <c>GetElementAsync</c>; the
/// element row here is the only proof that a real id from a real find resolves to the rect the
/// capture is given. The capture itself stays mocked (a real GDI capture is UIAutomation-category).
/// <para>
/// The minimised row minimises the fixture's OWN window with <c>SW_SHOWMINNOACTIVE</c> (no
/// activation, no input) and the fixture closes it afterwards. In <see cref="DesktopCollection"/>
/// because fixture windows appear and close, like every other OwnedControlsWindow class.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class ScreenToolsTargetOwnedWindowIntegrationTests : IDisposable
{
    private const int SW_SHOWMINNOACTIVE = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private readonly OwnedControlsWindow _window = new();
    private readonly WindowService _windows = new();
    private readonly UIAutomationService _uia;
    private readonly Mock<IScreenshotService> _shot = new();
    private readonly ScreenTools _tools;

    public ScreenToolsTargetOwnedWindowIntegrationTests()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        _shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 100, 100, ImageFormat.Png, 100, 100, 1.0));
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(0, 0));
        _uia = new UIAutomationService(input.Object, _windows);
        _tools = new ScreenTools(_shot.Object, new Mock<IOcrService>().Object, _windows, input.Object, _uia,
            new Mock<IFlashOverlay>().Object, new ScreenshotOptions(1.0, Flash: false));
    }

    public void Dispose()
    {
        _uia.Dispose();
        _window.Dispose();
    }

    private ScreenRegion CapturedRect()
    {
        var call = _shot.Invocations.Should().ContainSingle().Subject;
        return call.Arguments[0].Should().BeOfType<ScreenRegion>().Subject;
    }

    private Bounds WindowRect()
    {
        GetWindowRect(_window.Handle, out var r).Should().BeTrue("precondition: the fixture window exists");
        return new Bounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [Fact]
    public async Task A_title_matches_the_owned_window_exactly_and_captures_its_GetWindowRect()
    {
        _window.Exists.Should().BeTrue("precondition");
        var expected = WindowRect();   // 520x260 at (140,140): on screen on any desk

        var result = await _tools.Screenshot(format: "png", output: "inline", window: _window.Title);

        CapturedRect().Should().Be(new ScreenRegion(expected.X, expected.Y, expected.Width, expected.Height));
        using var doc = JsonDocument.Parse(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text);
        var target = doc.RootElement.GetProperty("target");
        target.GetProperty("hwnd").GetInt64().Should().Be((long)_window.Handle);
        target.GetProperty("matchStrategy").GetString().Should().Be("exact");
        doc.RootElement.TryGetProperty("targetClipped", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_element_id_from_a_real_find_captures_that_controls_rect_inside_its_window()
    {
        _window.Exists.Should().BeTrue("precondition");
        ElementInfo? button = null;
        for (int attempt = 0; attempt < 5 && button is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _uia.FindElementAsync(_window.EnabledButton, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);
            button = found.Matches.FirstOrDefault(m => m.Name == _window.EnabledButton);
        }
        button.Should().NotBeNull("precondition: UIA sees the fixture's button");
        button!.Bounds.Should().NotBeNull();

        await _tools.Screenshot(format: "png", output: "inline", element_id: button.ElementId);

        var b = button.Bounds!;
        var rect = CapturedRect();
        rect.Should().Be(new ScreenRegion(b.X, b.Y, b.Width, b.Height));
        var window = WindowRect();
        rect.X.Should().BeGreaterThanOrEqualTo(window.X);
        rect.Y.Should().BeGreaterThanOrEqualTo(window.Y);
        (rect.X + rect.Width).Should().BeLessThanOrEqualTo(window.X + window.Width, "the button lies inside its window");
        (rect.Y + rect.Height).Should().BeLessThanOrEqualTo(window.Y + window.Height);
    }

    [Fact]
    public async Task A_minimised_owned_window_is_refused_by_name_and_stays_minimised()
    {
        _window.Exists.Should().BeTrue("precondition");
        ShowWindow(_window.Handle, SW_SHOWMINNOACTIVE);
        for (int i = 0; i < 20 && !IsIconic(_window.Handle); i++) await Task.Delay(50);
        IsIconic(_window.Handle).Should().BeTrue("precondition: the fixture window is minimised");

        Func<Task> act = () => _tools.Screenshot(format: "png", output: "inline", hwnd: (long)_window.Handle);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain(_window.Title).And.Contain("restore");
        IsIconic(_window.Handle).Should().BeTrue("the tool is read-only: nothing is restored for the caller");
        _shot.Invocations.Should().BeEmpty();
    }
}
