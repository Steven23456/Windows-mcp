# A-10 — a second capture backend: Windows.Graphics.Capture

**Checklist item:** [A-10](../upstream-parity-checklist.md#a-10--alternative-capture-backend-wgc--dxgi--p3--ml) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 5, third item ·
**Status:** implemented 2026-09-05 (build clean, 1939/1939 headless tests green, 25/25
desktop-only capture tests green — 12 of them pull real compositor frames — see CHANGELOG
[Unreleased]) ·
**Effort:** ~1½ days including the interop spike and the RED/GREEN passes.

## Problem

`screenshot` had one way to read the screen: GDI's `Graphics.CopyFromScreen`. GDI copies what
the window manager composed for the GDI surface, which is black for DRM-protected video,
hardware-overlay swap chains and some GPU-accelerated windows, and it is the slow path on
high-refresh multi-monitor setups. Upstream keeps a backend registry (`dxcam` → `mss` →
Pillow) selected by an environment variable and echoes the backend used; A-7 reserved a
`backend` metadata field for exactly this.

## Decision

- **Windows.Graphics.Capture, monitor items, no new package.** `WgcCaptureBackend` asks the
  compositor for its own frames: for each monitor the requested rect touches, a
  `GraphicsCaptureItem` from `IGraphicsCaptureItemInterop.CreateForMonitor` (the activation
  factory via `RoGetActivationFactory`; the two COM interfaces it needs are declared with the
  leading methods only, in vtable order, per the COM rule), a free-threaded
  `Direct3D11CaptureFramePool` (BGRA8, two buffers), one frame, then `IDirect3DDxgiInterfaceAccess`
  → `ID3D11Texture2D` → a CPU-readable staging texture → `Map` → a row copy into a premultiplied
  BGRA Skia bitmap at the overlap's offset. The D3D11 device is created once per backend instance
  (hardware, BGRA support) and wrapped for WinRT with `CreateDirect3D11DeviceFromDXGIDevice`;
  every native surface is declared in `NativeMethods.txt` (roadmap C9). The compositor's own
  pointer is switched off (`IsCursorCaptureEnabled = false`) so A-11's overlay is the only cursor
  in the picture and `include_cursor:false` really means none. **Every** failure — WGC
  unsupported, no monitor under the rect, no frame within 2 s, any COM refusal — is a `false`
  from `TryCapture`, never an exception, so the caller can fall back.
- **The service chooses the frame source; the pipeline does not care.** `ScreenshotService`
  resolves the backend first (`ResolveBackend(requested, processDefault)`: both sides validated
  against `auto|gdi|wgc`, lower-cased, never trimmed — a padded value is a wrong value, like every
  option — and a call's `auto` defers to the process default), before a single pixel is
  allocated. `AcquireFrame` then hands the rest of the pipeline a writable Skia bitmap either
  way: `gdi` copies the locked GDI buffer out; `wgc` asks the compositor and refuses loudly
  (`InvalidOperationException` naming the backend, the rect and the way out) when it cannot
  serve; `auto` prefers the compositor where `GraphicsCaptureSession.IsSupported()` says so and
  falls back to GDI silently. Cursor overlay (through a GDI `Bitmap` view over the Skia pixels,
  because `DrawIconEx` needs an HDC), `ScaleMath.Fit`, `Downscale`, annotations and the four
  profiling stages are unchanged and run on both frames. `ScreenshotResult.Backend` is what
  produced the picture (`gdi` or `wgc`, never `auto`), and the tool's metadata `backend` is that
  value always — the outcome, not the request, A-7's rule again. The backend instance is created
  lazily on the first `wgc` frame and disposed with the service (`IDisposable`, idempotent; a
  capture after `Dispose` recreates it).
- **Three ways to say which.** `screenshot(backend: auto|gdi|wgc)` per call, validated with the
  other arguments before any work; `--screenshot-backend auto|gdi|wgc` /
  `WINDOWSMCP_SCREENSHOT_BACKEND` for the process default, parsed in `ServerOptions` only and
  carried on `ScreenshotOptions.Backend` into the tool layer (roadmap C7), both transports. The
  roadmap's `IScreenCaptureBackend` interface with a `Gdi` implementation was not built: the GDI
  path stays where it was, one internal class is enough for the second source, and the headless
  tests get an internal frame-source seam (`WgcFrameSource`) instead of fake backends.
- **`IsBorderRequired` is not set.** The roadmap flagged WGC's yellow capture border; the
  property is absent from the 19041 projection the project targets, and this Windows 11 build
  draws no border for monitor items, so nothing was added. If a build does draw one it will be in
  the picture; that is the follow-up.

## Changes

- `Abstractions`: `CaptureOptions +Backend`, `ScreenshotResult +Backend`,
  `ScreenshotOptions +Backend` (all trailing, defaults `auto`/`gdi`/`auto`).
- `Services/WgcCaptureBackend.cs` (new); `Services/ScreenshotService.cs` (`ResolveBackend`,
  `AcquireFrame`, `GdiFrame`, `TryWgc`, the seam, `Dispose`); `NativeMethods.txt`
  (`D3D11CreateDevice`, `CreateDirect3D11DeviceFromDXGIDevice`, `RoGetActivationFactory`,
  `WindowsCreateString`/`WindowsDeleteString`, `MonitorFromPoint`, `D3D11_SDK_VERSION`,
  `IDXGIDevice`, `ID3D11Texture2D`).
- `Hosting/ServerOptions.cs` (`ScreenshotBackend`, `KnownOptions`, Usage);
  `Hosting/WindowsMcpHost.cs` (`ScreenshotOptions` carries it).
- `Tools/ScreenTools.cs` — the `backend` argument, its validation, `CaptureOptions.Backend`,
  the `backend` metadata field, the "if it comes back black, retry with wgc" line in the
  description.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | DTO fields trailing with their defaults; `--screenshot-backend`/env on both transports, flag beats env, blank unset, lower-cased, never trimmed, unknown/missing/repeated rejected naming the choices, Usage | `ScreenshotBackendTests` (6), `ServerOptionsTests` (11 methods) | Unit |
| R2–R3 | `ResolveBackend` (named call wins, auto defers, case, both sides validated, null); resolved before anything is captured; `IDisposable` idempotent; optional options ctor; the real backend never throws from `IsSupported`, refuses no-monitor and off-monitor rects with no bitmap, double dispose; explicit `wgc` that cannot be served refuses naming the backend; the process default reaches the capture | `ScreenshotBackendTests` (5 methods, 25 cases), `ScreenshotBackendIntegrationTests` (7) | Unit / Integration |
| R4–R5 | Tool: forwards the request verbatim, default `auto`, case-insensitive, unknown → `ArgumentException` naming the choices before the monitor walk/cursor/capture/flash, metadata follows the result not the request in both output modes, last argument, `ocr` has none, descriptions; wire schema advertises it, metadata crosses HTTP | `ScreenToolsTests` (10), `OcrServiceTests` (1), `HttpTransportTests` (3) | Unit / Integration |
| R6 | DI: `ScreenshotBackend` on the registered options, singleton, disposed by the container, and the resolved service honours the configured backend / falls back by default | `WindowsMcpHostTests` (6) | Unit |
| R7 | Through the seam: `auto`/`wgc` encode the compositor's frame and say `wgc`, the pixels are the source's, the rect asked for is the caller's; `auto` + refusal or throw → GDI silently; `wgc` + refusal → the error, a throw not surfaced; `gdi` never consults the seam; cursor (inside/outside), downscale, annotations, the four stages on a compositor frame; no options/no region/live pointer defaults; the frame is disposed; a capture after `Dispose` still serves | `ScreenshotFrameSourceTests` (18 methods, 19 cases) | Unit |
| R8 | Real frames on this desktop: a WGC frame is the picture GDI takes, not black (pixel agreement); per-backend reporting; the multi-monitor union; cursor/downscale/annotate/stages on real frames; a new device after `Dispose`; the compositor's pointer is not in the frame; a client asking `wgc` over real HTTP gets a compositor frame and metadata saying so | `ScreenshotWgcCaptureTests` (11), `HttpTransportScreenshotImageTests` (1) | UIAutomation |

Coverage (headless): `ScreenTools`, `ServerOptions` 100 % line; `ScreenshotService` 91 % line /
89 % branch (the rest is `GdiFrame`, the not-supported arm and defensive encode guards, all
desktop-only or unreachable on a machine with WGC); `WgcCaptureBackend` 37 % headless by
construction — everything from `CaptureMonitor` down is native interop and is covered only by
the desktop tests. Bite check: six breaks — `auto` never falling back, `ResolveBackend`
trimming, the tool validating after the capture, metadata reporting the request, the option not
lower-cased, the compositor's frame not disposed — all caught (17 red in total).

The GREEN pass also found three defects in the spike, fixed before the commit: the row copy
had a vertical bounds guard but no horizontal one (now clamped in both axes against the
texture's real size, so a resolution change between the monitor enumeration and the capture
can never read past a row), the projected `IDirect3DSurface` wrapper was never disposed (one per
capture, which matters in an agent loop), and two RED-stub comment fragments were left in
production code.

## Deviations and follow-ups

- **The checklist's "done when"** — a GPU-accelerated window black under GDI captured correctly
  under the alternative — could not be reproduced on this box (no DRM or exclusive-overlay
  content to hand); what is proven is that `wgc` frames agree with GDI pixel-for-pixel on
  ordinary content, so the swap costs nothing, and the mechanism (the compositor's own texture)
  is the one that sees those surfaces by construction. Belongs in the live e2e sweep with a
  protected video playing.
- **`IsBorderRequired`** — see the decision above; a build that draws the capture border would
  need the 22H2 projection or a raw `IGraphicsCaptureSession3` QI.
- **A frame whose size differs from the requested rect** is reported as-is (`OriginalWidth/
  Height` are the frame's) and never re-cropped; the real backend always returns rect-sized, so
  nothing pins this.
- **Session 0 / no compositor** cannot be tested here; the path is the same `false` chain the
  no-monitor refusal test exercises (`auto` → GDI, `wgc` → the error).
- Multi-monitor rects at mixed DPI are composed in physical pixels, which matches `MonitorInfo`
  only because `Program.Main` sets PerMonitorV2 awareness; a host that skips that would get
  the clamp, not a crash, but also a partial picture.
