# B-12 — `multi_monitor` detail: work area, orientation, DPI, scale

**Checklist item:** [B-12](../upstream-parity-checklist.md#b-12--multi_monitor-detail-work-area-orientation-dpi-scale--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 1, third item ·
**Status:** implemented 2026-09-05 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~1 h including the RED/GREEN passes.

## Problem

`MonitorInfo` carried index, device name, bounds and the primary flag. An agent placing a window
had no work area (the taskbar is inside the bounds), no idea whether a display is rotated, and no
DPI or scale to reason about what a 150 % display does to the pixels it is told about. Upstream's
`DisplayInventory` reports all four.

## Decision

- **Four trailing fields on `MonitorInfo`**, defaulted so every existing construction compiles
  and A-8's region maths is untouched: `Bounds? WorkArea` (null when unknown, never a zero
  rect), `int Orientation` (0, 90, 180, 270), `int EffectiveDpi` (96 by default), `double Scale`
  (= `EffectiveDpi / 96`). Read from `MONITORINFOEXW.rcWork`, `EnumDisplaySettings(szDevice,
  ENUM_CURRENT_SETTINGS).dmDisplayOrientation × 90`, and `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`,
  each falling back to its default when Windows will not say. `DeviceName` stays `MonitorN` — the
  seven original fields say what they always said.
- **Only `multi_monitor` shows the detail.** The `screenshot` `displays` metadata keeps exactly
  A-8's six fields and the snapshot never carried monitors, so neither response changes; a
  regression guard pins both.

## Changes

- `Abstractions/Models/WindowDtos.cs` — `MonitorInfo +WorkArea, +Orientation, +EffectiveDpi,
  +Scale`.
- `Services/WindowService.cs` — `EnumerateMonitorsAsync` reads `MONITORINFOEXW`, the DPI and the
  current display mode; `NativeMethods.txt` (`GetDpiForMonitor`, `EnumDisplaySettings`,
  `MONITORINFOEXW`, `DEVMODEW`).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Field order and defaults; `Scale == EffectiveDpi / 96`; `WorkArea` a `Bounds` serialised by name, null when unknown; no other field added | `MonitorInfoTests` (6 methods) | Unit |
| R2 | Live: every monitor's work area non-null, inside its bounds and no taller; the primary's work area equals `SPI_GETWORKAREA`; DPI ≥ 96 with the matching scale and equal to an independent `GetDpiForMonitor` read; orientation one of four and equal to `EnumDisplaySettings`; the seven original fields unchanged | `WindowServiceMonitorDetailTests` (7) | Integration |
| R3 | `multi_monitor` JSON carries the four fields, over the tool and over real HTTP; `screenshot`'s `displays` entries keep exactly six fields; the snapshot carries no monitors | `WindowToolsTests` (1), `HttpTransportTests` (1), `ScreenToolsTests` (1), `MonitorInfoTests` (1) | Unit / Integration |

## Deviations and follow-ups

- This session's displays are 96 dpi and landscape, so the DPI and orientation cross-checks
  pass trivially here; they bite on a scaled or rotated display, and the GREEN pass confirmed
  they bite by breaking the DPI read.
- Per-monitor V2 awareness (set in `Program.Main`) is what makes `EffectiveDpi` the monitor's
  own value rather than the system's; a host that skips it gets the system DPI on every monitor.
