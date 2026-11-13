using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-7 (docs/design/E-7-capture-targets-ocr-words.md, "OCR positions / Tool"): <c>ocr</c> gains
/// <c>format</c>. <c>text</c> (the default) is exactly today's output through
/// <see cref="IOcrService.ExtractTextAsync"/>; <c>json</c> is one object with every line and word
/// in virtual-desktop pixels, built from <see cref="IOcrService.RecognizeAsync"/>, with integer
/// word centres that go straight to <c>click</c>. The engine side is mocked here; the real engine
/// is <c>OcrServiceRecognizeIntegrationTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenToolsOcrJsonTests
{
    private static WindowInfo Win(string title, long hwnd, Bounds bounds, WindowState state = WindowState.Normal) =>
        new(title, hwnd, 4242, "proc", state, bounds, 0, IsActive: true, IsBrowser: false, MonitorIndex: 0);

    private sealed class Rig
    {
        public Mock<IOcrService> Ocr { get; } = new();
        public Mock<IWindowService> Windows { get; } = new(MockBehavior.Strict);
        public Mock<IUIAutomationService> Uia { get; } = new(MockBehavior.Strict);
        public Mock<IScreenshotService> Shot { get; } = new();
        public Mock<IInputService> Input { get; } = new();
        public Mock<IFlashOverlay> Flash { get; } = new();
        public ScreenTools Tools { get; }

        /// <param name="recognized">What the engine returns; its Region is replaced by the rect the tool asked for.</param>
        public Rig(OcrResult? recognized = null, MonitorInfo[]? monitors = null, WindowInfo[]? inventory = null,
                   ElementInfo[]? elements = null, string text = "plain text\r\nsecond line")
        {
            Windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(monitors ?? ScreenToolsTests.SingleMonitor);
            Windows.Setup(w => w.ListAsync(true, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(inventory ?? []);
            var byId = (elements ?? []).ToDictionary(e => e.ElementId);
            Uia.Setup(u => u.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) => byId.TryGetValue(id, out var e)
                    ? Task.FromResult(e)
                    : Task.FromException<ElementInfo>(new KeyNotFoundException($"Element '{id}' not found")));
            Ocr.Setup(o => o.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>())).ReturnsAsync(text);
            Ocr.Setup(o => o.RecognizeAsync(It.IsAny<ScreenRegion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ScreenRegion r, CancellationToken _) =>
                    (recognized ?? new OcrResult("", [], r)) with { Region = r });
            Tools = new ScreenTools(Shot.Object, Ocr.Object, Windows.Object, Input.Object, Uia.Object, Flash.Object);
        }
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "format:json is one JSON object");
        return doc.RootElement.Clone();
    }

    private static OcrWord Word(string text, int x, int y, int w, int h) => new(text, new Bounds(x, y, w, h));

    /// <summary>Two lines, three words, at the origin of the primary display.</summary>
    private static OcrResult Sample => new(
        "Hello world\r\nSecond",
        [
            new OcrLine("Hello world", new Bounds(10, 20, 104, 12), [Word("Hello", 10, 20, 50, 12), Word("world", 66, 20, 48, 12)]),
            new OcrLine("Second", new Bounds(10, 40, 61, 13), [Word("Second", 10, 40, 61, 13)]),
        ],
        new ScreenRegion(0, 0, 1920, 1080));

    // ---- R37 — text: exactly today's output -----------------------------------------------------

    [Fact]
    public async Task Format_defaults_to_text_and_returns_exactly_the_extracted_text()
    {
        var rig = new Rig();

        var text = await rig.Tools.Ocr();

        text.Should().Be("plain text\r\nsecond line", "the text mode is the recognised text, nothing else");
        rig.Ocr.Verify(o => o.RecognizeAsync(It.IsAny<ScreenRegion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Format_text_with_a_window_target_returns_the_extracted_text_of_the_windows_rect()
    {
        var rig = new Rig(inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))]);

        var text = await rig.Tools.Ocr(window: "Notepad", format: "text");

        text.Should().Be("plain text\r\nsecond line", "a target changes the rect, not the shape of the text mode");
        rig.Ocr.Verify(o => o.ExtractTextAsync(new ScreenRegion(200, 150, 640, 480), It.IsAny<CancellationToken>()), Times.Once);
        rig.Ocr.Verify(o => o.RecognizeAsync(It.IsAny<ScreenRegion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("Text")]
    public async Task Format_text_is_matched_ignoring_case(string format)
    {
        // Every other enum-like argument in this tool class (format, output, backend) ignores case.
        var rig = new Rig();

        var text = await rig.Tools.Ocr(format: format);

        text.Should().Be("plain text\r\nsecond line");
    }

    // ---- R38 — anything else is refused before anything is read --------------------------------

    [Theory]
    [InlineData("xml")]
    [InlineData("")]
    [InlineData(" json")]
    [InlineData("jsonl")]
    [InlineData("html")]
    public async Task An_unknown_format_is_refused_naming_the_choices_before_any_capture_or_lookup(string format)
    {
        var rig = new Rig(inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))]);

        Func<Task> act = () => rig.Tools.Ocr(window: "Notepad", format: format);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("format").And.Contain("text").And.Contain("json");
        rig.Ocr.VerifyNoOtherCalls();
        rig.Windows.VerifyNoOtherCalls();   // not the inventory, not the monitors: an argument error costs nothing
        rig.Uia.VerifyNoOtherCalls();
    }

    // ---- R39/R43 — json: the shape -----------------------------------------------------------------

    [Fact]
    public async Task Format_json_calls_RecognizeAsync_with_the_resolved_rect_and_never_ExtractTextAsync()
    {
        var rig = new Rig(Sample, monitors: ScreenToolsTests.SideBySide);

        await rig.Tools.Ocr(display: "1", format: "json");

        rig.Ocr.Verify(o => o.RecognizeAsync(new ScreenRegion(1920, 0, 1920, 1080), It.IsAny<CancellationToken>()), Times.Once);
        rig.Ocr.Verify(o => o.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never,
            "one engine pass: the json mode's text comes from the same recognition as its words");
    }

    [Fact]
    public async Task Format_json_reports_the_coordinate_space_the_region_and_the_text()
    {
        var rig = new Rig(Sample);

        var root = Parse(await rig.Tools.Ocr(format: "json"));

        root.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
        var region = root.GetProperty("region");
        region.GetProperty("x").GetInt32().Should().Be(0);
        region.GetProperty("y").GetInt32().Should().Be(0);
        region.GetProperty("width").GetInt32().Should().Be(1920);
        region.GetProperty("height").GetInt32().Should().Be(1080);
        root.GetProperty("text").GetString().Should().Be("Hello world\r\nSecond");
    }

    [Fact]
    public async Task Format_json_lists_every_line_with_its_bounds_and_words_in_order()
    {
        var rig = new Rig(Sample);

        var lines = Parse(await rig.Tools.Ocr(format: "json")).GetProperty("lines");

        lines.GetArrayLength().Should().Be(2);
        var first = lines[0];
        first.GetProperty("text").GetString().Should().Be("Hello world");
        first.GetProperty("x").GetInt32().Should().Be(10);
        first.GetProperty("y").GetInt32().Should().Be(20);
        first.GetProperty("width").GetInt32().Should().Be(104);
        first.GetProperty("height").GetInt32().Should().Be(12);
        first.GetProperty("words").EnumerateArray().Select(w => w.GetProperty("text").GetString())
            .Should().Equal("Hello", "world");
        lines[1].GetProperty("text").GetString().Should().Be("Second");
        lines[1].GetProperty("words").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Format_json_words_carry_their_bounds_and_integer_centres()
    {
        var rig = new Rig(Sample);

        var words = Parse(await rig.Tools.Ocr(format: "json")).GetProperty("lines")[0].GetProperty("words");

        var hello = words[0];
        hello.GetProperty("x").GetInt32().Should().Be(10);
        hello.GetProperty("y").GetInt32().Should().Be(20);
        hello.GetProperty("width").GetInt32().Should().Be(50);
        hello.GetProperty("height").GetInt32().Should().Be(12);
        hello.GetProperty("centerX").GetInt32().Should().Be(35);
        hello.GetProperty("centerY").GetInt32().Should().Be(26);
        words[1].GetProperty("centerX").GetInt32().Should().Be(90, "66 + 48/2");
    }

    [Theory]
    [InlineData(10, 20, 5, 7, 12, 23)]            // odd sizes: width/2 and height/2 truncate
    [InlineData(10, 20, 1, 1, 10, 20)]            // a 1 px word's centre is its own pixel
    [InlineData(-101, -41, 5, 7, -99, -38)]       // x + width/2, NOT (2x + width)/2 (= -98 truncating toward zero)
    [InlineData(-1920, -40, 30, 10, -1905, -35)]  // a word at a negative-origin monitor's corner
    public async Task Word_centres_are_x_plus_half_width_by_integer_division(int x, int y, int w, int h, int cx, int cy)
    {
        var result = new OcrResult("w", [new OcrLine("w", new Bounds(x, y, w, h), [Word("w", x, y, w, h)])], new ScreenRegion(0, 0, 1, 1));
        var rig = new Rig(result, monitors: ScreenToolsTests.LeftOfPrimary);

        var word = Parse(await rig.Tools.Ocr(display: "all", format: "json")).GetProperty("lines")[0].GetProperty("words")[0];

        word.GetProperty("centerX").GetInt32().Should().Be(cx);
        word.GetProperty("centerY").GetInt32().Should().Be(cy);
    }

    [Fact]
    public async Task Format_json_region_is_the_rect_the_target_resolved_to()
    {
        var rig = new Rig(Sample, inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))]);

        var region = Parse(await rig.Tools.Ocr(window: "Notepad", format: "json")).GetProperty("region");

        region.GetProperty("x").GetInt32().Should().Be(200);
        region.GetProperty("y").GetInt32().Should().Be(150);
        region.GetProperty("width").GetInt32().Should().Be(640);
        region.GetProperty("height").GetInt32().Should().Be(480);
        rig.Ocr.Verify(o => o.RecognizeAsync(new ScreenRegion(200, 150, 640, 480), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("Json")]
    public async Task Format_json_is_matched_ignoring_case(string format)
    {
        var rig = new Rig(Sample);

        var root = Parse(await rig.Tools.Ocr(format: format));

        root.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    // ---- R40 — target / targetClipped, as for screenshot ----------------------------------------------

    [Fact]
    public async Task Format_json_carries_the_window_target()
    {
        var rig = new Rig(Sample, inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))]);

        var target = Parse(await rig.Tools.Ocr(window: "Notepad", format: "json")).GetProperty("target");

        target.GetProperty("kind").GetString().Should().Be("window");
        target.GetProperty("title").GetString().Should().Be("Notepad");
        target.GetProperty("hwnd").GetInt64().Should().Be(0x20);
        target.GetProperty("matchStrategy").GetString().Should().Be("exact");
        target.GetProperty("score").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Format_json_carries_the_element_target()
    {
        var rig = new Rig(Sample, elements: [new ElementInfo("el_3", "Status", "Text", true, false, new Bounds(5, 1040, 300, 30), null, null, null)]);

        var target = Parse(await rig.Tools.Ocr(element_id: "el_3", format: "json")).GetProperty("target");

        target.GetProperty("kind").GetString().Should().Be("element");
        target.GetProperty("elementId").GetString().Should().Be("el_3");
        target.GetProperty("name").GetString().Should().Be("Status");
        target.GetProperty("controlType").GetString().Should().Be("Text");
    }

    [Fact]
    public async Task Format_json_says_targetClipped_when_the_target_was_clipped()
    {
        var rig = new Rig(Sample, inventory: [Win("Big", 0x21, new Bounds(-8, -8, 1936, 1096), WindowState.Maximized)]);

        var root = Parse(await rig.Tools.Ocr(window: "Big", format: "json"));

        root.GetProperty("targetClipped").GetBoolean().Should().BeTrue();
        root.GetProperty("region").GetProperty("width").GetInt32().Should().Be(1920);
    }

    [Fact]
    public async Task Format_json_has_no_target_or_targetClipped_without_a_target()
    {
        var rig = new Rig(Sample);

        var root = Parse(await rig.Tools.Ocr(region: "0,0,800,600", format: "json"));

        root.TryGetProperty("target", out _).Should().BeFalse();
        root.TryGetProperty("targetClipped", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Format_json_has_no_targetClipped_when_the_target_fits()
    {
        var rig = new Rig(Sample, inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))]);

        var root = Parse(await rig.Tools.Ocr(window: "Notepad", format: "json"));

        root.TryGetProperty("targetClipped", out _).Should().BeFalse();
    }

    // ---- R41 — textAngle only when the engine reports one ---------------------------------------------

    [Fact]
    public async Task Format_json_carries_textAngle_when_the_engine_reports_one()
    {
        var rig = new Rig(Sample with { TextAngle = -2.5 });

        var root = Parse(await rig.Tools.Ocr(format: "json"));

        root.GetProperty("textAngle").GetDouble().Should().Be(-2.5);
    }

    [Fact]
    public async Task Format_json_omits_textAngle_when_the_engine_reports_none()
    {
        var rig = new Rig(Sample with { TextAngle = null });

        var root = Parse(await rig.Tools.Ocr(format: "json"));

        root.TryGetProperty("textAngle", out _).Should().BeFalse("absent, not null");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]   // what Windows.Media.Ocr actually reports for straight text (measured, review F2)
    public async Task Format_json_omits_textAngle_when_the_engine_reports_zero(string text)
    {
        // Decided in review round 2: textAngle is the signal that positions are approximate, so
        // straight text must not carry it — otherwise every read would say "approximate". Strings,
        // because xUnit's analyzer treats InlineData(0.0) and InlineData(-0.0) as duplicates.
        var angle = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        if (text == "-0") double.IsNegative(angle).Should().BeTrue("precondition: this row really is negative zero");
        var rig = new Rig(Sample with { TextAngle = angle });

        var json = await rig.Tools.Ocr(format: "json");

        Parse(json).TryGetProperty("textAngle", out _).Should().BeFalse();
        json.Should().NotContain("-0", "negative zero must not leak into the wire either");
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    public async Task Format_json_carries_even_a_small_non_zero_textAngle(double angle)
    {
        var rig = new Rig(Sample with { TextAngle = angle });

        Parse(await rig.Tools.Ocr(format: "json")).GetProperty("textAngle").GetDouble().Should().Be(angle);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Format_json_with_a_non_finite_textAngle_succeeds_without_the_key(string text)
    {
        // Review round 3 (R3): System.Text.Json refuses a non-finite double, so emitting one fails
        // the whole call. A non-finite angle is no angle: the read succeeds and carries no textAngle.
        var angle = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var rig = new Rig(Sample with { TextAngle = angle });

        var root = Parse(await rig.Tools.Ocr(format: "json"));

        root.TryGetProperty("textAngle", out _).Should().BeFalse();
        root.GetProperty("lines").GetArrayLength().Should().Be(2, "the rest of the result is intact");
    }

    // ---- R42 — nothing recognised is an empty result, not an error ------------------------------------

    [Fact]
    public async Task Format_json_with_nothing_recognised_is_empty_text_and_no_lines()
    {
        var rig = new Rig(new OcrResult("", [], new ScreenRegion(0, 0, 1, 1)));

        var root = Parse(await rig.Tools.Ocr(format: "json"));

        root.GetProperty("text").GetString().Should().Be("");
        root.GetProperty("lines").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("lines").GetArrayLength().Should().Be(0);
        root.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    // ---- what ocr still does not do -------------------------------------------------------------------

    [Fact]
    public async Task Format_json_never_reads_the_cursor_or_takes_a_screenshot_and_only_hides_the_flash()
    {
        // E-7 review F1 replaced "never touches the flash" (which pinned the bug): Hide once, no Show.
        var rig = new Rig(Sample);

        await rig.Tools.Ocr(format: "json");

        rig.Input.Invocations.Should().BeEmpty();
        rig.Shot.Invocations.Should().BeEmpty("OCR captures through its own service");
        rig.Flash.Verify(f => f.Hide(), Times.Once);
        rig.Flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    /// <summary>
    /// E-7 review F1: the glow of a previous screenshot is a top-most frame; an ocr whose rect
    /// contains it would read it. So ocr hides it before the OCR service runs — both formats, with
    /// or without a target — and never shows one of its own.
    /// </summary>
    [Theory]
    [InlineData("text", null, null)]
    [InlineData("json", null, null)]
    [InlineData("text", "Notepad", null)]
    [InlineData("json", "Notepad", null)]
    [InlineData("text", null, "el_3")]
    [InlineData("json", null, "el_3")]
    public async Task Ocr_hides_the_flash_before_the_read_in_both_formats_and_never_shows_one(string format, string? window, string? elementId)
    {
        var rig = new Rig(Sample,
            inventory: [Win("Notepad", 0x20, new Bounds(200, 150, 640, 480))],
            elements: [new ElementInfo("el_3", "Status", "Text", true, false, new Bounds(5, 1040, 300, 30), null, null, null)]);
        var order = new List<string>();
        rig.Flash.Setup(f => f.Hide()).Callback(() => order.Add("hide"));
        rig.Ocr.Setup(o => o.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("read"); return "t"; });
        rig.Ocr.Setup(o => o.RecognizeAsync(It.IsAny<ScreenRegion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScreenRegion r, CancellationToken _) => { order.Add("read"); return Sample with { Region = r }; });

        await rig.Tools.Ocr(window: window, element_id: elementId, format: format);

        order.Should().Equal(new[] { "hide", "read" }, "the glow is gone before the pixels are read, and hidden once");
        rig.Flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    // ---- GREEN — unspecified cases, pinned -------------------------------------------------------

    [Fact]
    public async Task A_null_format_is_refused_naming_the_choices_before_anything_is_read()
    {
        // A client that sends "format": null gets the same caller-facing refusal as an unknown
        // value (screenshot's own format has no such guard). Pinned: null is not silently "text".
        var rig = new Rig();

        Func<Task> act = () => rig.Tools.Ocr(format: null!);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("text").And.Contain("json");
        rig.Ocr.VerifyNoOtherCalls();
        rig.Windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Format_json_region_is_the_rect_the_service_reports_it_read()
    {
        // OcrResult.Region is "the rect that was captured"; the json reports that, and it is the rect
        // the tool asked for. Asserted here against a resolved display so a tool that reported the
        // primary display instead would fail.
        var rig = new Rig(Sample, monitors: ScreenToolsTests.LeftOfPrimary);

        var region = Parse(await rig.Tools.Ocr(display: "1", format: "json")).GetProperty("region");

        region.GetProperty("x").GetInt32().Should().Be(-1920);
        region.GetProperty("y").GetInt32().Should().Be(-40);
        region.GetProperty("width").GetInt32().Should().Be(1920);
        region.GetProperty("height").GetInt32().Should().Be(1080);
    }

    [Fact]
    public async Task Format_json_keeps_line_bounds_from_the_service_and_does_not_recompute_them()
    {
        // The tool is a projection of OcrResult: a line's x/y/width/height are the service's
        // (the union, OcrGeometry.ToLines), word centres are computed per word.
        var result = new OcrResult("a b",
            [new OcrLine("a b", new Bounds(100, 200, 300, 40), [Word("a", 100, 210, 20, 20), Word("b", 380, 200, 20, 40)])],
            new ScreenRegion(0, 0, 1, 1));
        var rig = new Rig(result);

        var line = Parse(await rig.Tools.Ocr(format: "json")).GetProperty("lines")[0];

        line.GetProperty("x").GetInt32().Should().Be(100);
        line.GetProperty("width").GetInt32().Should().Be(300);
        line.GetProperty("height").GetInt32().Should().Be(40);
        line.GetProperty("words")[1].GetProperty("centerY").GetInt32().Should().Be(220);
    }
}

/// <summary>
/// E-7 GREEN: <c>ocr(format:"json")</c> end to end through the REAL <see cref="WindowsMcp.Services.OcrService"/>
/// and the real Windows.Media.Ocr engine, with no screen: the pixels are the synthetic frame of
/// <see cref="WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests"/>, handed over by a fixed
/// capture. Every row of <see cref="ScreenToolsOcrJsonTests"/> mocks <see cref="IOcrService"/>; this is
/// the one that proves the tool, the service, the geometry and the engine compose — a word's
/// centre, as the model receives it, lies on that word, in the region's virtual-desktop space.
/// Needs a language pack, no desktop and no input: <c>Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ScreenToolsOcrJsonIntegrationTests
{
    [Fact]
    public async Task Ocr_json_through_the_real_engine_puts_every_word_centre_on_its_word_in_virtual_desktop_pixels()
    {
        const int w = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FrameWidth;
        const int h = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FrameHeight;
        var png = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.RenderFrame(
            WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.Phrase, out _);
        var ocr = new WindowsMcp.Services.OcrService(new WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FixedFrame(png, w, h, 1.0));
        var windows = new Mock<IWindowService>();
        windows.Setup(m => m.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ScreenToolsTests.LeftOfPrimary);
        var tools = new ScreenTools(new Mock<IScreenshotService>().Object, ocr, windows.Object, new Mock<IInputService>().Object,
            new Mock<IUIAutomationService>().Object, new Mock<IFlashOverlay>().Object);

        // A region on the secondary, left of and above the primary: negative virtual-desktop space.
        using var doc = JsonDocument.Parse(await tools.Ocr(region: $"-1500,-30,{w},{h}", format: "json"));
        var root = doc.RootElement;

        root.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
        root.GetProperty("region").GetProperty("x").GetInt32().Should().Be(-1500);
        root.GetProperty("text").GetString()!.ToUpperInvariant().Should().Contain("QUOKKA").And.Contain("ZEBRA");
        var words = root.GetProperty("lines").EnumerateArray().SelectMany(l => l.GetProperty("words").EnumerateArray()).ToArray();
        words.Should().NotBeEmpty();
        foreach (var word in words)
        {
            int x = word.GetProperty("x").GetInt32(), y = word.GetProperty("y").GetInt32();
            int cx = word.GetProperty("centerX").GetInt32(), cy = word.GetProperty("centerY").GetInt32();
            cx.Should().BeInRange(x, x + word.GetProperty("width").GetInt32() - 1, "the centre is a pixel of the word");
            cy.Should().BeInRange(y, y + word.GetProperty("height").GetInt32() - 1);
            cx.Should().BeInRange(-1500, -1500 + w - 1, "and of the region that was read");
            cy.Should().BeInRange(-30, -30 + h - 1);
        }
    }

    private static ScreenTools ToolsOver(byte[] png, int width, int height)
    {
        var ocr = new WindowsMcp.Services.OcrService(new WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FixedFrame(png, width, height, 1.0));
        var windows = new Mock<IWindowService>();
        windows.Setup(m => m.EnumerateMonitorsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ScreenToolsTests.SingleMonitor);
        return new ScreenTools(new Mock<IScreenshotService>().Object, ocr, windows.Object, new Mock<IInputService>().Object,
            new Mock<IUIAutomationService>().Object, new Mock<IFlashOverlay>().Object);
    }

    [Fact]
    public async Task Ocr_json_of_straight_text_through_the_real_engine_has_no_textAngle()
    {
        // The engine reports TextAngle = -0 for straight text; the tool must drop it (round-2 decision).
        const int w = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FrameWidth;
        const int h = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.FrameHeight;
        var png = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.RenderFrame(
            WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.Phrase, out _);

        using var doc = JsonDocument.Parse(await ToolsOver(png, w, h).Ocr(region: $"0,0,{w},{h}", format: "json"));

        doc.RootElement.GetProperty("text").GetString()!.ToUpperInvariant().Should().Contain("ZEBRA", "precondition: the engine read it");
        doc.RootElement.TryGetProperty("textAngle", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Ocr_json_of_tilted_text_through_the_real_engine_reports_the_angle_and_centres_on_the_glyphs()
    {
        // 15° clockwise: the engine measured 15.5 on the RED run. The centre the tool hands to
        // click must be within 10 px of where the word was drawn (the rotation fix, end to end).
        const int w = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.TiltWidth;
        const int h = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.TiltHeight;
        var png = WindowsMcp.Tests.Services.OcrServiceRecognizeIntegrationTests.RenderRotated(15, out var drawn);

        using var doc = JsonDocument.Parse(await ToolsOver(png, w, h).Ocr(region: $"0,0,{w},{h}", format: "json"));

        doc.RootElement.GetProperty("textAngle").GetDouble().Should().BeInRange(13, 17);
        var words = doc.RootElement.GetProperty("lines").EnumerateArray().SelectMany(l => l.GetProperty("words").EnumerateArray()).ToArray();
        foreach (var truth in drawn)
        {
            var word = words.First(x => new string(x.GetProperty("text").GetString()!.Where(char.IsLetter).ToArray())
                .Equals(truth.Text, StringComparison.OrdinalIgnoreCase));
            double dx = word.GetProperty("centerX").GetInt32() - truth.CenterX, dy = word.GetProperty("centerY").GetInt32() - truth.CenterY;
            Math.Sqrt(dx * dx + dy * dy).Should().BeLessThanOrEqualTo(10, $"{truth.Text}'s centre must be on its glyphs");
        }
    }
}
