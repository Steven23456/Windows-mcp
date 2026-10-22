# S-11 — the "AI is in control" indicator and the takeover hotkey

**Checklist item:** [S-11](../upstream-parity-checklist.md#s-11--ai-control-indicator-and-takeover-hotkey--p2--m) ·
**Status:** implemented 2026-10-21 on branch `feature/control-indicator`, not yet merged (see
CHANGELOG [Unreleased]; the review findings F1–F7 and what changed because of them are under
"Deviations and follow-ups") ·
**Effort:** ~2 days including the RED/GREEN/REVIEW passes.

## Problem

When an agent drives the mouse and keyboard through Windows-mcp, a person at that machine (the
local desktop, or a remote host served over HTTP) sees the pointer move and text appear with no
explanation and no obvious way to say "stop, I've got it".

Upstream 0.8.8 (PR #445) answered this with a full desktop-control layer:
- it gates tool calls behind an owner switch;
- it blocks physical input through low-level hooks;
- a large mouse movement or Ctrl+Alt+Shift+Backspace counts as a user takeover.

That layer produced two upstream bugs within a week:
- **#457:** the agent was refused whenever the user touched the mouse, because every touch
  restarted a 10 s idle wait.
- **#456:** the gate latched forever after a hook missed a key-up.

## Decision

We port the **visible half only**, a deliberate deviation from upstream:

- **Nothing is ever refused, delayed or cancelled.** No tool call is gated and no physical input
  is blocked. The indicator is information, not control.
- **Takeover is deliberate.** Only the key combination counts; mouse movement never does. The
  default combination is **Ctrl+Alt+Shift+Backspace** (upstream's), set with
  `--control-hotkey <combo>` / `WINDOWSMCP_CONTROL_HOTKEY`. Each part is resolved by the
  existing `ShortcutParser.ResolveKey`; it needs Ctrl, Alt or Win **written out** (Shift may be
  added) and exactly one non-modifier key. (This note first said any of Ctrl/Alt/Shift/Win;
  review F3 narrowed it — see below.)
- **What takeover does:** the indicator hides at once, and the AI's **next** tool result, from
  any tool, carries one extra text block saying the user took over and when. The note is
  delivered once. The next driving call shows the indicator again.
- **Visuals:**
  - a blue ring around the pointer, following it at about 60 Hz;
  - a pill banner at the top-centre of the work area of the monitor the pointer is on, scaled to
    that monitor's DPI.
  - Blue keeps it distinct from A-14's orange capture flash. There is no screen-edge glow.
- **Lifetime:**
  - It appears when a *driving* tool starts.
  - It stays while any driving call is in flight (refcounted, because HTTP clients can overlap).
  - It fades 5 s after the last one ends; a new driving call during the fade keeps it up.
- **Driving tools.** These are the tools that inject input or move focus: `click`, `drag`,
  `hover`, `type`, `key`, `shortcut`, `scroll`, `multi_select`, `multi_edit`,
  `interact_element`, `file_dialog`, `focus`, `switch_to_window`. Everything else, including
  `window`, `launch` and `powershell`, never shows it.
- **One switch.** `--control-indicator on|off` / `WINDOWSMCP_CONTROL_INDICATOR`, default **on**
  under both transports, for the same reason as A-14's flash: the indicator matters most when
  the controller is remote. When it is off, nothing is created and the hotkey is never
  registered.
- **The hotkey is registered only while the indicator is visible**, so other applications get
  the combination back the rest of the time. If registration fails because another application
  owns the combination, the banner drops the hotkey text and a warning is logged once per
  indicator instance (one per process in production).
- **Never in a picture.** Both windows get `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`
  on Windows 10 2004+ (build ≥ 19041; below it none is applied — review F2).
  `ScreenshotService.CaptureAsync`, the single path that `screenshot`, its annotated form and
  `ocr` all use, wraps the frame acquisition in `IControlIndicator.SuspendForCapture()`. That is
  a no-op when the affinity took effect, and otherwise hides and restores the windows around the
  grab.
- **Invisible to automation.** The windows are `WS_EX_LAYERED | WS_EX_TRANSPARENT |
  WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` popups with no title, the same as the
  flash. They are click-through, never take focus, and are dropped by A-1's `WindowFilter`.
- **A courtesy, never a failure.** With no interactive window station (session 0, Task
  Scheduler), every member is a silent no-op. Showing waits at most about 50 ms for the overlay
  thread. Nothing here ever throws into a tool call.

## Shape

| Piece | Kind | Role |
|---|---|---|
| `IControlIndicator` (Abstractions) | interface | `BeginAction()`, `EndAction()`, `IsVisible`, `IsCaptureExcluded`, `SuspendForCapture()`, `ConsumeTakeover()` |
| `Models/ControlIndicatorDtos.cs` | records | `HotkeyChord(uint Modifiers, ushort Key, string Display)` (`MOD_ALT=1`, `MOD_CONTROL=2`, `MOD_SHIFT=4`, `MOD_WIN=8`; `Display` such as `Ctrl+Alt+Shift+Backspace`); `ControlIndicatorOptions(bool Enabled, HotkeyChord Hotkey, TimeSpan Linger)` with `Default`; `TakeoverRecord(DateTimeOffset At, string Hotkey)` |
| `Services/ControlIndicatorState.cs` | pure, `TimeProvider` | refcount, fade deadline, dismissal, the pending takeover; thread-safe |
| `Services/ControlIndicatorArt.cs` | pure SkiaSharp | `CursorRing(dpiScale)`, `Banner(text, dpiScale)`, `BannerText(hotkey?)`, `RingRect`, `BannerRect` |
| `Services/LayeredWindow.cs` | Win32 helper | the window creation and DIB + `UpdateLayeredWindow` paint extracted from `FlashOverlay`; behaviour unchanged, except that the window procedure is now static (see below) |
| `Services/ControlIndicator.cs` | Win32 | the STA thread, two windows, the pointer-follow tick (which also runs the fade check), `RegisterHotKey` / `WM_HOTKEY`, display affinity |
| `Hosting/HotkeyParser.cs` | pure | `--control-hotkey` text → `HotkeyChord`, built on `ShortcutParser.ResolveKey` |
| `Hosting/DrivingTools.cs` | static set | the 13 driving tool names |
| `Hosting/WindowsMcpHost.AddWindowsMcp` | filter | begins and ends around driving calls; appends the takeover note to the next result |

The pure state has no timers. Once the overlay thread exists, its tick runs every 16 ms while
the indicator is visible and every 100 ms otherwise, and asks the state whether it should be
visible. That one tick is both the cursor follower and the fade, so the state stays deterministic
under a fake clock.

## Requirement matrix

| # | Requirement |
|---|---|
| R1 | `--control-indicator on\|off` (the `ParseSwitch` spellings, default on, CLI beats env) and `--control-hotkey` (default `ctrl+alt+shift+backspace`, CLI beats env). A combination without a modifier, with a non-modifier before the last part, with a modifier as the last part, or with an unknown key is refused with the option named. `Display` is the canonical `Ctrl+Alt+Shift+Win` order plus the key. Both options appear in Usage. `ControlIndicatorOptions` and `IControlIndicator` are registered as singletons even when off. |
| R2 | State: `Begin` makes it visible; overlapping `Begin`s are refcounted; it stays visible until the last `End`; it then stays visible for exactly `Linger` and is hidden after. A `Begin` during the fade cancels the fade. An unbalanced `End` clamps at zero. |
| R3 | State: a takeover while visible hides it at once (even with calls in flight), records `{At = now, Hotkey}`, and is consumed exactly once. A takeover while hidden records nothing. The next `Begin` after a takeover shows it again. An in-flight call ending after a takeover does not bring it back. |
| R4 | Art: the ring is square, its side scales with DPI, its centre is transparent (the pointer shows through), and it has a blue band. The banner height scales with DPI, its width grows with the text, its corners are transparent (a pill), and it has light text pixels on a dark fill. Both are premultiplied (no channel above alpha). A non-positive scale is refused. `BannerText` with and without a hotkey. The ring is centred on the pointer, and the banner is centred at the top of the work area with negative monitor origins honoured. |
| R5 | Host filter: a driving tool calls `BeginAction` before and `EndAction` after exactly once, including when the tool throws (both caller-facing and unexpected errors). A non-driving tool never calls them. A pending takeover is appended once, as the last content block, to the next result of **any** tool, including an `IsError` result. Every `DrivingTools` name exists in the tool inventory. |
| R6 | Capture: `CaptureAsync` brackets frame acquisition with `SuspendForCapture()` when an indicator is injected, and works unchanged without one. |
| R7 | Real windows: `BeginAction` makes `IsVisible` true; neither window appears in `WindowService.ListAsync` (including hidden ones); while visible the combination is registered (a second `RegisterHotKey` of the same chord fails), and after the fade it is free. A posted `WM_HOTKEY` hides it and makes `ConsumeTakeover` return a record. When off, nothing is created and nothing is registered. `Dispose` is idempotent and makes every later call a no-op. |
| R8 | Desktop: while visible, the ring window is on screen centred on the pointer. GDI and WGC captures of the ring and banner areas match captures taken with the indicator hidden. With display affinity disabled through a test seam, the same comparison fails, which proves the test catches a regression. |
| R9 | `FlashOverlay`'s existing tests stay green after the `LayeredWindow` extraction. |

## Risks

- **`WDA_EXCLUDEFROMCAPTURE`** needs Windows 10 2004 or later, and layered windows have had
  affinity quirks. R8 decides whether the affinity alone is enough on both backends. If it is
  not, `SuspendForCapture` hides unconditionally, which costs a brief flicker per capture but
  keeps the guarantee.
- **`RegisterHotKey`** is bound to the banner window; `WM_HOTKEY` is posted to its thread whatever
  has focus. R7 covers the handler with a posted message; the live check covers a real press.

## Deviations and follow-ups

**Decided by the desktop tests (R8).**
- **Display affinity alone is enough, on both backends.** Under per-monitor-v2 DPI awareness, GDI
  and WGC captures of the ring and banner areas match captures taken with the indicator hidden,
  and with the affinity switched off through the `ExcludeFromCapture` seam the same comparison
  fails. The first desktop failures came from the DPI-unaware test host, which virtualises
  `GetWindowRect`, `GetCursorPos` and `GetMonitorInfo` but not the pixels a capture reads; they
  were not an affinity quirk. So on 2004+ `SuspendForCapture` stays a no-op and no capture
  flickers.

**Changed by the review.**
- **F2 — Windows before 10 2004.** Below build 19041, `WDA_EXCLUDEFROMCAPTURE` "succeeds" but
  behaves as `WDA_MONITOR`, so every capture would show two black boxes. Below that build no
  affinity is applied, `IsCaptureExcluded` stays false, and `SuspendForCapture` hides the windows
  around each grab (a brief flicker per capture). The same fallback covers an affinity call that
  Windows refuses.
- **F3 — the written-modifier rule.** The note first allowed any of Ctrl/Alt/Shift/Win. While the
  indicator is up the combination is taken from every other application, so it must never be a
  key people type. Ctrl, Alt or Win must now be **written**, and Shift may be added. Shift alone
  (`shift+a` is just "A") is refused, and so is a combination whose only modifiers come from the
  keyboard layout (Shift for `!`, AltGr's Ctrl+Alt for `@`). Implied modifiers are still part of
  what is registered, or the key could not be pressed at all.
- **F4 — a monotonic fade.** The fade is measured with `TimeProvider.GetTimestamp` /
  `GetElapsedTime`, so a wall-clock step back cannot hold the indicator and the hotkey up. Only
  `TakeoverRecord.At`, which the agent reads, is wall-clock time.
- **F1 — a takeover is not spent on a result nobody reads.** When the call's token is cancelled
  by the time the tool returns, the filter leaves the takeover pending. A fault the SDK masks
  leaves the filter before the takeover is consumed, so it waits too. This narrows the window but
  cannot close it: a response lost after the filter has returned (a dropped connection, a client
  that stops reading) still spends the note.
- **F5 — the indicator can never fail a call.** The filter wraps `BeginAction`, `EndAction` and
  `ConsumeTakeover` so anything they throw is swallowed. A show that faults or stalls after
  registering the combination never strands the registration: the next tick hides and
  unregisters it, or keeps it for the retry rather than mistaking its own hold for someone else's.
- **Per-instance warning.** "Logged once per process" became once per `ControlIndicator`
  instance, which is the same thing for the server's one singleton.
- **`LayeredWindow`'s window procedure is static.** The extraction found a latent hazard:
  `FlashOverlay`'s window procedure was a per-instance delegate behind a window class that lives
  for the whole process. A second instance reused the class, so once the first was collected its
  windows would have been dispatched through a freed delegate. One static procedure now serves
  every class `LayeredWindow` registers.

**Known limitations (not fixed).**
- **F6** — the agent's own `shortcut` with exactly the takeover chord reaches the registered
  hotkey and is read as a user takeover. `WM_HOTKEY` does not say where the keys came from.
- **F7** — over HTTP with several clients, the pending note goes to whichever call completes
  first, which may be another client's.
- A monitor, DPI or work-area change while the pointer stays still repaints the ring and banner
  only when the pointer next moves.
- `MOD_NOREPEAT` is passed but cannot be observed by a test (a posted `WM_HOTKEY` carries no
  repeat state).
- Whether capture-excluded windows show for a person viewing the session over RDP is unverified.

**Test arrangements.**
- **D1** — `HttpTransportTests`' harness runs with the indicator off. It is on by default under
  both transports, so without this every test that calls a driving tool (`switch_to_window`'s
  miss, `multi_select`'s refusal, …) would draw on the desktop and register the user's real
  Ctrl+Alt+Shift+Backspace. The filter tests swap in a mock `IControlIndicator`, and the pipeline
  test turns the real one back on with a test-only chord.
- **Per-monitor-v2 is process-wide in the desktop classes.** `ControlIndicatorDesktopTests` and
  `ControlIndicatorPointerTests` set per-monitor-v2 DPI awareness, the server's own mode, through
  `SetProcessDpiAwarenessContext`. That call is process-wide and succeeds once, so every
  UIAutomation class that runs after them in the same test process inherits it.
  `ControlIndicatorPointerTests` also moves the real pointer, so it is `Category=UIAutomation` and
  attended-only.
