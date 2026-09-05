namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// There is one mouse pointer and one desktop: every test class that MOVES the pointer, PAINTS on
/// the screen, or asserts on live screen pixels belongs to this collection so xunit never runs two
/// of them at once.
/// <para>
/// The whole desktop bracket (<c>--filter "Category=UIAutomation"</c>) failed two tests that pass
/// in isolation —
/// <c>ScreenshotCursorTests.CaptureAsync_with_include_cursor_draws_the_cursor_and_reports_how</c>
/// and <c>ScreenshotWgcCaptureTests.CaptureAsync_wgc_leaves_the_pointer_out_of_the_frame</c>.
/// xunit runs test CLASSES in parallel, and both of those classes park the real pointer with
/// <c>InputService.HoverAsync</c> and then compare the pixels under it: one class moves the cursor
/// into (or out of) the other's captured rect between its "before" and "after" captures, so the
/// byte comparison the assertion rests on stops being about what the test itself changed. The
/// failure only ever appears when the classes run together, never alone.
/// </para>
/// <para>
/// Membership rule — join this collection when a class does any of:
/// moves the pointer (<c>HoverAsync</c> / <c>ClickAsync</c> / <c>SetCursorPos</c>), draws on the
/// desktop, or makes an assertion whose outcome depends on the actual pixels on screen (a
/// before/after byte comparison, a colour sample, a GDI-vs-WGC agreement check). Needing an
/// interactive desktop is NOT enough on its own: <c>ScreenshotServiceTests</c>,
/// <c>OcrServiceLiveTests</c> and <c>HttpTransportScreenshotImageTests</c> capture the screen but
/// assert only on size, format, metadata and PNG/JPEG magic bytes, and <see cref="EdgeCollection"/>'s
/// classes inject nothing and read no pixels — they stay parallel, which is what keeps the
/// bracket's wall-clock time down.
/// </para>
/// <para>
/// There is deliberately no fixture and no <c>DisableParallelization</c>: a collection is xunit's
/// unit of parallelism, so membership alone serialises the classes in it. The definition stays
/// empty on purpose.
/// </para>
/// </summary>
[CollectionDefinition(PointerAndPixelCollection.Name)]
public sealed class PointerAndPixelCollection
{
    /// <summary>The name every pointer/pixel test class carries on <c>[Collection]</c>.</summary>
    public const string Name = "Pointer and desktop pixels";
}
