using System.Text;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// A-7: <c>screenshot</c> returns MCP content blocks (<see cref="CallToolResult"/>) instead of a
/// JSON string, so the model sees the picture from one call. Everything here is mocked — the real
/// capture path is covered by <c>ScreenshotServiceTests</c> (UIAutomation) and the transport
/// round-trip by <c>HttpTransportScreenshotImageTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenToolsTests : IDisposable
{
    // 8 bytes that are not valid base64 of anything accidental, and start with the PNG magic.
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    /// <summary>Files the "file" output mode wrote under %TEMP%\WindowsMcp, removed in Dispose.</summary>
    private readonly List<string> _written = [];

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    // ---- harness --------------------------------------------------------------------------

    private static Mock<IScreenshotService> ShotMock(
        byte[]? bytes = null, int width = 100, int height = 100, ImageFormat? resultFormat = null)
    {
        var mock = new Mock<IScreenshotService>();
        mock.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScreenRegion? _, ImageFormat fmt, CancellationToken _) =>
            {
                var effective = resultFormat ?? fmt;
                return new ScreenshotResult(
                    bytes ?? (effective == ImageFormat.Jpeg ? JpegBytes : PngBytes),
                    width, height, effective);
            });
        return mock;
    }

    private static ScreenTools MakeTools(IScreenshotService? shot = null, IOcrService? ocr = null) =>
        new(shot ?? ShotMock().Object, ocr ?? new Mock<IOcrService>().Object);

    private static TextContentBlock TextBlock(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;

    private static ImageContentBlock ImageBlock(CallToolResult result) =>
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;

    /// <summary>
    /// SDK 2.2.0: <c>ImageContentBlock.Data</c> is the <b>base64 UTF-8 bytes</b> that go on the
    /// wire (<c>DecodedData</c> is the raw image). This is the base64 text a client sees.
    /// </summary>
    private static string Base64Of(ImageContentBlock block) => Encoding.UTF8.GetString(block.Data.Span);

    /// <summary>The metadata object carried by the single text block.</summary>
    private static JsonElement Meta(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(TextBlock(result).Text);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "the text block is one JSON object of metadata");
        return doc.RootElement.Clone();
    }

    /// <summary>Records the path a "file" capture wrote so Dispose can clean it up.</summary>
    private string TrackPath(CallToolResult result)
    {
        var path = Meta(result).GetProperty("path").GetString()!;
        _written.Add(path);
        return path;
    }

    // ---- R1 / R2 — inline is the default, text then image ----------------------------------

    [Fact]
    public async Task Screenshot_default_output_is_inline_text_then_image()
    {
        var mock = ShotMock(JpegBytes, 640, 480);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot();

        result.IsError.Should().NotBe(true, "a successful capture is not an error result");
        result.Content.Should().HaveCount(2, "inline returns exactly one metadata block and one image block");
        result.Content[0].Should().BeOfType<TextContentBlock>("the metadata block comes first");
        result.Content[1].Should().BeOfType<ImageContentBlock>("the image block comes second");
    }

    [Fact]
    public async Task Screenshot_inline_image_block_carries_the_captured_bytes()
    {
        var mock = ShotMock(JpegBytes, 640, 480);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, "jpeg");

        var image = ImageBlock(result);
        Base64Of(image).Should().Be(Convert.ToBase64String(JpegBytes), "Data is the base64 of exactly the captured bytes");
        image.DecodedData.ToArray().Should().Equal(JpegBytes);
        image.MimeType.Should().Be("image/jpeg");
    }

    // ---- R10 — the old base64 test, rewritten against the new contract ----------------------

    [Fact]
    public async Task Screenshot_returns_base64_png()
    {
        var mock = ShotMock(PngBytes, 100, 100);
        var tools = MakeTools(mock.Object);

        // output:"base64" is kept as an alias of "inline" for one release (A-7).
        var result = await tools.Screenshot(null, "png", "base64");

        Base64Of(ImageBlock(result)).Should().Be(Convert.ToBase64String(PngBytes));
        ImageBlock(result).MimeType.Should().Be("image/png");
        Meta(result).GetProperty("width").GetInt32().Should().Be(100);
        Meta(result).GetProperty("height").GetInt32().Should().Be(100);
    }

    // ---- R3 — base64 is an alias of inline -------------------------------------------------

    [Fact]
    public async Task Screenshot_base64_output_is_identical_to_inline()
    {
        var tools = MakeTools(ShotMock(PngBytes, 320, 240).Object);

        var inline = await tools.Screenshot("10,20,320,240", "png", "inline");
        var alias = await tools.Screenshot("10,20,320,240", "png", "base64");

        TextBlock(alias).Text.Should().Be(TextBlock(inline).Text, "'base64' is an alias, not a different shape");
        Base64Of(ImageBlock(alias)).Should().Be(Base64Of(ImageBlock(inline)));
        ImageBlock(alias).MimeType.Should().Be(ImageBlock(inline).MimeType);
        alias.Content.Should().HaveCount(inline.Content.Count);
    }

    // ---- R4 — file output ------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_file_output_returns_only_a_text_block()
    {
        var tools = MakeTools(ShotMock(PngBytes, 800, 600).Object);

        var result = await tools.Screenshot(null, "png", "file");

        result.Content.Should().ContainSingle("file mode returns the path only — no image block");
        result.Content[0].Should().BeOfType<TextContentBlock>();
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();

        var meta = Meta(result);
        _written.Add(meta.GetProperty("path").GetString()!);
        meta.GetProperty("width").GetInt32().Should().Be(800);
        meta.GetProperty("height").GetInt32().Should().Be(600);
        meta.GetProperty("format").GetString().Should().Be("png");
        meta.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    [Fact]
    public async Task Screenshot_file_output_writes_the_bytes_under_temp_windowsmcp()
    {
        var tools = MakeTools(ShotMock(PngBytes, 800, 600).Object);

        var result = await tools.Screenshot(null, "png", "file");

        var path = TrackPath(result);
        var expectedDir = Path.Combine(Path.GetTempPath(), "WindowsMcp");
        Path.GetDirectoryName(path).Should().Be(expectedDir.TrimEnd(Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue("the reported path must actually exist");
        (await File.ReadAllBytesAsync(path)).Should().Equal(PngBytes, "the file holds the captured bytes");
    }

    [Theory]
    [InlineData("png", ".png")]
    [InlineData("jpeg", ".jpg")]
    public async Task Screenshot_file_output_extension_matches_the_format(string format, string extension)
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, format, "file");

        TrackPath(result).Should().EndWith(extension);
    }

    // ---- R5 — output validation ------------------------------------------------------------

    [Theory]
    [InlineData("path")]
    [InlineData("")]
    [InlineData("inline ")]
    [InlineData("image")]
    public async Task Screenshot_unknown_output_throws_naming_the_choices(string output)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, "png", output);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("inline").And.Contain("file").And.Contain("base64");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()),
            Times.Never, "the mode is validated before anything is captured");
    }

    [Theory]
    [InlineData("INLINE")]
    [InlineData("Inline")]
    [InlineData("BASE64")]
    public async Task Screenshot_output_matching_is_case_insensitive(string output)
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, "png", output);

        result.Content.Should().HaveCount(2);
        Base64Of(ImageBlock(result)).Should().Be(Convert.ToBase64String(PngBytes));
    }

    [Fact]
    public async Task Screenshot_file_output_matching_is_case_insensitive()
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, "png", "FiLe");

        result.Content.Should().ContainSingle();
        TrackPath(result).Should().EndWith(".png");
    }

    // ---- R6 — format resolution -------------------------------------------------------------

    [Theory]
    [InlineData("inline")]
    [InlineData("base64")]
    public async Task Screenshot_format_auto_resolves_to_jpeg_for_inline_output(string output)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, "auto", output);

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), ImageFormat.Jpeg, It.IsAny<CancellationToken>()), Times.Once);
        ImageBlock(result).MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Screenshot_format_auto_resolves_to_png_for_file_output()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, "auto", "file");

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), ImageFormat.Png, It.IsAny<CancellationToken>()), Times.Once);
        TrackPath(result).Should().EndWith(".png");
    }

    [Fact]
    public async Task Screenshot_format_defaults_to_auto()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot();

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), ImageFormat.Jpeg, It.IsAny<CancellationToken>()),
            Times.Once, "the default output is inline, so the default format resolves to jpeg");
    }

    [Theory]
    [InlineData("png", "inline", ImageFormat.Png)]
    [InlineData("jpeg", "inline", ImageFormat.Jpeg)]
    [InlineData("png", "file", ImageFormat.Png)]
    [InlineData("jpeg", "file", ImageFormat.Jpeg)]
    [InlineData("png", "base64", ImageFormat.Png)]
    [InlineData("jpeg", "base64", ImageFormat.Jpeg)]
    public async Task Screenshot_explicit_format_is_passed_to_capture(string format, string output, ImageFormat expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format, output);
        if (output == "file") TrackPath(result);

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("PNG", ImageFormat.Png)]
    [InlineData("JPEG", ImageFormat.Jpeg)]
    [InlineData("Jpeg", ImageFormat.Jpeg)]
    [InlineData("AUTO", ImageFormat.Jpeg)]
    public async Task Screenshot_format_matching_is_case_insensitive(string format, ImageFormat expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(null, format, "inline");

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("jpg")]      // the extension is 'jpg'; the format name is 'jpeg'
    [InlineData("")]
    [InlineData("png ")]
    public async Task Screenshot_unknown_format_throws_naming_the_choices(string format)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format, "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("png").And.Contain("jpeg").And.Contain("auto");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()),
            Times.Never, "the format is validated before anything is captured");
    }

    // ---- R7 — inline metadata ---------------------------------------------------------------

    [Fact]
    public async Task Screenshot_inline_metadata_reports_size_format_and_coordinate_space()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 1920, 1080).Object);

        var meta = Meta(await tools.Screenshot(null, "jpeg"));

        meta.GetProperty("width").GetInt32().Should().Be(1920);
        meta.GetProperty("height").GetInt32().Should().Be(1080);
        meta.GetProperty("format").GetString().Should().Be("jpeg");
        meta.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Screenshot_metadata_omits_region_when_none_was_given(string? region)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var meta = Meta(await tools.Screenshot(region, "png", "inline"));

        meta.TryGetProperty("region", out _).Should().BeFalse("absent fields are absent, not null");
        mock.Verify(s => s.CaptureAsync(null, It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Screenshot_metadata_carries_the_region_when_one_was_given()
    {
        var tools = MakeTools(ShotMock(PngBytes, 300, 200).Object);

        var meta = Meta(await tools.Screenshot("10,20,300,200", "png", "inline"));

        var region = meta.GetProperty("region");
        region.GetProperty("x").GetInt32().Should().Be(10);
        region.GetProperty("y").GetInt32().Should().Be(20);
        region.GetProperty("width").GetInt32().Should().Be(300);
        region.GetProperty("height").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task Screenshot_file_metadata_carries_the_region_too()
    {
        // Ambiguity resolved: the metadata object has one shape; 'file' adds 'path', a region
        // adds 'region'. Flagged in the A-7 RED report.
        var tools = MakeTools(ShotMock(PngBytes, 300, 200).Object);

        var result = await tools.Screenshot("10,20,300,200", "png", "file");
        TrackPath(result);

        Meta(result).GetProperty("region").GetProperty("x").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Screenshot_inline_metadata_does_not_carry_a_path()
    {
        // The tool description advertises "path? (file output)". Nothing else asserted the
        // absence, so an unconditional meta["path"] would have slipped past the whole suite.
        var tools = MakeTools(ShotMock(JpegBytes, 640, 480).Object);

        var meta = Meta(await tools.Screenshot(null, "jpeg", "inline"));

        meta.TryGetProperty("path", out _).Should().BeFalse("inline output returns bytes, not a file");
    }

    // ---- R8 — region parsing is unchanged ----------------------------------------------------

    [Theory]
    [InlineData("10,20,300,200", 10, 20, 300, 200)]
    [InlineData(" 10 , 20 , 300 , 200 ", 10, 20, 300, 200)]     // TrimEntries
    [InlineData("-1920,-40,640,480", -1920, -40, 640, 480)]     // virtual desktop: negatives are legal
    public async Task Screenshot_passes_the_parsed_region_to_capture(string region, int x, int y, int w, int h)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(region, "png", "inline");

        mock.Verify(s => s.CaptureAsync(
            new ScreenRegion(x, y, w, h), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("1")]
    public async Task Screenshot_invalid_region_throws_and_never_captures(string region)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(region, "png", "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("region").And.Contain("x,y,w,h");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- edge: the reported format follows what the service actually encoded ------------------

    [Fact]
    public async Task Screenshot_mime_and_metadata_follow_the_encoded_result_not_the_request()
    {
        // The service is the authority on what it encoded (today's code reads result.Format for
        // both the extension and the reported format). If a backend ever falls back, the image
        // block must not claim a mime type the bytes do not have.
        var mock = ShotMock(PngBytes, 100, 100, resultFormat: ImageFormat.Png);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, "jpeg", "inline");

        ImageBlock(result).MimeType.Should().Be("image/png");
        Meta(result).GetProperty("format").GetString().Should().Be("png");
    }

    // ---- R9 — ocr is unchanged ---------------------------------------------------------------

    [Fact]
    public async Task Ocr_passes_the_region_and_returns_the_service_text()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello world");
        var tools = MakeTools(ocr: ocr.Object);

        var text = await tools.Ocr("5,6,7,8");

        text.Should().Be("hello world");
        ocr.Verify(s => s.ExtractTextAsync(new ScreenRegion(5, 6, 7, 8), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ocr_invalid_region_throws()
    {
        var ocr = new Mock<IOcrService>();
        var tools = MakeTools(ocr: ocr.Object);

        Func<Task> act = () => tools.Ocr("1,2,3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("x,y,w,h");
        ocr.Verify(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
