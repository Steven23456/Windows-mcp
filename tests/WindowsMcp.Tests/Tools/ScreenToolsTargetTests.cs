using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-7 (docs/design/E-7-capture-targets-ocr-words.md, "Targets"): <c>screenshot</c> and <c>ocr</c>
/// pointed at a WINDOW (<c>window</c> / <c>hwnd</c>, through the real B-10 <see cref="WindowMatcher"/>)
/// or an ELEMENT (<c>element_id</c>) instead of a rect. One resolver serves both tools, so most rows
/// run as a theory over both. Everything Windows is mocked; the window inventory and the element
/// cache are <see cref="MockBehavior.Strict"/> so a target capture that restored, raised, focused
/// or scrolled anything (the tools stay read-only) fails on the call itself.
/// <para>
/// The real inventory is <c>ScreenToolsTargetIntegrationTests</c>; the live "done when" is
/// <c>OcrWordPositionDesktopTests</c> (UIAutomation).
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ScreenToolsTargetTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ---- the rig ---------------------------------------------------------------------------

    private static WindowInfo Win(string title, long hwnd, int z, Bounds bounds, WindowState state = WindowState.Normal) =>
        new(title, hwnd, 1000 + (int)hwnd, "proc", state, bounds, z, IsActive: z == 0, IsBrowser: false, MonitorIndex: 0);

    /// <summary>
    /// The B-10 shapes: an exact title, a superstring of it, a substring-only title and a
    /// fuzzy-only one. Frontmost first.
    /// </summary>
    private static WindowInfo[] Desktop =>
    [
        Win("Untitled - Notepad", 0x10, 0, new Bounds(100, 100, 800, 600)),
        Win("Notepad", 0x20, 1, new Bounds(200, 150, 640, 480)),
        Win("Document1 - Word", 0x30, 2, new Bounds(50, 60, 1000, 700)),
        Win("Visual Studio Code - Insiders", 0x40, 3, new Bounds(300, 200, 1200, 800)),
    ];

    private static ElementInfo El(string id, Bounds? bounds, bool offscreen = false, string name = "Save", string type = "Button") =>
        new(id, name, type, IsEnabled: true, IsOffscreen: offscreen, bounds, Value: null, IsChecked: null, IsSelected: null);

    private sealed class Rig
    {
        public List<string> Order { get; } = [];
        public Mock<IScreenshotService> Shot { get; } = new();
        public Mock<IOcrService> Ocr { get; } = new();
        public Mock<IWindowService> Windows { get; } = new(MockBehavior.Strict);
        public Mock<IInputService> Input { get; } = new();
        public Mock<IUIAutomationService> Uia { get; } = new(MockBehavior.Strict);
        public Mock<IFlashOverlay> Flash { get; } = new();
        public ScreenTools Tools { get; }

        public Rig(MonitorInfo[]? monitors = null, WindowInfo[]? inventory = null,
                   ElementInfo[]? elements = null, SnapshotResult? snapshot = null)
        {
            Windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Order.Add("monitors"); return monitors ?? ScreenToolsTests.SingleMonitor; });
            Windows.Setup(w => w.ListAsync(true, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Order.Add("list"); return inventory ?? Desktop; });

            var byId = (elements ?? []).ToDictionary(e => e.ElementId);
            Uia.Setup(u => u.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) =>
                {
                    Order.Add("element");
                    return byId.TryGetValue(id, out var e)
                        ? Task.FromResult(e)
                        : Task.FromException<ElementInfo>(new KeyNotFoundException(
                            $"Element '{id}' not found; ids expire with each snapshot - take a new one."));
                });
            Uia.Setup(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Order.Add("snapshot"); return snapshot ?? ScreenToolsTests.EmptySnapshot; });

            Shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Order.Add("capture");
                    return new ScreenshotResult(PngBytes, 100, 100, ImageFormat.Png, 100, 100, 1.0);
                });
            Ocr.Setup(o => o.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Order.Add("ocr"); return "ocr text"; });
            Ocr.Setup(o => o.RecognizeAsync(It.IsAny<ScreenRegion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ScreenRegion r, CancellationToken _) => { Order.Add("ocr"); return new OcrResult("", [], r); });
            Input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(100, 100));
            Flash.Setup(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()))
                .Callback(() => Flash.SetupGet(f => f.IsVisible).Returns(true));

            Tools = new ScreenTools(Shot.Object, Ocr.Object, Windows.Object, Input.Object, Uia.Object, Flash.Object);
        }
    }

    public static TheoryData<string> BothTools => new() { "screenshot", "ocr" };

    /// <summary>One call of either tool with the five target arguments; screenshot as inline PNG so nothing is written to disk.</summary>
    private static async Task<CallToolResult?> Call(Rig rig, string tool,
        string? region = null, string? display = null, string? window = null, long? hwnd = null, string? elementId = null,
        bool annotate = false)
    {
        if (tool == "screenshot")
        {
            return await rig.Tools.Screenshot(region, display, format: "png", output: "inline", annotate: annotate,
                window: window, hwnd: hwnd, element_id: elementId);
        }
        await rig.Tools.Ocr(region, display, window, hwnd, elementId);
        return null;
    }

    /// <summary>The one rect the tool handed to the capture (screenshot) or to the text OCR (ocr).</summary>
    private static ScreenRegion CapturedRect(Rig rig, string tool)
    {
        Mock mock = tool == "screenshot" ? rig.Shot : rig.Ocr;
        var name = tool == "screenshot" ? nameof(IScreenshotService.CaptureAsync) : nameof(IOcrService.ExtractTextAsync);
        var calls = mock.Invocations.Where(i => i.Method.Name == name).ToList();
        calls.Should().ContainSingle($"{tool} reads the screen exactly once per call");
        return calls[0].Arguments[0].Should().BeOfType<ScreenRegion>().Subject;
    }

    private static void NothingCaptured(Rig rig)
    {
        rig.Shot.Invocations.Should().BeEmpty("a refused call must not cost a capture");
        rig.Ocr.Invocations.Should().BeEmpty("a refused call must not cost an OCR read");
        rig.Input.Invocations.Should().BeEmpty("nor a cursor read");
        // Show only: a refused ocr may or may not have hidden a previous glow (review F1: "a refused
        // call need not hide"); what it must never do is announce a capture that did not happen.
        rig.Flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never,
            "no glow announcing a capture that never happened");
    }

    private static JsonElement Meta(CallToolResult? result)
    {
        result.Should().NotBeNull();
        using var doc = JsonDocument.Parse(result!.Content.OfType<TextContentBlock>().First().Text);
        return doc.RootElement.Clone();
    }

    private static void ShouldBeRect(JsonElement rect, ScreenRegion expected)
    {
        rect.GetProperty("x").GetInt32().Should().Be(expected.X);
        rect.GetProperty("y").GetInt32().Should().Be(expected.Y);
        rect.GetProperty("width").GetInt32().Should().Be(expected.Width);
        rect.GetProperty("height").GetInt32().Should().Be(expected.Height);
    }

    private static ScreenRegion R(Bounds b) => new(b.X, b.Y, b.Width, b.Height);

    // ======== R2 — at most one kind of target; two kinds is refused before anything is looked up =

    public static TheoryData<string, string?, string?, string?, long?, string?, string, string> TwoKinds()
    {
        var data = new TheoryData<string, string?, string?, string?, long?, string?, string, string>();
        foreach (var tool in new[] { "screenshot", "ocr" })
        {
            data.Add(tool, "10,20,300,200", null, "Notepad", null, null, "region", "window");
            data.Add(tool, "10,20,300,200", null, null, 0x20L, null, "region", "hwnd");
            data.Add(tool, "10,20,300,200", null, null, null, "el_1", "region", "element_id");
            data.Add(tool, null, "0", "Notepad", null, null, "display", "window");
            data.Add(tool, null, "0", null, 0x20L, null, "display", "hwnd");
            data.Add(tool, null, "0", null, null, "el_1", "display", "element_id");
            data.Add(tool, null, null, "Notepad", null, "el_1", "window", "element_id");
            data.Add(tool, null, null, null, 0x20L, "el_1", "hwnd", "element_id");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TwoKinds))]
    public async Task Two_kinds_of_target_are_refused_naming_both_parameters_before_any_service_call(
        string tool, string? region, string? display, string? window, long? hwnd, string? elementId, string first, string second)
    {
        var rig = new Rig();

        Func<Task> act = () => Call(rig, tool, region, display, window, hwnd, elementId);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain(first).And.Contain(second, "the message names both parameters the caller combined");
        rig.Windows.VerifyNoOtherCalls();   // not even the monitor inventory: this is an argument error
        rig.Uia.VerifyNoOtherCalls();
        NothingCaptured(rig);
    }

    [Fact]
    public async Task All_three_kinds_together_are_refused_before_any_service_call()
    {
        var rig = new Rig();

        Func<Task> act = () => Call(rig, "screenshot", "10,20,300,200", null, "Notepad", null, "el_1");

        await act.Should().ThrowAsync<ArgumentException>();
        rig.Windows.VerifyNoOtherCalls();
        rig.Uia.VerifyNoOtherCalls();
        NothingCaptured(rig);
    }

    [Theory]
    [InlineData("10,20,300,200", null, "Notepad", null, null)]
    [InlineData(null, "0", null, 0x20L, null)]
    [InlineData(null, null, "Notepad", null, "el_1")]
    public async Task Screenshot_and_ocr_refuse_a_combination_with_the_same_message(
        string? region, string? display, string? window, long? hwnd, string? elementId)
    {
        // One resolver serves both tools, so they cannot drift in what they accept or say.
        var rig = new Rig();

        Func<Task> shot = () => Call(rig, "screenshot", region, display, window, hwnd, elementId);
        Func<Task> ocr = () => Call(rig, "ocr", region, display, window, hwnd, elementId);

        var shotMessage = (await shot.Should().ThrowAsync<ArgumentException>()).Which.Message;
        (await ocr.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(shotMessage);
    }

    // ======== R3 — window + hwnd is one kind; hwnd wins ========================================

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task Window_and_hwnd_together_are_allowed_and_the_hwnd_wins(string tool)
    {
        var rig = new Rig();

        var result = await Call(rig, tool, window: "Notepad", hwnd: 0x30);

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(50, 60, 1000, 700), "B-10: an hwnd wins over a title");
        if (result is not null)
        {
            var target = Meta(result).GetProperty("target");
            target.GetProperty("hwnd").GetInt64().Should().Be(0x30);
            target.GetProperty("matchStrategy").GetString().Should().Be("hwnd");
        }
    }

    // ======== R4 — a blank window / element_id counts as not given =============================

    [Theory]
    [InlineData("screenshot", "", null)]
    [InlineData("screenshot", "   ", null)]
    [InlineData("screenshot", null, "")]
    [InlineData("screenshot", null, " \t ")]
    [InlineData("screenshot", "  ", "  ")]
    [InlineData("ocr", "", null)]
    [InlineData("ocr", null, "  ")]
    [InlineData("ocr", " ", "\t")]
    public async Task A_blank_window_or_element_id_is_no_target_and_the_primary_display_is_captured(
        string tool, string? window, string? elementId)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide);

        var result = await Call(rig, tool, window: window, elementId: elementId);

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(0, 0, 1920, 1080));
        rig.Windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Uia.Verify(u => u.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        if (result is not null)
            Meta(result).TryGetProperty("target", out _).Should().BeFalse("no target picked the rect");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_blank_window_beside_a_region_is_not_a_second_kind(string tool)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide);

        await Call(rig, tool, region: "10,20,300,200", window: "   ");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(10, 20, 300, 200));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_blank_element_id_beside_a_display_is_not_a_second_kind(string tool)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide);

        await Call(rig, tool, display: "1", elementId: "");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(1920, 0, 1920, 1080));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_blank_window_beside_an_hwnd_targets_the_hwnd(string tool)
    {
        var rig = new Rig();

        await Call(rig, tool, window: " ", hwnd: 0x40);

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(300, 200, 1200, 800));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_blank_window_beside_an_element_id_targets_the_element(string tool)
    {
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        await Call(rig, tool, window: "", elementId: "el_5");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(300, 400, 80, 24));
    }

    // ======== R5 — no target: today's behaviour, and no window or element lookup ================

    [Theory]
    [InlineData("screenshot", null, null, 0, 0, 1920, 1080)]
    [InlineData("screenshot", "10,20,300,200", null, 10, 20, 300, 200)]
    [InlineData("screenshot", null, "1", 1920, 0, 1920, 1080)]
    [InlineData("ocr", null, null, 0, 0, 1920, 1080)]
    [InlineData("ocr", "10,20,300,200", null, 10, 20, 300, 200)]
    [InlineData("ocr", null, "1", 1920, 0, 1920, 1080)]
    public async Task Without_a_target_the_rect_is_resolved_as_before_and_nothing_is_looked_up(
        string tool, string? region, string? display, int x, int y, int w, int h)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide);

        await Call(rig, tool, region, display);

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(x, y, w, h));
        rig.Windows.Verify(m => m.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never,
            "a rect capture must not pay for a window inventory");
        rig.Uia.Verify(u => u.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ======== R7 — window: the A-1 inventory through the real B-10 matcher ======================

    [Theory]
    [InlineData("screenshot", "notepad", 0x20L, "exact")]                       // ordinal ignore-case equality
    [InlineData("screenshot", "Word", 0x30L, "substring")]
    [InlineData("ocr", "NOTEPAD", 0x20L, "exact")]
    [InlineData("ocr", "Word", 0x30L, "substring")]
    public async Task A_window_title_captures_that_windows_rect(string tool, string title, long expectedHwnd, string strategy)
    {
        WindowMatcher.Match(Desktop, title, null).Strategy.Should().Be(strategy, "precondition: the row exercises that strategy");
        var rig = new Rig();

        await Call(rig, tool, window: title);

        CapturedRect(rig, tool).Should().Be(R(Desktop.Single(w => w.Hwnd == expectedHwnd).Bounds));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_fuzzy_title_captures_the_matchers_choice(string tool)
    {
        const string query = "Visual Studo Code";
        var expected = WindowMatcher.Match(Desktop, query, null);
        expected.Strategy.Should().Be("fuzzy", "precondition: only the fuzzy stage can match this query");
        var rig = new Rig();

        await Call(rig, tool, window: query);

        CapturedRect(rig, tool).Should().Be(R(expected.Window.Bounds));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_hwnd_alone_captures_that_windows_rect(string tool)
    {
        var rig = new Rig();

        await Call(rig, tool, hwnd: 0x40);

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(300, 200, 1200, 800));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_target_reads_the_inventory_with_minimised_and_without_hidden_windows_once(string tool)
    {
        // The same inventory window(action:...) uses, so a minimised window is FOUND (and then
        // refused by name) instead of being reported as "no such window".
        var rig = new Rig();

        await Call(rig, tool, window: "Notepad");

        rig.Windows.Verify(w => w.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the virtual screen the rect is clipped to, and the displays reported, are one inventory");
    }

    // ---- R8 — no match ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_title_nothing_matches_is_the_matchers_KeyNotFoundException_listing_the_open_windows(string tool)
    {
        var expected = ((Action)(() => WindowMatcher.Match(Desktop, "xyzzy plugh", null)))
            .Should().Throw<KeyNotFoundException>("precondition").Which.Message;
        var rig = new Rig();

        Func<Task> act = () => Call(rig, tool, window: "xyzzy plugh");

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Be(expected, "the same refusal window(action:...) gives");
        message.Should().Contain("Untitled - Notepad").And.Contain("Document1 - Word");
        NothingCaptured(rig);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_hwnd_nothing_has_is_the_matchers_KeyNotFoundException(string tool)
    {
        var rig = new Rig();

        Func<Task> act = () => Call(rig, tool, hwnd: 0x99);

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("0x99");
        NothingCaptured(rig);
    }

    // ---- R9 — minimised: refused by name, nothing restored --------------------------------------

    [Theory]
    [InlineData("screenshot", -32000, -32000, 160, 28)]   // where Windows parks an iconic window
    [InlineData("screenshot", 100, 100, 800, 600)]        // minimised is refused whatever rect it reports
    [InlineData("ocr", -32000, -32000, 160, 28)]
    [InlineData("ocr", 100, 100, 800, 600)]
    public async Task A_minimised_window_is_refused_naming_it_and_restore_and_nothing_is_restored(
        string tool, int x, int y, int w, int h)
    {
        var rig = new Rig(inventory: [Win("Calculator", 0x50, 0, new Bounds(x, y, w, h), WindowState.Minimized)]);

        Func<Task> act = () => Call(rig, tool, window: "Calculator");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("Calculator", "the message names the window");
        message.ToLowerInvariant().Should().Contain("restore", "and tells the caller how to give it a picture");
        message.Should().NotContain("off-screen", "a minimised window is minimised, not lost off the edge");
        rig.Windows.Verify(m => m.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.Verify(m => m.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.AtMostOnce());
        rig.Windows.VerifyNoOtherCalls();   // no ExecuteAsync("restore"), no BringToFrontAsync, no SetBoundsAsync
        NothingCaptured(rig);
    }

    // ---- R10/R11 — clipped to the virtual screen ------------------------------------------------

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_maximised_window_overhanging_its_monitor_by_its_frame_is_clipped(string tool)
    {
        var rig = new Rig(inventory: [Win("Big", 0x60, 0, new Bounds(-8, -8, 1936, 1096), WindowState.Maximized)]);

        var result = await Call(rig, tool, window: "Big");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(0, 0, 1920, 1080));
        if (result is not null)
        {
            var meta = Meta(result);
            ShouldBeRect(meta.GetProperty("region"), new ScreenRegion(0, 0, 1920, 1080));
            meta.GetProperty("targetClipped").GetBoolean().Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_half_off_the_right_edge_is_clipped_to_what_is_on_screen(string tool)
    {
        var rig = new Rig(inventory: [Win("Half", 0x61, 0, new Bounds(1500, 100, 800, 600))]);

        var result = await Call(rig, tool, window: "Half");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(1500, 100, 420, 600));
        if (result is not null)
            Meta(result).GetProperty("targetClipped").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_spanning_two_monitors_is_clipped_to_the_virtual_screen_not_to_one_monitor(string tool)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide, inventory: [Win("Seam", 0x62, 0, new Bounds(1800, 100, 400, 300))]);

        var result = await Call(rig, tool, window: "Seam");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(1800, 100, 400, 300));
        if (result is not null)
            Meta(result).TryGetProperty("targetClipped", out _).Should().BeFalse("nothing was removed");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_on_a_negative_origin_secondary_captures_its_negative_rect(string tool)
    {
        var rig = new Rig(ScreenToolsTests.LeftOfPrimary, inventory: [Win("Left", 0x63, 0, new Bounds(-1800, -20, 800, 600))]);

        var result = await Call(rig, tool, window: "Left");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(-1800, -20, 800, 600),
            "virtual-desktop coordinates: a window left of and above the primary is not clamped to 0,0");
        if (result is not null)
        {
            var meta = Meta(result);
            ShouldBeRect(meta.GetProperty("region"), new ScreenRegion(-1800, -20, 800, 600));
            meta.TryGetProperty("targetClipped", out _).Should().BeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_overhanging_a_negative_origin_corner_is_clipped_there(string tool)
    {
        var rig = new Rig(ScreenToolsTests.LeftOfPrimary, inventory: [Win("Corner", 0x64, 0, new Bounds(-2000, -100, 500, 300))]);

        await Call(rig, tool, window: "Corner");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(-1920, -40, 420, 240));
    }

    // ---- R12 — nothing left: off-screen ---------------------------------------------------------

    [Theory]
    [InlineData("screenshot", 5000, 100, 800, 600)]    // wholly right of the desktop
    [InlineData("screenshot", 100, 100, 0, 0)]         // an empty rect
    [InlineData("screenshot", 1920, 0, 300, 300)]      // touches the right edge, overlaps nothing
    [InlineData("ocr", 5000, 100, 800, 600)]
    [InlineData("ocr", 100, 100, 0, 0)]
    public async Task A_window_with_nothing_on_screen_is_refused_as_off_screen(string tool, int x, int y, int w, int h)
    {
        var rig = new Rig(inventory: [Win("Lost Window", 0x65, 0, new Bounds(x, y, w, h))]);

        Func<Task> act = () => Call(rig, tool, window: "Lost Window");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("Lost Window", "the message names the window")
            .And.Contain("off-screen")
            .And.Contain(x.ToString(System.Globalization.CultureInfo.InvariantCulture), "and its bounds");
        message.Should().Match(m => m.Contains("1919") || m.Contains("1920"), "and the virtual screen it missed");
        NothingCaptured(rig);
    }

    // ---- R14 — targetClipped only when clipping removed something --------------------------------

    [Theory]
    [InlineData(100, 100, 800, 600)]       // well inside
    [InlineData(0, 0, 1920, 1080)]         // exactly the virtual screen
    public async Task TargetClipped_is_absent_when_the_window_lies_wholly_on_screen(int x, int y, int w, int h)
    {
        var rig = new Rig(inventory: [Win("Fits", 0x66, 0, new Bounds(x, y, w, h))]);

        var meta = Meta(await Call(rig, "screenshot", window: "Fits"));

        meta.TryGetProperty("targetClipped", out _).Should().BeFalse("absent fields are absent, not false (the A-7 rule)");
    }

    // ======== R15-R19 — element ==================================================================

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_id_captures_the_elements_rect(string tool)
    {
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        await Call(rig, tool, elementId: "el_5");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(300, 400, 80, 24));
        rig.Uia.Verify(u => u.GetElementAsync("el_5", It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_unknown_element_id_is_the_element_lookups_own_KeyNotFoundException(string tool)
    {
        var rig = new Rig(elements: []);

        Func<Task> act = () => Call(rig, tool, elementId: "el_99");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message
            .Should().Be("Element 'el_99' not found; ids expire with each snapshot - take a new one.",
                "the cache's message already tells the caller what to do; it passes through untouched");
        NothingCaptured(rig);
    }

    public static TheoryData<string, int?, int, int, int> NoRect()
    {
        var data = new TheoryData<string, int?, int, int, int>();
        foreach (var tool in new[] { "screenshot", "ocr" })
        {
            data.Add(tool, null, 0, 0, 0);          // Bounds null: collapsed or virtualised
            data.Add(tool, 300, 400, 0, 24);        // zero width
            data.Add(tool, 300, 400, 80, 0);        // zero height
            data.Add(tool, 300, 400, -5, 24);       // negative width
            data.Add(tool, 300, 400, 80, -1);       // negative height
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NoRect))]
    public async Task An_element_with_no_rectangle_is_refused_naming_it_and_scroll_into_view(
        string tool, int? x, int y, int w, int h)
    {
        Bounds? bounds = x is null ? null : new Bounds(x.Value, y, w, h);
        var rig = new Rig(elements: [El("el_7", bounds)]);

        Func<Task> act = () => Call(rig, tool, elementId: "el_7");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("el_7")
            .And.Contain("interact_element")
            .And.Contain("scroll_into_view", "the one verb that may bring it on screen");
        NothingCaptured(rig);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_reporting_IsOffscreen_with_real_on_screen_bounds_is_captured(string tool)
    {
        // D-7: browsers over-report IsOffscreen; the bounds are the evidence, not the flag.
        var rig = new Rig(elements: [El("el_8", new Bounds(500, 300, 120, 40), offscreen: true)]);

        await Call(rig, tool, elementId: "el_8");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(500, 300, 120, 40));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_overhanging_the_virtual_screen_is_clipped_like_a_window(string tool)
    {
        var rig = new Rig(elements: [El("el_9", new Bounds(1900, 1060, 80, 40))]);

        var result = await Call(rig, tool, elementId: "el_9");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(1900, 1060, 20, 20));
        if (result is not null)
            Meta(result).GetProperty("targetClipped").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_on_a_negative_origin_secondary_captures_its_negative_rect(string tool)
    {
        var rig = new Rig(ScreenToolsTests.LeftOfPrimary, elements: [El("el_10", new Bounds(-1000, -30, 100, 20))]);

        await Call(rig, tool, elementId: "el_10");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(-1000, -30, 100, 20));
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_wholly_off_the_virtual_screen_is_refused_as_off_screen(string tool)
    {
        var rig = new Rig(elements: [El("el_11", new Bounds(2500, 100, 80, 24))]);

        Func<Task> act = () => Call(rig, tool, elementId: "el_11");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_11").And.Contain("off-screen");
        NothingCaptured(rig);
    }

    // ======== R20 — read-only: nothing focused, raised, restored or scrolled ======================

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_capture_calls_nothing_on_the_window_service_but_the_two_reads(string tool)
    {
        var rig = new Rig();

        await Call(rig, tool, window: "Notepad");

        rig.Windows.Verify(w => w.ListAsync(true, false, It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.VerifyNoOtherCalls();
        rig.Uia.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_capture_calls_nothing_on_the_element_service_but_the_lookup(string tool)
    {
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        await Call(rig, tool, elementId: "el_5");

        rig.Uia.Verify(u => u.GetElementAsync("el_5", It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.VerifyNoOtherCalls();   // no scroll_into_view, no focus, no snapshot
        rig.Windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
        rig.Windows.VerifyNoOtherCalls();
    }

    // ======== R21-R26 — what screenshot's metadata says =========================================

    [Fact]
    public async Task Screenshot_metadata_target_for_a_window_names_it_and_how_it_was_matched()
    {
        var rig = new Rig();

        var target = Meta(await Call(rig, "screenshot", window: "Word")).GetProperty("target");

        target.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "kind", "title", "hwnd", "matchStrategy", "score" });
        target.GetProperty("kind").GetString().Should().Be("window");
        target.GetProperty("title").GetString().Should().Be("Document1 - Word");
        target.GetProperty("hwnd").GetInt64().Should().Be(0x30);
        target.GetProperty("matchStrategy").GetString().Should().Be("substring");
        target.GetProperty("score").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Screenshot_metadata_target_for_a_fuzzy_window_carries_the_matchers_score()
    {
        const string query = "Visual Studo Code";
        var expected = WindowMatcher.Match(Desktop, query, null);
        var rig = new Rig();

        var target = Meta(await Call(rig, "screenshot", window: query)).GetProperty("target");

        target.GetProperty("matchStrategy").GetString().Should().Be("fuzzy");
        target.GetProperty("score").GetInt32().Should().Be(expected.Score);
        target.GetProperty("title").GetString().Should().Be(expected.Window.Title);
    }

    [Fact]
    public async Task Screenshot_metadata_target_for_an_hwnd_says_hwnd()
    {
        var rig = new Rig();

        var target = Meta(await Call(rig, "screenshot", hwnd: 0x10)).GetProperty("target");

        target.GetProperty("kind").GetString().Should().Be("window");
        target.GetProperty("title").GetString().Should().Be("Untitled - Notepad");
        target.GetProperty("hwnd").GetInt64().Should().Be(0x10);
        target.GetProperty("matchStrategy").GetString().Should().Be("hwnd");
        target.GetProperty("score").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Screenshot_metadata_target_for_an_element_names_it()
    {
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24), name: "Save as", type: "SplitButton")]);

        var target = Meta(await Call(rig, "screenshot", elementId: "el_5")).GetProperty("target");

        target.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "kind", "elementId", "name", "controlType" });
        target.GetProperty("kind").GetString().Should().Be("element");
        target.GetProperty("elementId").GetString().Should().Be("el_5");
        target.GetProperty("name").GetString().Should().Be("Save as");
        target.GetProperty("controlType").GetString().Should().Be("SplitButton");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("10,20,300,200", null)]
    [InlineData(null, "1")]
    public async Task Screenshot_metadata_has_no_target_for_a_rect_capture(string? region, string? display)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide);

        var meta = Meta(await Call(rig, "screenshot", region, display));

        meta.TryGetProperty("target", out _).Should().BeFalse();
        meta.TryGetProperty("targetClipped", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Notepad", null)]
    [InlineData(null, "el_5")]
    public async Task Screenshot_metadata_for_a_target_has_no_selectedDisplays_and_reports_the_captured_rect(
        string? window, string? elementId)
    {
        var rig = new Rig(ScreenToolsTests.SideBySide, elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        var meta = Meta(await Call(rig, "screenshot", window: window, elementId: elementId));

        meta.TryGetProperty("selectedDisplays", out _).Should().BeFalse("no display picked this rect");
        ShouldBeRect(meta.GetProperty("region"), CapturedRect(rig, "screenshot"));
        meta.GetProperty("displays").GetArrayLength().Should().Be(2, "every monitor is still listed");
    }

    [Fact]
    public async Task Screenshot_note_for_a_target_away_from_the_origin_gives_the_targets_offset()
    {
        // Image (0,0) is the window's top-left: the A-8 sentence must say so, or every click the
        // model derives from the picture lands 200 px left and 150 px up.
        var rig = new Rig();

        var meta = Meta(await Call(rig, "screenshot", window: "Notepad"));

        meta.GetProperty("note").GetString().Should().Be(ScreenTools.CoordinateNote(new ScreenRegion(200, 150, 640, 480), 1.0));
    }

    [Fact]
    public async Task Screenshot_of_a_target_flashes_the_rect_it_captured()
    {
        var rig = new Rig(inventory: [Win("Half", 0x61, 0, new Bounds(1500, 100, 800, 600))]);

        await Call(rig, "screenshot", window: "Half");

        rig.Flash.Verify(f => f.Show(new ScreenRegion(1500, 100, 420, 600), It.IsAny<TimeSpan>()), Times.Once,
            "the glow outlines what was captured: the clipped rect");
    }

    // ---- R27 — annotate with a target ------------------------------------------------------------

    private static SnapshotElement Element(string id, Bounds bounds) =>
        new(id, "Notepad", "Button", "B", bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, bounds,
            "click", false, false, null, null, null, null, null, null, null);

    [Fact]
    public async Task Screenshot_annotate_with_a_window_resolves_the_window_before_the_snapshot_and_filters_by_its_rect()
    {
        var inside = Element("el_1", new Bounds(250, 200, 40, 20));       // inside Notepad (200,150,640,480)
        var outside = Element("el_2", new Bounds(1500, 900, 40, 20));
        var snapshot = new SnapshotResult([], null, new CursorPosition(0, 0), -1, [inside, outside], [], null, false, 500, 2, 0);
        var rig = new Rig(snapshot: snapshot);

        await Call(rig, "screenshot", window: "Notepad", annotate: true);

        rig.Order.Should().ContainInOrder("list", "snapshot", "capture");
        rig.Order.IndexOf("list").Should().BeLessThan(rig.Order.IndexOf("snapshot"), "the first walk comes after the window is known");
        rig.Uia.Verify(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var options = rig.Shot.Invocations.Single().Arguments[1].Should().BeOfType<CaptureOptions>().Subject;
        options.Annotations.Should().BeEquivalentTo(new[] { new AnnotationBox("el_1", inside.Bounds) },
            "only what lies in the captured (window) rect is boxed");
    }

    [Fact]
    public async Task Screenshot_annotate_with_an_element_looks_the_element_up_before_the_snapshot_evicts_its_id()
    {
        // annotate's walk is a snapshot, and a snapshot replaces the id cache: an element id looked
        // up AFTER it would be gone (or name something else).
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        await Call(rig, "screenshot", elementId: "el_5", annotate: true);

        rig.Order.Should().ContainInOrder("element", "snapshot", "capture");
        rig.Order.IndexOf("element").Should().BeLessThan(rig.Order.IndexOf("snapshot"));
    }

    // ======== GREEN — the cases the design note left unspecified, pinned to current behaviour =====

    [Theory]
    [InlineData("screenshot", 0L)]
    [InlineData("screenshot", -1L)]
    [InlineData("ocr", 0L)]
    [InlineData("ocr", -1L)]
    public async Task An_hwnd_of_zero_or_below_is_a_window_nothing_has_not_no_target(string tool, long hwnd)
    {
        // hwnd is long?, so 'not given' is null; 0 is a handle like any other and no top-level
        // window has it. Treating 0 as "unset" would silently capture the primary display instead.
        var rig = new Rig();

        Func<Task> act = () => Call(rig, tool, hwnd: hwnd);

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("handle");
        NothingCaptured(rig);
    }

    [Fact]
    public async Task An_hwnd_of_zero_still_wins_over_a_title_that_would_match()
    {
        var rig = new Rig();

        Func<Task> act = () => Call(rig, "screenshot", window: "Notepad", hwnd: 0);

        await act.Should().ThrowAsync<KeyNotFoundException>("B-10: an hwnd wins, whatever its value");
        NothingCaptured(rig);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_id_with_surrounding_whitespace_is_looked_up_verbatim(string tool)
    {
        // Pinned, not designed: no element_id argument anywhere in the server is trimmed (click,
        // type, interact_element, ...), and the ids the server mints never carry whitespace, so the
        // cache's own KeyNotFoundException is the answer — consistent with every sibling tool.
        var rig = new Rig(elements: [El("el_5", new Bounds(300, 400, 80, 24))]);

        Func<Task> act = () => Call(rig, tool, elementId: " el_5 ");

        await act.Should().ThrowAsync<KeyNotFoundException>();
        rig.Uia.Verify(u => u.GetElementAsync(" el_5 ", It.IsAny<CancellationToken>()), Times.Once);
        NothingCaptured(rig);
    }

    [Fact]
    public async Task Screenshot_to_a_file_carries_target_and_targetClipped_too()
    {
        // One metadata shape for both output modes (the A-7/A-9 rule).
        var rig = new Rig(inventory: [Win("Half", 0x61, 0, new Bounds(1500, 100, 800, 600))]);

        var result = await rig.Tools.Screenshot(format: "png", output: "file", window: "Half");
        var meta = Meta(result);
        try
        {
            meta.GetProperty("target").GetProperty("title").GetString().Should().Be("Half");
            meta.GetProperty("targetClipped").GetBoolean().Should().BeTrue();
            ShouldBeRect(meta.GetProperty("region"), new ScreenRegion(1500, 100, 420, 600));
        }
        finally
        {
            try { File.Delete(meta.GetProperty("path").GetString()!); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("window", "Notepad")]
    [InlineData("element_id", "el_5")]
    public async Task The_refusal_of_two_kinds_comes_before_an_otherwise_invalid_region_or_display_is_parsed(string kind, string value)
    {
        // The kinds rule is an argument rule; it must not depend on the rect arguments being valid.
        var rig = new Rig();

        Func<Task> act = () => kind == "window"
            ? rig.Tools.Screenshot("1,2,3", format: "png", output: "inline", window: value)
            : rig.Tools.Screenshot(display: "9", format: "png", output: "inline", element_id: value);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(kind);
        rig.Windows.VerifyNoOtherCalls();
        NothingCaptured(rig);
    }

    // ======== E-7 review F4 — a target on no monitor is off-screen ===============================

    /// <summary>
    /// Unequal monitors: 1920x1080 at the origin and 2560x1440 to its right. The virtual screen is
    /// (0,0,4480,1440), and the strip x 0..1919, y 1080..1439 is inside it but on no monitor.
    /// </summary>
    private static MonitorInfo[] Unequal =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, 0, 2560, 1440, false),
    ];

    [Theory]
    [InlineData("screenshot", 100, 1100, 800, 300)]   // wholly in the gap
    [InlineData("screenshot", 100, 1080, 800, 300)]   // touching monitor 0's bottom edge is not on it
    [InlineData("ocr", 100, 1100, 800, 300)]
    public async Task A_window_wholly_in_the_gap_between_unequal_monitors_is_refused_as_off_screen(
        string tool, int x, int y, int w, int h)
    {
        var rig = new Rig(Unequal, inventory: [Win("Gap Window", 0x70, 0, new Bounds(x, y, w, h))]);

        Func<Task> act = () => Call(rig, tool, window: "Gap Window");

        (await act.Should().ThrowAsync<InvalidOperationException>("GDI would return black, OCR 'nothing recognised'"))
            .Which.Message.Should().Contain("Gap Window").And.Contain("off-screen");
        NothingCaptured(rig);
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_wholly_in_the_gap_between_unequal_monitors_is_refused_as_off_screen(string tool)
    {
        var rig = new Rig(Unequal, elements: [El("el_20", new Bounds(100, 1100, 800, 300))]);

        Func<Task> act = () => Call(rig, tool, elementId: "el_20");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_20").And.Contain("off-screen");
        NothingCaptured(rig);
    }

    [Theory]
    [InlineData("screenshot", 100, 900, 800, 300)]     // half on monitor 0, half in the gap
    [InlineData("screenshot", 1800, 1000, 400, 300)]   // gap, monitor 0 and monitor 1
    [InlineData("ocr", 100, 900, 800, 300)]
    public async Task A_window_partly_in_the_gap_is_captured_as_is_and_not_reported_clipped(
        string tool, int x, int y, int w, int h)
    {
        // Pinned (D-56): only the virtual-screen clip counts as clipping; the gap part is captured.
        var rig = new Rig(Unequal, inventory: [Win("Part Window", 0x71, 0, new Bounds(x, y, w, h))]);

        var result = await Call(rig, tool, window: "Part Window");

        CapturedRect(rig, tool).Should().Be(new ScreenRegion(x, y, w, h));
        if (result is not null)
            Meta(result).TryGetProperty("targetClipped", out _).Should().BeFalse();
    }

    // ======== E-7 review F5 — refusal wording ==================================================

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_with_no_bounds_says_its_window_may_have_closed(string tool)
    {
        var rig = new Rig(elements: [El("el_21", null)]);

        Func<Task> act = () => Call(rig, tool, elementId: "el_21");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("el_21").And.Contain("scroll_into_view");
        message.Should().MatchRegex("(?i)closed", "an element whose window has closed has no rectangle either");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_element_wholly_off_the_virtual_screen_says_its_window_may_be_minimised(string tool)
    {
        var rig = new Rig(elements: [El("el_22", new Bounds(-32000, -32000, 80, 24))]);

        Func<Task> act = () => Call(rig, tool, elementId: "el_22");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("el_22").And.Contain("off-screen");
        message.Should().MatchRegex("(?i)minimi[sz]", "a minimised window parks its controls far off the desktop");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_window_with_an_empty_rect_says_it_may_have_just_closed(string tool)
    {
        var rig = new Rig(inventory: [Win("Vanishing Window", 0x72, 0, new Bounds(0, 0, 0, 0))]);

        Func<Task> act = () => Call(rig, tool, window: "Vanishing Window");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("Vanishing Window").And.Contain("off-screen");
        message.Should().MatchRegex("(?i)closed|closing", "a window that just closed reports an empty rect");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task A_minimised_window_with_a_huge_title_still_gets_the_restore_instruction_inside_the_cap(string tool)
    {
        var title = new string('T', 5000);
        var rig = new Rig(inventory: [Win(title, 0x73, 0, new Bounds(-32000, -32000, 160, 28), WindowState.Minimized)]);

        Func<Task> act = () => Call(rig, tool, hwnd: 0x73);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Length.Should().BeLessThanOrEqualTo(2000, "ToolErrors caps caller-facing messages at 2,000 characters");
        message.ToLowerInvariant().Should().Contain("restore", "the instruction comes after the name and must survive the cap");
        message.Should().Contain("TTTT", "the title is clipped, not dropped");
    }

    [Theory]
    [MemberData(nameof(BothTools))]
    public async Task An_off_screen_window_with_a_huge_title_still_says_off_screen_inside_the_cap(string tool)
    {
        var title = new string('T', 5000);
        var rig = new Rig(inventory: [Win(title, 0x74, 0, new Bounds(5000, 100, 800, 600))]);

        Func<Task> act = () => Call(rig, tool, hwnd: 0x74);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Length.Should().BeLessThanOrEqualTo(2000);
        message.Should().Contain("off-screen").And.Contain("5000,100,800,600", "the bounds survive too");
    }
}
