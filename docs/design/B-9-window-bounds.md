# B-9 — `window` move, resize and set_bounds

**Checklist item:** [B-9](../upstream-parity-checklist.md#b-9--window-resize--move--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 3, second item ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, 5/5 desktop tests green
— see CHANGELOG [Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

`window` could minimise, maximise, restore and close, but not place: an agent arranging two
windows side by side, or making a dialog fit a screenshot, had nothing to call. Upstream's
`App(mode=resize)` does `MoveWindow` on a named or the active window and refuses a minimised or
maximised one.

## Decision

- **Three actions on the existing tool**, no new tool: `move` needs `x` and `y`, `resize` needs
  `width` and `height`, `set_bounds` needs all four; the refusal names what the action needs.
  The target is matched like the other actions (B-10's `WindowMatcher`: `hwnd` wins, else
  exact → substring → fuzzy) or is the foreground window when neither is given. The
  unknown-action message now lists ten actions.
- **A pure geometry step** (`WindowGeometry.Apply(match, x, y, width, height, restoreFirst,
  native)`) behind a seam (`IWindowGeometryNative`: `IsIconic`, `IsZoomed`, `Restore`,
  `SetWindowPos`, `GetRect`; `Win32WindowGeometryNative` is the only user32 caller): validate
  first (at least one value, sizes positive — a call that asks for nothing reads no window);
  ask the **window**, not the inventory, whether it is minimised or maximised and refuse naming
  the state and `restore_first` unless that flag sends `SW_RESTORE` first (`Restored` true);
  read the rect; one `SetWindowPos` with `SWP_NOZORDER | SWP_NOACTIVATE` always (a move never
  raises or activates), `SWP_NOMOVE` when neither `x` nor `y` was given and `SWP_NOSIZE` when
  neither size was, a half-given pair filled from the current rect; read the rect again. The
  result is `WindowBoundsResult(Window, Before, After, MatchStrategy, Score, Restored)` and
  `After` is the outcome, whatever user32 returned and however Windows clamped it (C11).
- The inventory and the re-read both use `GetWindowRect`, so `window(action:"set_bounds", …)`
  followed by `window list` reports the same numbers.

## Changes

- `Abstractions`: `WindowBoundsResult`; `IWindowService.SetBoundsAsync`.
- `Services/WindowGeometry.cs` (`IWindowGeometryNative`, `Validate`, `Apply`, the `SWP_*`
  values), `Services/Win32WindowGeometryNative.cs` (new); `Services/WindowService.cs`
  (`SetBoundsAsync`, the geometry seam).
- `Tools/WindowTools.cs` — the three actions, `x`/`y`/`width`/`height`/`restore_first` appended
  after `hwnd`, the description.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Validate: nothing asked names all four; a non-positive size refused; any single value accepted, a negative `x` included; `Apply` validates before touching the window | `WindowGeometryTests` (4 methods) | Unit |
| R2 | Apply on a fake: never raises or activates; move adds `NOSIZE` and takes the size from the rect, resize adds `NOMOVE`, set_bounds neither; a half pair filled from `Before`; minimised and maximised refused naming the state, nothing moved; the live `IsIconic`/`IsZoomed` decide, not the inventory; `restore_first` restores before the move (both states) and leaves a normal window alone; `GetRect` before and after in order; `After` is the outcome even when clamped or when `SetWindowPos` returned false; the matcher's verdict passed through; the flag values | `WindowGeometryTests` (14 methods) | Unit |
| R3 | Real `SetWindowPos` on a window this process owns: moved and resized to the rect the inventory then reports; move keeps the size, resize the position; `hwnd` wins over a wrong title; the foreground is not taken; a minimised window refused then accepted with `restoreFirst`; nothing asked moves nothing; an unknown title or a dead hwnd is a `KeyNotFoundException` | `WindowServiceBoundsTests` (9) | Integration |
| R4 | Tool: the three actions forward exactly their pair(s), `restore_first` on all three, `hwnd` accepted, no target left to the service, case-insensitive; the response serialises both rects and the verdict; per-action refusals name what is needed; the reading and state actions ignore the new parameters; the ten-action message; description and parameter docs, the A-1 parameters first; the schema and a round trip over HTTP | `WindowToolsBoundsTests` (28), `HttpTransportTests` (2) | Unit / Integration |
| R5 | Notepad set to (100,100,800,600) and `window list` reports that rect; move keeps the size and resize the position on a real app window; a maximised and a minimised Notepad refused, then accepted with `restore_first`; no target moves the foreground window | `WindowBoundsDesktopTests` (5) | UIAutomation |

## Deviations and follow-ups

- `Restored` means "SW_RESTORE was sent" and covers maximised as well as minimised, the B-10
  reading.
- A per-monitor-DPI move across monitors of different scale may be clamped or re-scaled by
  Windows; `After` reports what happened, and the desktop here has one scale.
- `MatchStrategy` is `"foreground"` when no target was named; it is not one of the matcher's
  four, and the tool's description says so.
