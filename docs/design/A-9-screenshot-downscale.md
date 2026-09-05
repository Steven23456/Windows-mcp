# A-9 — `screenshot`: auto-downscale, `--screenshot-scale`, coordinate-scale report

**Checklist item:** [A-9](../upstream-parity-checklist.md#a-9--auto-downscale-scale-env-coordinate-scale-report--p1--s) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 1, second item; carries the three follow-ups
[A-7](A-7-screenshot-image-content.md) left (encode extraction, `BuildHttpApp` seam, quality) ·
**Status:** implemented 2026-09-04 (build clean, 585/585 headless + 9/9 desktop-only tests green —
see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

Every capture was encoded at full resolution: a 4K desktop is a ~10 MB PNG, which no client
will put in a model's context, and A-7's inline default made that the normal case. Upstream
downscales anything over 1920×1080, lets an env var shrink further, and tells the model the
factor to multiply image coordinates by before clicking.

## Decision

- **Pure core** `ScaleMath.Fit(origW, origH, maxW, maxH, userScale) → (Width, Height,
  CoordinateScale)`: fit = min(1, maxW/origW, maxH/origH) with a cap ≤ 0 ignored; total = fit ×
  userScale; each side = max(1, round-away-from-zero(orig × total)); never upscales.
  `CoordinateScale = origW / Width` — derived from the width because that is the transform the
  pixels actually got; when the height is the limiting side the two differ by rounding and the
  width one is the honest one. NaN is rejected by a positive range test (`!(x > 0 && x <= 1)`),
  the same idiom `ServerOptions` uses, because NaN fails every comparison and would slip past
  `x <= 0 || x > 1`.
- **Service.** `CaptureAsync(region, CaptureOptions? options)` replaces the bare `ImageFormat`
  parameter. `CaptureOptions(Format, MaxWidth=1920, MaxHeight=1080, Scale=1.0, Quality=90)`;
  `ScreenshotResult` gains `OriginalWidth`, `OriginalHeight`, `CoordinateScale`. Pipeline:
  capture → `Fit` → `Downscale` only when the size changes → `Encode`. The GDI buffer stays
  locked for the whole chain (the Skia bitmap is a zero-copy view of it). `Downscale` uses
  `SKBitmap.ScalePixels` with a Mitchell cubic (`SKFilterQuality` no longer exists in SkiaSharp
  4; Mitchell is the closest to upstream's LANCZOS without ringing). `Encode` validates
  quality 1–100 for both formats so a bad value cannot hide behind PNG.
- **OCR never downscales**: `OcrService` passes `MaxWidth: 0, MaxHeight: 0`.
- **Process option** `--screenshot-scale <0.1-1.0>` / `WINDOWSMCP_SCREENSHOT_SCALE` in
  `ServerOptions` (roadmap C7): parsed with `NumberStyles.AllowDecimalPoint` + invariant culture
  (no sign, exponent, NaN, Infinity, or `0,5`), valid under **both** transports, so it is parsed
  before the stdio early return. `AddWindowsMcp(builder, options)` registers a public
  `ScreenshotOptions(Scale)` singleton; `ScreenTools` takes it as an optional constructor
  parameter and multiplies it into the call's own `scale`. The caps stay per-call tool arguments;
  the roadmap's wider `ScreenshotOptions(MaxWidth, MaxHeight, Backend, DisableFlash)` is for the
  later items to grow into.
- **Tool.** `max_width=1920`, `max_height=1080` (0 = no limit), `scale=1.0` in (0, 1],
  `quality=90`; all validated before the capture with messages naming the argument and range.
  Metadata gains `originalWidth`/`originalHeight` always and, only when `CoordinateScale ≠ 1`,
  `coordinateScale` (a number) plus `note`: "multiply image pixel coordinates by N before passing
  them to click/drag/scroll" (invariant culture, `2` not `2.0`). Absent otherwise, so the model
  only sees the instruction when it applies. Same shape for `file` output.
- **`BuildHttpApp(options, cert, configureServices?)`** — the seam A-7 asked for, applied after
  `AddWindowsMcp` so the caller's registration wins. The transport tests now prove the whole
  screenshot surface headless with a mocked capture service.

## Changes

- `Abstractions/Models/ScreenDtos.cs` — `CaptureOptions`, `ScreenshotOptions`, three new
  `ScreenshotResult` fields. `IScreenshotService.CaptureAsync` signature.
- `Services/ScaleMath.cs` (new), `Services/ScreenshotService.cs` (`Downscale`, `Encode`,
  pipeline), `Services/OcrService.cs` (no-cap options).
- `Tools/ScreenTools.cs` — four parameters, validation, effective scale, metadata, description.
- `Hosting/ServerOptions.cs` (`ScreenshotScale`, flag, env, Usage), `Hosting/WindowsMcpHost.cs`
  (`AddWindowsMcp(options)`, `ScreenshotOptions` singleton, `configureServices`), `Program.cs`.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Tool schema advertises the four parameters with their defaults and the multiply contract | `HttpTransportTests.Screenshot_schema_advertises_*`, `*_still_discovered_*` | Integration |
| R2 | `Fit` rules: cap, no-limit, user scale on top, never upscale, ≥ 1 px, width-derived scale, reject bad scale (NaN) and source, round away from zero | `ScaleMathTests` (8 methods, 28 cases) | Unit |
| R3 | `Downscale` exact size / new bitmap / colour kept; `Encode` magic bytes, quality changes size, 1–100 validated for both formats, PNG ignores quality | `ScreenshotEncodeTests` (9 methods) | Unit |
| R4 | Real pipeline: cap → 100×50 from 200×100, originals and scale 2.0, user scale on top → 4.0, no-limit unchanged, null options = defaults | `ScreenshotServiceTests` (5 A-9 methods) | UIAutomation |
| R4a | Cancellation checked before the screen is touched (0×0 tripwire region) | `ScreenshotEncodeTests.CaptureAsync_*cancel*` (2) | Unit |
| R5 | `--screenshot-scale`: default, range, culture, CLI beats env, blank unset, stdio too, Usage | `ServerOptionsTests` (9 methods, 30 cases) | Unit |
| R6 | DI: singleton `ScreenshotOptions` from `ServerOptions`; `Default` = 1.0 | `WindowsMcpHostTests` (4) | Unit |
| R7 | Tool: args → `CaptureOptions`, validation before capture, effective scale, metadata always/only-when rules, invariant formatting, file mode too; A-7's 50 tests still pass | `ScreenToolsTests` (16 A-9 methods, 36 cases) | Unit |
| R8 | OCR captures with no cap | `OcrServiceTests` (3) | Unit |
| R9 | Image + `originalWidth`/`coordinateScale`/`note` survive real HTTP with a mocked service; `configureServices` runs after `AddWindowsMcp` | `HttpTransportTests` (2) | Integration |

Coverage: `ScaleMath`, `ScreenTools`, `ServerOptions` 100 % line; `ScreenshotService` 31 %
headless (the capture path is desktop-only and covered by the UIAutomation tests). Bite check:
eight one-line breaks, two of which were caught by **nothing** until a test was added — the
`AwayFromZero` rounding (a `ToEven` swap changed no existing expectation) and the first
cancellation check (a later in-flight check masked its removal on a box with a desktop).

## Deviations and follow-ups

- **Quality default stays 90** (roadmap said 85). It is now a per-call `quality` argument, so
  the default is the pre-existing constant and the caller can go lower.
- **R10 — the stdio host wiring has no test.** `Program.RunStdioAsync` → `AddWindowsMcp(options)`
  is the only thing making `--screenshot-scale` work under the default transport, and nothing
  drives the stdio host in-process; a change to `AddWindowsMcp(ServerOptions.Stdio)` would keep
  the suite green. Candidate for a `BuildStdioHost(options, configureServices?)` seam mirroring
  `BuildHttpApp`, or a live-exe smoke in the `todo.md` sweep. Logged there.
- **OCR's real path** (`BitmapDecoder` → `OcrEngine`) has no live test at all — pre-existing,
  the `disk_inspect` shape. Logged in `todo.md` for the e2e sweep; A-8 touches the shared
  region parser and is the natural place for a `UIAutomation` OCR test.
- **The effective scale is not re-validated** after the multiplication. Unreachable through
  `ServerOptions` (clamped to [0.1, 1.0]); a host constructing `ScreenshotOptions(0)` directly
  would get `ScaleMath`'s `ArgumentOutOfRangeException` rather than the tool's
  `ArgumentException`. Noted, not fixed.
- `Downscale`'s `!ScalePixels` guard is unreachable in practice (Skia throws earlier on a
  degenerate source); kept as a cheap invariant, its comment no longer claims callers can see it.
