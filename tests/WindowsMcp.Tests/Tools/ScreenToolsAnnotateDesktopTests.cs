using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Moq;
using SkiaSharp;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// A-6 (R6) end to end on a live desktop: the real <c>ScreenshotService</c>, the real
/// <c>WindowService</c>/<c>InputService</c> and the real <c>UIAutomationService</c>, with Notepad
/// in the foreground. Everything else about annotate is proven with mocks
/// (<c>ScreenToolsTests</c>) or on synthetic bitmaps (<c>AnnotatorTests</c>); this is the one test
/// that proves the whole chain — walk the desktop, filter to the captured rect, draw, encode —
/// actually produces a picture with a box in it. Needs the interactive desktop, so
/// <c>Category=UIAutomation</c>: excluded from headless runs.
/// </summary>
[Trait("Category", "UIAutomation")]
// DesktopCollection: it opens a Notepad window through the fixture AND asserts on captured
// pixels, both halves of that collection's membership rule.
[Collection(DesktopCollection.Name)]
public class ScreenToolsAnnotateDesktopTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    public ScreenToolsAnnotateDesktopTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    /// <summary>
    /// The real graph — only OCR and the A-14 flash are mocked: nothing here calls OCR, and a real
    /// glow would paint on the desktop these tests are capturing for reasons unrelated to A-6.
    /// </summary>
    private static ScreenTools RealTools(UIAutomationService uia) =>
        new(new ScreenshotService(), new Mock<IOcrService>().Object,
            new WindowService(), new InputService(), uia, new Mock<IFlashOverlay>().Object);

    private static UIAutomationService NewUia() => new(new InputService(), new WindowService());

    private static JsonElement Meta(CallToolResult result)
    {
        var block = result.Content[0].Should().BeOfType<TextContentBlock>().Subject;
        using var doc = JsonDocument.Parse(block.Text);
        return doc.RootElement.Clone();
    }

    private static string SnapshotText(CallToolResult result) =>
        result.Content[1].Should().BeOfType<TextContentBlock>().Subject.Text;

    private static bool Contains(SKBitmap bmp, SKColor colour)
    {
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) == colour) return true;
        return false;
    }

    private static int CountOf(SKBitmap bmp, SKColor colour)
    {
        var count = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) == colour) count++;
        return count;
    }

    private async Task<string> NotepadRegionAsync()
    {
        // Prefer the window the fixture opened: the title search finds an arbitrary Notepad
        // window on a desktop that has more than one, and the region would frame the wrong app.
        var listed = await new WindowService().ListAsync();
        var window = listed.FirstOrDefault(w => w.Hwnd == _np.Hwnd)
            ?? listed.FirstOrDefault(w => w.Title.Contains("Notepad", StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException("Notepad has no listed window");
        var b = window.Bounds;
        return $"{b.X},{b.Y},{b.Width},{b.Height}";
    }

    [Fact]
    public async Task Screenshot_annotate_over_notepad_lists_the_editor_and_draws_a_box_for_it()
    {
        using var uia = NewUia();
        var tools = RealTools(uia);

        var result = await tools.Screenshot(
            region: await NotepadRegionAsync(), format: "png", output: "inline", annotate: true);

        result.Content.Should().HaveCount(3);

        var text = SnapshotText(result);
        Regex.IsMatch(text, @"^\s*(el_\d+) \(-?\d+,-?\d+\) (document|edit) ", RegexOptions.Multiline).Should()
            .BeTrue("the editor is an interactive element of the window the picture is of; got:\n{0}", text);

        var meta = Meta(result);
        meta.GetProperty("annotated").GetBoolean().Should().BeTrue();
        meta.GetProperty("annotations").GetInt32().Should().BeGreaterThanOrEqualTo(1,
            "at least the editor's box landed on the image");

        var image = result.Content[2].Should().BeOfType<ImageContentBlock>().Subject;
        using var decoded = SKBitmap.Decode(image.DecodedData.ToArray());
        CountOf(decoded, Annotator.ColorFor(0)).Should().BeGreaterThan(20,
            "the first box is always drawn when anything was, and a 2 px stroke plus a chip is far more than 20 px");
    }

    [Fact]
    public async Task Screenshot_annotate_of_the_primary_display_annotates_something()
    {
        // The default agent-loop call: no region, no display, just annotate. Notepad is in the
        // foreground on the primary display, so there is always at least one element to box.
        using var uia = NewUia();
        var tools = RealTools(uia);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        result.Content.Should().HaveCount(3);
        Meta(result).GetProperty("annotations").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        var image = result.Content[2].Should().BeOfType<ImageContentBlock>().Subject;
        using var decoded = SKBitmap.Decode(image.DecodedData.ToArray());
        Contains(decoded, Annotator.ColorFor(0)).Should().BeTrue("the first box is on the picture");
    }

    [Fact]
    public async Task Screenshot_annotate_with_a_grid_still_returns_the_three_blocks()
    {
        using var uia = NewUia();
        var tools = RealTools(uia);

        var result = await tools.Screenshot(
            format: "png", output: "inline", annotate: true, grid_columns: 4, grid_rows: 3);

        result.Content.Should().HaveCount(3);
        var grid = Meta(result).GetProperty("grid");
        grid.GetProperty("columns").GetInt32().Should().Be(4);
        grid.GetProperty("rows").GetInt32().Should().Be(3);
    }
}
