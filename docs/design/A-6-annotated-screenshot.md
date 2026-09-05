# A-6 — `screenshot(annotate:true)`: boxes, label chips and a captioned grid on the capture

**Checklist item:** [A-6](../upstream-parity-checklist.md#a-6--annotated-screenshot-bounding-boxes-labels-grid-cursor--p2--m) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 4 — the one item that needs both tracks (phase 1's
capture pipeline and phase 3's snapshot) ·
**Status:** implemented 2026-09-05 (build clean, 1562/1562 headless tests green, 7/7 desktop-only
tests green, one real annotated capture inspected — see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

The model could see the screen (A-7) and could list the elements with centres (A-2), but had to
reconcile the two by itself. Upstream's `Snapshot(use_vision, use_annotation)` draws a numbered
box around every interactive element, highlights the cursor, and can overlay a reference grid, so
the picture and the list share labels.

## Decision

- **Annotations ride into the capture.** `CaptureOptions` gains `Annotations`
  (`AnnotationBox(Label, Bounds)` in virtual-desktop pixels) and `Grid` (`GridSpec(Columns,
  Rows)`); `ScreenshotResult` reports `AnnotationsDrawn`. The service draws them **after the
  downscale and before the encode**, so a 2 px box and an 11 px label stay legible at the output
  size, and maps them through the same `coordinateScale` the metadata reports. The drawing happens
  on a copy: the unscaled path's Skia bitmap is a zero-copy view of a read-only GDI lock. Nothing
  to draw means a byte-identical plain encode.
- **A pure `Annotator`** (SkiaSharp only, 100 % line and branch): a twelve-colour opaque palette
  indexed by list position, so a colour always means the same label even when an off-image box is
  skipped; `ToImage` maps bounds by subtracting the captured origin, dividing by the scale,
  rounding **half away from zero** (banker's rounding puts a box half a pixel off), widening a
  sub-pixel box to 1 px so a tiny element stays visible, then clipping — null when nothing is in
  the picture; `ChipRect` puts the label just above the box's top-left, inside it when there is
  no room above, and never off the image; `UseDarkText` picks black or white text by luminance
  (yellow is light, navy is dark); `Draw` paints the grid first, then each box as a 2 px stroke and
  a filled chip. Grid lines are a translucent dark grey at every interior division, each captioned
  with the **virtual-desktop** coordinate it sits on — the number the model passes to `click` —
  not the image pixel. The roadmap's mid-grey line was invisible on a mid-grey capture.
- **The tool.** `screenshot(annotate:false, grid_columns:0, grid_rows:0)`. With `annotate`, after
  the rect is resolved and the cursor read and **before the capture**, one
  `SnapshotAsync(Desktop)`; the interactive elements and scrollables whose bounds overlap the
  captured rect (half-open, so one touching the far edge is out) are kept in snapshot order; the
  kept elements become the boxes with `Label = ElementId`, null when nothing was kept. The result
  is **three** content blocks — metadata, then `SnapshotRenderer.Render` of the snapshot filtered to
  what the picture contains, then the image — so label N in the picture is row N of the text block
  from the same call, and the `el_N` ids go straight to `click`/`interact_element`. Metadata gains
  `annotated`, `annotations` (boxes that landed, from `AnnotationsDrawn`) and `grid` when asked.
  A grid alone needs no walk. `output:"file"` writes the annotated bytes and keeps the element
  list. Grid arguments are 0–64.
- **Scope is the desktop, not the foreground** (roadmap said "same scope rules, foreground
  default"): a capture is a display or an arbitrary rect and can show several windows; a
  foreground-only walk would leave the rest unlabelled. The real primary-display capture that
  produced 116 boxes across two windows is the evidence.
- **The cursor is A-11's**, already composited when `include_cursor` is true (the default); the
  annotator does not redraw it.
- **Id lifetime:** the walk is a snapshot walk, so an annotated screenshot evicts the ids the
  previous `snapshot` issued (roadmap C5). The description says so.

## Changes

- `Abstractions/Models/ScreenDtos.cs` — `AnnotationBox`, `GridSpec`, `CaptureOptions
  +Annotations, +Grid`, `ScreenshotResult +AnnotationsDrawn`.
- `Services/Annotator.cs` (new); `Services/ScreenshotService.cs` — `EncodeAnnotated`, both
  encode paths route through it.
- `Tools/ScreenTools.cs` — `IUIAutomationService` injected (a required fifth constructor
  parameter; DI resolves it), `annotate`/`grid_columns`/`grid_rows`, the snapshot-before-capture
  step, the filtered element-list block, metadata, descriptions.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | DTO shapes and defaults, appended after the A-11 fields | `ScreenshotAnnotateTests` (5) | Unit |
| R2 | Palette (12 distinct, opaque, cycles, negative throws); `ToImage` (identity, offset, scale, away-from-zero, four-edge clip, half-open null, flush edge, sub-pixel widening, non-positive scale); `ChipRect` (above, inside, right edge, tiny image); `UseDarkText`; `Draw` (stroke not fill, chip colour, distinct colours, skipped box keeps its index, overpaint order, scale, empty no-op, return value); grid (every interior division and nowhere else, grey not palette, columns/rows only, ≤1 draws nothing, captions in virtual-desktop coordinates, non-positive scale) | `AnnotatorTests` (48) | Unit |
| R3 | `EncodeAnnotated`: nothing-to-draw is byte-identical (null and empty), boxes reach the encoded bytes and map through the scale, off-rect not counted, grid-only counts 0, format honoured, quality validated, **draws on a copy and leaves the caller's bitmap untouched** | `ScreenshotAnnotateTests` (10) | Unit |
| R3b | Real capture: box drawn on the read-only GDI view's copy, at downscaled coordinates, none drawn when outside (byte-identical, retry-for-quiet), none by default | `ScreenshotAnnotateDesktopTests` (4) | UIAutomation |
| R4 | Tool: half-open overlap on both axes against the **captured** rect, snapshot order, null when nothing kept, three blocks in order, text block = `Render(filtered)`, scrollables filtered too, one desktop snapshot, snapshot after the rect and before the capture, no walk without `annotate`, grid alone, negative grid rejected before any work, grid + annotate together, `annotations` = `AnnotationsDrawn` not the kept count, `grid` metadata, file output, argument order, description | `ScreenToolsTests` (A-6 section, 24 methods) | Unit |
| R5 | Over real HTTP with all four services mocked: three blocks, `el_` in the second, `annotated:true` | `HttpTransportTests` (1) | Integration |
| R6 | Notepad fixture, real service graph: the editor's id in the text block and a palette colour on its mapped top edge; default primary-display call; grid + annotate | `ScreenToolsAnnotateDesktopTests` (3) | UIAutomation |

Bite check: eight one-line breaks (closed overlap, empty list for null, snapshot after capture,
drawing in place instead of on a copy, banker's rounding, fixed chip colour, image-pixel captions,
kept-count metadata) — all caught; the copy-vs-in-place one only after the GREEN pass added a
headless test for it.

## Deviations and follow-ups

- **No `scope` on `screenshot`**: annotate always walks the desktop (above). A per-call scope can
  be added later if a walk of a busy desktop proves too slow for a region capture.
- **Label chips overlap in dense areas** (a toolbar of 20 buttons); v1 accepts that — the text
  block carries the ids either way. Chip de-confliction is a follow-up if it hurts in practice.
- **`grid_columns`/`grid_rows` are capped at 64** (the GREEN pass noted an unbounded value would
  draw a line per pixel); every other numeric argument on the tool has a range.
- The roadmap named `LabelPlacement.Clamp`; it shipped as `Annotator.ChipRect`.
