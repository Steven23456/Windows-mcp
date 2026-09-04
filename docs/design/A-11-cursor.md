# A-11 — `screenshot`: cursor position in the metadata, cursor drawn on the capture

**Checklist item:** [A-11](../upstream-parity-checklist.md#a-11--cursor-position-in-responses-and-drawn-on-captures--p2--s) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 1, last screenshot-track item; `snapshot` (A-2) reuses
`CursorPosition` and `CursorMath` for its header ·
**Status:** implemented 2026-09-04 (build clean, 829/829 headless + 15/15 desktop-only tests green —
see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

Nothing reported where the pointer was, and the capture never showed it — so the model could not
tell whether its last `click` landed, or where a `drag` would start. Upstream heads every
snapshot with `Cursor Position: (x, y)` and highlights the cursor in annotated images.

## Decision

- **Position**: `IInputService.GetCursorPositionAsync() → CursorPosition(X, Y)` — the same
  virtual-desktop pixels `click` accepts (roadmap C1), read with the `GetCursorPos` the service
  already used. The **monitor index is a tool-layer concern**: `CursorMath.MonitorIndexOf(x, y,
  monitors)` (left/top inclusive, right/bottom exclusive — the seam pixel belongs to the right-hand
  monitor; first match wins for mirrored monitors; −1 off every monitor) against the same
  inventory the capture rect was resolved from, so no second enumeration. The roadmap's
  `CursorInfo(X, Y, MonitorIndex)` on the service would have made `InputService` enumerate monitors
  it has no business knowing about.
- **Metadata**: `cursor {x, y, monitorIndex}` **always**, drawn or not; `cursorDrawn: "icon" |
  "ring"` only when the pointer was painted (absent, never null). The read happens after argument
  validation and before the capture, once per call, and a failed read propagates — a broken cursor
  read is a broken desktop, not something to mask.
- **Drawing** (`include_cursor`, default true): on the **full-resolution bitmap, before the A-9
  downscale**, so the mark shrinks with the picture (the roadmap's "pass the scale to the ring"
  would have drawn a fixed 12 px ring twice the size relative to a 2×-reduced image). First the
  real cursor image — `GetCursorInfo` (`CURSOR_SHOWING`), `GetIconInfo` for the hotspot (its
  bitmaps deleted), `DrawIconEx` through the bitmap's HDC — then, when the cursor is hidden or the
  composite refuses, `CursorOverlay.DrawRing`: a white 3 px stroke at radius 12 and a black 2 px
  stroke at radius 8, anti-aliased, centred on the pixel's centre (+0.5 — the RED test at radius
  10 caught the corner-centred version bleeding half a pixel of white into the gap), clipped at
  the edge. The icon path runs before `LockBits` (it needs the HDC); the ring path takes its own
  read-write lock. `ScreenshotService.DrawCursor(bmp, rect, cursor, tryIcon)` takes the icon step
  as a parameter so all three outcomes are unit-tested on a synthetic bitmap.
- **One read, not two.** The tool hands the position it read to the capture in
  `CaptureOptions.Cursor`; the service reads live only when a direct caller passes none. The GREEN
  pass flagged that with two independent reads the metadata's `cursor` and the painted mark could
  disagree by a mouse movement, and `cursorDrawn` could be absent while `cursor` said "inside".
- `ocr` never draws it (`OcrService` leaves `IncludeCursor` false) and never reads it.

## Changes

- `Abstractions`: `CursorPosition`; `IInputService.GetCursorPositionAsync`; `CaptureOptions`
  `+IncludeCursor, +Cursor`; `ScreenshotResult +CursorDrawn`.
- `Services/CursorMath.cs`, `Services/CursorOverlay.cs` (new); `Services/InputService.cs`;
  `Services/ScreenshotService.cs` (`DrawCursor`, `TryDrawCursorIcon`, ring fallback);
  `NativeMethods.txt` `+GetCursorInfo, GetIconInfo, DrawIconEx, DeleteObject`.
- `Tools/ScreenTools.cs` — `IInputService` injected, `include_cursor` (last parameter), `cursor`
  and `cursorDrawn` metadata, descriptions.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Live read inside the virtual screen; same space in and out (`Hover(x,y)` → read `(x,y)`); cancellation first | `InputServiceTests.GetCursorPositionAsync_*` (3) | Integration / Unit |
| R2 | `MonitorIndexOf`: primary, second, seam pixel, negative origin, own `Index` not position, mirrored first-match, off/gap/empty → −1 | `CursorMathTests` (9 methods, 20 cases) | Unit |
| R3 | `RingPoint` origin subtraction and inclusive last pixel; `DrawRing` white r12 / black r8 / centre and gap untouched / anti-aliased / edge-clipped / far outside no-op | `CursorOverlayTests` (12 methods, 27 cases) | Unit |
| R4 | `DrawCursor` → icon (no ring on top) / ring (real pixels) / null off-rect; bitmap-relative point; the Win32 icon path round-trips and does not leak; real capture paints and reports; off / default / outside draw nothing; overlay before downscale | `ScreenshotEncodeTests.DrawCursor_*` (6), `CursorIconInteropTests` (2, Integration), `ScreenshotCursorTests` (5, UIAutomation) | Unit / Integration / UIAutomation |
| R5 | Tool: `cursor` always, index from the capture inventory, −1 off-monitor, virtual-desktop not region-relative, one read, before capture, failure propagates, invalid output/region cost no read, `include_cursor` forwarded (default true), position forwarded, `cursorDrawn` echoed / absent, file output same shape, `ocr` never reads | `ScreenToolsTests` (A-11 section, 17 methods) | Unit |
| R6 | Over real HTTP with mocked services: `cursor.x` 2000, `monitorIndex` 1, `cursorDrawn` icon; `include_cursor` in the schema with default true | `HttpTransportTests` (2) | Integration |
| R7 | Description advertises `cursor`, `monitorIndex`, `cursorDrawn`, `include_cursor` | `ScreenToolsTests.Screenshot_description_documents_the_cursor_metadata_and_the_new_argument` | Unit |

Coverage headless: `CursorMath`, `CursorOverlay`, `ScreenTools` 100 % line and branch;
`ScreenshotService` 58 % (the capture path is desktop-only; the new decision logic is fully
branched). Bite check: nine one-line breaks (`include_cursor` dropped, `cursorDrawn` null, index
always 0, read after capture, no ring fallback, `RingPoint` off-by-one, +0.5 removed, OCR drawing
the cursor, `DrawIconEx` inverted) each caught by one to four tests. Bite nine also proved the
icon path is live: the failure message listed the real arrow's pixels, composited inside a
headless `dotnet test`.

## Deviations and follow-ups

- **`TryDrawCursorIcon`'s hotspot subtraction is untestable in practice**: the standard arrow's
  hotspot is (0,0), so `x − hotX` is a no-op on any normal desktop. Left uncovered rather than
  pinned by a test that passes either way.
- **`GetCursorPos` failure** (`InvalidOperationException` naming the Win32 error) cannot be
  provoked from a test; same class as `MoveCursor`'s existing untested twin.
- **`ScreenshotCursorTests` retries for a quiet region** (up to 8 attempts, 150 ms apart): a caret
  blink under the top-left 200×100 made two cursor-off captures differ on the dev box. A
  persistently busy region still fails with a clear message rather than a false negative.
- `InputServiceTests` moves the real mouse (Integration category, matching the file's existing
  click test) — the "headless-safe" filter jerks a developer's pointer. Pre-existing convention.
