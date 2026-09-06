# Section B roadmap — input, apps, and window ergonomics (B-1 … B-12)

**Scope:** every item in [section B](../upstream-parity-checklist.md#b--input-apps-and-window-ergonomics)
of the parity checklist. This is the implementation plan; each item still gets its own
`docs/design/<ID>-<slug>.md` note when it is picked up (checklist rule 1), and this file is the
place those notes link back to for the cross-item decisions. ·
**Status:** planned 2026-09-05 against `main` @ `f96de50` (65 tools, v0.7.3, sections D and A
closed). Where the code deviates from the plan below, the item carries a **Shipped as** line and
its design note has the reasoning. ·
**Baseline facts** used below were read from the code on that commit; the `file:line` anchors
will drift, the member names will not.

## 1. What section B is, in one paragraph

Section A made the desktop *readable*: one `snapshot` returns every window, the cursor and every
interactive element with an `el_N` id and a centre. Section B makes it *drivable* with the same
economy. Today an agent that wants to fill a field does `click(x,y)` → `shortcut("ctrl+a")` →
`key("backspace")` → `type(text)` → `key("enter")`, five round-trips where upstream does one;
waits with `powershell("Start-Sleep 2")` and pays a PowerShell cold start; launches with a
guessed path; and `switch_to_window` fails for a title that exists because a background process
may not call `SetForegroundWindow`. The twelve items split into three tracks: **input verbs**
(B-1 type, B-4 click, B-3 scroll, B-2 drag, B-7 batches) that take the snapshot's ids and do the
whole gesture; **apps and windows** (B-8 launch, B-10 match and focus, B-9 move/resize, B-11
argv); and **waiting and geometry** (B-5 wait, B-6 wait_for conditions, B-12 monitor detail).
Nothing in section C or S is needed; A-1, A-2 and D-2/D-3/D-5 are already in.

## 2. Cross-item decisions (settle once, every design note inherits them)

| # | Decision | Why |
|---|---|---|
| C1 | **One element-target resolver for every input tool.** `click`, `type`, `scroll`, `hover`, `drag` and the batch tools accept `element_id` as an alternative to coordinates; the tool layer resolves it through `IUIAutomationService.GetElementAsync` (the id cache A-2/D-4 already keep) and a pure `ElementTarget.CentreOf(ElementInfo)` that refuses an off-screen element or empty bounds with one message. Coordinates and `element_id` are mutually exclusive; giving both is an `ArgumentException`. Built in B-4, reused by B-1, B-3, B-2, B-7. | The snapshot hands out ids; every verb should take them. One resolver means one off-screen rule, one error text, one test. D-2's `interact_element(click)` stays the pattern-first path; `click(element_id)` is the physical one. |
| C2 | **Coordinates stay virtual-desktop pixels** (A-roadmap C1); anywhere `x`/`y` become optional (B-3, B-2's `from`) the fallback is the live cursor (`GetCursorPositionAsync`, A-11) and the response says which point was used. | No second coordinate space, and a call with nothing given is still deterministic and reported. |
| C3 | **Tool count: 65 → 68.** `wait` (B-5), `multi_select` and `multi_edit` (B-7) are new tools with upstream's names; everything else is a parameter or an action on an existing tool (`window` gains `move`/`resize`/`set_bounds`; `switch_to_window`/`focus` gain fuzzy matching and `hwnd`; `launch` gains the catalog and the window wait; `start_process` gains `args_json`/`cwd`). `hover` is kept; `click(clicks:0)` is the parity alias. | Every extra tool is a description on every call; three is the floor that keeps upstream's names for the verbs an agent reaches for by habit (`wait` above all). The batches could ride on `click`/`type` with a JSON list, but a list-shaped parameter on a scalar tool is worse for the model than a second tool. |
| C4 | **`wait_for` returns a result, never `null`, and never throws on timeout**: `{satisfied, condition, elapsedMs, attempts, detail, element?}`. Today's `"null"` string on timeout goes; the checklist's open question is settled here. `condition` defaults to `element_exists`, which is exactly today's behaviour, and the existing `text`/`kind`/`scope`/`window`/`include_offscreen` parameters keep their meaning and position, so every current call still works and `text` stays first. | A timeout is an expected outcome the agent acts on, not an error; a structured result with the last `detail` is what upstream returns and what the skill playbook can key on. Behaviour change → CHANGELOG Changed with the one-line migration. |
| C5 | **One `WindowMatcher`, pure, for every title lookup**: exact (ordinal-ignore-case) → substring → fuzzy (token-set partial ratio, score ≥ 70) over A-1's `ListAsync` inventory, plus an explicit `hwnd` that bypasses matching. Built in B-10; `window(action:…)`, `switch_to_window`, `focus`, B-9 and B-8's window wait all call it. `MatchWindows` in `UIAutomationService` (A-2's exact-then-substring) is *not* replaced — snapshot scopes stay strict, a walk must not fuzz. The response always carries `matchedTitle` and `score`. | Upstream's `_find_window_by_name` is what makes `switch_to_window("notepad")` work; one implementation means one score threshold to tune and one test table. |
| C6 | **Fuzzy scoring is in-repo, no package**: `internal static class FuzzyMatch` with `Ratio` (normalised Levenshtein), `PartialRatio` and `TokenSetRatio`, 0–100, unit-tested against a table of upstream `thefuzz` results. Shared by B-10 (titles) and B-8 (app names). | One small file; a NuGet fuzzy library is a dependency for 60 lines, and the scores must be reproducible in tests. |
| C7 | **No PowerShell in B-8.** The app catalog is built from the two Start Menu folders' `.lnk` files (`ShortcutResolver`, already here) and from packaged apps through the WinRT `PackageManager.FindPackagesForUser("")` → `Package.GetAppListEntriesAsync()` (display name + AUMID, in the `net10.0-windows10.0.19041.0` projection, no new package). Launch: a `.lnk`/path via `ShellExecute` (today's `LaunchAsync`); an AUMID via `IApplicationActivationManager.ActivateApplication` (one COM interface, vtable rule, returns the **PID**). The catalog is cached in-process for 5 min and refreshed on a miss. | `Get-StartApps` costs a PowerShell cold start (seconds to tens of seconds under Defender, `CLAUDE.md`) and takes the serialization gate on every `launch`. The WinRT route is in-process, returns the PID the window wait needs, and is unit-testable behind `IAppCatalogService`. |
| C8 | **Long text is pasted, short text is typed**: a pure `TypePlanner` decides — `paste` when `text.Length ≥ 200` and it contains no control characters other than `\n`/`\t`; otherwise per-key with `\n` → Enter, `\t` → Tab and a 5 ms pace (`pace_ms` overridable). Paste goes through `IClipboardService` and **restores the previous clipboard text** afterwards (best effort; a non-text clipboard is not restored and the response says `clipboardRestored:false`). The response always says `method: keys \| paste`. `interact_element(type)` and B-7's `multi_edit` call the same path so `clear`/`caret`/`press_enter` mean the same thing everywhere (D-2 deferred `clear` to here for exactly this reason). | `TextEntry` bursts drop keys in some apps and 5 000 characters at 5 ms is 25 s; a paste is one keystroke. Restoring the clipboard is what makes it safe to do behind the user's back. |
| C9 | **Every native call new to B is declared in `NativeMethods.txt`** (`AttachThreadInput`, `BringWindowToTop`, `AllowSetForegroundWindow`, `GetWindowThreadProcessId`, `GetCurrentThreadId`, `keybd_event`/`SendInput` for the ALT nudge, `GetDpiForMonitor`, `EnumDisplaySettings`, `MonitorFromWindow`, `GetWindowPlacement`), except the two COM interfaces (`IApplicationActivationManager`; `IShellFolder` only if the WinRT route falls short) which follow the vtable-gap rule. `SetWindowPos`, `ShowWindow`, `IsIconic`, `GetMonitorInfo` are already declared. | Repo convention (A-roadmap C9). |
| C10 | **Injected input is `UIAutomation`-category, always** (the phase-4 rule): any test that clicks, types, drags, scrolls, moves the pointer or changes the foreground window is `UIAutomation` and joins the `DesktopCollection` when it reads pixels, moves the pointer, or opens a Notepad window (modern Notepad is one process, and the fixture identifies its window by an inventory diff, so two fixtures must never launch at once). Pure planners (`TypePlanner`, `ElementTarget`, `WindowMatcher`, `FuzzyMatch`, `DragPath`, the app-catalog matcher, `wait_for`'s condition evaluator) are `Unit`; the wiring gets one `UIAutomation` test each on the Notepad fixture. | Section B is almost entirely injection; the only way it is testable at all is to keep the decision logic pure, which is also what makes the GREEN bite checks mean something. |
| C11 | **Foreground changes are reported, not assumed**: every tool that brings a window forward (`switch_to_window`, `focus`, `launch`, B-9, B-7's per-entry clicks via B-1's target click) re-reads `GetForegroundWindow` after the attempt and returns `{success, strategy, restored}` with `strategy` naming which step worked (`SetForegroundWindow` \| `AttachThreadInput` \| `AltNudge`), `false` when none did. | Windows refuses `SetForegroundWindow` to a background process and reports it truthfully; the tool should too (A-7's "outcome, not request" rule). |
| C12 | **One PR per phase, one version bump per phase**, CHANGELOG bullet per item under `[Unreleased]`, `docs-agent` before each PR. Phase 1 → 0.8.x, phase 2 → 0.9.0 (tool signatures change: optional coordinates, `wait_for` result), phase 3 → 0.9.x, phase 4 → 0.10.0 (two new tools). The release cut stays the user's `/version-bump`. | Matches how A landed. Behaviour changes get a minor bump. |

## 3. Order and phases

```
Phase 1  quick wins            B-5 → B-10 → B-12 → B-11                              ~½ day
Phase 2  input verbs           B-4 (with the target resolver) → B-1 → B-3 → B-2      ~1 day
Phase 3  apps and windows      B-8 → B-9                                              ~1 day
Phase 4  composites            B-6 → B-7                                              ~½ day
```

Phase 1 first because `wait` and a `switch_to_window` that works are the two things an agent
hits on every task today, and B-10's matcher is the dependency of half of section B. Phase 2 is
the largest and stands alone once C1's resolver exists; B-4 goes first because it *is* the
resolver. Phase 3 needs B-10's matcher (B-8's window wait, B-9's target). Phase 4 last: B-7 is
built out of B-1 and B-4, and B-6's `active_window` condition uses B-10's matcher. Phases 1 and 2
can run in parallel branches; 3 and 4 cannot start before 1.

### Dependency graph (checklist "Depends on" column, corrected per C1/C5)

```
B-4 ──► B-1 ──► B-7          B-4 builds the element-target resolver (C1)
B-4 ──► B-3, B-2             B-1 builds TypePlanner (C8), reused by interact_element(type)
B-10 ─► B-8, B-9, B-6        B-10 builds WindowMatcher + FuzzyMatch (C5, C6)
B-5, B-11, B-12 stand alone  A-1/A-2/D-2/D-3/D-5 are done and are what B-6/B-8/B-10 needed
```

## 4. Per-item plan

Each item: what changes, the decisions that go beyond the checklist sketch, the RED test matrix
seed (what `test-agent` should be handed), and the done-when bar. "Touches" are as in the
checklist unless corrected.

### Phase 1 — quick wins

#### B-5 — Plain `wait` tool  `P1 · S · ~1 h`

- New tool `wait(seconds: double)` in `InputTools` (it lives with the verbs an agent interleaves
  it with): `Task.Delay`, honours the cancellation token, `seconds` in `(0, 60]` — `0`, negative,
  NaN, and > 60 are `ArgumentException`s naming the range (a longer wait is a `wait_for`).
  Returns `{"waited": <seconds as given>}`. Annotated `ReadOnly = true, Idempotent = true` on the
  `[McpServerTool]` attribute (the SDK carries the hints; C-7 will do the other 67).
- **RED seed.** Range table (0.001, 1, 60 accepted; 0, −1, 60.001, NaN, ∞ rejected naming
  0–60); elapsed ≥ requested and < requested + 250 ms (`Unit`, real delay of 50 ms);
  cancellation cuts it short with `OperationCanceledException`; the tool is discovered over
  HTTP with the two annotations; SKILL.md's playbook no longer says `Start-Sleep`.
- **Done when.** `wait(1.5)` returns after ~1.5 s with no PowerShell process spawned.
- **Shipped as** ([note](B-5-wait.md)): as planned.

#### B-10 — Fuzzy window matching and robust bring-to-foreground  `P1 · M · ~2 h`

- `Services/FuzzyMatch.cs` (C6) and `Services/WindowMatcher.cs` (C5): `Match(WindowInfo[]
  inventory, string? title, long? hwnd)` → `(WindowInfo Window, string Strategy /* exact |
  substring | fuzzy | hwnd */, int Score)` or a `KeyNotFoundException` that lists the open titles
  (A-2's message). Ties: the frontmost (`ZOrder`) wins. Minimised windows are candidates.
- `IWindowService.SwitchToAsync(string title)` → `BringToFrontAsync(string? title, long? hwnd)`
  returning `ForegroundResult(WindowInfo Window, string MatchStrategy, int Score, bool Restored,
  string? Strategy, bool Success)`. Sequence: `IsIconic` → `ShowWindow(SW_RESTORE)` (`Restored`);
  `SetForegroundWindow`; if the foreground is still not ours: `AttachThreadInput(current,
  GetWindowThreadProcessId(hwnd))` + `BringWindowToTop` + `SetForegroundWindow` + detach (skipped
  with a note when `AttachThreadInput` is refused — elevated target); last resort the ALT nudge
  (`keybd_event(VK_MENU)` down/up, then `SetForegroundWindow`). After each step re-read
  `GetForegroundWindow` (C11). `switch_to_window` and `focus` keep their `title` parameter and gain
  `hwnd`; `window(action: minimize|maximize|restore|close)` goes through the same matcher, so
  `window(action:"close", title:"notepad")` works.
- **RED seed.** `FuzzyMatch` against a table of a dozen `thefuzz` scores (`"notepad"` vs
  `"Untitled - Notepad"` ≥ 70; `"edge"` vs `"Untitled - Notepad"` < 70); `WindowMatcher`: exact
  beats substring beats fuzzy, case-insensitive, ties by z-order, `hwnd` wins over `title`, no
  title and no hwnd is an `ArgumentException`, no match lists the open titles; `ForegroundResult`
  strategy names; `Integration`: `BringToFrontAsync` on the foreground window itself reports
  success with `Strategy: SetForegroundWindow`; `UIAutomation` (Notepad fixture behind another
  window): `switch_to_window("notepad")` brings it forward and `GetActiveAsync` agrees; a
  minimised Notepad is restored and `Restored:true`.
- **Done when.** `switch_to_window("notepad")` brings "Untitled - Notepad" to the front from
  behind another app, and says which strategy did it.
- **Shipped as** ([note](B-10-window-matching.md)): as planned, except that a refused
  `AttachThreadInput` (an elevated target) is skipped silently — `ForegroundResult` carries no
  note field, so a refused attach and a rung that ran and failed both show as the ladder moving
  on to the nudge. `AllowSetForegroundWindow(-1)` was not added (it only helps when our process
  already holds the foreground, which rung 1 covers). The desktop tests forced a repair of
  `NotepadFixture`: modern Notepad is one process hosting every window, so the fixture now
  closes the window it opened rather than the process it launched.

#### B-12 — `multi_monitor` detail  `P2 · S · ~1 h`

- `MonitorInfo` gains trailing `WorkArea (Bounds)`, `Orientation (0|90|180|270)`, `EffectiveDpi`,
  `Scale (double, dpi/96)`, `IsPrimary` stays; read from `GetMonitorInfo.rcWork`,
  `EnumDisplaySettings.dmDisplayOrientation`, `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`. Additive:
  every existing constructor call and A-8's region maths are untouched; the snapshot header and
  `screenshot` metadata do not change (the detail is `multi_monitor`'s).
- **RED seed.** Trailing-field shape; `Scale == EffectiveDpi / 96.0`; `WorkArea` inside `Bounds`
  with height ≤ bounds height (the taskbar); orientation in the four values; `Integration`: on
  this session every monitor's DPI ≥ 96 and the primary's work area is the `SPI_GETWORKAREA`
  rect; the JSON over HTTP carries the four fields.
- **Done when.** `multi_monitor` reports a 150 % display as `EffectiveDpi:144, Scale:1.5`.
- **Shipped as** ([note](B-12-monitor-detail.md)): as planned; `WorkArea` is `Bounds?` (null
  when Windows will not say, never a zero rect).

#### B-11 — `start_process` with argv list and cwd  `P2 · S · ~1 h`

- `start_process(command, args_json?, cwd?, use_shell_execute=false)`: `args_json` is a JSON
  string array (also accepted already-parsed, the Claude Desktop quirk B-7 documents) →
  `ProcessStartInfo.ArgumentList` (no quoting); with `args_json`, `command` is the executable
  only; `cwd` must exist (`DirectoryNotFoundException` naming it); `command` alone keeps today's
  behaviour byte-for-byte. Returns `{pid, executable, args, cwd}`.
- **RED seed.** Argument parsing table (JSON array, already-parsed array, not-an-array rejected);
  `ArgumentList` receives the items unquoted (`Integration`: `cmd /c echo` with an argument
  containing a space and a quote comes back intact); missing cwd rejected before any spawn;
  `command`-only call unchanged (existing tests).
- **Done when.** `start_process("notepad.exe", args_json:["C:\\path with space\\a.txt"])` opens
  that file.
- **Shipped as** ([note](B-11-start-process-argv.md)): as planned; the result is
  `{pid, executable, args, cwd}` for both call shapes (the old `"started (pid=N)"` string is
  gone), and the raw-JSON-array binding of `args_json` is left for the live e2e sweep.

### Phase 2 — input verbs

#### B-4 — `click` by element id; `clicks=0` hover  `P2 · S · ~2 h`

- `Services/UiTree/ElementTarget.cs` (C1): `CentreOf(ElementInfo)` → `(int X, int Y)` or
  `InvalidOperationException` naming the id and the reason (off-screen / empty bounds).
  `InputTools` gains `IUIAutomationService` and a `ResolveTargetAsync(int? x, int? y, string?
  element_id)` helper every verb uses. `click(x?, y?, element_id?, button, clicks)`: `clicks:0`
  = move only (`HoverAsync`), reported as `{action:"hover"}`; the result carries the resolved
  point and, when an id was used, the id and its name.
- **RED seed.** `ElementTarget` table (centre by integer division, off-screen refused, empty
  bounds refused, negative coordinates fine — a left monitor); the resolver's exclusivity rule
  (both / neither → `ArgumentException`); `click(element_id)` calls `GetElementAsync` then
  `ClickAsync` at the centre (mocked); `clicks:0` calls `HoverAsync` and never `ClickAsync`;
  descriptions; `UIAutomation`: `snapshot` → `click(element_id: <Notepad's editor>)` focuses it.
- **Done when.** `click(element_id:"el_12")` clicks the centre of that element and refuses
  `el_N` that is off-screen with a message that says so.
- **Shipped as** ([note](B-4-click-by-element.md)): as planned; the resolver lives in
  `InputTools` and takes the parameter names in play so `drag`'s refusals name its own
  parameters. Optional response fields are emitted as `null` rather than omitted.

#### B-1 — `type`: target, clear, caret, press_enter, long-text paste  `P1 · M · ~3 h`

- `type(text, x?, y?, element_id?, clear=false, caret="idle" /* start|idle|end */,
  press_enter=false, pace_ms=5)`: target via C1 (a physical click at the point; an id goes
  through D-2's `focus` first and clicks only if focus did not land); caret via Home/End
  (`ctrl+home`/`ctrl+end` for a multi-line `Document`); `clear` = `ctrl+a`, `backspace`; then
  `TypePlanner` (C8) → keys or paste; `press_enter` = `enter` last. `IInputService.TypeAsync`
  gains a `TypeOptions` overload; the old one stays. Returns `{typed, method, target,
  clipboardRestored?}`. `interact_element(type)` gains `clear` through the same options.
- **RED seed.** `TypePlanner` table (length threshold, control characters force keys, `\n`/`\t`
  mapping, pace); clipboard set → `ctrl+v` → restore in that order (mocked `IClipboardService`
  and a recording `IInputService`); `clipboardRestored:false` when the prior clipboard was not
  text; `clear` sends exactly `ctrl+a`,`backspace` before the text; `caret:"end"` sends End
  before typing; `press_enter` last; target exclusivity (C1); `UIAutomation` on Notepad: `type(…,
  clear:true)` replaces the content and `get_text` reads it back; 5 000 characters arrive intact
  via paste and the clipboard holds what it held before.
- **Done when.** `type("hello", element_id, clear:true, press_enter:true)` replaces a field's
  content and submits; 5 000 characters arrive intact.
- **Shipped as** ([note](B-1-type.md)): an id is a physical click at the centre like every
  verb (no `focus`-first step); the caret moves are chords (`ctrl+home`/`ctrl+end`) because
  `PressKeyAsync` resolves one token; the response is flattened to `{typed, method,
  clipboardRestored?, x?, y?, elementId?, name?}`; **`interact_element(type)` did not gain
  `clear`** — it inherits the newline → Enter split and the paste path through the
  single-argument `TypeAsync`, and `clear`/`caret`/`press_enter` on it stay open (D-2's
  follow-up line in the checklist). The simulator sink types one character per call with the
  pace between them, which the desktop forced (see the note).

#### B-3 — `scroll` at current cursor or at an element  `P2 · S · ~1 h`

- `scroll(direction, amount=3, x?, y?, element_id?, shift_wheel=false)`: no target → the live
  cursor (C2); `shift_wheel` = hold Shift and use the vertical wheel for `left`/`right` (apps
  without a horizontal wheel). `x`/`y` move from required to optional — a schema change, called
  out in CHANGELOG. Response carries the point used.
- **RED seed.** No coordinates → `GetCursorPositionAsync` then `ScrollAsync` at that point;
  `element_id` → centre; `shift_wheel` maps `left`/`right` to Shift + vertical wheel and is
  refused for `up`/`down`; direction table unchanged; `UIAutomation`: Notepad with a long file
  scrolls under the cursor and the snapshot's scroll percent changes.
- **Done when.** `scroll(direction:"down")` with no coordinates scrolls under the cursor.
- **Shipped as** ([note](B-3-scroll.md)): as planned; the checklist's `use_shift_wheel` is
  `shift_wheel`, refused for a vertical direction at the tool before the cursor is read.

#### B-2 — `drag`: duration, intermediate motion, from current cursor  `P2 · S · ~2 h`

- `drag(to_x?, to_y?, element_id?, from_x?, from_y?, from_element_id?, button, duration_ms=300,
  steps=20)`: `from` defaults to the live cursor (C2); pure `DragPath.Points(from, to, steps)`
  with a first nudge of `SM_CXDRAG+1` pixels so the target recognises a drag; press, `steps`
  interpolated `MoveCursor`s spaced `duration/steps`, release; `duration_ms` ≤ 10 000, `steps` in
  2–200. Middle-button rejection stays. `from_x`/`to_x` positional names stay for old callers.
- **RED seed.** `DragPath` table (endpoints exact, monotone, the nudge first, `steps` points);
  bounds on duration/steps; from-cursor default; the sequence down → moves → up recorded on a
  fake simulator; `UIAutomation`: a text drag-select in Notepad selects the range (read back with
  `ctrl+c` + clipboard).
- **Done when.** Dragging a file between two Explorer windows works; a Notepad text drag-select
  works.
- **Shipped as** ([note](B-2-drag.md)): the four coordinates stay the first four parameters
  (`from_x, from_y, to_x, to_y`), then `element_id`, `from_element_id`; the response names the
  destination element only. The Explorer file drag is not in the tests (no fixture for it);
  the Notepad text drag-select is.

### Phase 3 — apps and windows

#### B-8 — Launch by Start Menu name with fuzzy match and window wait  `P1 · M · ~5 h` (spike first)

- `IAppCatalogService` (C7): `ListAsync()` → `AppEntry(Name, Kind /* shortcut|packaged|path */,
  Target /* .lnk target or AUMID */, Source)`, cached 5 min; `ResolveAsync(name)` → best
  `FuzzyMatch` (≥ 70, exact and prefix first) or a `KeyNotFoundException` listing the five
  nearest. `launch(app_name, wait_for_window=true, timeout_ms=10000)`: a path or `.exe` that
  exists → `ShellExecute` as today; otherwise the catalog; packaged →
  `IApplicationActivationManager.ActivateApplication` for the PID; then poll A-1's `ListAsync`
  for a window whose `Pid` matches, else a fuzzy title match on the app name (C5), up to the
  timeout. Returns `{matchedName, kind, score, pid, hwnd?, title?, windowDetected}` — "launched"
  vs "sent, window not detected" is the boolean, not a string.
- **RED seed.** Catalog matcher table (`"calc"` → Calculator, `"edge"` → Microsoft Edge,
  `"visual studio code"` → Visual Studio Code, `"vs code"` too; a nonsense name lists nearest);
  cache TTL and refresh-on-miss (fake clock); the path short-circuit; the window wait by PID then
  by title (mocked inventory that appears on the third poll); timeout → `windowDetected:false`
  with the PID; `Integration`: the real catalog contains Notepad and Calculator on this session;
  `UIAutomation`: `launch("notepad")` returns an `hwnd` that `window list` shows, then it is
  closed.
- **Done when.** `launch("calc")`, `launch("edge")`, `launch("visual studio code")` all open and
  return the window handle.
- **Shipped as** ([note](B-8-launch-catalog.md)): as planned, except that `ShortcutResolver`
  is not involved — a shortcut's name is the `.lnk` file name and its launch target is the
  `.lnk` path, which `ShellExecute` opens directly, so no shortcut is ever resolved to its
  executable. Only an explicit `.exe` name short-circuits to `PATH`; a bare word goes to the
  catalog even when a same-named executable exists (`"calc"` was short-circuited in the first
  cut and the unit tests caught it). `edge` and `visual studio code` are proven to resolve, not
  opened; the desktop launches Calculator and Notepad.

#### B-9 — Window resize / move  `P2 · S · ~2 h`

- `window(action: move|resize|set_bounds, title?/hwnd?, x?, y?, width?, height?,
  restore_first=false)` through `WindowMatcher` (C5), default target the foreground window;
  `SetWindowPos` with `SWP_NOZORDER|SWP_NOACTIVATE` (`move` keeps size, `resize` keeps position,
  `set_bounds` sets both); a minimised or maximised target is refused naming its state unless
  `restore_first`, which does `SW_RESTORE` first. Returns the new `Bounds` re-read from the
  window (C11's rule: the outcome).
- **RED seed.** Argument table per action (missing width for `resize` etc.); state refusal and
  `restore_first`; `SetWindowPos` flags (mocked native seam or a `Unit` on the pure argument
  builder); `UIAutomation` on Notepad: `set_bounds` then `window list` shows the new rect; a
  maximised Notepad is refused, then accepted with `restore_first`.
- **Done when.** `window(action:"set_bounds", title:"notepad", x:100, y:100, width:800,
  height:600)` and the inventory reports exactly that rect.
- **Shipped as** ([note](B-9-window-bounds.md)): as planned; a half-given pair (`x` without
  `y`) is a legal move with the other half taken from the current rect, and the no-target case
  reports `MatchStrategy: "foreground"`.

### Phase 4 — composites

#### B-6 — `wait_for` conditions and window filter  `P2 · M · ~3 h`

- `wait_for(text?, timeout_ms, interval_ms, kind, scope, window, include_offscreen,
  condition="element_exists" /* text_exists|active_window|element_exists|element_enabled|
  focused_element, aliases text|window|element|enabled|focused */, use_dom=false)`. Pure
  `WaitConditions.Evaluate(condition, text, SnapshotResult|WindowInfo[])` → `(bool, string
  detail)`: `active_window` runs against A-1's list with `WindowMatcher` (C5) and needs no walk;
  the element conditions run against `find_element`'s path (D-5's guarded walk, today's scope
  filter); `text_exists` runs against a `snapshot` of the scope (`use_dom` → `Pages` text for
  browser windows). Result per C4; `timeout_ms` ≤ 120 000, `interval_ms` ≤ 5 000.
- **RED seed.** Condition parsing and aliases; evaluator table on hand-built results (enabled
  vs disabled, focused vs not, text in a page vs in chrome); `active_window` never walks (the
  UIA mock is not called); attempts and elapsed reported; timeout → `satisfied:false` with the
  last detail, no throw; the old call shape (`text` only) behaves as `element_exists`;
  `UIAutomation`: `launch("notepad")` then `wait_for(condition:"active_window", text:"notepad")`
  resolves; `wait_for(condition:"text_exists", text:"Probe heading", use_dom:true)` on the Edge
  fixture resolves.
- **Done when.** `wait_for(condition:"active_window", text:"Notepad")` resolves after `launch`.

#### B-7 — `multi_select` / `multi_edit` batch tools  `P2 · S–M · ~2 h`

- `multi_select(targets_json, ctrl=true)` — targets are `{x,y}` or `{element_id}` objects (a
  JSON string or an already-parsed array); holds Ctrl (or not) and clicks each through C1's
  resolver. `multi_edit(entries_json)` — entries `{x,y}|{element_id}` + `text` and the B-1
  options per entry (`clear`, `press_enter`); each entry runs B-1's whole path. Both return
  per-entry results and **stop at the first failure**, reporting its index and the results so
  far; the Ctrl key is always released (`finally`).
- **RED seed.** Target/entry parsing table (string, parsed array, mixed shapes, an entry with
  both forms rejected naming the index); the click sequence with Ctrl down first and up last
  even when entry 2 throws; per-entry results and the failure index; `multi_edit` calls the
  B-1 path with the entry's options (recording `IInputService`); `UIAutomation`: `multi_edit` on
  two Notepad windows' editors (scope by `element_id` from one `snapshot`) fills both.
- **Done when.** `multi_edit([{element_id:"el_3", text:"a"}, {element_id:"el_9", text:"b"}])`
  fills both fields in one call.

## 5. Effort and sequencing summary

| Phase | Items | Days | Version | Unlocks |
|---|---|---|---|---|
| 1 | B-5, B-10, B-12, B-11 | ½ | 0.8.x | B-8, B-9, B-6 (matcher); the playbook's `wait` |
| 2 | B-4, B-1, B-3, B-2 | 1 | 0.9.0 | B-7; `interact_element(type, clear)` |
| 3 | B-8, B-9 | 1 | 0.9.x | — |
| 4 | B-6, B-7 | ½ | 0.10.0 | C-5's `wait_for(use_dom)` consumer is done |
| | **Total** | **~3 days** | | |

Estimates are **wall clock for this workflow**, not implementer effort: the A roadmap's "days"
were written for a human working one item at a time, and section A's phase 5 (estimated 8½ of
those days) shipped in one session. What costs time here is the two `test-agent` passes per item
(~20 min each), the desktop test runs (~2–3 min each) and one `docs-agent` pass per phase —
about 8 hours of agent time across the twelve items — plus B-8's spike. Phases 1 and 2 in
parallel branches save ~½ day.

## 6. Risks and how the plan absorbs them

- **`SetForegroundWindow` policy.** The `AttachThreadInput` trick is refused for an elevated
  target and the ALT nudge is the documented last resort; both are reported by name (C11), so a
  `false` is diagnosable. If neither works on a given box, `switch_to_window` is no worse than
  today and says why.
- **Packaged-app enumeration cost.** `FindPackagesForUser` over a few hundred packages with
  `GetAppListEntriesAsync` per package can take a second or two cold; the 5-minute cache and a
  warm-up on the first `launch` absorb it. If the WinRT route proves unreliable for some
  packages, the `shell:AppsFolder` `IShellFolder` enumeration is the fallback (COM, vtable
  rule) — not PowerShell.
- **Clipboard contention.** B-1's paste can collide with another app holding the clipboard
  (`ClipboardServiceTests` is already environment-flaky for this reason). The planner falls back
  to keys when the clipboard cannot be set and says so in `method`.
- **Schema changes on `scroll`/`drag`/`click`** (coordinates optional, `element_id` added) are
  additive for existing callers but change the advertised JSON schema; `HttpTransportTests`'s
  schema tests pin every one, and CHANGELOG Changed carries them.
- **`wait_for`'s `"null"` → result** (C4) is the one contract break; the skill playbook's
  `wait_for` line is updated in the same PR and the old shape is not kept.
- **Injected input in tests** lands on whatever has focus (phase-4 lesson): every `UIAutomation`
  test in B targets the Notepad fixture by id or title, never "the focused window", and the
  pointer-moving ones join the `DesktopCollection`.

## 7. Decisions taken before phase 1 (2026-09-05)

Three questions were put to the owner and decided as recommended; they are settled, not open:

1. **C3 — three new tools** (`wait`, `multi_select`, `multi_edit`; 65 → 68), not JSON-list
   parameters on `click`/`type`.
2. **C4 — `wait_for` always returns a structured result**; `{satisfied:false, …}` on timeout
   replaces today's `"null"`. The one contract break in the section.
3. **C7 — the app catalog is built in-process** (WinRT `PackageManager` + Start Menu `.lnk`
   through `ShortcutResolver`, packaged apps launched through the activation manager for the
   PID), not through `Get-StartApps` in PowerShell.

Everything else in section 2 is a recommendation the individual design notes can overturn with
a stated reason.
