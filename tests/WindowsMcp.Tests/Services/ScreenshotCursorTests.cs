using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-11 (R4): the composite itself — <c>CaptureOptions.IncludeCursor</c> draws the cursor onto the
/// captured bitmap and <c>ScreenshotResult.CursorDrawn</c> says how ("icon" from the real cursor
/// bitmap, "ring" from <see cref="CursorOverlay"/>). Nothing here can be mocked: the cursor is a
/// live Win32 object and the pixels come from <c>CopyFromScreen</c>, so this is the only place the
/// GDI half of A-11 is exercised at all — <c>CursorOverlayTests</c> proves the ring geometry and
/// <c>ScreenToolsTests</c> only proves the option is passed on.
/// <para>
/// Needs an interactive desktop AND control of the mouse (it moves the cursor with
/// <see cref="InputService.HoverAsync"/> and does not put it back — same bracket as the other
/// cursor-moving tests), hence <c>Category=UIAutomation</c>: excluded from headless runs.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(PointerAndPixelCollection.Name)]
public class ScreenshotCursorTests
{
    /// <summary>Full resolution, PNG, lossless — so a byte comparison means what it says.</summary>
    private static CaptureOptions Options(bool includeCursor, int maxWidth = 0) =>
        new(ImageFormat.Png, MaxWidth: maxWidth, MaxHeight: 0, IncludeCursor: includeCursor);

    /// <summary>The top-left 200x100 of the primary display; the primary's origin is (0,0) by definition.</summary>
    private static readonly ScreenRegion TopLeft = new(0, 0, 200, 100);

    [Fact]
    public async Task CaptureAsync_with_include_cursor_draws_the_cursor_and_reports_how()
    {
        await new InputService().HoverAsync(50, 50);   // inside TopLeft
        var service = new ScreenshotService();

        // A caret blink or an animation under the region makes two identical captures differ;
        // retry for a quiet moment rather than fail on the first flicker. The assertion below is
        // what turns a persistently busy region into a clear message instead of a false failure.
        ScreenshotResult before, with, after;
        var attempt = 0;
        while (true)
        {
            before = await service.CaptureAsync(TopLeft, Options(includeCursor: false));
            with = await service.CaptureAsync(TopLeft, Options(includeCursor: true));
            after = await service.CaptureAsync(TopLeft, Options(includeCursor: false));
            if (before.Bytes.AsSpan().SequenceEqual(after.Bytes) || ++attempt >= 8) break;
            // Back-to-back retries sample consecutive frames of whatever is animating and are all
            // equally busy; a gap makes each attempt an independent look at the region.
            await Task.Delay(150);
        }

        after.Bytes.Should().Equal(before.Bytes,
            "the pixels under the region must be static for this comparison to mean anything — " +
            "run this with a quiet desktop under the top-left 200x100 of the primary display (8 attempts)");
        with.CursorDrawn.Should().BeOneOf("icon", "ring",
            "the real cursor is composited when it can be, and the drawn ring is the fallback");
        with.Bytes.Should().NotEqual(before.Bytes, "the cursor is actually painted onto the bitmap");
    }

    [Fact]
    public async Task CaptureAsync_without_include_cursor_draws_nothing_and_reports_null()
    {
        await new InputService().HoverAsync(50, 50);   // inside TopLeft — and still not drawn

        var result = await new ScreenshotService().CaptureAsync(TopLeft, Options(includeCursor: false));

        result.CursorDrawn.Should().BeNull("nothing was drawn, so the field is absent, not a lie");
    }

    [Fact]
    public async Task CaptureAsync_default_options_do_not_draw_the_cursor()
    {
        await new InputService().HoverAsync(50, 50);

        var result = await new ScreenshotService().CaptureAsync(TopLeft);

        result.CursorDrawn.Should().BeNull("IncludeCursor defaults to false at the service layer");
    }

    [Fact]
    public async Task CaptureAsync_with_the_cursor_outside_the_region_draws_nothing()
    {
        var monitors = await new WindowService().EnumerateMonitorsAsync();
        var primary = RegionMath.Primary(monitors);
        primary.Width.Should().BeGreaterThan(400, "the test needs somewhere off the captured rect to park the cursor");
        await new InputService().HoverAsync(primary.X + primary.Width / 2, primary.Y + primary.Height / 2);

        var result = await new ScreenshotService().CaptureAsync(
            new ScreenRegion(primary.X, primary.Y, 100, 100), Options(includeCursor: true));

        result.CursorDrawn.Should().BeNull("the cursor is not in this picture, so nothing is drawn");
    }

    [Fact]
    public async Task CaptureAsync_with_include_cursor_and_a_downscale_still_reports_the_drawn_kind()
    {
        // The composite happens on the full-resolution GDI bitmap, BEFORE the A-9 resize: doing it
        // after would draw a full-size cursor on a half-size image.
        await new InputService().HoverAsync(50, 50);

        var result = await new ScreenshotService().CaptureAsync(
            TopLeft, Options(includeCursor: true, maxWidth: 100));

        result.Width.Should().Be(100, "the downscale still runs");
        result.Height.Should().Be(50);
        result.CoordinateScale.Should().Be(2.0);
        result.CursorDrawn.Should().BeOneOf("icon", "ring");
    }
}
