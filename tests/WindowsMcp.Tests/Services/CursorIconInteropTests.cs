using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-11 (R4, GREEN): the one part of the cursor overlay that is native interop rather than
/// arithmetic — <see cref="ScreenshotService.TryDrawCursorIcon"/>, which is
/// <c>GetCursorInfo</c> → <c>GetIconInfo</c> → two <c>DeleteObject</c>s → <c>DrawIconEx</c>
/// through the bitmap's HDC. Every other A-11 test either mocks the service or needs the
/// interactive desktop, so without this the CsWin32 declarations (<c>CURSORINFO.cbSize</c>, the
/// <c>ICONINFO</c> out-pointer, the handle lifetimes) are never executed in a headless run — and a
/// wrong struct size there is silent memory corruption, not a failing assertion.
/// <para>
/// Read-only with respect to the desktop: it paints onto a bitmap this test owns and moves
/// nothing. Whether a cursor is visible at all depends on the session, so the outcome is not
/// forced — but each outcome is asserted: composited means pixels changed, refused means the
/// bitmap is untouched and the caller can fall back to the ring.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class CursorIconInteropTests
{
    private static readonly System.Drawing.Color Grey = System.Drawing.Color.FromArgb(255, 128, 128, 128);

    private static System.Drawing.Bitmap GreyCapture(int size = 64)
    {
        var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(Grey);
        return bmp;
    }

    private static HashSet<System.Drawing.Color> Colours(System.Drawing.Bitmap bmp)
    {
        var colours = new HashSet<System.Drawing.Color>();
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                colours.Add(bmp.GetPixel(x, y));
        return colours;
    }

    [Fact]
    public void TryDrawCursorIcon_round_trips_the_real_win32_calls_and_reports_what_it_did()
    {
        using var bmp = GreyCapture();

        var composited = ScreenshotService.TryDrawCursorIcon(bmp, 32, 32);

        if (composited)
        {
            Colours(bmp).Should().HaveCountGreaterThan(1,
                "DrawIconEx reported success, so the cursor image is on the bitmap");
        }
        else
        {
            Colours(bmp).Should().ContainSingle(
                "a refusal must leave the capture clean for the ring fallback to draw on")
                .Which.Should().Be(Grey);
        }
    }

    [Fact]
    public void TryDrawCursorIcon_survives_being_called_repeatedly()
    {
        // GetIconInfo hands back copies of the mask and colour bitmaps that the caller must delete;
        // leaking them exhausts the GDI object quota (10,000 per process by default). Twenty
        // rounds is far short of that, but a failure to release the HDC or a corrupted handle
        // shows up immediately as a false return or an exception.
        using var bmp = GreyCapture();

        var results = new List<bool>();
        for (var i = 0; i < 20; i++)
            results.Add(ScreenshotService.TryDrawCursorIcon(bmp, 32, 32));

        results.Should().AllSatisfy(r => r.Should().Be(results[0],
            "the same call on the same desktop must give the same answer every time — a run that " +
            "starts succeeding and then fails is a leaked GDI handle"));
    }
}
