# A-14 — the post-capture flash overlay and snapshot/screenshot profiling

**Checklist item:** [A-14](../upstream-parity-checklist.md#a-14--post-capture-flash-overlay-and-snapshot-profiling--p3--m) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 5, first item ·
**Status:** implemented 2026-09-05 (build clean, 1720/1720 headless tests green — 12 of them
drive the real overlay window — and 5/5 desktop-only tests green — see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

A person sitting at the target machine had no indication that a remote agent had just captured
their screen, and nobody had numbers for where a slow snapshot or screenshot spent its time.
Upstream draws an orange glow around the captured area for a few seconds after every capture
and can log per-stage timings.

## Decision

- **The glow is on under both transports, one switch.** The roadmap's first draft turned it off
  under HTTP; that was dropped because transport is a poor proxy for "is anyone watching", and
  the overlay matters most when the controller is remote. The switch is `--flash on|off` /
  `WINDOWSMCP_FLASH` — the parser has no valueless flags (every option takes a value; `--help` is
  the only exception), so the roadmap's `--no-flash` / `WINDOWSMCP_DISABLE_FLASH` ship as
  `--flash off`, and the old spellings are pinned as *unknown options* rather than silent aliases.
  `--profile-snapshot on|off` / `WINDOWSMCP_PROFILE_SNAPSHOT` is the second switch. Both accept
  `on|off|true|false|1|0` case-insensitively, un-trimmed (a padded value is a wrong value, like
  every other option), and cross into the tool layer on `ScreenshotOptions(Scale, Flash, Profile)`
  and `UiTreeOptions(MaxElements, Profile)` (roadmap C7).
- **`FlashGlow` is pure** (SkiaSharp, 100 % covered): the window rect is the captured rect
  inflated by a 10 px margin; the bitmap is premultiplied BGRA, transparent inside the captured
  area, ten concentric 1 px orange frames whose alpha rises from 40 at the outside to 255 where it
  meets the picture. `FlashOverlay` owns the Win32: a lazily started STA thread (a 20 ms work
  queue plus a `PeekMessage` pump), a `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST |
  WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` popup registered once per process, painted with
  `UpdateLayeredWindow` from a top-down 32 bpp DIB filled from the Skia pixels, positioned
  top-most without activation, hidden by a timer after the duration. `Show` blocks (bounded, 2 s)
  until the thread has done the work, so `IsVisible` is honest the moment it returns, and every
  failure path — no window station, a class that will not register, a rect too small to frame —
  returns false and sets `IsVisible` false. The glow is a courtesy; it can never fail a capture.
  The window is a tool window with no title, so A-1's `WindowFilter` drops it and `snapshot` never
  walks it.
- **The tool hides before and shows after.** `screenshot` calls `Hide()` before **every** capture,
  whatever the switch says (a previous call's glow must never be in a picture), and `Show(rect,
  3.5 s)` around the captured rect after a capture that succeeded, only when `Flash` is on. A
  rejected argument or a failed capture never touches the overlay; `ocr` never does. Metadata
  carries `flash: true` when the glow is actually visible after `Show` (the outcome, per A-7's
  rule, not the request — on a host with no window station it stays absent).
- **Profiling reports only when asked.** The roadmap said timings go in the JSON "always"; they
  go only when `--profile-snapshot` is on, so an unprofiled response is byte-identical to before.
  `SnapshotResult.Stages` = `header` (cursor, monitors, window list) and `walk`; the text form
  ends with `Timing: header 12 ms, walk 130 ms (total 142 ms)` after the truncation note.
  `ScreenshotResult.Stages` = `capture`, `cursor`, `resize`, `encode` (all four always, a no-op
  stage reads 0). The tool merges its own `resolve`, `cursor`, `snapshot` (only with `annotate`)
  and `capture` with the service's — the service's finer number wins a name clash — into a
  `stages` metadata object. Both the tool and the services log the stages at **Information**
  (the roadmap said Debug; the stderr logger's minimum is Information, so Debug would never be
  seen) through new optional `ILogger` constructor parameters on `ScreenshotService` and
  `ScreenTools`.

## Changes

- `Abstractions`: `IFlashOverlay` (`Show`, `Hide`, `IsVisible`); `StageTiming`;
  `ScreenshotOptions +Flash, +Profile`; `UiTreeOptions +Profile`; `CaptureOptions +Profile`;
  `ScreenshotResult +Stages`, `SnapshotResult +Stages` (JSON-omitted when null).
- `Services/FlashGlow.cs`, `Services/FlashOverlay.cs` (new); `Services/ScreenshotService.cs`
  (stages, logger); `Services/UIAutomationService.cs` (stages, log);
  `Services/UiTree/SnapshotRenderer.cs` (timing line); `NativeMethods.txt` (the layered-window
  and GDI entries).
- `Hosting/ServerOptions.cs` (`Flash`, `ProfileSnapshot`, `ParseSwitch`, Usage);
  `Hosting/WindowsMcpHost.cs` (both options records carry the switches; `IFlashOverlay`
  singleton, always registered — the thread only starts on the first `Show`).
- `Tools/ScreenTools.cs` — `IFlashOverlay` injected, hide/show around the capture, `flash` and
  `stages` metadata, logger.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Both switches: defaults, both transports, the six spellings in any case, padded/unknown values rejected naming the option and values, CLI beats env, blank unset, bare/`=`/repeat errors, the roadmap's old spellings are unknown options, Usage; both options records filled; the overlay registered as a singleton even when off | `ServerOptionsTests` (11 methods, ~70 cases), `WindowsMcpHostTests` (7) | Unit |
| R2 | `FlashGlow`: margin, window rect (negative origins), size, premultiplied BGRA, inner rect transparent, orange band on four sides with closed corners, alpha fade, too-small refused; `FlashOverlay` on the real desktop: visible immediately and gone after the duration, a second `Show` replaces, `Hide` idempotent, a too-small rect is a silent no-op, not in the window inventory, `Dispose` idempotent and everything after it a no-op | `FlashGlowTests` (20), `FlashOverlayTests` (12) | Unit / Integration |
| R2b | The glow really paints on screen and disappears on `Hide`; the framed area is untouched | `FlashOverlayDesktopTests` (2) | UIAutomation |
| R3 | Tool: hide → capture → show, the resolved rect and 3.5 s, off → hide only, failed capture → no show, invalid argument and `ocr` touch nothing, `flash` metadata; the container's overlay is the one that flashes; no new tool parameters | `ScreenToolsTests` (10), `HttpTransportTests` (2) | Unit / Integration |
| R4 | Profiling: DTOs JSON-invisible when off; snapshot stages header/walk in order and bounded by `CaptureMs`, null when off, nothing else changes; the four capture stages in order (desktop); renderer's timing line last, after the note, order and total pinned; the tool's `stages` object, the `snapshot` stage only with annotate, service wins a clash; the `snapshot` tool carries timings in both formats and nothing when off; stages **logged at Information** and nothing logged when off | `StageTimingDtosTests` (8), `UIAutomationSnapshotArgumentTests` (5), `ScreenshotServiceTests` (3, UIAutomation), `SnapshotRendererTests` (7), `ScreenToolsTests` (9), `UIAutomationToolsTests` (4) | Unit / UIAutomation |

Coverage: `FlashGlow`, `SnapshotRenderer`, `ScreenTools`, `ServerOptions` 100 % line;
`FlashOverlay` 95 % (the rest is the failure chain only a session-0 host or a racing `Dispose`
reaches). Bite check: twelve breaks — the hide before the capture removed, show when off, show
before the capture, `yes` accepted, the timer never firing, the band drawn as a fill, the timing
line before the note, the service losing the name clash, header/walk swapped, both log lines
demoted to Debug, `IsVisible` forced true — all caught; the last three only after the GREEN pass
added logging and degenerate-rect tests.

## Deviations and follow-ups

- **Session 0 / no window station** cannot be tested here; the code path is the same
  `return false` chain the too-small-rect test exercises. Belongs in the live e2e sweep (run the
  exe under Task Scheduler and call `screenshot`).
- **`--profile-snapshot` does not profile `get_state`**; the switch's help text names snapshot
  and screenshot, which is what it covers.
- The overlay's message pump never sees a posted message in practice (a non-activating layered
  tool window gets its messages sent, not posted); it stays as liveness insurance so the shell
  never marks the window hung.
