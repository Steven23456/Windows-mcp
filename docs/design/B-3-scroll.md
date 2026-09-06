# B-3 — `scroll` at the cursor or at an element, and Shift+wheel

**Checklist item:** [B-3](../upstream-parity-checklist.md#b-3--scroll-at-current-cursor-or-at-an-element--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 2, third item ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~1 h including the RED/GREEN passes.

## Problem

`scroll(x, y, direction, amount)` demanded coordinates: an agent that had just clicked
something had to repeat the point to scroll it, and could not scroll a snapshot element by id.
Some apps ignore the horizontal wheel and only scroll sideways on Shift + the vertical wheel.

## Decision

- **`scroll(direction, amount, x?, y?, element_id?, shift_wheel)`** — `direction` comes first
  now, a positional break called out in CHANGELOG; coordinates and `element_id` go through the
  shared resolver (C1); with no target the wheel turns under the live cursor and the response
  says `target: "cursor"` (C2). Response `{direction, amount, x, y, target, shiftWheel,
  elementId?, name?}`.
- **`shift_wheel`** holds Shift and sends the vertical wheel for `left`/`right` (wheel up = left,
  wheel down = right, the convention), and is refused for `up`/`down` at the tool, before the
  cursor is read. `IInputService.ScrollAsync` gains the `shiftWheel` overload; the old one
  delegates with `false`.

## Changes

- `Abstractions/IInputService.cs` — the overload; `Services/InputService.cs` — direction
  validated up front, Shift held in a `try/finally`; `Tools/InputTools.cs` — `Scroll` re-signed
  and re-described.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | No target → the cursor is read and scrolled and `target:"cursor"`; coordinates → `point` and the cursor never read; `element_id` → the centre and `element` with id and name; the exclusivity and half-pair rules; an off-screen element refused | `InputToolsScrollTests` (7 methods) | Unit |
| R2 | `shift_wheel` forwarded for left/right in any case, refused for up/down before any call, false by default for a horizontal scroll; `direction` first and the rest optional; description; schema with only `direction` required | `InputToolsScrollTests` (5), `HttpTransportTests` (1) | Unit / Integration |
| R3 | `scroll("down")` with no coordinates scrolls the editor under the cursor (the snapshot's scroll percent moves) | `InputToolsDesktopTests` (1) | UIAutomation |

## Deviations and follow-ups

- An unknown `direction` is still the service's refusal (it owns the table); the tool only
  validates `shift_wheel` against it.
- Whether Shift + wheel scrolls a given app sideways is the app's choice; the response reports
  what was sent, not what moved.
