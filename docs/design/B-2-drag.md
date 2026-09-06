# B-2 — `drag`: duration, intermediate motion, from the current cursor

**Checklist item:** [B-2](../upstream-parity-checklist.md#b-2--drag-duration-intermediate-motion-from-current-cursor--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 2, last item ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

`DragAsync` was button down, one absolute jump, button up. File managers, canvases and browser
drag-and-drop recognise a drag only after the pointer moves past the system drag threshold and
then keeps moving, so the jump dropped nothing anywhere. The origin had to be given even when
the pointer was already there.

## Decision

- **A pure path** (`DragPath.Points(from, to, steps, nudge)`): the first point is a nudge of
  `nudge` pixels along the direction of travel (a distance, not a per-axis offset) when there
  is one and the drag is longer than it, else the origin; then `steps` evenly spaced points
  ending exactly on the destination, `steps + 1` points in all, monotone on each axis and inside
  the rectangle the drag spans; a zero-distance drag is just the destination. The service reads
  the nudge once as `SM_CXDRAG + 1`.
- **`IInputService.DragAsync(…, durationMs, steps)`**: press at the origin, move through the
  points with `duration / steps` between them, release on the destination — released in a
  `finally`, so a cancelled drag never leaves the button down. The old overload keeps today's
  press-jump-release for byte-compatibility. Middle button stays refused.
- **`drag(from_x?, from_y?, to_x?, to_y?, element_id?, from_element_id?, button, duration_ms,
  steps)`**: destination by point or `element_id` (required; the refusal names `to_x` and
  `element_id`), origin by point, `from_element_id`, or the live cursor when nothing is given
  (C2); `duration_ms` 0–10 000, `steps` 2–200, the refusal naming the range. The four
  coordinates stay the first four parameters. Response `{fromX, fromY, toX, toY, button,
  durationMs, steps, fromTarget: point|element|cursor, elementId?, name?}` — the id and name
  are the destination's.

## Changes

- `Services/DragPath.cs` (new, pure); `Abstractions/IInputService.cs` and
  `Services/InputService.cs` — the overload; `Tools/InputTools.cs` — `Drag` re-signed and
  re-described.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Path: no nudge is a plain interpolation from the origin; the nudge first, as a distance on a diagonal; `steps + 1` points; ends exactly on the destination; monotone; inside the rectangle; zero distance; shorter than the nudge never overshoots; fewer than one step refused by name | `DragPathTests` (9 methods, 20+ cases) | Unit |
| R2 | Tool: the new overload only, defaults echoed; the cursor as the default origin; element destination and element origin; no destination refused naming `to_x` and `element_id`; conflicting forms refused; duration and step ranges with both ends accepted; middle forwarded for the service to refuse, unknown button refused; the four coordinates first; description; schema with nothing required | `InputToolsDragTests` (14 methods), `InputServiceTypeTests` (1), `HttpTransportTests` (1) | Unit / Integration |
| R3 | A drag across a line of Notepad text selects it (read back with Ctrl+C) | `InputToolsDesktopTests` (1) | UIAutomation |

## Deviations and follow-ups

- The interpolation is from the origin, with the nudge as an extra first point, so the second
  point may sit before the nudge on a very short drag; the tests pin only the first point, the
  last point, the count and monotonicity, so either reading is allowed.
- Whether a given target accepts the drop is the target's business; the response reports the
  motion that was made.
