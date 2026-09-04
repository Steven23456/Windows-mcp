using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// CaptureAsync calls Graphics.CopyFromScreen, which requires an interactive desktop
// session — it throws Win32Exception "The handle is invalid" under headless/service
// sessions (local non-interactive runs and GitHub-hosted Windows runners alike). That
// is the same constraint as the UIAutomation bucket, so it is categorized here to be
// excluded by the documented headless-safe filter (Category!=UIAutomation), not left
// mislabeled as read-only Integration.
[Trait("Category", "UIAutomation")]
public class ScreenshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_returns_non_empty_png_with_dimensions()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), ImageFormat.Png);

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Png);
        // PNG magic bytes: 89 50 4E 47
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    // A-7 made JPEG the default for inline output, so this is the format nearly every
    // screenshot call now encodes. ScreenToolsTests mocks IScreenshotService, so without this
    // the real SkiaSharp JPEG encode path would ship with no test through the real collaborator
    // at all (the failure mode CLAUDE.md records for disk_inspect mode:reclaimable).
    [Fact]
    public async Task CaptureAsync_jpeg_returns_jpeg_bytes()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), ImageFormat.Jpeg);

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Jpeg);
        // JPEG SOI marker plus the start of the next marker: FF D8 FF
        result.Bytes.Take(3).Should().Equal(new byte[] { 0xFF, 0xD8, 0xFF });
    }
}
