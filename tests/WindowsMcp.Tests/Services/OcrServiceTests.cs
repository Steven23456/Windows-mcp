using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-9 (R8): OCR must never see a downscaled image — halving the pixels is exactly what makes
/// small text unreadable to the recognizer. The capture options <see cref="OcrService"/> asks for
/// are therefore a requirement in their own right, and the only place they are observable is the
/// call it makes.
/// </summary>
[Trait("Category", "Unit")]
public class OcrServiceTests
{
    /// <summary>Marks the point in <c>ExtractTextAsync</c> we care about; nothing after the capture is under test.</summary>
    private sealed class CaptureReached : Exception;

    private static Mock<IScreenshotService> ThrowingShotMock()
    {
        var mock = new Mock<IScreenshotService>();
        // Fail the capture on purpose: the WinRT decode/OCR that follows needs an installed
        // language pack and real image bytes, and neither is the subject of this test.
        mock.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CaptureReached());
        return mock;
    }

    [Fact]
    public async Task ExtractTextAsync_captures_png_with_no_size_limit()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);

        Func<Task> act = () => service.ExtractTextAsync(new ScreenRegion(10, 20, 30, 40));

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(
            new ScreenRegion(10, 20, 30, 40),
            It.Is<CaptureOptions>(o => o.Format == ImageFormat.Png && o.MaxWidth == 0 && o.MaxHeight == 0),
            It.IsAny<CancellationToken>()),
            Times.Once, "OCR never downscales: 0 x 0 means no limit, and PNG keeps the glyph edges");
    }

    /// <summary>
    /// A-11 (R5): OCR must never get a cursor drawn on its bitmap — a mouse pointer over a word is
    /// exactly the kind of occlusion that turns recognised text into noise. The rule lives here,
    /// in the service that builds the options, not in the tool.
    /// </summary>
    [Fact]
    public async Task ExtractTextAsync_never_asks_for_the_cursor_to_be_drawn()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);

        Func<Task> act = () => service.ExtractTextAsync(new ScreenRegion(10, 20, 30, 40));

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(),
            It.Is<CaptureOptions>(o => !o.IncludeCursor),
            It.IsAny<CancellationToken>()),
            Times.Once, "the cursor would occlude the very glyphs OCR is reading");
    }

    /// <summary>
    /// A-10 (R4): OCR names no backend, so it takes the process default — <c>auto</c> means the
    /// compositor where it works and GDI where it does not, which is exactly what a text read
    /// wants. Naming one here would freeze OCR on GDI for every server, whatever it was started
    /// with, and would return black pixels for the GPU-accelerated windows A-10 exists for.
    /// </summary>
    [Fact]
    public async Task ExtractTextAsync_leaves_the_backend_at_the_process_default()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);

        Func<Task> act = () => service.ExtractTextAsync(new ScreenRegion(10, 20, 30, 40));

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(),
            It.Is<CaptureOptions>(o => o.Backend == "auto"),
            It.IsAny<CancellationToken>()),
            Times.Once, "the tool has no 'backend' argument for ocr, so the server's own default applies");
    }

    [Fact]
    public async Task ExtractTextAsync_passes_a_null_region_through_unchanged()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);

        Func<Task> act = () => service.ExtractTextAsync();

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(
            null,
            It.Is<CaptureOptions>(o => o.MaxWidth == 0 && o.MaxHeight == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractTextAsync_does_not_capture_when_already_cancelled()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.ExtractTextAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}


/// <summary>
/// A-9's open follow-up, taken up by A-8: <c>OcrService</c>'s real path — capture bytes →
/// <c>BitmapDecoder</c> → <c>SoftwareBitmap</c> → <c>OcrEngine.RecognizeAsync</c> — had no live
/// test at all. Everything above mocks <see cref="IScreenshotService"/> and would stay green if
/// the WinRT chain threw on every call (the <c>disk_inspect mode:reclaimable</c> failure mode in
/// CLAUDE.md). What is asserted is deliberately weak — that the chain RUNS and returns a string —
/// because the recognised text depends on whatever is on the screen; the value is the wiring, not
/// the words.
/// <para>
/// Needs an interactive desktop (<c>CopyFromScreen</c>) and an installed OCR language pack, so it
/// carries the UIAutomation trait and lives in its own class: a vstest <c>Category!=UIAutomation</c>
/// filter does not exclude a test that also carries another Category value.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
public class OcrServiceLiveTests
{
    [Fact]
    public async Task ExtractTextAsync_runs_the_real_decode_and_recognize_path()
    {
        var service = new OcrService(new ScreenshotService());

        var text = await service.ExtractTextAsync(new ScreenRegion(0, 0, 400, 200));

        text.Should().NotBeNull("OcrEngine returns a (possibly empty) string for a blank region, never null");
    }
}
