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
