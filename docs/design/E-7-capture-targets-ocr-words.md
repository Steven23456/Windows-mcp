# E-7 — Capture by window / element; OCR word positions

Checklist item: `docs/upstream-parity-checklist.md` § E-7. Builds on A-8 (`ResolveRegionAsync`,
`RegionMath`), B-10 (`WindowMatcher`) and the element cache.

**Status:** implemented 2026-11-13 (see CHANGELOG [Unreleased]; not verified live — the
`UIAutomation` category, including `OcrWordPositionDesktopTests` and `OcrServiceLiveTests`, was not
run). "Not in scope" below, and two more gaps found in the source, are logged as parity
D-54 … D-58; gaps from the review are D-59 … D-65.

## Two changes

1. `screenshot` and `ocr` can be pointed at **a window** or **an element** instead of a rect the
   caller had to look up and pass back as `region`.
2. `ocr(format:"json")` returns **where** each line and word is, in virtual-desktop pixels, so text
   read from an application with no UI Automation tree (a remote-desktop session, a game, a canvas)
   can be clicked.

Both tools stay read-only: nothing is focused, raised, restored or scrolled.

## Targets

Both tools gain three optional parameters, appended after their existing ones:

| parameter | meaning |
|---|---|
| `window` (`string?`) | a window title, matched by `WindowMatcher` (exact → substring → fuzzy ≥ 70) |
| `hwnd` (`long?`) | a window handle from `window(action:"list")`; with `window` too, `hwnd` wins (B-10) |
| `element_id` (`string?`) | an element id (`el_N` from a snapshot, or an id from `find_element` / `get_state`) |

A call names **at most one kind of target**: a rect (`region` and/or `display`, whose own rule —
`region` wins — is unchanged), a window (`window` and/or `hwnd`), or an element (`element_id`).
Two kinds together is an `ArgumentException` naming both parameters, before anything is looked up.
A blank or whitespace `window` / `element_id` counts as not given (as `region` and `display`
already do). With no target at all the default is unchanged: the primary display.

One resolver (`ResolveRegionAsync`, extended) serves both tools, so they cannot drift.

### Window → rect

`WindowMatcher.Match(await windows.ListAsync(includeMinimized: true, includeHidden: false), window, hwnd)`
— the same inventory and matcher `window(action:…)` uses, so no match is the same
`KeyNotFoundException` listing the open titles. Then:

- `State == Minimized` → `InvalidOperationException`: the window is minimised and has no picture;
  the message names the title and says to restore it (`window` action `restore`). Nothing is
  restored for the caller.
- The window's `Bounds` are **clipped to the virtual screen** (the union of the monitors). A
  maximised window's rect overhangs its monitor by its invisible frame, and a window dragged half
  off the edge is still worth reading; `region`'s "rejected, not clipped" rule is for a rect the
  caller typed, not for one a window reports.
- Nothing left after clipping (zero or negative width or height: the window lies wholly outside the
  virtual screen, or has an empty rect) → `InvalidOperationException` naming the title, its bounds
  and the virtual screen: "off-screen".

### Element → rect

`uia.GetElementAsync(element_id)` — an unknown or expired id is that call's own
`KeyNotFoundException`. Then:

- `Bounds` null, or width or height ≤ 0 → `InvalidOperationException`: the element has no
  rectangle on screen (off-screen, collapsed or virtualised); the message names the id and says
  `interact_element` `scroll_into_view` may bring it into view.
- Otherwise the bounds are clipped to the virtual screen like a window's; nothing left → the same
  "off-screen" refusal. `IsOffscreen:true` alone is **not** a refusal when real bounds remain
  on-screen (browsers over-report it, D-7).

### What a target capture is

It is the **screen rectangle the target occupies**, not the target's own surface: a window lying
over it is in the picture. The descriptions say so, and say to bring the window to the front first
(`switch_to_window`) when that matters. (Per-window capture through Windows.Graphics.Capture is a
different feature.)

A window rect is `GetWindowRect`'s, which on Windows 10/11 includes the invisible resize border: a
few pixels of whatever is beside the window appear at the left, right and bottom edges. Accepted.

### What the caller is told

`screenshot` metadata gains `target` when a window or element picked the rect:

- `{"kind":"window","title":…,"hwnd":…,"matchStrategy":"hwnd|exact|substring|fuzzy","score":N}`
- `{"kind":"element","elementId":…,"name":…,"controlType":…}`

and `targetClipped:true` when clipping removed part of the target's rect. `region` (always present)
is the rect actually captured. `selectedDisplays` is absent for a target capture.

## OCR positions

### Service

`IOcrService` gains

```csharp
Task<OcrResult> RecognizeAsync(ScreenRegion region, CancellationToken ct = default);
```

with, in `Abstractions/Models`:

```csharp
public record OcrResult(string Text, OcrLine[] Lines, ScreenRegion Region, double? TextAngle = null);
public record OcrLine(string Text, Bounds Bounds, OcrWord[] Words);
public record OcrWord(string Text, Bounds Bounds);
```

- `Region` is the rect that was captured; every `Bounds` is in **virtual-desktop pixels**.
- The capture is the one `ExtractTextAsync` already takes (PNG, no size cap, no cursor, backend at
  the process default). `ExtractTextAsync` is unchanged in contract and returns the same string.
