# A-1 — `window(action:"list"|"active")`: the whole-desktop window inventory

**Checklist item:** [A-1](../upstream-parity-checklist.md#a-1--whole-desktop-window-inventory--p1--m) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 2; unlocks A-2's snapshot header, A-12, B-6, B-8, B-10 ·
**Status:** implemented 2026-09-05 (build clean, 1159/1159 headless tests green, of which 17 go
through the real enumerator — see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

Nothing enumerated windows. `IWindowService` could act on an exact title through `FindWindow`,
switch to one, launch one, and list monitors; the model had no way to learn what was open, which
window was in front, or where a window sat. Upstream heads every snapshot with a table of the
focused and open windows (name, z-order, status, size, handle, process, browser flag).

## Decision

- **Facts and judgement are separate.** `WindowService.ListAsync` walks `EnumWindows` (its order
  is z-order, topmost first) and fills a `WindowProbe` per window — visibility, extended style,
  DWM cloaking, rect, raw title, class name, iconic/zoomed, pid, process name (cached per pid
  within a call) — every read guarded because a window can die mid-enumeration. The pure
  `WindowFilter` then decides, so every rule is provable on hand-written probes with no desktop.
- **The filter** (`WindowFilter.Keep`): drop when not visible; drop `WS_EX_TOOLWINDOW` unless
  `WS_EX_APPWINDOW` forces a taskbar button; drop DWM-cloaked (UWP ghosts, windows on another
  virtual desktop); drop zero-area; drop the shell chrome classes upstream drops
  (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `Progman`, `WorkerW`, `IME`, `MSCTFIME UI`, exact
  and ordinal); drop an untitled window unless `include_hidden`; drop a minimized one unless
  `include_minimized` (default true). The title is judged **after** `UiText.Sanitize` (A-13), so a
  codicon-only caption counts as untitled.
- **`WindowInfo`**: `Title` (sanitised), `Hwnd`, `Pid`, `ProcessName`, `State`
  (`Minimized` beats `Maximized` — a minimized window keeps `WS_MAXIMIZE`; serialised **by name**
  via `JsonStringEnumConverter`, because the model reads it), `Bounds` in virtual-desktop pixels,
  `ZOrder` = index in the **filtered** list, `IsActive` = the `GetForegroundWindow` handle,
  `IsBrowser` from the process name (`chrome, msedge, firefox, brave, opera, vivaldi`, with or
  without `.exe`; the set is `internal` for A-5), `MonitorIndex` = `CursorMath.MonitorIndexOf` of
  the window's **centre** (a window straddling a seam is reported once; a minimized window parked
  at −32000 is −1), `DesktopId` reserved for A-12 and null.
- **`GetActiveAsync` is the list route**: `WindowFilter.ActiveOf(await ListAsync())`, so the
  active window's `ZOrder` is its real inventory position rather than a lie, and it is null when
  the foreground window is filtered out (the desktop, a cloaked window). `ActiveOf` is its own
  method because on a quiet desktop "first window" and "active window" coincide and no live test
  can tell `FirstOrDefault()` from `FirstOrDefault(IsActive)` — the GREEN bite check proved that.
- **Tool surface** (roadmap C4, no new tool): `window` gains `list` and `active`, plus
  `include_minimized` / `include_hidden`. The tool validates the **action first**, then the title
  for the four acting actions, then dispatches; `list`/`active` ignore `title`. An unknown action
  is an `ArgumentException` naming all six — it used to reach the service and come back as
  `Success:false` when the title was not found. The service's `ExecuteAsync` is unchanged.
- **Roadmap C9**: the native surface goes through `NativeMethods.txt` (`EnumWindows`,
  `IsWindowVisible`, `GetWindowLong`, `GetWindowRect`, `GetWindowThreadProcessId`, `IsIconic`,
  `IsZoomed`, `DwmGetWindowAttribute`, `GetClassName`, `GetForegroundWindow`,
  `GetWindowTextLength`); `UIAutomationService`'s stray `DllImport GetForegroundWindow` — the
  last one in `src/` — is retired. CsWin32 has no `GetWindowLongPtr` entry; `GetWindowLong` maps
  to the pointer-sized export on x64.

## Changes

- `Abstractions`: `WindowState`, `WindowInfo`, `WindowProbe`; `IWindowService.ListAsync`,
  `GetActiveAsync`.
- `Services/WindowFilter.cs` (new: `Keep`, `StateOf`, `IsBrowser`, `BrowserProcesses`, `Build`,
  `ActiveOf`); `Services/WindowService.cs` (`ListAsync`, `GetActiveAsync`, `Probe`);
  `Services/UIAutomationService.cs` (C9); `NativeMethods.txt`.
- `Tools/WindowTools.cs` — `list`/`active` dispatch, validation order, descriptions.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | DTO shapes; `DesktopId` null; `State` by name | `WindowFilterTests.Build_carries_every_probed_fact_through_unchanged`, `Build_leaves_DesktopId_null_until_A_12`, `WindowToolsTests.Window_list_writes_the_state_as_its_name` | Unit |
| R2 | Every filter rule and its near-misses (tool window ± app window, cloaked, zero/negative area, six chrome classes and six look-alikes, untitled ± `includeHidden`, title judged after sanitising, minimized ± `includeMinimized`, flags never rescue); `StateOf` precedence; `IsBrowser` ± `.exe`, any case, rejects look-alikes; `BrowserProcesses` is case-insensitive; `Build`: order kept and renumbered, title sanitised, exactly one active or none, browser flag, monitor from the centre (seam case), −1 off-monitor / empty inventory, empty desktop; `ActiveOf` picks the flagged entry not the first, null when none/empty | `WindowFilterTests` (33 methods, 71 cases) | Unit |
| R3 | Through the real enumerator: the session actually has windows, every field sane and hwnds unique, `ZOrder` contiguous from 0, at most one active, `MonitorIndex` is −1 or a real index, no "Program Manager", titles sanitised, `GetActiveAsync` equals the flagged list entry **including `ZOrder`**, `includeMinimized:false` is a renumbered subset with no `Minimized`, `includeHidden:true` a superset, cancellation honoured, `ExecuteAsync` not-found and missing-title paths | `WindowServiceTests` (18) | Integration |
| R4 | Tool: `list` JSON array with every field, flags forwarded, case-insensitive, title ignored; `active` one object / `{"found":false}`; acting actions need a non-blank title and never reach the service without one; unknown/empty/padded action rejected naming all six **before** the title is looked at; action forwarded as written; `switch_to_window`/`focus`/`launch` regression guards | `WindowToolsTests` (17 methods, 30 cases) | Unit |

Coverage: `WindowFilter` and `WindowTools` 100 % line and branch; `WindowService` 77 % line —
the enumeration path **is** exercised headless (about 3 500 probes per suite run); what remains
is the defensive halves of guarded Win32 reads, the ≥512-character-title heap arm, and the
pre-existing acting/launch/switch bodies that need a disposable window. Bite check: ten one-line
breaks (app-window exemption, raw-title judgement, probe-index z-order, origin-based monitor,
`.exe` strip, title-before-action ordering, first-instead-of-active, empty enumeration, chrome
classes unmatchable, unsanitised title); all caught except first-instead-of-active, which is
what `ActiveOf` and its three tests now pin.

## Deviations and follow-ups

- **"EnumWindows order is z-order, topmost first" is not provable headless** — it needs a known
  foreground and stacking. It is the documented contract of `EnumWindows` and the live e2e sweep
  is where to eyeball it.
- **`ListAsync_excludes_the_shell_chrome` does not bite on Windows 11 build 28000**: Program
  Manager is already dropped by an earlier rule (visibility/cloaking), so the class rule's proof
  is the six-row unit theory; the live test is a backstop.
- **`WindowService.ExecuteAsync("bogus", null)`** still reports the missing title rather than the
  unknown action — unreachable through the tool now, left alone.
- **Two `include_hidden` readings**: the name suggests invisible windows; the rule is untitled
  ones (invisible windows are never listed). The description says so.
- A-2's snapshot header consumes `ListAsync` and `ActiveOf` as-is; A-12 fills `DesktopId`.
