using System.Text.Json;
using FluentAssertions;
using Moq;
using SkiaSharp;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;
using ImageFormat = WindowsMcp.Abstractions.Models.ImageFormat;

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

    // ======== E-7 — RecognizeAsync: the same capture, with word positions ======================

    [Fact]
    public async Task RecognizeAsync_captures_the_given_region_as_png_with_no_size_limit_no_cursor_and_the_default_backend()
    {
        // The design note: "The capture is the one ExtractTextAsync already takes (PNG, no size
        // cap, no cursor, backend at the process default)". Each is a rule with its own reason
        // above (A-9 R8, A-11 R5, A-10 R4); a second path that drifted on any of them would read
        // different pixels from the text mode for the same call.
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);

        Func<Task> act = () => service.RecognizeAsync(new ScreenRegion(-1920, -40, 300, 200));

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(
            new ScreenRegion(-1920, -40, 300, 200),
            It.Is<CaptureOptions>(o => o.Format == ImageFormat.Png && o.MaxWidth == 0 && o.MaxHeight == 0
                                       && !o.IncludeCursor && o.Backend == "auto"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecognizeAsync_asks_for_exactly_the_capture_options_ExtractTextAsync_asks_for()
    {
        // "One private capture-and-recognise path behind both members": the options records are
        // equal, field for field, so a later field added to one path cannot be missed by the other.
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);
        var region = new ScreenRegion(10, 20, 30, 40);

        await ((Func<Task>)(() => service.ExtractTextAsync(region))).Should().ThrowAsync<CaptureReached>();
        await ((Func<Task>)(() => service.RecognizeAsync(region))).Should().ThrowAsync<CaptureReached>();

        var options = mock.Invocations
            .Where(i => i.Method.Name == nameof(IScreenshotService.CaptureAsync))
            .Select(i => i.Arguments[1])
            .ToArray();
        options.Should().HaveCount(2);
        options[1].Should().Be(options[0], "the text mode and the json mode read the same pixels");
    }

    [Fact]
    public async Task RecognizeAsync_passes_the_callers_token_to_the_capture()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);
        using var cts = new CancellationTokenSource();

        Func<Task> act = () => service.RecognizeAsync(new ScreenRegion(0, 0, 10, 10), cts.Token);

        await act.Should().ThrowAsync<CaptureReached>();
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task RecognizeAsync_does_not_capture_when_already_cancelled()
    {
        var mock = ThrowingShotMock();
        var service = new OcrService(mock.Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => service.RecognizeAsync(new ScreenRegion(0, 0, 10, 10), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void OcrResult_text_angle_defaults_to_null()
    {
        // The json mode emits textAngle only when the engine reported one; the record's default
        // is what "did not report" looks like.
        new OcrResult("", [], new ScreenRegion(0, 0, 1, 1)).TextAngle.Should().BeNull();
    }
}

/// <summary>
/// E-7 through the REAL Windows.Media.Ocr engine and the REAL WinRT decode, with no screen: the
/// frame is a synthetic PNG rendered here (SkiaSharp) and handed over by a fixed
/// <see cref="IScreenshotService"/>, so the only thing standing in for Windows is the pixel source.
/// Every unit test above throws at the capture and would stay green if the engine's word rects were
/// never read, read in the wrong space, or mapped without the origin — the
/// <c>disk_inspect mode:reclaimable</c> lesson in CLAUDE.md. Needs an installed OCR language pack
/// (as every OCR call does) but no desktop session, no foreground window and no input, so it is
/// <c>Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public class OcrServiceRecognizeIntegrationTests
{
    /// <summary>A capture that is always the same frame, reporting the given coordinate scale.</summary>
    internal sealed class FixedFrame(byte[] png, int width, int height, double coordinateScale) : IScreenshotService
    {
        public Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, CaptureOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ScreenshotResult(png, width, height, ImageFormat.Png,
                (int)Math.Round(width * coordinateScale), (int)Math.Round(height * coordinateScale), coordinateScale));
    }

    internal const int FrameWidth = 640;
    internal const int FrameHeight = 140;
    internal const string Phrase = "QUOKKA ZEBRA";

    /// <summary>
    /// Black text on white at a known place; <paramref name="ink"/> is where the glyphs landed, in
    /// image pixels, so a mapped word can be checked against where it really is.
    /// </summary>
    internal static byte[] RenderFrame(string? text, out SKRect ink)
    {
        ink = SKRect.Empty;
        using var bitmap = new SKBitmap(FrameWidth, FrameHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            if (text is not null)
            {
                using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                using var font = new SKFont(typeface, 56);
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                const float x = 40, baseline = 90;
                canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
                font.MeasureText(text, out var measured);
                ink = new SKRect(x + measured.Left, baseline + measured.Top, x + measured.Right, baseline + measured.Bottom);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string Clean(string word) => new string(word.Where(char.IsLetter).ToArray()).ToUpperInvariant();

    private static bool Inside(Bounds b, ScreenRegion r) =>
        b.X >= r.X && b.Y >= r.Y && b.X + b.Width <= r.X + r.Width && b.Y + b.Height <= r.Y + r.Height;

    private static Bounds Union(IEnumerable<Bounds> boxes)
    {
        var all = boxes.ToArray();
        int left = all.Min(b => b.X), top = all.Min(b => b.Y);
        int right = all.Max(b => b.X + b.Width), bottom = all.Max(b => b.Y + b.Height);
        return new Bounds(left, top, right - left, bottom - top);
    }

    [Fact]
    public async Task The_rig_works_ExtractTextAsync_reads_the_synthetic_frame()
    {
        // Guards the rig, not E-7: if this fails the language pack or the rendering is the
        // problem, and every RecognizeAsync failure below says nothing about RecognizeAsync.
        var service = new OcrService(new FixedFrame(RenderFrame(Phrase, out _), FrameWidth, FrameHeight, 1.0));

        var text = await service.ExtractTextAsync(new ScreenRegion(0, 0, FrameWidth, FrameHeight));

        text.ToUpperInvariant().Should().Contain("QUOKKA").And.Contain("ZEBRA");
    }

    [Fact]
    public async Task RecognizeAsync_maps_every_word_into_virtual_desktop_pixels_through_a_negative_origin()
    {
        // A capture on a monitor left of and above the primary: image (0,0) is (-1500,-300).
        var region = new ScreenRegion(-1500, -300, FrameWidth, FrameHeight);
        var service = new OcrService(new FixedFrame(RenderFrame(Phrase, out var ink), FrameWidth, FrameHeight, 1.0));

        var result = await service.RecognizeAsync(region);

        result.Region.Should().Be(region, "Region is the rect that was captured");
        result.Lines.Should().NotBeEmpty();
        var words = result.Lines.SelectMany(l => l.Words).ToArray();
        words.Select(w => Clean(w.Text)).Should().Contain(new[] { "QUOKKA", "ZEBRA" });
        // Full-box containment holds for STRAIGHT text only (a tilted word's upright box may reach
        // past the region); the frame is straight, and this says so if the engine ever disagrees.
        (result.TextAngle ?? 0).Should().Be(0, "precondition: the rendered frame is straight text");
        words.Should().OnlyContain(w => Inside(w.Bounds, region), "every word is somewhere in the captured rect");

        // Where the glyphs really are, moved by the origin, with a few pixels of engine slack.
        const int slack = 8;
        foreach (var w in words.Where(w => Clean(w.Text) is "QUOKKA" or "ZEBRA"))
        {
            w.Bounds.X.Should().BeGreaterThanOrEqualTo(region.X + (int)ink.Left - slack);
            (w.Bounds.X + w.Bounds.Width).Should().BeLessThanOrEqualTo(region.X + (int)Math.Ceiling(ink.Right) + slack);
            w.Bounds.Y.Should().BeGreaterThanOrEqualTo(region.Y + (int)ink.Top - slack);
            (w.Bounds.Y + w.Bounds.Height).Should().BeLessThanOrEqualTo(region.Y + (int)Math.Ceiling(ink.Bottom) + slack);
            w.Bounds.Width.Should().BePositive();
            w.Bounds.Height.Should().BePositive();
        }
        var quokka = words.First(w => Clean(w.Text) == "QUOKKA");
        var zebra = words.First(w => Clean(w.Text) == "ZEBRA");
        (quokka.Bounds.X + quokka.Bounds.Width).Should().BeLessThanOrEqualTo(zebra.Bounds.X, "reading order is left to right");
    }

    [Fact]
    public async Task RecognizeAsync_line_bounds_are_the_union_of_their_words()
    {
        var region = new ScreenRegion(300, 200, FrameWidth, FrameHeight);
        var service = new OcrService(new FixedFrame(RenderFrame(Phrase, out _), FrameWidth, FrameHeight, 1.0));

        var result = await service.RecognizeAsync(region);

        result.Lines.Should().NotBeEmpty();
        foreach (var line in result.Lines)
        {
            line.Words.Should().NotBeEmpty("a line with no words is dropped");
            line.Bounds.Should().Be(Union(line.Words.Select(w => w.Bounds)));
        }
    }

    [Fact]
    public async Task RecognizeAsync_text_is_exactly_what_ExtractTextAsync_returns_for_the_same_pixels()
    {
        var png = RenderFrame(Phrase, out _);
        var region = new ScreenRegion(0, 0, FrameWidth, FrameHeight);
        var service = new OcrService(new FixedFrame(png, FrameWidth, FrameHeight, 1.0));

        var text = await service.ExtractTextAsync(region);
        var result = await service.RecognizeAsync(region);

        result.Text.Should().Be(text, "the json mode's 'text' is the text mode's output, not a re-join of the words");
    }

    [Fact]
    public async Task RecognizeAsync_honours_the_captures_coordinate_scale()
    {
        // OCR's own capture is never downscaled (scale 1), but the note says the scale is honoured:
        // the same frame reported at scale 2 maps every edge to twice the distance from the origin
        // (± 1 for rounding each edge separately).
        var png = RenderFrame(Phrase, out _);
        var origin = new ScreenRegion(-1000, 100, FrameWidth * 2, FrameHeight * 2);
        var one = await new OcrService(new FixedFrame(png, FrameWidth, FrameHeight, 1.0))
            .RecognizeAsync(origin with { Width = FrameWidth, Height = FrameHeight });
        var two = await new OcrService(new FixedFrame(png, FrameWidth, FrameHeight, 2.0)).RecognizeAsync(origin);

        var a = one.Lines.SelectMany(l => l.Words).ToArray();
        var b = two.Lines.SelectMany(l => l.Words).ToArray();
        b.Select(w => w.Text).Should().Equal(a.Select(w => w.Text), "same pixels, same words");
        for (int i = 0; i < a.Length; i++)
        {
            (b[i].Bounds.X - origin.X).Should().BeCloseTo(2 * (a[i].Bounds.X - origin.X), 1);
            (b[i].Bounds.Y - origin.Y).Should().BeCloseTo(2 * (a[i].Bounds.Y - origin.Y), 1);
            b[i].Bounds.Width.Should().BeCloseTo(2 * a[i].Bounds.Width, 2);
            b[i].Bounds.Height.Should().BeCloseTo(2 * a[i].Bounds.Height, 2);
        }
    }

    [Fact]
    public async Task RecognizeAsync_of_a_blank_frame_is_empty_text_and_no_lines_not_an_error()
    {
        var region = new ScreenRegion(0, 0, FrameWidth, FrameHeight);
        var service = new OcrService(new FixedFrame(RenderFrame(null, out _), FrameWidth, FrameHeight, 1.0));

        var result = await service.RecognizeAsync(region);

        result.Text.Should().BeEmpty();
        result.Lines.Should().BeEmpty();
        result.Region.Should().Be(region);
    }

    // ======== E-7 review F2 — tilted text ========================================================

    internal const int TiltWidth = 1000;
    internal const int TiltHeight = 600;
    private static readonly string[] TiltWords = ["QUOKKA", "ZEBRA", "MANGO"];

    /// <summary>Where one word's glyphs really are after the rotation, in image pixels.</summary>
    internal sealed record DrawnWord(string Text, double CenterX, double CenterY, double Left, double Top, double Right, double Bottom);

    /// <summary>
    /// The phrase drawn rotated by <paramref name="degrees"/> (Skia: positive is clockwise on screen)
    /// about the image centre. Each word's unrotated ink box is measured, its four corners are put
    /// through the same rotation, and the result is the word's true centre and axis-aligned box.
    /// </summary>
    internal static byte[] RenderRotated(double degrees, out DrawnWord[] drawn)
    {
        const float size = 64, gap = 60, baseline = TiltHeight / 2f + 22;
        float cx = TiltWidth / 2f, cy = TiltHeight / 2f;
        using var typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, size);
        var widths = TiltWords.Select(w => font.MeasureText(w)).ToArray();
        float x = cx - (widths.Sum() + gap * (TiltWords.Length - 1)) / 2;

        var list = new List<DrawnWord>();
        using var bitmap = new SKBitmap(TiltWidth, TiltHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.RotateDegrees((float)degrees, cx, cy);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            double rad = degrees * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
            (double X, double Y) Rot(double px, double py) =>
                (cx + (px - cx) * cos - (py - cy) * sin, cy + (px - cx) * sin + (py - cy) * cos);
            for (int i = 0; i < TiltWords.Length; i++)
            {
                canvas.DrawText(TiltWords[i], x, baseline, SKTextAlign.Left, font, paint);
                font.MeasureText(TiltWords[i], out var ink);
                var corners = new[]
                {
                    Rot(x + ink.Left, baseline + ink.Top), Rot(x + ink.Right, baseline + ink.Top),
                    Rot(x + ink.Left, baseline + ink.Bottom), Rot(x + ink.Right, baseline + ink.Bottom),
                };
                var centre = Rot(x + (ink.Left + ink.Right) / 2, baseline + (ink.Top + ink.Bottom) / 2);
                list.Add(new DrawnWord(TiltWords[i], centre.X, centre.Y,
                    corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y)));
                x += widths[i] + gap;
            }
        }
        drawn = list.ToArray();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The requirement behind the design note's "Tilted text" decision: whatever angle the engine
    /// detects, a word's centre as the caller receives it (x + width/2, y + height/2 of the mapped
    /// bounds, through the capture origin) lies on that word's glyphs. Two bounds, both required:
    /// inside the rotated word's axis-aligned ink box with 4 px of slack (anti-aliasing, engine
    /// padding), AND at most 10 px from the word's true centre (the ink is about 49 px tall, so a
    /// click there is on the glyphs; the box alone is too loose at small angles, where a long word's
    /// box is tall). The message carries the measurement — angle reported, true centre, reported
    /// centre, distance — so a failure is evidence for the fix, not just a red.
    /// <para>
    /// Measured on the RED run (Windows.Media.Ocr, en-US): the engine reports TextAngle ≈ the Skia
    /// rotation (+5 → 5.5, −5 → −5.1, +15 → 15.5, −15 → −16.1) and the word rects in the DESKEWED
    /// frame (all centres on the image centre's row); rotating each reported centre about the image
    /// centre by +TextAngle (y-down: x' = cx + dx·cos − dy·sin, y' = cy + dx·sin + dy·cos) puts it on
    /// the true centre to within ~3 px.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-15.0)]
    [InlineData(-5.0)]
    [InlineData(5.0)]
    [InlineData(15.0)]
    public async Task RecognizeAsync_word_centres_lie_on_the_glyphs_of_rotated_text(double degrees)
    {
        const int tolerance = 4;
        const double maxDistance = 10;
        var region = new ScreenRegion(1000, -500, TiltWidth, TiltHeight);
        var service = new OcrService(new FixedFrame(RenderRotated(degrees, out var drawn), TiltWidth, TiltHeight, 1.0));

        var result = await service.RecognizeAsync(region);

        var words = result.Lines.SelectMany(l => l.Words).ToArray();
        var report = new List<string> { $"rotation {degrees}°, engine TextAngle={result.TextAngle?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, text='{result.Text}'" };
        var misses = new List<string>();
        foreach (var truth in drawn)
        {
            var word = words.FirstOrDefault(w => Clean(w.Text) == truth.Text);
            if (word is null) { misses.Add($"{truth.Text}: not recognised"); continue; }
            int cx = word.Bounds.X + word.Bounds.Width / 2 - region.X, cy = word.Bounds.Y + word.Bounds.Height / 2 - region.Y;
            double dx = cx - truth.CenterX, dy = cy - truth.CenterY;
            bool inside = cx >= truth.Left - tolerance && cx <= truth.Right + tolerance && cy >= truth.Top - tolerance && cy <= truth.Bottom + tolerance;
            report.Add(FormattableString.Invariant(
                $"{truth.Text}: true centre ({truth.CenterX:0.0},{truth.CenterY:0.0}) box [{truth.Left:0},{truth.Top:0}..{truth.Right:0},{truth.Bottom:0}]; reported rect (image) {word.Bounds.X - region.X},{word.Bounds.Y - region.Y},{word.Bounds.Width}x{word.Bounds.Height} centre ({cx},{cy}); d=({dx:0.0},{dy:0.0}) |d|={Math.Sqrt(dx * dx + dy * dy):0.0}; inside={inside}"));
            if (!inside || Math.Sqrt(dx * dx + dy * dy) > maxDistance) misses.Add(truth.Text);
        }
        var evidence = string.Join(" || ", report);
        Console.WriteLine(evidence);

        misses.Should().BeEmpty($"every word centre must land on its glyphs. Measurement: {evidence}");
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

    /// <summary>
    /// E-7: the json path through a REAL screen capture. Shape only — what is on the screen is not
    /// ours to choose — but every word has a non-empty rect, every line's bounds are the union of
    /// its words, and every word is in the captured rect. "In the rect" depends on the angle (design
    /// note, "Decisions from the review"): for straight text (TextAngle null or 0) the whole box is
    /// inside; for tilted text a word's box is the upright box around the rotated word and may
    /// legitimately reach past the region (a 180x20 box at 620,10 in an 800x400 image at 15° ends
    /// at x 836), so only its CENTRE — the point click receives — must be inside. Reads the screen;
    /// injects nothing.
    /// </summary>
    [Fact]
    public async Task RecognizeAsync_runs_the_real_capture_decode_and_recognize_path()
    {
        using var shot = new ScreenshotService();
        var service = new OcrService(shot);
        var region = new ScreenRegion(0, 0, 800, 400);

        var result = await service.RecognizeAsync(region);

        result.Region.Should().Be(region);
        result.Text.Should().NotBeNull();
        bool straight = result.TextAngle is not { } angle || angle == 0;   // the engine reports -0 for straight text; -0 == 0
        foreach (var line in result.Lines)
        {
            line.Words.Should().NotBeEmpty("a line with no words is dropped");
            foreach (var word in line.Words)
            {
                word.Bounds.Width.Should().BePositive();
                word.Bounds.Height.Should().BePositive();
                if (straight)
                {
                    word.Bounds.X.Should().BeGreaterThanOrEqualTo(region.X);
                    word.Bounds.Y.Should().BeGreaterThanOrEqualTo(region.Y);
                    (word.Bounds.X + word.Bounds.Width).Should().BeLessThanOrEqualTo(region.X + region.Width);
                    (word.Bounds.Y + word.Bounds.Height).Should().BeLessThanOrEqualTo(region.Y + region.Height);
                }
                else
                {
                    (word.Bounds.X + word.Bounds.Width / 2).Should().BeInRange(region.X, region.X + region.Width - 1,
                        $"at TextAngle {result.TextAngle} only the centre of '{word.Text}' is bound to the region");
                    (word.Bounds.Y + word.Bounds.Height / 2).Should().BeInRange(region.Y, region.Y + region.Height - 1);
                }
            }
            int left = line.Words.Min(w => w.Bounds.X), top = line.Words.Min(w => w.Bounds.Y);
            int right = line.Words.Max(w => w.Bounds.X + w.Bounds.Width), bottom = line.Words.Max(w => w.Bounds.Y + w.Bounds.Height);
            line.Bounds.Should().Be(new Bounds(left, top, right - left, bottom - top));
        }
    }
}

/// <summary>
/// E-7's "Done when": <c>ocr(window:…, format:"json")</c> returns a word whose centre, clicked,
/// lands on that word. Through the REAL window inventory, the REAL capture, the REAL OCR engine and
/// a REAL double-click into a Notepad window this class opens on a file of known words; the proof is
/// the word Notepad then has selected, read back over UI Automation.
/// <para>
/// INJECTS INPUT (a double-click), so it is UIAutomation-category and in
/// <see cref="DesktopCollection"/>; never run unattended. The window is named by the file this test
/// writes, not by "Notepad" (the checklist's wording), so another Notepad on the desk cannot be
/// the one that is read or clicked.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class OcrWordPositionDesktopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmcp-e7-" + Guid.NewGuid().ToString("N"));

    public OcrWordPositionDesktopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Ocr_window_json_word_centre_double_clicked_selects_that_word()
    {
        var stem = "e7ocr" + Guid.NewGuid().ToString("N")[..8];
        var file = Path.Combine(_dir, stem + ".txt");
        await File.WriteAllTextAsync(file, "QUOKKA ZEBRA MANGO");

        using var np = new NotepadFixture(file);
        np.BringToForeground();
        await Task.Delay(500);

        using var shot = new ScreenshotService();
        var input = new InputService();
        var tools = new ScreenTools(shot, new OcrService(shot), new WindowService(), input,
            new Mock<IUIAutomationService>().Object, new Mock<IFlashOverlay>().Object);

        using var doc = JsonDocument.Parse(await tools.Ocr(window: stem, format: "json"));

        var zebra = doc.RootElement.GetProperty("lines").EnumerateArray()
            .SelectMany(l => l.GetProperty("words").EnumerateArray())
            .FirstOrDefault(w => w.GetProperty("text").GetString()!.Trim().Equals("ZEBRA", StringComparison.OrdinalIgnoreCase));
        zebra.ValueKind.Should().Be(JsonValueKind.Object, $"OCR of the Notepad window must find ZEBRA; got: {doc.RootElement.GetProperty("text").GetString()}");

        await input.ClickAsync(zebra.GetProperty("centerX").GetInt32(), zebra.GetProperty("centerY").GetInt32(), clicks: 2);
        await Task.Delay(400);

        var editor = np.Automation.FromHandle((IntPtr)np.Hwnd)
            .FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Document))
            ?? throw new Xunit.Sdk.XunitException("No Document element in the fixture's Notepad window");
        var selected = editor.Patterns.Text.Pattern.GetSelection().Select(r => r.GetText(-1)).FirstOrDefault() ?? "";
        selected.Trim().Should().Be("ZEBRA", "a double-click at the word's reported centre selects exactly that word");
    }
}
