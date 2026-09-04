using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-9 extracted the resize and the encode out of <see cref="ScreenshotService.CaptureAsync"/>
/// (the A-7 follow-up "extract the encode step"), so both are testable on a synthetic bitmap with
/// no desktop — unlike <c>ScreenshotServiceTests</c>, which needs a real screen. These are the
/// fast regression net; the capture path only wires them together. The one part of
/// <c>CaptureAsync</c> itself that needs no desktop — the cancellation guard that runs before the
/// screen is touched — is at the bottom.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenshotEncodeTests
{
    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(color);
        return bmp;
    }

    /// <summary>Deterministic noise — a solid or gradient bitmap compresses the same at every JPEG quality.</summary>
    private static SKBitmap Noise(int width, int height, int seed = 12345)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var random = new Random(seed);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor(
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255));
            }
        }
        return bmp;
    }

    // ---- R3a — Downscale ---------------------------------------------------------------------

    [Theory]
    [InlineData(100, 50, 50, 25)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(64, 64, 1, 1)]
    public void Downscale_returns_a_bitmap_of_exactly_the_requested_size(int srcW, int srcH, int dstW, int dstH)
    {
        using var src = Solid(srcW, srcH, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, dstW, dstH);

        dst.Width.Should().Be(dstW);
        dst.Height.Should().Be(dstH);
    }

    [Fact]
    public void Downscale_returns_a_new_bitmap_and_leaves_the_source_untouched()
    {
        using var src = Solid(200, 100, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, 100, 50);

        dst.Should().NotBeSameAs(src, "the caller owns and disposes the result");
        src.Width.Should().Be(200, "the source must survive the resize — the capture buffer is still mapped");
        src.Height.Should().Be(100);
    }

    [Fact]
    public void Downscale_preserves_the_colour_of_a_solid_bitmap()
    {
        using var src = Solid(200, 100, SKColors.Red);

        using var dst = ScreenshotService.Downscale(src, 100, 50);

        var centre = dst.GetPixel(50, 25);
        centre.Red.Should().BeGreaterThan(200, "solid red must still be red after the resize");
        centre.Green.Should().BeLessThan(60);
        centre.Blue.Should().BeLessThan(60);
    }

    // ---- R3b — Encode ------------------------------------------------------------------------

    [Fact]
    public void Encode_png_starts_with_the_png_magic_bytes()
    {
        using var bmp = Solid(32, 32, SKColors.Blue);

        var bytes = ScreenshotService.Encode(bmp, ImageFormat.Png, 90);

        bytes.Should().NotBeEmpty();
        bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    [Fact]
    public void Encode_jpeg_starts_with_the_jpeg_soi_marker()
    {
        using var bmp = Solid(32, 32, SKColors.Blue);

        var bytes = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 90);

        bytes.Should().NotBeEmpty();
        bytes.Take(2).Should().Equal(new byte[] { 0xFF, 0xD8 });
    }

    [Fact]
    public void Encode_jpeg_quality_changes_the_size_of_the_output()
    {
        // The A-9 'quality' parameter is worthless if it is not actually handed to the encoder;
        // a hardcoded 90 (what A-7 shipped) makes these two arrays identical in length.
        using var bmp = Noise(256, 256);

        var low = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 30);
        var high = ScreenshotService.Encode(bmp, ImageFormat.Jpeg, 95);

        low.Length.Should().BeLessThan(high.Length, "a lower JPEG quality must produce fewer bytes");
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg, 0)]
    [InlineData(ImageFormat.Jpeg, -1)]
    [InlineData(ImageFormat.Jpeg, 101)]
    [InlineData(ImageFormat.Png, 0)]
    [InlineData(ImageFormat.Png, 101)]
    public void Encode_rejects_a_quality_outside_1_to_100(ImageFormat format, int quality)
    {
        using var bmp = Solid(8, 8, SKColors.Green);

        // Ambiguity resolved (flagged in the RED report): quality is validated for BOTH formats,
        // so an out-of-range value cannot slip through by choosing png.
        var act = () => ScreenshotService.Encode(bmp, format, quality);

        var ex = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("quality");
        ex.Message.Should().Contain("100", "the message must name the 1-100 range");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Encode_accepts_the_inclusive_quality_bounds(int quality)
    {
        using var bmp = Solid(8, 8, SKColors.Green);

        ScreenshotService.Encode(bmp, ImageFormat.Jpeg, quality).Should().NotBeEmpty();
    }

    [Fact]
    public void Encode_png_ignores_the_quality_argument()
    {
        // The tool advertises "quality: JPEG encoder quality, 1-100 (default 90); ignored for
        // png". Same bitmap, opposite ends of the range: PNG is lossless, so the bytes must be
        // identical — if a future change routed quality into the PNG encoder the promise breaks.
        using var bmp = Noise(64, 64);

        var lowest = ScreenshotService.Encode(bmp, ImageFormat.Png, 1);
        var highest = ScreenshotService.Encode(bmp, ImageFormat.Png, 100);

        highest.Should().Equal(lowest, "png output must not depend on the jpeg quality argument");
    }

    // ---- R4a — CaptureAsync's pre-capture guard (the only part of it that needs no desktop) ---

    [Fact]
    public async Task CaptureAsync_throws_before_touching_the_screen_when_already_cancelled()
    {
        // A 0x0 region is the tripwire: reaching `new Bitmap(0, 0, ...)` throws ArgumentException,
        // so only a cancellation check that runs BEFORE the capture setup can produce an
        // OperationCanceledException here. Asserting on a 10x10 region instead would pass on a
        // box with a desktop even if the guard were deleted (the later in-flight checks would
        // still catch it after the screen had already been copied).
        var service = new ScreenshotService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CaptureAsync_cancellation_guard_runs_before_the_options_are_validated()
    {
        // A cancelled call must not be reported as a bad-argument error: the caller gave up, it
        // did not misuse the API. Scale 5.0 would be an ArgumentOutOfRangeException from
        // ScaleMath.Fit if the guard were not first.
        var service = new ScreenshotService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.CaptureAsync(
            new ScreenRegion(0, 0, 0, 0), new CaptureOptions(ImageFormat.Png, Scale: 5.0), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
