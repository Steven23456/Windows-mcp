using FluentAssertions;
using SkiaSharp;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-14 (R2) through the REAL window. <see cref="FlashGlowTests"/> proves the geometry and the
/// pixels and would stay green if no window were ever created, if it were created on the calling
/// thread with no message pump, or if it were left on screen forever — the failure mode CLAUDE.md
/// records for <c>disk_inspect mode:reclaimable</c>. These tests create the window for real.
/// <para>
/// Read-only and headless-safe in the same bracket as <see cref="WindowServiceTests"/>: it needs an
/// interactive window station (a desktop session) but no foreground app, no input and no capture,
/// so it is <c>Category=Integration</c>. Whether the glow is actually PAINTED is
/// <see cref="FlashOverlayDesktopTests"/> (UIAutomation), which captures the screen.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class FlashOverlayTests
{
    /// <summary>Top-left of the primary display, inset so the whole glow is on screen.</summary>
    private static readonly ScreenRegion Captured = new(10, 10, 200, 100);

    /// <summary>Polls <paramref name="predicate"/> until it holds or <paramref name="timeoutMs"/> passes.</summary>
    private static async Task<bool> WithinAsync(int timeoutMs, Func<bool> predicate)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    [Fact]
    public async Task Show_puts_the_glow_up_and_takes_it_down_after_the_duration()
    {
        using var overlay = new FlashOverlay();

        overlay.Show(Captured, TimeSpan.FromMilliseconds(300));

        overlay.IsVisible.Should().BeTrue("Show is synchronous enough that the caller can rely on it");
        // 700 ms of slack over the 300 ms duration: this is a timer on another thread, not a race
        // the test gets to lose, but it must not be an unbounded wait either.
        (await WithinAsync(1000, () => !overlay.IsVisible)).Should()
            .BeTrue("the glow tears itself down when the duration expires - nothing else calls Hide");
    }

    [Fact]
    public async Task Show_while_the_glow_is_up_replaces_it_instead_of_stacking()
    {
        using var overlay = new FlashOverlay();

        overlay.Show(Captured, TimeSpan.FromMilliseconds(200));
        overlay.Show(new ScreenRegion(300, 10, 200, 100), TimeSpan.FromMilliseconds(600));

        overlay.IsVisible.Should().BeTrue("the second Show replaces the first, it does not fail");
        // The second call's duration is the one in force; the first call's 200 ms must not take the
        // replacement down early, and the window must not be left up by a cancelled first timer.
        await Task.Delay(350);
        overlay.IsVisible.Should().BeTrue("the first Show's timer must not hide the second Show's glow");
        (await WithinAsync(1000, () => !overlay.IsVisible)).Should().BeTrue();
    }

    [Fact]
    public void Hide_is_idempotent_and_clears_the_visible_flag()
    {
        using var overlay = new FlashOverlay();
        overlay.Show(Captured, TimeSpan.FromSeconds(30));

        overlay.Hide();
        overlay.Hide();

        overlay.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Hide_on_an_overlay_that_was_never_shown_does_nothing()
    {
        // ScreenTools calls Hide() before EVERY capture, including the first one of the process.
        using var overlay = new FlashOverlay();

        var act = () => overlay.Hide();

        act.Should().NotThrow("the first capture of a process hides an overlay that does not exist yet");
        overlay.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var overlay = new FlashOverlay();
        overlay.Show(Captured, TimeSpan.FromSeconds(30));

        var act = () =>
        {
            overlay.Dispose();
            overlay.Dispose();
        };

        act.Should().NotThrow("the DI container disposes the singleton; a second dispose must be free");
    }

    [Fact]
    public void Show_after_Dispose_is_a_silent_no_op()
    {
        // Shutdown ordering is not something a tool call can control: a capture racing the host's
        // disposal must not turn into a crash on the way out.
        var overlay = new FlashOverlay();
        overlay.Dispose();

        var act = () => overlay.Show(Captured, TimeSpan.FromMilliseconds(200));

        act.Should().NotThrow();
        overlay.IsVisible.Should().BeFalse("a disposed overlay shows nothing");
    }

    [Theory]
    [InlineData(0, 0)]        // a degenerate rect: FlashGlow.Render refuses anything under 21x21
    [InlineData(0, 50)]
    [InlineData(50, 0)]
    [InlineData(-4, -4)]
    public void Show_of_a_rect_too_small_to_frame_is_a_silent_no_op(int width, int height)
    {
        // FlashGlow.Render THROWS for a window under 2*Margin+1 a side (FlashGlowTests), and the
        // overlay's contract is that nothing here ever throws and IsVisible simply stays false -
        // the glow is a courtesy, never a reason a screenshot fails. This is the seam between the
        // two, and it is the only caller-reachable failure path of Show.
        using var overlay = new FlashOverlay();

        var act = () => overlay.Show(new ScreenRegion(10, 10, width, height), TimeSpan.FromSeconds(30));

        act.Should().NotThrow("a capture must not fail because the glow could not be drawn");
        overlay.IsVisible.Should().BeFalse("nothing was put on screen, so nothing claims to be");
    }

    [Fact]
    public void Hide_after_Dispose_is_a_silent_no_op()
    {
        var overlay = new FlashOverlay();
        overlay.Dispose();

        var act = () => overlay.Hide();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task A_visible_overlay_is_not_in_the_window_inventory()
    {
        // WS_EX_TOOLWINDOW with no title: WindowFilter drops it twice over. If it were listed,
        // every snapshot would report a window the user cannot see and cannot act on - and
        // snapshot would walk it.
        var service = new WindowService();
        using var overlay = new FlashOverlay();

        WindowInfo[] before, during, after;
        var attempt = 0;
        while (true)
        {
            before = await service.ListAsync();
            overlay.Show(Captured, TimeSpan.FromSeconds(30));
            during = await service.ListAsync();
            overlay.Hide();
            after = await service.ListAsync();
            // Something else opening or closing a window mid-test makes the count comparison
            // meaningless; retry for a quiet moment rather than fail on it.
            if (before.Length == after.Length || ++attempt >= 5) break;
            await Task.Delay(150);
        }

        after.Length.Should().Be(before.Length,
            "the window list must be stable for this comparison to mean anything - run this on a " +
            "desktop where nothing is opening and closing windows (5 attempts)");
        during.Length.Should().Be(before.Length, "the overlay adds nothing to the inventory");

        var ours = before.Where(w => w.Pid == Environment.ProcessId).Select(w => w.Hwnd).ToHashSet();
        during.Where(w => w.Pid == Environment.ProcessId).Should()
            .OnlyContain(w => ours.Contains(w.Hwnd),
                "the overlay belongs to this process, so a new window of ours in the list IS the overlay");
    }
}

/// <summary>
/// A-14 (R2): the glow on the actual screen. Needs the interactive desktop AND a capture, and it
/// paints over the top-left corner of the primary display, so it is <c>Category=UIAutomation</c> —
/// excluded from headless runs like every other test that draws on someone's desktop.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class FlashOverlayDesktopTests
{
    /// <summary>The captured rect the glow frames; its window rect is exactly (0,0,220,120).</summary>
    private static readonly ScreenRegion Captured = new(FlashGlow.Margin, FlashGlow.Margin, 200, 100);

    /// <summary>The window rect: what a capture has to cover to contain the whole band.</summary>
    private static readonly ScreenRegion Framed = new(0, 0, 200 + 2 * FlashGlow.Margin, 100 + 2 * FlashGlow.Margin);

    private static CaptureOptions Lossless => new(ImageFormat.Png, MaxWidth: 0, MaxHeight: 0);

    /// <summary>Orange enough to be the glow and not the wallpaper: red-dominant, low blue.</summary>
    private static bool IsGlowOrange(SKColor c) => c.Red > 180 && c.Green is > 60 and < 200 && c.Blue < 90;

    /// <summary>Counts glow-coloured pixels in the top band of a captured PNG.</summary>
    private static int OrangeInTopBand(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        bmp.Should().NotBeNull("the capture must decode as an image");
        var count = 0;
        for (var y = 0; y < FlashGlow.Margin; y++)
            for (var x = 0; x < bmp!.Width; x++)
                if (IsGlowOrange(bmp.GetPixel(x, y))) count++;
        return count;
    }

    [Fact]
    public async Task The_glow_is_actually_painted_on_the_screen_and_disappears_when_hidden()
    {
        var screenshot = new ScreenshotService();
        using var overlay = new FlashOverlay();

        // A caret blink or an animation under the corner makes two captures of a quiet desktop
        // differ; retry for a quiet moment rather than fail on the first flicker (the pattern
        // ScreenshotCursorTests uses).
        ScreenshotResult before, during, after;
        var attempt = 0;
        while (true)
        {
            before = await screenshot.CaptureAsync(Framed, Lossless);
            overlay.Show(Captured, TimeSpan.FromSeconds(30));
            await Task.Delay(150);   // let the compositor put the layered window up
            during = await screenshot.CaptureAsync(Framed, Lossless);
            overlay.Hide();
            await Task.Delay(150);
            after = await screenshot.CaptureAsync(Framed, Lossless);
            if (before.Bytes.AsSpan().SequenceEqual(after.Bytes) || ++attempt >= 8) break;
            await Task.Delay(200);
        }

        after.Bytes.Should().Equal(before.Bytes,
            "an overlay that has been hidden leaves the desktop exactly as it found it - run this " +
            "with a quiet top-left 220x120 of the primary display (8 attempts)");
        OrangeInTopBand(before.Bytes).Should().Be(0,
            "the desktop under the top-left corner must not already be glow-orange for this test to mean anything");
        OrangeInTopBand(during.Bytes).Should().BeGreaterThan(50,
            "the band is 10 px tall and 220 px wide, so a painted glow is hundreds of orange pixels");
        OrangeInTopBand(after.Bytes).Should().Be(0, "Hide takes the glow off the screen, it does not just forget it");
    }

    [Fact]
    public async Task The_glow_leaves_the_captured_area_alone()
    {
        // The whole point of the inner rect being transparent: the overlay frames the picture, it
        // does not tint it. A capture of just the framed area during the flash must be unchanged.
        var screenshot = new ScreenshotService();
        using var overlay = new FlashOverlay();

        ScreenshotResult before, during;
        var attempt = 0;
        while (true)
        {
            before = await screenshot.CaptureAsync(Captured, Lossless);
            overlay.Show(Captured, TimeSpan.FromSeconds(30));
            await Task.Delay(150);
            during = await screenshot.CaptureAsync(Captured, Lossless);
            overlay.Hide();
            await Task.Delay(150);
            var after = await screenshot.CaptureAsync(Captured, Lossless);
            if (before.Bytes.AsSpan().SequenceEqual(after.Bytes) || ++attempt >= 8) break;
            await Task.Delay(200);
        }

        during.Bytes.Should().Equal(before.Bytes,
            "the inner rect is fully transparent, so the framed area is byte-identical during the flash");
    }
}
