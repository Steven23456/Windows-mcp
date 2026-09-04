# D-3 — Cursor placement on secondary monitors

**Checklist item:** [D-3](../upstream-parity-checklist.md#d-3--cursor-placement-is-wrong-on-secondary-monitors--p1--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) · **Order:** do this **first** — D-2's physical-click fallback clicks
element bounds, and on a second monitor that only lands once this is fixed. Effort: a few hours.

## Problem

`src/WindowsMcp/Services/InputService.cs:50` `MoveCursorToVirtualDesktop` normalises `x`,`y` against
the **primary** monitor (`SM_CXSCREEN` / `SM_CYSCREEN`, `:47`) but hands the result to
H.InputSimulator's `MoveMouseToPositionOnVirtualDesktop`, which sends
`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK` and therefore expects 0..65535 across the whole
**virtual screen**. Concretely, on this dev box (virtual screen 7680×2160, primary 3840 wide) a
request for `x = 5000` is scaled as if the screen were 3840 wide, lands around `x = 10000`, and is
clamped to the right edge. A monitor left of or above the primary has negative coordinates and can
never be reached at all. Every mouse tool inherits the bug: `click`, `drag`, `hover`, `scroll`.

## Decision

Position the cursor with `SetCursorPos(x, y)` (already declared in `src/WindowsMcp/NativeMethods.txt`)
and keep sending button and wheel events exactly as today — H.InputSimulator's `LeftButtonClick`,
`LeftButtonDown/Up`, `VerticalScroll` etc. carry no position, so they act wherever the cursor is.
This is what upstream does, it is pixel-exact (no 65535 rounding), and it is DPI-correct because
`Program.cs:36` opts the process into Per-Monitor-V2, so `SetCursorPos`, UIA `BoundingRectangle`,
and `multi_monitor` all speak the same physical virtual-desktop pixels.

After the move, read back `GetCursorPos` (also declared). `SetCursorPos` silently clamps anything
outside the virtual screen to the nearest edge — the same "landed somewhere else" failure this item
is about — so a mismatch throws `ArgumentOutOfRangeException` naming the requested point, where the
cursor actually ended up, and the virtual-screen rectangle
(`SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, `SM_CYVIRTUALSCREEN`).

**Rejected:** fixing the normalisation instead
(`nx = (x - vLeft) * 65535 / (vWidth - 1)`). Also correct, but integer truncation can land a pixel
off on wide virtual screens and it keeps a second coordinate system alive for no benefit. Raw-input
consumers (games) that only see `SendInput` motion are out of scope; B-2 (drag motion) can revisit.

## Changes

- `src/WindowsMcp/Services/InputService.cs`
  - Replace `MoveCursorToVirtualDesktop` and the `ScreenWidth` / `ScreenHeight` properties with
    `private static void MoveCursor(int x, int y)`: `PInvoke.SetCursorPos(x, y)`, then
    `PInvoke.GetCursorPos(out var p)`; if `p.X != x || p.Y != y` build the virtual-screen rectangle
    from the four metrics and throw
    `ArgumentOutOfRangeException($"({x},{y}) is outside the virtual screen {rect}; cursor landed at ({p.X},{p.Y})")`.
  - `ClickAsync`, `DragAsync` (both ends), `HoverAsync` (and through it `ScrollAsync`) call
    `MoveCursor`. No other behaviour change; `DragAsync` keeps its no-intermediate-motion shape
    (that is B-2).
- `src/WindowsMcp/Program.cs:27-30` — the comment still says InputService does "coordinate
  normalization". Say instead: PMv2 keeps `SetCursorPos`, UIA bounds and screenshots in one
  physical-pixel space.
- `src/WindowsMcp/Tools/InputTools.cs` — `click`, `drag`, `hover`, `scroll` descriptions state the
  coordinate space once: *"physical pixels on the virtual desktop; origin = top-left of the primary
  monitor; monitors left of / above it have negative coordinates; see `multi_monitor`"*.
  `screenshot` / `ocr` regions stay primary-only until A-8 — do not claim otherwise.
- `docs/architecture/DATAFLOW.md:156` (click walkthrough) and `COMPONENTS.md:456` (InputService):
  drop the normalisation wording; say `SetCursorPos` + relative button events.

## Tests

`tests/WindowsMcp.Tests/Services/InputServiceTests.cs`, `[Trait("Category","Integration")]` — the
file already documents that input injection fails under the test runner's UIPI mismatch, and
`SetCursorPos` is subject to the same rule.

- `HoverAsync_lands_exactly_on_every_monitor`: `new WindowService().EnumerateMonitorsAsync()` →
  for each `MonitorInfo`, hover to its centre → `GetCursorPos` (a `DllImport` in the test, like
  `NotepadFixture`'s `SetForegroundWindow`) equals the request exactly. On a one-monitor box this
  still exercises the primary; on this dev box it covers the second monitor.
- `HoverAsync_rejects_point_outside_virtual_screen`: `(vLeft - 1000, vTop - 1000)` →
  `ArgumentOutOfRangeException` whose message contains both the requested and the actual point.
- Manual: `multi_monitor` → take the non-primary monitor's bounds → `click` its centre on a known
  window → confirm with `get_state` / the foreground window (not `screenshot`, which is primary-only).

## Docs / CHANGELOG

One bullet under `CHANGELOG.md [Unreleased] ### Fixed`. No tool-count change. Then tick D-3 in the
checklist and board.

## Done when

Every monitor centre reported by `multi_monitor` is reached exactly (integration test green on a
multi-monitor box); off-screen coordinates fail loudly instead of clamping; the four mouse tools
state the coordinate space. This supersedes the checklist's "unit test proves the normalisation" —
there is no normalisation left to test.