- The mapping is pure and separately tested (`OcrGeometry.ToVirtualDesktop`): for an engine
  rectangle `(x, y, w, h)` in image pixels, a capture origin `(ox, oy)` and the capture's
  `CoordinateScale` `s` (1 for OCR's uncapped capture, but honoured), the edges are
  `left = ox + round(x·s)`, `top = oy + round(y·s)`, `right = ox + round((x+w)·s)`,
  `bottom = oy + round((y+h)·s)` (midpoint away from zero), and the bounds are
  `(left, top, right − left, bottom − top)`. Mapping edges, not sizes, keeps adjacent words
  adjacent. A non-zero origin and a negative origin (a monitor left of or above the primary) are
  the two cases the checklist names.
- A line's `Bounds` is the union of its words' bounds; a line with no words is dropped.
- `TextAngle` is the engine's detected rotation in degrees, when it reports one.

### Tool

`ocr` gains `format`: `text` (default) | `json`; anything else is an `ArgumentException` before any
capture.

- `text` — exactly today's output: the recognised text, nothing else, via `ExtractTextAsync`.
- `json` — one object:

```json
{"coordinateSpace":"virtual-desktop",
 "region":{"x":…,"y":…,"width":…,"height":…},
 "target":{…},
 "text":"…",
 "lines":[{"text":"…","x":…,"y":…,"width":…,"height":…,
           "words":[{"text":"…","x":…,"y":…,"width":…,"height":…,"centerX":…,"centerY":…}]}]}
```

  `centerX` / `centerY` are the word's centre (`x + width/2`, `y + height/2`, integer division) and
  go straight to `click`. `target` and `targetClipped` appear as for `screenshot`; `textAngle`
  appears when the engine reports one. Nothing recognised is `"text":""`, `"lines":[]`, not an
  error.

## Shape in the code

- `Tools/ScreenTools.cs` — `ResolveRegionAsync(region, display, window, hwnd, elementId)` returning
  the rect, the monitors, the selected displays and a `CaptureTarget?`; both tools call it.
- `Services/RegionMath.cs` — `Clip(Bounds, ScreenRegion virtualScreen)` → `ScreenRegion?` (pure).
- `Services/OcrGeometry.cs` — the pure mapping.
- `Services/OcrService.cs` — one private capture-and-recognise path behind both members.
- No new tool: the count stays 70. Annotations unchanged (both read-only).

## Decisions from the review

- **The flash glow never reaches OCR.** `ocr` calls `IFlashOverlay.Hide()` before its capture, in
  both formats, exactly as `screenshot` does: the glow of a previous capture is a top-most frame
  around that capture's rect and would otherwise be read (or hide text) when the next `ocr` rect
  contains it. `ocr` never shows a glow of its own.
- **A target on no monitor is off-screen.** The virtual screen is the bounding box of the monitors,
  so with unequal monitors a rect can lie inside it and still on no monitor. After clipping, a
  target rect that intersects **no** monitor is refused with the same "off-screen" refusal (GDI
  would return black, and OCR "nothing recognised"). A rect only partly in such a gap is captured
  as is (D-56).
- **Tilted text.** Measured with the real engine
  (`OcrServiceRecognizeIntegrationTests.RecognizeAsync_word_centres_lie_on_the_glyphs_of_rotated_text`,
  a rendered 1000×600 frame of text rotated by ±5° and ±15°): Windows.Media.Ocr reports
  `TextAngle` close to the true rotation (+5 → 5.5, −5 → −5.1, +15 → 15.5, −15 → −16.1) and the
  word rectangles in its **deskewed** frame, not the image's axes. Before the fix the reported word
  centres were about 25–30 px from the glyphs at ±5° and 76–85 px at ±15°. `OcrGeometry` now
  rotates each word's four corners back by `+TextAngle` about the image centre (y down, in image
  pixels, before scale and origin) and reports their axis-aligned box, which puts the centres on
  the glyphs to within about 3 px. `textAngle` appears in the JSON only when non-zero, and the
  description says that each rectangle is then the upright bounding box around its tilted word or
  line, so it is larger than the text and may reach past `region`, while `centerX`/`centerY` are
  still the words' centres.
- **Non-finite angles.** A text angle that is not a finite number is treated as no angle: no
  rotation and no `textAngle` key (`OcrService` and the tool both check `double.IsFinite`).
  `OcrGeometry` refuses a non-zero angle without an image size with an `ArgumentException`, since
  the pivot would otherwise silently be the image origin.
- **Refusal wording.** A title in a refusal is clipped (`UiRefusals.Clip`) so a very long title
  cannot push the instruction past the 2,000-character cap. The no-rectangle element refusal also
  names the case where the element's window has closed; the off-screen refusals say the window may
  be minimised (element) or may have just closed (window with an empty rect).
- **Descriptions.** `format:"json"` on a whole dense screen can be very large — narrow it with
  `window`, `element_id` or `region`; a maximised window normally reports `targetClipped:true`
  (its invisible frame overhangs its monitor); with `annotate:true` an `el_N` id from an earlier
  snapshot is replaced by the new ids in the returned list, while an id from `find_element` or
  `get_state` stays valid; with `textAngle` each rectangle is the upright bounding box around its
  tilted word or line, larger than the text and possibly past `region`, and `centerX`/`centerY`
  are still the words' centres.
- **Left as they are:** `hwnd` 0 or negative is a handle nothing has (a miss, not "no target");
  an `element_id` is looked up verbatim, as every element tool does; a minimised window that
  matches ahead of a visible one with the same title is refused (the `window` tool's rule).

## Not in scope

- Capturing a covered or minimised window's own surface.
- DWM extended-frame bounds (the visible rect without the invisible border).
- A word cap or paging for `format:"json"` on a screen full of text.
