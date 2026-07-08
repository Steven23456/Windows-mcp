using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ScreenToolsTests
{
    [Fact]
    public async Task Screenshot_returns_base64_png()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var shotMock = new Mock<IScreenshotService>();
        shotMock
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(pngBytes, 100, 100, ImageFormat.Png));

        var tools = new ScreenTools(shotMock.Object, new Mock<IOcrService>().Object);
        // output:"base64" required — the tool now defaults to output:"file" (returns a saved path,
        // no inline data), so this base64-intent test must opt into base64 mode explicitly.
        var result = await tools.Screenshot(null, "png", "base64");

        result.Should().Contain(Convert.ToBase64String(pngBytes));
        result.Should().Contain("100");
    }
}
