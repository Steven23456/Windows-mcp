# A-8 — `screenshot` / `ocr`: multi-display capture and virtual-desktop coordinates

**Checklist item:** [A-8](../upstream-parity-checklist.md#a-8--multi-display-capture-and-virtual-desktop-coordinates--p1--m) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 1, third item; closes the live-OCR follow-up from
[A-9](A-9-screenshot-downscale.md) ·
**Status:** implemented 2026-09-04 (build clean, 743/743 headless + 17/17 desktop-only tests green —
see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

`screenshot` and `ocr` captured the primary display only (`SM_CXSCREEN × SM_CYSCREEN` at 0,0);
`region` was `x,y,w,h` with no validation and an undocumented coordinate space, so a rect on a
second monitor either worked by accident or failed inside GDI, and a rect off the edge was
silently clipped — a picture whose coordinates no longer meant what the model thought. Nothing
told the model that image (0,0) is not desktop (0,0) on a second monitor.

## Decision

- **Coordinate space is the virtual desktop everywhere** (roadmap C1) — the same signed pixels
  `click`/`drag`/`scroll` already normalise to after D-3. The capture itself needed no change:
  `CopyFromScreen(r.X, r.Y, …)` already takes virtual-desktop coordinates, so a negative origin
  just works; only the default rect and the validation were missing.
- **Default stays the primary display** (roadmap C3), `display` opts into more: `all`, or
  comma-separated zero-based indices in `multi_monitor` order (`1`, `0,2`), de-duplicated in the
  order given; several are captured as their **union**. `region` wins over `display`; an invalid
  `display` still errors when `region` wins — a bad value is a bad call, not something to ignore.
- **Pure core `RegionMath`** (`ParseRegion`, `ParseDisplays`, `Union`, `VirtualScreen`,
  `Validate`, `Primary`), no Win32, shared by both tools through one private
  `ScreenTools.ResolveRegionAsync` (enumerate once → parse display → parse region → validate |
  union | primary). `ParseRegion` moved out of the tool; a non-integer part or a non-positive
  size is a named `ArgumentException`, never a `FormatException`. `Validate` rejects anything not
  entirely inside the virtual screen with the bounds in `click`'s wording (`x L..R, y T..B`,
  inclusive last pixel) and does its arithmetic in `long` — the GREEN pass showed a width near
  `int.MaxValue` wrapped the far edge negative and passed as "inside", reaching GDI with a
  2-gigapixel bitmap request.
- **Metadata**: `region` is now **always** present (the rect actually captured — image (0,0) is
  its origin), `displays` always lists every monitor (`index, x, y, width, height, isPrimary`),
  `selectedDisplays` only when `display` picked the rect. `CoordinateNote(region, scale)` is a
  pure function with three outcomes: null at origin (0,0) and scale 1; A-9's sentence verbatim
  for a scaled origin capture; and the full transform for anything off-origin —
  `virtual-desktop x = 1920 + imageX × 2, y = 0 + imageY × 2 — use these for click/drag/scroll`.
  The tool description tells the model the rule once; the note repeats it only when it applies.
- **`multi_monitor` indices are the position in the returned list**, not the `EnumDisplayMonitors`
  counter: a failed `GetMonitorInfo` used to leave a gap (0, 2), and `display` selects by position
  while the metadata reports `Index` — the two numberings have to be one numbering. Fixed in
  `WindowService.EnumerateMonitorsAsync`; an `Integration` test asserts the invariant on real
  hardware.

## Changes

- `Services/RegionMath.cs` (new); `Tools/ScreenTools.cs` — `IWindowService` injected, `display`
  on both tools, shared resolver, `CoordinateNote`, metadata, shared `[Description]` constants;
  `Services/WindowService.cs` — positional monitor index.
- No interface or DTO change (`IWindowService.EnumerateMonitorsAsync` and `MonitorInfo` as they
  were).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `RegionMath` rules: parse region (arity, non-integer, empty part, non-positive size), parse displays (`all`, order, de-dupe, range, non-integer, empty), union (side-by-side, stacked, negative origin, order-independent), virtual screen, validate (inside, edges, straddling, 1 px outside each side, negative-origin wording, overflow), primary (flag, fallback, empty) | `RegionMathTests` (26 methods, 80 cases) | Unit |
| R2 | The `[Description]`s state the coordinate space, "rejected not clipped", `all`/indices/union/primary default/region-wins, and both tools advertise the same text | `ScreenToolsTests.*_description_*` (3) | Unit |
| R3 | Tool resolution: primary default (even when not first), `display` rects and unions, subset unions, bad index/unparseable → no capture, region wins, invalid display still rejected, out-of-bounds region → no capture, straddling captured, one enumeration per call on every path, empty inventory → caller-facing error, enumeration failure not swallowed | `ScreenToolsTests` (A-8 section, 24 methods) | Unit |
| R4 | Metadata: `region`/`displays` always (file and inline), `selectedDisplays` only when display chose, `CoordinateNote` three branches + invariant culture, note in metadata for off-origin captures with/without scale | `ScreenToolsTests` (11 methods) | Unit |
| R5 | `ocr` resolves through the same resolver, same rect, same error text, one enumeration | `ScreenToolsTests.Ocr_*` (6) | Unit |
| R6 | Real GDI capture of the union of every monitor sizes the bitmap to the union | `ScreenshotServiceTests.CaptureAsync_captures_the_union_of_every_monitor` | UIAutomation |
| R7 | The real OCR chain (`BitmapDecoder` → `OcrEngine`) runs (the A-9 follow-up; a smoke test by design) | `OcrServiceLiveTests.ExtractTextAsync_runs_the_real_decode_and_recognize_path` | UIAutomation |
| R8 | Over real HTTP with mocked capture and inventory: `display:"1"` → `region.x` 1920, `selectedDisplays [1]`, two `displays` | `HttpTransportTests.Screenshot_display_selects_the_second_monitor_over_http` | Integration |
| R9 | Through the real `WindowService`: positional zero-based indices with one primary; our virtual screen equals `SM_*VIRTUALSCREEN`; default / `0` / `all` resolve real rects; an off-desktop region is rejected with the real bounds | `ScreenToolsMonitorInventoryTests` (5) | Integration |

Coverage: `RegionMath` and `ScreenTools` 100 % line and branch headless — and that was already
true before GREEN added 26 tests, which is the recurring lesson: the overflow defect, the empty
inventory, the description contract and the real-inventory invariant were all found by re-walking
requirements, not by hit counts. Bite check: ten one-line breaks (display winning, display
unvalidated, inclusive-edge `- 1`, union clamped to 0, `selectedDisplays` inverted, both
`CoordinateNote` conditions, double enumeration, primary ignored, empty guard) each caught by
two to fifteen tests.

## Deviations and follow-ups

- **Which error wins when both `region` and `display` are invalid** is the display's (it is
  parsed first). Not pinned; either order satisfies "an invalid value still errors".
- **`display:"all"` on an empty inventory** selects nothing and the error comes from `Union`
  ("No monitors to capture"). Both halves are pinned so they cannot drift.
- **Single-monitor dev box**: the `Integration` inventory tests resolve the same rect on every
  path here; the multi-monitor discrimination is proven by the mocked unit tests and by the
  union capture test on a two-screen desk. They need a desktop session and fail (not skip) in a
  session-0 runner, like `InputServiceTests.HoverAsync_lands_exactly_on_every_monitor`.
- **Behaviour changes to migrate:** `region` is always in the metadata now; an out-of-bounds
  region errors instead of clipping; `screenshot(region:…, display:"7")` on a one-monitor box
  errors where it used to work silently.
