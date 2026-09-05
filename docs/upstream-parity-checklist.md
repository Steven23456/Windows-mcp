# Upstream Parity Checklist — CursorTouch/Windows-MCP → Windows-mcp (.NET)

**Baseline:** 2026-09-04
**Upstream:** [CursorTouch/Windows-MCP](https://github.com/CursorTouch/Windows-MCP) `main` = **v0.8.5**
(released 2026-08-01; Python ≥ 3.14, FastMCP 3, 20 tools).
**Ours:** `main` @ `8cb40b6` + the phase-2/3 branches, 65 tools, plugin `0.7.3`, `CHANGELOG.md
[Unreleased]` carries the section-A phase-1 work (A-7, A-8, A-9, A-11, A-13), phase 2's A-1 and
phase 3's A-2/A-3/A-4. SDK `ModelContextProtocol` 2.2.0.
**Status:** Living document — check items off as they ship.

This is the working list of everything upstream can do that this server cannot (plus nine
defects: D-1…D-4 from the original comparison, D-5…D-9 added later under rule 4 — **all nine are
now fixed**; section A's phase 1 — A-7, A-9, A-8, A-11 and A-13 — is done, phase 2 shipped A-1,
phase 3 shipped A-2 with A-3 and A-4 inside it, and the rest of section A is the next work).
Each item carries enough context to write a design note and an implementation plan without re-reading
upstream from scratch: what upstream does and where, what we do today and where, an
implementation sketch, files to touch, tests, and a "done when" bar.

---

## How to use this file

1. Pick an item. Change its `- [ ] Not started` line to `- [ ] In progress — <link>` and link
   the design note or PR. Design notes go under `docs/design/`, named `<ID>-<slug>.md`
   (e.g. `docs/design/A-2-desktop-snapshot.md`).
2. When it ships: tick the box, add `— shipped in <version> (<PR>)`.
3. Every shipped item must also update: `CHANGELOG.md [Unreleased]`, the tool counts in
   `README.md` ("Tool reference") and `docs/architecture/*`, and `skills/windows/SKILL.md` if
   the playbook should steer Claude toward the new capability.
4. Do not silently expand an item's scope. If a plan discovers a neighbour gap, add a new item.

**Repo conventions that apply to every item** (see `CLAUDE.md`):
`TreatWarningsAsErrors=true`; DTOs are `record`s in `WindowsMcp.Abstractions/Models`; services are
`sealed` behind an `IXxxService`; tools stay thin (`async Task<string>` with JSON out, or
`Task<CallToolResult>` when the result carries an image — `screenshot`); new services
are registered as singletons in `Hosting/WindowsMcpHost.AddWindowsMcp` (tools auto-discover);
destructive actions require `confirm: true`; tests that need the interactive desktop carry
`[Trait("Category","UIAutomation")]`; the redeploy path is `scripts/build-release.ps1` → `bundle/WindowsMcp.exe` (gitignored; binaries are never committed).

**Reading the upstream reference code.** Paths below are relative to `src/windows_mcp/` in the
upstream repo. Fetch a copy into the scratchpad (never into this repo):

```bash
mkdir -p "$SCRATCH/upstream" && curl -sL https://github.com/CursorTouch/Windows-MCP/archive/refs/heads/main.tar.gz | tar xz -C "$SCRATCH/upstream"
```

**Legend.** Priority: **P1** core computer-use parity (agents fail or fall back to raw PowerShell
without it) · **P2** ergonomics / fewer round-trips · **P3** nice-to-have. Effort: **S** < 1 day ·
**M** 1–3 days · **L** multi-day / new subsystem. Line numbers are as of the baseline and will drift;
function names are the stable anchor.

---

## Board

| ID | Item | Pri | Effort | Depends on | Status |
|---|---|---|---|---|---|
| D-1 | `shortcut`/`key` reject letters, digits, bare keys | P1 | S | — | ☑ |
| D-2 | `interact_element` missing click / focus / type | P1 | S | D-3 | ☑ |
| D-3 | Cursor placement wrong on secondary monitors | P1 | S | — | ☑ |
| D-4 | `assert_element` advertises `value` / `focused` but implements neither | P2 | S | — | ☑ |
| D-5 | `find_element(kind=any)` / `wait_for` fail on the first stale element; whole-desktop walk | P1 | S | — | ☑ |
| D-6 | `find_element(kind=interactive)` excludes Edit, ComboBox, ListItem, TabItem, … | P2 | S | — | ☑ |
| D-7 | `find_element` / `wait_for` return off-screen elements by default | P2 | S | D-5 | ☑ |
| D-8 | `powershell` ships the CLIXML progress stream to the model on every call | P2 | S | — | ☑ |
| D-9 | `job output` still returns raw CLIXML on stderr | P3 | S | D-8 | ☑ |
| A-1 | Whole-desktop window inventory | P1 | M | — | ☑ |
| A-2 | Desktop-wide labeled interactive-element snapshot | P1 | L | A-1 | ☑ |
| A-3 | Scrollable regions with scroll percentages | P2 | S | A-2 | ☑ |
| A-4 | Element budget, truncation note, UIA caching | P1 | M | A-2 | ☑ |
| A-5 | Browser DOM mode (Chromium; Firefox IA2) | P2 | L | A-2 | ☐ |
| A-6 | Annotated screenshot (boxes, labels, grid, cursor) | P2 | M | A-2, A-7 | ☑ |
| A-7 | Return screenshot as MCP image content | P1 | S | — | ☑ |
| A-8 | Multi-display / virtual-desktop-coordinate capture | P1 | M | — | ☑ |
| A-9 | Auto-downscale + scale env + coordinate-scale report | P1 | S | A-7 | ☑ |
| A-10 | Alternative capture backend (WGC / DXGI) | P3 | M–L | — | ☐ |
| A-11 | Cursor position in responses + drawn on capture | P2 | S | — | ☑ |
| A-12 | Virtual desktops (report; optional manage) | P3 | L | A-1 | ☐ |
| A-13 | Unicode hygiene (PUA strip, surrogate repair) | P2 | S | — | ☑ |
| A-14 | Post-capture flash overlay + snapshot profiling | P3 | M | — | ☐ |
| B-1 | `type`: target, clear, caret, press_enter, paste path | P1 | M | D-2 | ☐ |
| B-2 | `drag`: duration / intermediate motion / from-cursor | P2 | S | D-3 | ☐ |
| B-3 | `scroll` at current cursor or element | P2 | S | — | ☐ |
| B-4 | `click` by element id; `clicks=0` hover | P2 | S | D-2 | ☐ |
| B-5 | Plain `wait` tool | P1 | S | — | ☐ |
| B-6 | `wait_for` conditions + window filter | P2 | M | A-1, A-2 | ☐ |
| B-7 | `multi_select` / `multi_edit` batch tools | P2 | S–M | B-1 | ☐ |
| B-8 | Launch by Start Menu name (fuzzy) + wait for window | P1 | M | A-1 | ☐ |
| B-9 | Window resize / move | P2 | S | — | ☐ |
| B-10 | Fuzzy window match + robust bring-to-foreground | P1 | M | A-1 | ☐ |
| B-11 | `start_process` with argv list + cwd | P2 | S | — | ☐ |
| B-12 | `multi_monitor`: work area, orientation, DPI, scale | P2 | S | — | ☐ |
| C-1 | File tools: offset/limit, append, overwrite, recursive, pattern | P2 | M | — | ☐ |
| C-2 | Registry delete + subkey listing on the tool surface | P2 | S | — | ☐ |
| C-3 | Process list CPU %, sort, limit; graceful kill | P2 | M | — | ☐ |
| C-4 | Notification `app_id` (AUMID) | P3 | S | — | ☐ |
| C-5 | `scrape`: DOM source, query, MCP sampling summary | P2 | M | A-5 (DOM part) | ☐ |
| C-6 | `powershell`: per-call timeout; env rebuild from registry | P2 | S–M | — | ☐ |
| C-7 | Tool annotations on all 65 tools | P2 | S | — | ☐ |
| S-1 | Tool allow/deny lists (`--tools`, `--exclude-tools`) | P2 | S | — | ☐ |
| S-2 | IP allowlist (CIDR v4/v6) | P2 | S | S-8 | ☐ |
| S-3 | CORS origins | P3 | S | — | ☐ |
| S-4 | OAuth 2.0 + PKCE (or external IdP bearer) | P3 | L | — | ☐ |
| S-5 | PEM cert/key files + self-signed generator | P2 | S–M | — | ☐ |
| S-6 | Config file + `auth` helper | P2 | M | — | ☐ |
| S-7 | `install` / `uninstall` at-logon task | P2 | M | S-6 | ☐ |
| S-8 | Unauthenticated `/health` | P2 | S | — | ☐ |
| S-9 | Claude Desktop Extension (`.mcpb`) + registry `server.json` | P3 | M | — | ☐ |
| S-10 | Per-tool black-box tester skill | P3 | S | — | ☐ |

**Suggested order.** **All defects (D-1 … D-9) are done** — the D section is closed. Next the
screenshot cluster (A-7, A-9, A-8, A-11) because every agent loop starts with a screenshot.
Then A-1 → A-2 → A-4, which unlock B-6, B-8, B-10, A-3, A-6. Quick wins B-5, B-1, B-2, B-3,
C-2, C-7, S-8, S-1 can be interleaved anywhere. A-5, A-12, S-4 last.
The section-A sequencing, cross-item decisions (coordinate space, defaults, tool count, element
ids, env vars) and per-item test seeds are in [`docs/design/A-roadmap.md`](design/A-roadmap.md).

---

## D — Defects exposed by the comparison

### D-1 — `shortcut` and `key` reject letters, digits, and bare keys  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-1-shortcut-parser.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** `Shortcut` splits on `+`; single-character parts are sent literally and
multi-character parts go through `_KEY_ALIASES` (`backspace`, `capslock`, `scrolllock`,
`windows`/`command`→Win, `option`→Alt) then `uia.SendKeys` (`desktop/service.py` `shortcut()`).
`"win"` alone opens Start; `"ctrl+c"`, `"win+r"`, `"ctrl+shift+esc"` all work.

**Ours today.** `Services/InputService.cs:17` `KeyMap` holds 19 named keys plus `F1`–`F12`
(static ctor). `PressShortcutAsync` (`:126`) throws `Unknown key in shortcut` for any part not in
the map and rejects fewer than two parts. So `ctrl+c`, `ctrl+v`, `win+r`, `ctrl+1` and bare `win`
all fail. `PressKeyAsync` (`:117`) has the same map.

**Implementation sketch.**
- Extract a pure `ShortcutParser` (static, `internal`) returning `VirtualKeyCode[]` so it is unit-
  testable without injecting input.
- Resolution order per part: named map → single char: `A`–`Z` → `VirtualKeyCode.VK_A..`,
  `0`–`9` → `VK_0..`; other printable chars via `PInvoke.VkKeyScan(char)` (low byte = VK, high
  byte = shift state; add SHIFT to modifiers when set).
- Add aliases: `windows`, `super`, `cmd`, `meta` → LWIN; `return`; `del`; `ins`/`insert`;
  `prtsc`/`printscreen`; `capslock`; `numlock`; `scrolllock`; `apps`/`menu`; `pause`; numpad keys.
- Allow a single-part shortcut (`win`, `esc`) — send `KeyPress`.

**Touches.** `Services/InputService.cs`, new `Services/ShortcutParser.cs`,
`tests/.../Services/InputServiceTests.cs`, `Tools/InputTools.cs` descriptions.

**Tests.** Parser tests (no input injection): each example above resolves; unknown token still
throws with the offending token named; case-insensitive.

**Done when.** `shortcut("ctrl+c")`, `("ctrl+shift+s")`, `("win+r")`, `("alt+f4")`, `("win")`,
`("ctrl+1")` all succeed; `key("a")` works; error text for a bad token names the token.

### D-2 — `interact_element` advertises click / focus / type but only implements toggle / select / invoke  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-2-interact-element-actions.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** Not a single tool upstream — but label-targeted `Click`/`Type` are the
primary way agents act on elements there, so this is the closest equivalent and must work.

**Ours today.** `Tools/UIAutomationTools.cs` `InteractElement` description lists
`click, toggle, select, focus, type`. `Services/UIAutomationService.cs:237` `InteractAsync`
handles only `toggle`, `select`, `invoke`; anything else throws `Unknown interact action`.
`FocusAsync` exists (`:312`) but is unwired. `select` requires a `value` it never uses.

**Implementation sketch.**
- `click`: Invoke pattern → else SelectionItem.Select → else Toggle → else physical left click at
  `Bounds` centre via `IInputService.ClickAsync` (inject `IInputService`, or move the fallback to
  the tool). Keep `invoke` as an alias.
- `focus`: `el.Focus()` (existing `FocusAsync`).
- `type`: focus, then `ValuePattern.SetValue(value)` when supported and not read-only; else
  keyboard `TextEntry`. Optional `clear:true` (select-all + delete) — align semantics with B-1.
- `select`: drop the spurious `value` requirement, or use `value` to pick a child item by name in
  a combo/list (ExpandCollapse → find child by name → SelectionItem.Select).
- Return what happened (`{action, pattern:"Invoke"|"physical-click"|...}`) instead of `"interacted"`.

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs`,
`tests/.../Tools/UIAutomationToolsTests.cs` (mock), `tests/.../Services/UIAutomationServiceTests.cs`
(`UIAutomation` category, Notepad fixture).

**Done when.** Every action named in the tool description works or returns a specific
"pattern X not supported on <controlType>" message; the description and implementation agree.

### D-3 — Cursor placement is wrong on secondary monitors  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-3-cursor-virtual-desktop.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** All coordinates are virtual-desktop pixels; `uia.Click(x,y)`/`MoveTo`
call `SetCursorPos` directly, so negative and beyond-primary coordinates land correctly.

**Ours today.** `Services/InputService.cs:49` `MoveCursorToVirtualDesktop` scales `x,y` by
`65535 / SM_CXSCREEN` (primary monitor size) but passes the result to
`MoveMouseToPositionOnVirtualDesktop`, which expects normalisation against the **virtual screen**
(`SM_XVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, …). Any coordinate on a second monitor lands elsewhere.

**Implementation sketch.**
- Simplest: `PInvoke.SetCursorPos(x, y)` then send button events without `MOUSEEVENTF_ABSOLUTE`
  (H.InputSimulator `LeftButtonClick()` after positioning does this).
- Or keep absolute moves and normalise correctly:
  `nx = (x - vLeft) * 65535 / (vWidth - 1)`, same for y, using the four virtual-screen metrics.
- The process already opts into **Per-Monitor-V2 DPI awareness** at startup (`Program.cs`
  calls `SetProcessDpiAwarenessContext`, falling back to `SetProcessDpiAwareness`); keep it so
  UIA bounds and screenshot pixels share one coordinate space, and document that space
  ("physical pixels, virtual desktop, origin = primary monitor top-left") in every
  coordinate-taking tool description.

**Touches.** `Services/InputService.cs` (extract the maths into a pure function), app manifest,
`tests/.../Services/InputServiceTests.cs`.

**Done when.** A unit test proves the normalisation for a monitor left of / above primary; a
manual check clicks a target on a secondary monitor via `multi_monitor` bounds.

### D-4 — `assert_element` advertises `value` and `focused` but implements neither  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-4-assert-element-states.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found while planning D-2** (`docs/design/D-2-interact-element-actions.md`). `Tools/UIAutomationTools.cs`
`AssertElement` lists `exists, enabled, checked, value, visible, focused`;
`Services/UIAutomationService.cs:220` `AssertElementAsync` handles `exists`, `enabled`, `checked`,
`visible` and throws `Unknown assertion state` for `value` and `focused`.
`docs/architecture/COMPONENTS.md` also claims both work.

**Sketch.** `focused` → `el.Properties.HasKeyboardFocus`. `value` needs something to compare
against: add an optional `expected` parameter (`value` passes when `ValuePattern.Value == expected`),
or drop `value` from the description. Either way put the observed state in the `FAIL:` text.

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs`,
`tests/.../Tools/UIAutomationToolsTests.cs`, `docs/architecture/COMPONENTS.md`.

**Done when.** Every state named in the description is implemented; `FAIL:` names the observed state.

---

### D-5 — `find_element(kind=any)` and `wait_for` fail on the first stale element  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/D-5-find-path-resilience.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found 2026-09-04 in use**, not by the upstream comparison — logged under rule 4. `kind=any`
errored twice on a busy desktop while `text` and `interactive` worked; reproduced the same day on
a second machine: `find_element("zzqxv", kind="any")` → "An error occurred invoking
'find_element'", `kind="text"` → `{"Matches":[]}`.

**Ours today.** `Services/UIAutomationService.cs` `FindElementAsync` calls
`_automation.GetDesktop().FindAllDescendants()` — every element of every process on the desktop,
materialised by one cross-process `FindAll`, with no `CacheRequest` and no element cap
(`Take(20)` runs after the walk) — then filters in LINQ with **unguarded** reads: `el.Name` in the
text predicate, `el.ControlType` in `MatchesKind` (`text` / `interactive`),
`el.Patterns.Scroll.IsSupported` (`scrollable`); `ToInfo` guards everything except
`el.BoundingRectangle`. With `kind=any`, `MatchesKind` short-circuits to `true`, so `el.Name` is
read on every element until 20 match, and one element that vanished between the walk and the read
(a tooltip, a closing menu, a virtualised list row) raises `UIA_E_ELEMENTNOTAVAILABLE` and the
whole call fails. `text` / `interactive` narrow on `ControlType` first and usually survive, but
that read is just as unguarded. `WaitForAsync` hard-codes `FindKind.Any` and does not catch
between polls, so the first transient failure ends the wait instead of being retried — the one
thing a wait is for. The whole-desktop walk also holds the single STA worker for its duration
(every other UIA call queues behind it), and `wait_for` repeats it every `interval_ms`.

**Upstream behaviour.** `tree/service.py` walks per window (active window first) on its own
thread with retry, reads properties through a `CacheRequest` (`tree/cache_utils.py`) and stops at
the `TreeElementBudget` (A-4). A dead element costs one node, never the call.

**Implementation sketch.**
- Guard every read on the find path with the existing `TryGetName` / `TryGetControlType` plus new
  `TryGetBounds` / `TryIsScrollable`; a failed read means "skip this element", never "fail the
  call". Reuse D-4's `IsElementGone` predicate (`ElementNotAvailableException`, or `COMException`
  with `UIA_E_ELEMENTNOTAVAILABLE` / the RPC failures) and its ProcessId liveness probe — a dead
  Win32 window's element answers reads with defaults (ControlType Pane, ProcessId 0) rather than
  throwing, so the walker must not rely on an exception alone.
- Scope the walk: `scope` = `foreground` (default: `GetForegroundRoot()`, the root `get_state`
  already uses and the window the agent is acting on) | `window` (a `window` title, matched
  exact-then-substring against the top-level windows' UIA names) | `desktop` on `find_element` and
  `wait_for`. `desktop` walks the desktop's top-level children one window at a time, each in its
  own try/catch, so a window closing mid-walk drops out instead of killing the search.
  **Scope added 2026-09-04 at the user's request** (rule 4: recorded, not silent): `window` makes a
  multi-step workflow deterministic when focus moves, and takes over B-6's `window_name` filter
  — see the design note.
- Push the kind filter into the UIA condition where it can be (`ConditionFactory.ByControlType`
  OR-ed for `text` / `interactive`, `TrueCondition` for `any` / `scrollable`) so the provider
  marshals fewer elements; `Name` still has to be read client-side (UIA has no "contains").
- `WaitForAsync`: forward `kind` and `scope` instead of hard-coding `Any`; catch per poll, keep
  the last exception, and if the deadline passes with nothing found report it in the timeout
  message. Give the loop an internal overload that takes the poll delegate so the retry is
  unit-testable without UIA.
- Leave the `CacheRequest` and the element budget to A-4, but shape the walker as one method per
  window root so A-4 wraps it without another rewrite.

**Touches.** `Services/UIAutomationService.cs`, `Abstractions/IUIAutomationService.cs`,
`Tools/UIAutomationTools.cs` (`scope` on `find_element` / `wait_for`; descriptions),
`docs/architecture/COMPONENTS.md`, `DATAFLOW.md` (the `wait_for` diagram),
`skills/windows/SKILL.md` §4.

**Tests.** Unit: the tool forwards `kind` and `scope`; the `WaitForAsync` loop keeps polling when
a poll throws and returns the first hit. `UIAutomation` category: `FindElementAsync("", Any)`
against the Notepad fixture returns without throwing, in both scopes, ten times in a row;
`WaitForAsync` for text that appears after 500 ms returns it.

**Done when.** `find_element(kind=any)` and `wait_for` return on a busy desktop; a stale element
is skipped, not fatal; the default scope is the foreground window and `scope=desktop` is opt-in;
the STA worker is not held for a whole-desktop walk unless asked.

---

### D-6 — `find_element(kind=interactive)` excludes Edit, ComboBox, ListItem, TabItem, RadioButton, Slider, TreeItem  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-6-interactive-control-types.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** `MatchesKind` (`Services/UIAutomationService.cs`):
`Interactive => Button | CheckBox | Hyperlink | MenuItem`. Edit, ComboBox, ListItem, TabItem,
RadioButton, SplitButton, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar and Document
are all excluded — so the Claude Code prompt box (an `Edit`) is not "interactive", nor is any
dropdown, list row, tab, or radio button. `kind=text` (`Text | Edit | Document`) is currently the
only way to find an input.

**Upstream behaviour.** `tree/config.py` `INTERACTIVE_CONTROL_TYPE_NAMES` = Button, ListItem,
MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink, SplitButton, TabItem, TreeItem,
DataItem, HeaderItem, TextBox, Spinner, Slider, ScrollBar, plus `INTERACTIVE_ROLES` via the
LegacyIAccessible role for controls that misreport their type. `DocumentControl` is its own class
(`DOCUMENT_CONTROL_TYPE_NAMES`, action `scroll`), listed alongside the interactive elements in the
snapshot. A-2 ports the whole classifier.

**Implementation sketch.** Replace the four-type test with a `static readonly HashSet<ControlType>`
of the upstream set (`Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink,
SplitButton, TabItem, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar`) **plus
`Document`** — for a flat `kind` filter a text area you type into is interactive; A-2 can split
it back out when there is a separate document list. State the set in the `kind` parameter
description. When A-2 lands, `find_element` calls the classifier instead of keeping its own list
(A-2's acceptance test).

**Touches.** `Services/UIAutomationService.cs`, `Tools/UIAutomationTools.cs` (description),
`docs/architecture/COMPONENTS.md`.

**Tests.** `UIAutomation` category: `FindElementAsync("", Interactive)` on the Notepad fixture
returns the editor (`Edit` / `Document`) and at least one `MenuItem`; a unit test pins the set
against the list above so a later edit is a visible diff.

**Done when.** `find_element("", kind=interactive)` on Notepad lists the editor and the menu
items; the set matches upstream's and is stated in the description.

---

### D-7 — `find_element` and `wait_for` return off-screen elements by default  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-7-offscreen-filter.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Ours today.** `FindElementAsync` has no `IsOffscreen` filter; `ToInfo` reports the flag and
leaves it to the caller. Observed 2026-09-04: 18 of 21 `kind=text` hits were `IsOffscreen: true`
(collapsed panes, virtualised list rows, minimised windows). Worse, `Take(20)` runs before any
filtering the caller could do, so off-screen hits **crowd out** on-screen ones — an on-screen match
can be absent from the 20 returned. `wait_for` inherits both and can "find" a not-yet-visible
element and return early. (Off-screen is not the same as negative coordinates: a monitor left of
or above the primary has negative bounds and is on-screen — D-3.)

**Upstream behaviour.** `tree/service.py` `tree_traversal`: `is_visible = area > 0 and not
is_offscreen` (narrow exceptions for `EditControl` and browser `ListItemControl`) — off-screen
nodes never reach the output. The A-2 sketch already says "off-screen dropped"; this item is the
same rule applied to the tools that exist today.

**Implementation sketch.** `include_offscreen` (`bool`, default `false`) on `find_element` and
`wait_for`; the filter is `!TryGetIsOffscreen(el)` and non-empty bounds, applied **before**
`Take(20)`. Decide in implementation whether to keep upstream's `Edit` exception (an edit control
scrolled out of view reports off-screen yet is still the right target for `type`); if adopted,
say so in the description. `IsOffscreen` stays in `ElementInfo` for `get_element`.

**Touches.** `Services/UIAutomationService.cs`, `Abstractions/IUIAutomationService.cs`,
`Tools/UIAutomationTools.cs`, `docs/architecture/COMPONENTS.md`, `skills/windows/SKILL.md` §4.

**Tests.** `UIAutomation` category: `find_element("", kind=text)` returns only
`IsOffscreen == false` results by default; with `include_offscreen=true` the count is ≥ the
default count. Unit: the tool forwards the flag.

**Done when.** Default results contain no `IsOffscreen: true` element; the 20-cap applies after
the filter; `include_offscreen=true` restores today's behaviour.

---

### D-8 — `powershell` ships the CLIXML progress stream to the model on every call  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/D-8-powershell-clixml-stderr.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Not an upstream-parity item** — logged here (rule 4) because it is the highest token-ROI fix on
the most-used tool. Related: C-6 touches the same service; do D-8 first or together.

**Ours today.** Windows PowerShell 5.1 with redirected stderr wraps its non-stdout streams in
CLIXML. Every cold start emits a `progress` record ("Preparing modules for first use.") and any
`Write-Progress` — including the ones inside `Invoke-WebRequest` and module autoload — adds
another. `PowerShellService.RunAsync` keeps the raw stream in `PSResult.Stderr`, and
`ShellTools.Powershell` serialises the whole `PSResult`, so each call carries ~0.6–3 KB of XML the
model reads and ignores. Measured 2026-09-04 through the service's exact process setup: a
one-liner with one `Write-Progress` → 596 characters of CLIXML on stderr; the same script with
`$ProgressPreference='SilentlyContinue'` in the preamble → 0. `Errors[]` is already clean: since
`6c96350` (2026-08-24) `ExtractErrors` decodes only `<S S="Error">` records (test
`RunAsync_progress_records_on_stderr_do_not_fail_the_command`), so the blob is carried once, in
`Stderr`, not twice — except when `XElement.Parse` fails, in which case the `RawLines()` fallback
puts the raw XML lines into `Errors[]` as well.

**Implementation sketch.**
- `PowerShellInvocation.BuildArgumentsAsync`: add `$ProgressPreference='SilentlyContinue'` to the
  preamble (script scope; a caller's script can set it back). Welcome side effect:
  `Invoke-WebRequest` / `Invoke-RestMethod` are markedly faster without the progress bar. Update
  the preamble comment and the tool description ("progress output is suppressed — there is no
  console to draw it on").
- `PowerShellService`: decode `Stderr` the way `Errors` already is — when the stream is CLIXML,
  replace it with the decoded text of the error / warning / verbose / debug / information records
  (one line each, stream-prefixed, e.g. `WARNING: careful`) and drop `progress` records;
  non-CLIXML stderr and unparseable CLIXML stay raw. `Errors[]` unchanged. This also covers a
  script that re-enables progress and the parse-failure fallback.
- Keep `PSResult`'s shape (`Success, Stdout, Stderr, ExitCode, Errors`) so `job` output and the
  existing tests are untouched.

**Touches.** `Services/PowerShellInvocation.cs`, `Services/PowerShellService.cs`,
`Tools/ShellTools.cs` (description), `docs/architecture/COMPONENTS.md` (PowerShellService
bullets), `tests/.../Services/PowerShellServiceTests.cs`.

**Tests.** Pure tests on a captured CLIXML sample for the decoder (progress dropped, warning kept
as text, error kept, raw stderr passthrough, concatenated `<Objs>` documents); the existing
`RunAsync_progress_records_on_stderr_do_not_fail_the_command` flips its
`Stderr.Should().Contain("progress")` precondition to assert the stream is now empty; one
real-process test that `Write-Warning 'careful'` yields `Stderr` containing `careful` and not
`<Objs`.

**Done when.** A `powershell` call that triggers module first-use returns `Stderr: ""`; a
`Write-Warning` arrives as text; `Errors[]` and `Success` semantics are unchanged; the response for
`'hi'` is under 200 bytes.

---

### D-9 — `job output` still returns raw CLIXML on stderr  `P3 · S`
- [x] Done 2026-09-04 — [design note](design/D-9-job-clixml-stderr.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Found while implementing [D-8](design/D-8-powershell-clixml-stderr.md)** — logged under rule 4
rather than widening D-8.

**Ours today.** D-8 gave `PowerShellService` a whole-stream `ClixmlStderr.Decode`, and put
`$ProgressPreference='SilentlyContinue'` in `PowerShellInvocation`'s preamble — which background
jobs share, so the bulk of the noise is gone for them too. But `JobService` pumps stderr
incrementally into a `BoundedTextBuffer` and serves a `Tail(n)` of it, so it has no whole document
to parse and a tail can cut a CLIXML record in half. A job whose script re-enables progress, or
that writes warnings/verbose, still returns raw `<Objs>` XML from `job output`.

**Sketch.** Either decode at job **completion** (the full stream is known then, and `job output` on
a finished job is the common case), or a streaming line filter that drops `<Obj S="progress">`
spans as they arrive. The first is simpler and covers the case that matters; the second also helps
`job output` on a still-running job.

**As landed:** both, via a third trick. Decode once in `MonitorAsync` (before the state flips to a
terminal value, so no reader sees a finished job with raw XML), rewriting the buffer through a new
`BoundedTextBuffer.ReplaceAll` so `Tail`/`Length`/`TrimmedChars` stay consistent at no per-read
cost; and decode a copy on read while a job is **running**, which works because `ClixmlStderr` now
retries on everything up to the last `</Objs>` and drops a trailing partial document. A stream with
no complete document still passes through raw.

**Touches.** `Services/JobService.cs`, `tests/.../Services/JobServiceTests.cs`,
`docs/architecture/COMPONENTS.md` (JobService bullets).

**Done when.** `job output` on a finished job that emitted warnings returns prefixed text, not
`<Objs`; a running job's tail is no worse than today.

---

## A — Desktop state and screenshots

### A-1 — Whole-desktop window inventory  `P1 · M`
- [x] Done 2026-09-05 — [design note](design/A-1-window-inventory.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** Every `Snapshot`/`Screenshot` response lists **Focused Window** and
**Opened Windows** as a table: name, depth (z-order), status (Maximized/Minimized/Normal/Hidden),
width, height, handle; each `Window` also carries `process_id` and `is_browser`.
Source: `desktop/service.py` `get_controls_handles()` (EnumWindows callback, ~924),
`get_windows()` (~1036), `get_active_window()` (~959), `is_overlay_window()` (~910),
`get_window_status()` (~314); `desktop/views.py` `Window`, `Status`, `Browser`.

**Ours (before A-1).** No enumeration anywhere. `IWindowService` had `ExecuteAsync`, `SwitchToAsync`,
`LaunchAsync`, `EnumerateMonitorsAsync` only. `window` acted on an exact title through
`PInvoke.FindWindow`. Now `window(action:"list"|"active")` (see the design note).

**Implementation sketch.**
- New DTO `WindowInfo(string Title, long Hwnd, int Pid, string ProcessName, WindowState State,
  Bounds Bounds, int ZOrder, bool IsActive, bool IsBrowser, int MonitorIndex)` in
  `Models/WindowDtos.cs`.
- `IWindowService.ListAsync(bool includeMinimized = true, bool includeHidden = false)`:
  `EnumWindows` in z-order → keep `IsWindowVisible`, non-empty title, skip
  `WS_EX_TOOLWINDOW`, skip DWM-cloaked (`DwmGetWindowAttribute(DWMWA_CLOAKED)` — filters
  UWP ghosts and other-virtual-desktop windows), skip zero-area, mark
  `IsIconic`/`IsZoomed`; `GetWindowThreadProcessId` → PID/name; `MonitorFromWindow` → index.
  Upstream's overlay filter (`is_overlay_window`) shows which chrome to drop (taskbar, Program
  Manager, input-method windows).
- Tool surface: extend `window` with `action: list | active` (no new tool count), and reuse the
  same list inside A-2's snapshot header.

**Touches.** `Abstractions/IWindowService.cs`, `Models/WindowDtos.cs`,
`Services/WindowService.cs`, `Tools/WindowTools.cs`, new `tests/.../Services/WindowServiceTests.cs`
(integration: returns without throwing; the test host's own console window is present when run
interactively), `tests/.../Tools/WindowToolsTests.cs`.

**Done when.** `window(action="list")` returns every user-visible top-level window with the fields
above in z-order, and `action="active"` returns the foreground one.

### A-2 — Desktop-wide labeled interactive-element snapshot  `P1 · L`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream behaviour.** `Snapshot` (`tools/snapshot.py`, helpers in `tools/_snapshot_helpers.py`)
walks **every** window (active first, then the others) and returns three text blocks rendered by
`tree/views.py`: the **UI Tree** (semantic hierarchy), plus interactive and scrollable lists in
the form

```
window "Untitled - Notepad"
├── (612,388) button "Save"  [action: click]  [shortcut:Ctrl+S]
├── (300,200) edit "Text Editor"  [action: fill]  [focused]  [value:"hello"]
└── (900,40) checkbox "Word wrap"  [action: toggle]  [toggle:On]
```

Centre coordinates are what `Click`/`Type`/`Scroll`/`Move` accept as `loc`, and the list index is
the `label` those tools also accept. Classification lives in `tree/config.py`
(`INTERACTIVE_CONTROL_TYPE_NAMES`, `INTERACTIVE_ROLES` via LegacyIAccessible role,
`INFORMATIVE_*`, `STRUCTURAL_*`, `DEFAULT_ACTIONS`); traversal rules in `tree/service.py`
`tree_traversal()` (~379) and `get_nodes()` (~835): visible, non-zero area, clipped to the window
rectangle via `iou_bounding_box()` (~240), off-screen dropped, per-window thread with retry.
Metadata collected per node: `has_focused`, `is_password`, `value`, `min`/`max` (RangeValue),
`toggle_state`, `expand_collapse_state`, `shortcut` (AccessKey/AcceleratorKey). Action hints
come from `_ACTION_MAP` (edit→fill, checkbox→toggle, combobox→select, slider→slide,
document→scroll, else click). Rendering: `_render_tree()`, `_node_meta_str()`,
`_render_semantic_node()`.

**Ours (before A-2).** `get_state` built a JSON `ElementTree` of the foreground window only,
three levels deep, with no classification, no action hints, no centres and no window list, and
`el_N` ids accumulated forever. Now `snapshot` (see the design note); `get_state` is kept,
budgeted.

**Implementation sketch.**
- New `IUIAutomationService.SnapshotAsync(SnapshotOptions)` → `SnapshotResult`:
  `Windows` (A-1), `ActiveWindow`, `Cursor` (A-11), `Interactive[]`, `Scrollable[]` (A-3),
  optional `SemanticTree`, `Truncated`/`ElementLimit` (A-4), `CaptureMs`.
- `InteractiveElement(string ElementId, string Window, string ControlType, string Name,
  int CenterX, int CenterY, Bounds Bounds, string Action, ElementMeta Meta)`.
- Options: `scope: foreground|desktop` (default desktop), `window_title` filter, `include_tree`,
  `max_elements`, `format: text|json` — the compact text format is roughly 5–10× cheaper in
  tokens than the JSON tree; make `text` the default and keep `json` for programmatic use.
- Port the control-type/role sets and the per-node metadata reads to FlaUI
  (`el.Patterns.RangeValue`, `.Toggle`, `.ExpandCollapse`, `.Value`, `HasKeyboardFocus`,
  `IsPassword`, `AccessKey`, `AcceleratorKey`, `LegacyIAccessible.Role`).
- Use a FlaUI `CacheRequest` with the property/pattern set for `TreeScope.Subtree` (A-4).
- Element cache: reset per snapshot (IDs = list index, like upstream labels) or keep `el_N` but
  evict on the next snapshot — decide in the spec; document that IDs are only valid until the next
  snapshot.
- Keep `get_state` as-is for compatibility (or make it `snapshot(scope=foreground, format=json)`).

**Touches.** `Abstractions/IUIAutomationService.cs`, `Models/UIAutomationDtos.cs`,
`Services/UIAutomationService.cs` (consider splitting a `Services/UiTree/` folder: classifier,
traverser, renderer), `Tools/UIAutomationTools.cs`, `skills/windows/SKILL.md`, README, docs.

**Tests.** Pure unit tests for the classifier, renderer and action map on fake node records;
`UIAutomation`-category integration against the Notepad fixture asserting the edit control is
listed with `action: fill` and a centre inside its bounds; a timing assertion under the element cap.
**Acceptance test carried over from D-6:** the classifier lists an `Edit` (Notepad's editor, the
Claude Code prompt box) and every type in upstream's `INTERACTIVE_CONTROL_TYPE_NAMES` as
interactive, and `find_element(kind=interactive)` calls this classifier instead of keeping its own list.

**Done when.** One call returns all visible windows' interactive elements with centre coordinates,
action hints and metadata, in the compact text form, bounded by the element cap, and the
coordinates work unchanged with `click`/`type`/`scroll`.

### A-3 — Scrollable regions with scroll percentages  `P2 · S`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `scrollable_elements_to_string()` lists each ScrollPattern element with
`[v:37%]`/`[h:0%]` from `vertical_scroll_percent`/`horizontal_scroll_percent` and the
`vertical_scrollable`/`horizontal_scrollable` flags (`tree/views.py` `_scroll_meta_str`).

**Ours (before A-3).** `find_element(kind=scrollable)` returned `ElementInfo` with no scroll data.
Now the snapshot's scrollable list carries `ScrollInfo`; `find_element` still does not populate
the new `ElementInfo.Scroll` (follow-up in the design note).

**Sketch.** `ScrollableElement` record with `VerticalPercent`, `HorizontalPercent`,
`VerticallyScrollable`, `HorizontallyScrollable` from `Patterns.Scroll`; include in A-2 output and
in `find_element(kind=scrollable)`; `scroll(element_id)` (B-3) uses its centre.

**Done when.** Scrollable list shows percentages and "Reached top/bottom" can be inferred.

### A-4 — Element budget, truncation note, UIA caching  `P1 · M`
- [x] Done 2026-09-05 — [design note](design/A-2-desktop-snapshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `tree/budget.py` `TreeElementBudget` — default 500 elements, env
`WINDOWS_MCP_MAX_TREE_ELEMENTS`; traversal stops early, `TreeState.truncated=true`, and every
rendered block appends `_truncation_note()` telling the agent to narrow the view or raise the
limit. `tree/cache_utils.py` builds a `CacheRequest` so property reads are one cross-process call
per subtree instead of one per property.

**Ours (before A-4).** `BuildTree` recursion was depth-limited (3) but not count-limited — a large
grid at depth ≤ 3 still exploded; no cache request (each `TryGetX` was a COM round-trip).
Now `ElementBudget` (`Services/UiTree/`) bounds both `snapshot` and `get_state`: the default comes
from `--max-tree-elements` / `WINDOWSMCP_MAX_TREE_ELEMENTS` (500) through the injected
`UiTreeOptions`, a per-call `max_elements` overrides it, and a stopped walk reports `Truncated`
with `ElementLimit` plus the one-sentence note in the text render. `UiTraverser` walks each window
under a single FlaUI `CacheRequest` (`TreeScope.Subtree`, pattern properties cached too).
`FindElementAsync` is unchanged — it keeps its own UIA-side conditions and 20-match cap, and its
cache request is a follow-up (see the design note).

**Sketch.** `max_elements` param (default 500) + `WINDOWSMCP_MAX_TREE_ELEMENTS` env; counter
threaded through traversal; `Truncated`, `ElementLimit` in the result + a note in text output;
apply to both `get_state` and A-2; FlaUI `CacheRequest` (`Add(Name, ControlType,
BoundingRectangle, IsEnabled, IsOffscreen, HasKeyboardFocus, …)`, `TreeScope = Subtree`,
`AutomationElementMode = Full`) activated around the walk.

**Done when.** A synthetic 5 000-row grid returns in bounded time with `truncated:true` and the
note; a profile shows fewer COM calls per element than before.

### A-5 — Browser DOM mode (`use_dom`)  `P2 · L`
- [ ] Not started

**Upstream.** For Chrome/Edge the traversal looks for the element whose `AutomationId ==
"RootWebArea"` and walks only that subtree, classifying by LegacyIAccessible role; browser chrome
is dropped; text nodes are collected as `dom_informative_nodes`; the scrollable document becomes
`dom_node` with scroll percent; `_dom_correction()` (~294) dedupes and fixes Chromium quirks.
Firefox exposes web content only via MSAA/IAccessible2, so `tree/ia2.py` fetches the root
`IAccessible` with `AccessibleObjectFromWindow` (oleacc) and walks it into the same shapes.
`Scrape(use_dom)` and `WaitFor(use_dom)` consume it; `Browser` enum = chrome.exe, msedge.exe,
firefox.exe.

**Ours.** Nothing browser-specific.

**Sketch.** Phase 1 (Chromium): `use_dom:true` on the A-2 snapshot — locate `RootWebArea`
under the active browser window (`FindFirstDescendant(cf.ByAutomationId("RootWebArea"))`),
traverse it with the same classifier, emit `DomText` block and the document's scroll percent.
Phase 2 (Firefox): declare `IAccessible` (available via the `Accessibility` interop assembly) and
walk `accChildCount`/`accChild`; IA2 (`IAccessible2`) needs hand-declared COM interfaces —
observe the **vtable-gap rule** in `CLAUDE.md`. Expose through `snapshot(use_dom)`,
`scrape(source=dom)` (C-5), `wait_for(use_dom)` (B-6).

**Done when.** With a page open in Edge, `snapshot(use_dom=true)` lists page links/inputs and
page text without address-bar/tab-strip elements; Firefox is a documented follow-up.

### A-6 — Annotated screenshot (bounding boxes, labels, grid, cursor)  `P2 · M`
- [x] Done 2026-09-05 — [design note](design/A-6-annotated-screenshot.md); in `CHANGELOG.md [Unreleased]`, ships with the next release

**Upstream.** `Snapshot(use_vision=true, use_annotation=true)` draws a coloured rectangle and
numbered label per interactive node, highlights the cursor, and optionally overlays a reference
grid (`width_reference_line`/`height_reference_line`) — `desktop/service.py`
`get_annotated_screenshot()` (~1217, `draw_annotation`, `draw_label`, `get_random_color`).

**Ours (before A-6).** Plain capture only. Now `screenshot(annotate:true, grid_columns, grid_rows)` — see the design note.

**Sketch.** SkiaSharp (already referenced) `SKCanvas` over the captured bitmap: box per element
(2 px, colour from a fixed palette by index), label chip with contrast text at the box's top-left
(clamped inside the image), cursor crosshair/ring (A-11), grid lines every `w/n`, `h/n` with
coordinate captions. Params on the snapshot/screenshot tool: `annotate`, `grid_columns`,
`grid_rows`. Must run after the element walk so labels match IDs.

**Done when.** The annotated image's label N sits on element N from the same call.

### A-7 — Return the screenshot as MCP image content  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/A-7-screenshot-image-content.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `build_snapshot_response()` returns `[text, Image(data=png, format="png")]` — an
MCP `ImageContent` block, so the model sees the picture directly.

**Ours (before A-7).** `Tools/ScreenTools.cs` `Screenshot` returned a JSON string: `output="file"` → path;
`output="base64"` → `{data_base64: …}` inside a **string**. Neither is an image content block, so
no client renders it and the model cannot look at it without a second tool.

**Sketch.** Return `CallToolResult` (or `IEnumerable<AIContent>` /
`Microsoft.Extensions.AI.DataContent`) — verify the accepted return types in SDK 2.2.0 — with a
`TextContentBlock` (metadata JSON: size, scale, region, cursor) plus an `ImageContentBlock`
(`MimeType = image/png|jpeg`). Keep `output=file` as an option; make inline the default.
Respect the ~1 MB tool-result limit some clients enforce (A-9).

**Touches.** `Tools/ScreenTools.cs`, `tests/.../Tools/ScreenToolsTests.cs` (assert content-block
types), `HttpTransportTests` smoke (image survives the HTTP transport).

**Done when.** Claude Code / Claude Desktop display the screenshot inline from a single call.

### A-8 — Multi-display capture and virtual-desktop coordinates  `P1 · M`
- [x] Done 2026-09-04 — [design note](design/A-8-multi-display-capture.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `display=[0]`/`[0,1]` (zero-based indices from `DisplayInventory`) captures the
union rect of the chosen monitors (`get_display_union_rect`, ~1177);
`region=[left,top,right,bottom]` in virtual-desktop pixels (`parse_region_selection`, ~1135)
takes precedence; out-of-bounds regions raise instead of clipping. Default = full desktop.
The response echoes `Visible Displays`, `Selected Displays`, `Screenshot Region`,
`Coordinate Space: Virtual desktop coordinates`.

**Ours (before A-8).** `Services/ScreenshotService.cs` defaulted to `SM_CXSCREEN × SM_CYSCREEN`
(primary only); `region` was `x,y,w,h` with no validation and an undocumented coordinate space;
`ocr` shared `ScreenTools.ParseRegion`.

**Sketch.** `display` param (`int[]` or `"all"`), reuse `IWindowService.EnumerateMonitorsAsync`
(order = index) to compute the union rect; `region` validated against the virtual screen
(`SM_XVIRTUALSCREEN…`) → `ArgumentException` when outside; response metadata lists displays,
selected displays, region, and states the coordinate space; same for `ocr`. Decide the default
(primary = cheaper tokens; upstream = all) and document it.

**Done when.** `screenshot(display=[1])` captures the second monitor; a region straddling two
monitors captures correctly; an out-of-bounds region errors.

### A-9 — Auto-downscale, scale env, coordinate-scale report  `P1 · S`
- [x] Done 2026-09-04 — [design note](design/A-9-screenshot-downscale.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** Images larger than 1920×1080 are downscaled (LANCZOS) and
`WINDOWS_MCP_SCREENSHOT_SCALE` (0.1–1.0) applies on top; the text block reports
`Screenshot Original Size`, `Screenshot Coordinate Scale: 2.0 — multiply every image pixel
coordinate by 2.0 before passing to Click…` (`_snapshot_helpers.py`, `desktop/service.py`
`max_image_size` branch).

**Ours (before A-9).** Full resolution always; a 4K capture was a ~10 MB PNG.

**Sketch.** `max_width`/`max_height` (default 1920/1080), `scale` param and
`WINDOWSMCP_SCREENSHOT_SCALE` env, `SKBitmap.Resize(..., SKFilterQuality.High)`; JPEG `quality`
param; response carries `originalWidth/Height`, `scale`, and the explicit multiply-by note.
Ties into A-7's metadata block.

**Done when.** A 3840×2160 capture returns ≤ 1920 wide with the correct scale factor reported.

### A-10 — Alternative capture backend (WGC / DXGI)  `P3 · M–L`
- [ ] Not started

**Upstream.** `desktop/screenshot.py` backend registry: `dxcam` (DXGI desktop duplication) →
`mss` → Pillow, selected by `WINDOWS_MCP_SCREENSHOT_BACKEND=auto|dxcam|mss|pillow`; the used
backend is echoed in the response.

**Ours.** GDI `Graphics.CopyFromScreen` only — returns black for DRM/exclusive-fullscreen surfaces
and is slower on high-refresh multi-monitor setups.

**Sketch.** `IScreenCaptureBackend` with `Gdi` and `WindowsGraphicsCapture`
(`Windows.Graphics.Capture` via CsWinRT, works with HDR/accelerated content) or DXGI Desktop
Duplication; `WINDOWSMCP_SCREENSHOT_BACKEND=auto|gdi|wgc`; fall back on failure and report which
backend produced the frame.

**Done when.** A GPU-accelerated window that captures black under GDI captures correctly under
the alternative backend.

### A-11 — Cursor position in responses and drawn on captures  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/A-11-cursor.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `Cursor Position: (x, y)` heads every snapshot/screenshot (`get_cursor_location`);
the cursor is highlighted in annotated images.

**Ours (before A-11).** Nothing reported the pointer: `InputService` called `GetCursorPos` only
to read back its own `SetCursorPos` (D-3), and no response or capture carried the cursor.

**Sketch.** `PInvoke.GetCursorPos` → `Cursor {X,Y, MonitorIndex}` in the A-2/A-7 metadata;
`GetCursorInfo` + `DrawIconEx` onto the captured bitmap when `include_cursor:true` (default
true), or a drawn ring when the real cursor cannot be composited.

**Done when.** Screenshot metadata reports the cursor and the image shows it.

### A-12 — Virtual desktops  `P3 · L`
- [ ] Not started

**Upstream.** Every snapshot shows **Active Desktop** and **All Desktops** (names). `vdm/core.py`
wraps the documented `IVirtualDesktopManager` (is-window-on-current, window's desktop GUID) and
the **undocumented, per-build** `IVirtualDesktopManagerInternal` (create/remove/rename/switch,
move window) with names read from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{guid}\Name`.
Only current/all are wired into tools; the rest are library functions.

**Ours.** Nothing.

**Sketch.** Phase 1 (safe): documented `IVirtualDesktopManager` (`CLSID_VirtualDesktopManager`)
to tag A-1 windows with `DesktopId`, plus registry names → `virtual_desktop(action=list|current)`.
Phase 2 (optional): switch/create/remove/rename/move via the internal interface behind
build-number detection with graceful "unsupported on this build" failure; `remove` needs
`confirm:true`. Observe the vtable-gap rule.

**Done when.** Phase 1: window list shows which desktop each window is on and the current desktop
name.

### A-13 — Unicode hygiene  `P2 · S`
- [x] Done 2026-09-04 — [design note](design/A-13-unicode-hygiene.md); in `CHANGELOG.md [Unreleased]`, ships with the next release (0.8.0 per roadmap C11)

**Upstream.** `desktop/utils.py` `remove_private_use_chars()` strips U+E000–U+F8FF (VS Code's
codicons in element names) and `repair_surrogates()` fixes lone UTF-16 surrogates (emoji in window
titles) before any string reaches the JSON encoder — one bad title used to take the whole snapshot
down.

**Ours (before A-13).** Names flowed straight from UIA into `JsonSerializer`. Measured on
.NET 10: a lone surrogate does **not** throw, it is silently written as U+FFFD — a lossy rewrite
the model cannot see — and PUA glyphs passed through as token noise. Now every name, value,
`get_text` result, table header/cell and `assert_element` observation goes through
`UiText.Sanitize` (see the design note).

**Sketch.** A `UiText.Sanitize(string)` helper applied in `ToInfo`/`TryGetName` and to window
titles: strip PUA, replace lone surrogates with U+FFFD, trim control chars.

**Done when.** A window title containing an emoji and a VS Code sidebar both serialise cleanly.

### A-14 — Post-capture flash overlay and snapshot profiling  `P3 · M`
- [ ] Not started

**Upstream.** `desktop/flash_overlay.py`: a layered, click-through, always-on-top window draws an
orange glow around the captured area for ~3.5 s **after** capture (torn down before the next
capture so it never appears in an image); `WINDOWS_MCP_DISABLE_FLASH` turns it off.
`WINDOWS_MCP_PROFILE_SNAPSHOT` logs per-stage timings (context, tree, region filter, capture,
resize, build).

**Ours.** Neither.

**Sketch.** Flash: `WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOPMOST` window with
`UpdateLayeredWindow` on a dedicated thread; env `WINDOWSMCP_DISABLE_FLASH`. Profiling:
`Stopwatch` per stage → stderr at Debug level when `WINDOWSMCP_PROFILE_SNAPSHOT` is set.

**Done when.** A capture shows the glow and the next capture does not contain it.

---

## B — Input, apps, and window ergonomics

### B-1 — `type`: target, clear, caret, press_enter, long-text paste  `P1 · M`
- [ ] Not started

**Upstream.** `Type(text, loc|label, clear, caret_position=start|idle|end, press_enter)`:
clicks the target, moves the caret (Home/End), clears with Ctrl+A + Backspace, then either
per-key `SendKeys` with escaping and 40 ms pacing (short text or text containing `\n`, `\t`,
`{`, `}`) or a **clipboard paste** for long plain text that restores the previous clipboard
(`desktop/service.py` `type()` ~716, `_paste_text()` ~747, `_escape_text_for_sendkeys()`).

**Ours.** `type(text)` = `Keyboard.TextEntry` into whatever has focus; no targeting, no clear, no
enter, no long-text strategy (long `TextEntry` bursts drop keys in some apps).

**Sketch.** `TypeOptions(int? X, int? Y, string? ElementId, bool Clear, Caret Caret,
bool PressEnter, int? PaceMs)`; click/focus target first (element via D-2 focus, or physical
click); clear via `ctrl+a`,`backspace`; paste path when `text.Length ≥ threshold` and no control
chars — set clipboard through `IClipboardService`, `ctrl+v`, restore prior text; `\n` → Enter,
`\t` → Tab when typing per key. Return `{typed, method: keys|paste}`.

**Touches.** `Abstractions/IInputService.cs`, `Models/InputDtos.cs`, `Services/InputService.cs`,
`Tools/InputTools.cs`, tests (mock clipboard; `UIAutomation` test types into Notepad and reads
back via `get_text`).

**Done when.** `type("hello", element_id, clear=true, press_enter=true)` replaces a field's
content and submits; 5 000 characters arrive intact.

### B-2 — `drag`: duration, intermediate motion, from current cursor  `P2 · S`
- [ ] Not started

**Upstream.** `Move(loc, drag=true, from_loc?, duration≤10s)` → `uia.DragDrop(cx,cy,x,y,
moveSpeed=1, duration)` with interpolated intermediate points; `from_loc` omitted = start at the
current cursor (`drag()` ~829).

**Ours.** `DragAsync` = button down, one absolute jump, button up (`InputService.cs:74`). Many
targets (file managers, canvases, DnD in browsers) need intermediate `WM_MOUSEMOVE`s to
recognise a drag.

**Sketch.** `from_x/from_y` optional (default `GetCursorPos`); `duration_ms` (cap 10 000) and
`steps` (default ~20): press, then `steps` interpolated moves with `duration/steps` delays, small
initial nudge to exceed `SM_CXDRAG`, release. Keep the middle-button rejection.

**Done when.** Dragging a file between two Explorer windows works; a Notepad text drag-select
works.

### B-3 — `scroll` at current cursor or at an element  `P2 · S`
- [ ] Not started

**Upstream.** `Scroll(loc=None)` scrolls at the current mouse position; `label` targets an
element; `type=horizontal` uses Shift+wheel for apps without horizontal wheel support.

**Ours.** `scroll(x, y, direction, amount)` — coordinates mandatory.

**Sketch.** Make `x,y` optional; add `element_id` (centre from Bounds / A-3); if horizontal scroll
has no effect, optional `use_shift_wheel:true`.

**Done when.** `scroll(direction="down")` with no coordinates scrolls under the cursor.

### B-4 — `click` by element id; `clicks=0` hover  `P2 · S`
- [ ] Not started

**Upstream.** `Click(loc|label, button, clicks 0|1|2)`; `0` = hover only.

**Ours.** `click(x,y,button,clicks)`; separate `hover`. `interact_element` cannot physically
click (D-2).

**Sketch.** `element_id` alternative on `click` (resolve centre; refuse if `IsOffscreen`); accept
`clicks=0` as hover for parity. Mostly falls out of D-2.

### B-5 — Plain `wait` tool  `P1 · S`
- [ ] Not started

**Upstream.** `Wait(duration)` sleeps N seconds (`tools/input.py` ~422). Agents call it
constantly between launch/click and the next snapshot.

**Ours.** None. Agents fall back to `powershell("Start-Sleep 2")`, which pays a PowerShell
cold-start (seconds to tens of seconds under Defender — see `CLAUDE.md`) and takes the
serialization gate.

**Sketch.** `wait(seconds: double)` → `Task.Delay`, capped (e.g. 60 s), honour `CancellationToken`,
returns `"waited Ns"`. Annotate read-only/idempotent (C-7).

### B-6 — `wait_for` conditions and window filter  `P2 · M`
- [ ] Not started

**Upstream.** `WaitFor(condition, text?, window_name?, timeout≤120s, interval≤5s, use_dom)`
with conditions `text_exists`, `active_window`, `element_exists`, `element_enabled`,
`focused_element` (aliases `text|window|element|enabled|focused`); polls the tree in-process;
returns elapsed + attempts + a detail string; raises `TimeoutError` with the last detail
(`tools/input.py` `_matches_wait_condition`, `_validate_wait_for_args`).

**Ours.** `wait_for(text, timeout_ms, interval_ms)` — element-name text only, foreground only.
**Scope note:** the `window_name` filter is delivered by [D-5](design/D-5-find-path-resilience.md)
(`scope=foreground|window|desktop` on both `find_element` and `wait_for`); B-6 keeps the
`condition` enum and `use_dom`. Do not build the filter twice.

**Sketch.** `condition` enum + `window_title` filter + `use_dom` (A-5); evaluate against the A-2
snapshot (cheaper: A-1 window list for `active_window`); return
`{satisfied, elapsedMs, attempts, detail}`; timeout returns `satisfied:false` with detail rather
than throwing (keeps the existing contract of returning `null` on timeout — decide in spec).

**Done when.** `wait_for(condition="active_window", text="Notepad")` resolves after `launch`.

### B-7 — `multi_select` / `multi_edit` batch tools  `P2 · S–M`
- [ ] Not started

**Upstream.** `MultiSelect(locs|labels, press_ctrl=true)` holds Ctrl while clicking each point
(`multi_select()` ~868); `MultiEdit(locs=[[x,y,text],…]|labels=[[label,text],…])` clicks + types
each field (`multi_edit()` ~880). Both tolerate JSON-stringified lists (Claude Desktop quirk).

**Ours.** None; agents issue N round-trips.

**Sketch.** `multi_select(points_json | element_ids, ctrl=true)` and
`multi_edit(entries_json)` where entries are `{x,y}|{element_id}` + `text`, reusing B-1's
typing path; return per-entry results; stop on first failure and report the index.

### B-8 — Launch by Start Menu name with fuzzy match and window wait  `P1 · M`
- [ ] Not started

**Upstream.** `App(mode=launch, name)`: builds a name→AppID map from `Get-StartApps`
(CSV), falls back to scanning Start Menu `.lnk` folders, adds locale display names from
`shell:AppsFolder`; fuzzy-matches the request (`thefuzz` `extractOne`, score ≥ 70); launches a
path via `Start-Process -PassThru` (captures PID) or an AUMID via
`Start-Process shell:AppsFolder\<AUMID>` after `_check_app_exists`; then waits up to 10 s for a
window by PID, else by regex title; reports "launched" vs "sent, window not detected"
(`launch_app()` ~528, `get_apps_from_start_menu()` ~327, `app()` ~475).

**Ours.** `launch(app_name)` = ShellExecute, returns a PID, no matching, no wait
(`WindowService.cs:63`). `ShortcutResolver` (IShellLink) already exists for the startup report.

**Sketch.** `IAppCatalogService` building the map from `Get-StartApps` (or the
`shell:AppsFolder` `IShellFolder` enumeration, no PowerShell) + Start Menu `.lnk` via
`ShortcutResolver`, cached with a TTL; a small Levenshtein/partial-ratio matcher (`internal`,
unit-tested); packaged apps via `IApplicationActivationManager.ActivateApplication(AUMID)`
(returns PID) or `explorer.exe shell:AppsFolder\<AUMID>`; wait loop over A-1's window list by PID
then fuzzy title; return `{matchedName, score, pid, hwnd, title, windowDetected}`. `launch` keeps
accepting a path.

**Done when.** `launch("calc")`, `launch("edge")`, `launch("visual studio code")` all open and
return the window handle.

### B-9 — Window resize / move  `P2 · S`
- [ ] Not started

**Upstream.** `App(mode=resize, name?, window_loc=[x,y], window_size=[w,h])` on a named or the
active window via `MoveWindow`; refuses minimized/maximized windows (`resize_app()` ~441).

**Ours.** `window` has minimize/maximize/restore/close only.

**Sketch.** `window(action="move"|"resize"|"set_bounds", title?/hwnd?, x,y,w,h, restore_first)`
→ `SetWindowPos`; default target = foreground; error on minimized/maximized unless
`restore_first:true`; return new bounds.

### B-10 — Fuzzy window matching and robust bring-to-foreground  `P1 · M`
- [ ] Not started

**Upstream.** `_find_window_by_name()` (~412) fuzzy-matches (score ≥ 70) over the snapshot's
window list; `switch_app()` restores from minimized; `bring_window_to_top()` (~574) tries
`SetForegroundWindow`+`BringWindowToTop`, then `AllowSetForegroundWindow(-1)`,
`AttachThreadInput` to the target thread (skipped when elevated/Access Denied), retries, and
reports "Restored … and switched" vs "Switched".

**Ours.** `FindWindow(null, exactTitle)` + a bare `SetForegroundWindow` (`WindowService.cs:51`),
which Windows refuses when our process is not the foreground process — the common case for an
MCP server — so `switch_to_window`/`focus` often return `false` for a title that exists.

**Sketch.** Match: exact → case-insensitive substring → fuzzy over A-1's list; accept `hwnd` for
precision; foreground: `IsIconic`→`SW_RESTORE`; `SetForegroundWindow`; on failure
`AttachThreadInput(GetCurrentThreadId, GetWindowThreadProcessId(hwnd))` + `BringWindowToTop`
+ `SetForegroundWindow` + detach; last resort the ALT-key nudge (`keybd_event(VK_MENU)`);
return `{matchedTitle, score, strategy, restored}`.

**Done when.** `switch_to_window("notepad")` brings a window titled "Untitled - Notepad" to the
front from behind another app.

### B-11 — `start_process` with argv list and cwd  `P2 · S`
- [ ] Not started

**Upstream.** `App(mode=launch_executable, executable, args=[…], cwd)` validates the exe and cwd
exist, uses `Popen([...], shell=False)` (no quoting bugs), returns `{pid, executable, args, cwd}`
(`tools/app.py` `_launch_executable`).

**Ours.** `start_process(command)` — one command-line string, no cwd (`ProcessTools.cs:125`).

**Sketch.** Add `args_json` (`string[]`) → `ProcessStartInfo.ArgumentList`, `cwd`,
`use_shell_execute`; validate paths; keep `command` for backward compatibility.

### B-12 — `multi_monitor` detail: work area, orientation, DPI, scale  `P2 · S`
- [ ] Not started

**Upstream.** `DisplayInventory` → index, device, primary, bounds, work_area, resolution,
orientation, effective_dpi, scale (`tools/display.py`, `uia.DisplayInfo`).

**Ours.** `MonitorInfo(Index, DeviceName, X, Y, Width, Height, IsPrimary)`.

**Sketch.** Extend the record: `WorkArea` (`GetMonitorInfo.rcWork`), `Orientation`
(`EnumDisplaySettings.dmDisplayOrientation`), `EffectiveDpi` (`GetDpiForMonitor`,
`MDT_EFFECTIVE_DPI`), `Scale = dpi/96`. Requires Per-Monitor-V2 awareness (D-3) for physical
pixels.

---

## C — Files, registry, processes, notifications, scrape, shell

### C-1 — File tools: offset/limit, append, overwrite, recursive, pattern  `P2 · M`
- [ ] Not started

**Upstream.** `FileSystem` modes (`tools/filesystem.py`, `filesystem/service.py`): `read` with
line `offset`/`limit`; `write` with `append` and parent creation; `copy`/`move` with `overwrite`
(refuse when the destination exists otherwise); `delete` with `recursive` (refuse non-empty dirs
otherwise); `list` with `pattern`, `recursive`, `show_hidden`; `search` glob; `info`. Relative
paths resolve from the user's Desktop.

**Ours.** `file_read(max_bytes, encoding)`, `file_write(confirm)` (no append),
`file_manage(copy|move|delete|list)` with no overwrite/recursive/pattern flags. (We are ahead on
hashing, ADS, duplicates, archives, and confirm gates.)

**Sketch.** `file_read`: `offset_lines`, `limit_lines` (line window, 1-based like upstream);
`file_write`: `append`, `create_parents` (default true); `file_manage`: `overwrite` (default
false → error if exists), `recursive` (default false → error on non-empty dir), `list` gains
`pattern`, `recursive`, `include_hidden`. Decision: keep absolute-path-only (reject relative with
a clear message) — Desktop-relative resolution is a foot-gun; record the decision in the spec.

**Done when.** Each new flag has a unit test in `FileSystemServiceTests`/`FileToolsTests`.

### C-2 — Registry delete and subkey listing on the tool surface  `P2 · S`
- [ ] Not started

**Upstream.** `Registry(mode=delete, path, name?)` removes a value or, without `name`, the whole
key recursively; `mode=list` returns values **and** sub-keys in one call (`registry/service.py`).

**Ours.** `registry_get` without `value_name` returns value names only; `RegistryService`
already implements `EnumerateValuesAsync` (`:22`) and `EnumerateSubKeysAsync` (`:49`) but no tool
exposes them; no delete at all.

**Sketch.** `registry_get(hive, path)` → `{values:[{name,kind,data}], subKeys:[…]}`; new
`registry_delete(hive, path, value_name?, recursive=false, confirm)` — whole-key delete requires
`confirm:true` **and** `recursive:true` when it has subkeys. Update the Safety-rails list.

### C-3 — Process list CPU %, sort, limit; graceful kill  `P2 · M`
- [ ] Not started

**Upstream.** `Process(list, name?, sort_by=memory|cpu|name, limit=20)` prints PID, name,
CPU %, memory (psutil, fuzzy name filter > 60); `kill(force)` = `terminate()` (graceful) vs
`kill()` (`process/service.py`).

**Ours.** `process(list)` has memory, path, lineage, orphan detection (ahead), but no CPU column,
sort, or limit; kill is always hard.

**Sketch.** CPU: two `TotalProcessorTime` samples ~250 ms apart normalised by core count, or
`Win32_PerfFormattedData_PerfProc_Process` via `IWmiService`; `sort_by`, `limit`; kill:
`graceful:true` → `CloseMainWindow()` / `WM_CLOSE`, wait N s, then `Kill()`; keep `confirm`.

### C-4 — Notification `app_id` (AUMID)  `P3 · S`
- [ ] Not started

**Upstream.** `Notification(title, message, app_id)` — the AUMID is mandatory because Windows
uses it as toast identity (`notifications/service.py`).

**Ours.** `NotificationService.cs:25` hardcodes `'Windows-MCP'`. Unregistered AUMIDs are dropped
on some builds.

**Sketch.** Optional `app_id` param (default keeps the current value); document the registration
requirement; optionally register a Start Menu shortcut carrying our AUMID on first use.

### C-5 — `scrape`: DOM source, query focus, MCP-sampling summary  `P2 · M`
- [ ] Not started

**Upstream.** `Scrape(url, query?, use_dom=false, use_sampling=true)`: HTTP fetch → markdownify,
**or** the active browser tab's DOM text with "Reached top / Scroll down to see more" hints
(`use_dom`); then, if the client supports sampling, `ctx.sample()` summarises the raw content with
a boilerplate-stripping system prompt focused on `query`; falls back to raw when sampling is
unsupported (`tools/scrape.py`).

**Ours.** `scrape(url)` → HTML→Markdown via ReverseMarkdown, private IPs rejected.

**Sketch.** `source: http|dom` (dom via A-5), `query`, `summarize:true` → server-initiated
sampling through the SDK (`IMcpServer` sampling request — verify the 2.2.0 API and check the
client's `sampling` capability first), `max_chars` truncation for raw output.

### C-6 — `powershell`: per-call timeout; environment rebuild from registry  `P2 · S–M`
- [ ] Not started

**Upstream.** `PowerShell(command, timeout=30)` with `run_with_graceful_timeout`;
`powershell/service.py` `_read_reg_env()`/`_dedup_path()` rebuild the child environment
(HKLM `Session Manager\Environment` + HKCU `Environment`, `REG_EXPAND_SZ` expanded, PATH merged
and de-duplicated) because MCP hosts frequently spawn the server with a stripped environment,
which makes `git`, `node`, etc. "not found" inside tool calls.

**Ours.** 15-min backstop + `background:true` jobs (ahead), but no per-call `timeout_seconds`;
`PowerShellInvocation.cs` does not touch the environment.

**Sketch.** `timeout_seconds` (≤ backstop) → cancel + tree-kill; environment: when the inherited
`PATH` lacks `%SystemRoot%\System32` or is empty, rebuild from the registry as above and inject
into `ProcessStartInfo.Environment` (foreground and jobs share `PowerShellInvocation`).

### C-7 — Tool annotations on all 65 tools  `P2 · S`
- [ ] Not started

**Upstream.** Every tool declares `ToolAnnotations(title, readOnlyHint, destructiveHint,
idempotentHint, openWorldHint)` — clients use them for auto-approve and confirmation UX.

**Ours.** `[McpServerTool]` everywhere with no properties set.

**Sketch.** `[McpServerTool(Name=…, Title=…, ReadOnly=…, Destructive=…, Idempotent=…,
OpenWorld=…)]` — verify property names on `McpServerToolAttribute` in SDK 2.2.0. Classification
table in the plan (read-only: screenshot, ocr, get_*, find_element, system_info, …; destructive:
file_write, file_manage, registry_set, process kill, service, power_action, firewall, env set;
open-world: scrape, http_request, powershell, network). Add a `ServerInfoTests` assertion that
every listed tool has explicit annotations.

---

## S — Server, transport, packaging

### S-1 — Tool allow/deny lists  `P2 · S`
- [ ] Not started

**Upstream.** `--tools A,B` (explicit set, overrides) / `--exclude-tools X,Y` (+ env
`WINDOWS_MCP_TOOLS`/`_EXCLUDE_TOOLS`, `[tools] exclude=[…]` in config) filtered in
`__main__.py` `_apply_tool_filter()`; unknown names error at startup. Lets an operator run a
screenshot-only or no-PowerShell server.

**Ours.** All 65 tools always.

**Sketch.** `ServerOptions` gains `Tools`/`ExcludeTools` (flags + `WINDOWSMCP_TOOLS`/
`WINDOWSMCP_EXCLUDE_TOOLS`, valid for both transports); in `WindowsMcpHost.AddWindowsMcp` filter
`McpServerOptions.ToolCollection` after `WithToolsFromAssembly()`; validate names against the
discovered set; `--help` documents it; `ServerOptions` unit tests + an `HttpTransportTests`
case listing tools with an exclusion.

### S-2 — IP allowlist  `P2 · S`
- [ ] Not started

**Upstream.** `--ip-allowlist 10.0.0.0/8,192.168.1.5` (IPv4/IPv6, CIDR) →
`IPAllowlistMiddleware` (403 for others, `/health` exempt) — `infrastructure/security.py`.

**Ours.** README tells the operator to scope a firewall rule.

**Sketch.** `--ip-allowlist` / `WINDOWSMCP_IP_ALLOWLIST` parsed to `IPNetwork` list (HTTP mode
only); `app.Use` **before** the bearer gate; use `HttpContext.Connection.RemoteIpAddress` (do not
trust `X-Forwarded-For` unless a `--trust-proxy` flag is added); exempt `/health` (S-8);
`HttpTransportTests`: allowed loopback passes, `203.0.113.0/24`-only config yields 403.

### S-3 — CORS origins  `P3 · S`
- [ ] Not started

**Upstream.** `--cors-origins https://a.example` → CORS middleware; none emitted by default.

**Sketch.** `--cors-origins` / env → `AddCors` + `UseCors` only when set; document that
browser-based MCP clients need it and native clients do not.

### S-4 — OAuth 2.0 + PKCE (or external-IdP bearer)  `P3 · L`
- [ ] Not started

**Upstream.** `infrastructure/oauth.py`: in-process authorization server — RFC 8414 metadata at
`/.well-known/oauth-authorization-server`, `/oauth/authorize` (code + PKCE S256 required),
`/oauth/token`, pre-provisioned confidential client (`--oauth-client-id/secret`), 1 h tokens,
5 min codes, in-memory store; `AuthKeyMiddleware` accepts the static key **or** a valid OAuth token.

**Ours.** Static bearer only (constant-time compare in `WindowsMcpHost.IsAuthorized`).

**Sketch.** Two options for the spec to weigh: (a) port the minimal AS as above (self-contained,
single-process); (b) `Microsoft.AspNetCore.Authentication.JwtBearer` validating tokens from an
external IdP (Entra ID, Auth0) + the SDK's protected-resource metadata support — less code, more
standard. Either way keep the static key path working and unchanged for stdio.

### S-5 — PEM cert/key files and a self-signed generator  `P2 · S–M`
- [ ] Not started

**Upstream.** `--ssl-certfile/--ssl-keyfile` (PEM) and `auth --with-tls` which generates a
self-signed pair into `~/.windows-mcp/` (`_gen_tls`).

**Ours.** `--cert-thumbprint` from the certificate store only (better integrated with Windows,
but PEM files are what most people already have from mkcert/Let's Encrypt).

**Sketch.** `--cert-file` + `--key-file` → `X509Certificate2.CreateFromPemFile` (mutually
exclusive with `--cert-thumbprint`); `WindowsMcp.exe gen-cert --dns <host> [--out dir]`
creates a self-signed cert in `CurrentUser\My`, prints the thumbprint, exports `.cer` and `.pem`
for the client. `ServerOptions` tests; `HttpTransportTests` HTTPS case with a PEM pair.

### S-6 — Config file and `auth` helper  `P2 · M`
- [ ] Not started

**Upstream.** `~/.windows-mcp/config.toml` (`[server]`, `[security]`, `[tools]`) loaded by
`infrastructure/config.py`, precedence CLI > config > default, strict type validation;
`windows-mcp auth` generates a 32-byte key, saves it, and prints ready-to-paste client JSON for
stdio / SSE / Streamable HTTP.

**Ours.** CLI + `WINDOWSMCP_*` env only (`Hosting/ServerOptions.cs`).

**Sketch.** `%USERPROFILE%\.windows-mcp\config.json` (or `%LOCALAPPDATA%\WindowsMcp\config.json`;
JSON — .NET has no built-in TOML), `--config <path>`; precedence CLI > env > file > default,
still inside the pure `ServerOptions.Parse` (pass the file contents in, no I/O in the parser);
`WindowsMcp.exe auth [--transport http] [--port] [--bind] [--with-tls]` writes the key and prints
the `.mcp.json` / `claude mcp add` snippets. Keep "no args = stdio" untouched.

### S-7 — `install` / `uninstall` at-logon task  `P2 · M`
- [ ] Not started

**Upstream.** `windows-mcp install [--transport] [--host] [--port]` registers an **at-logon**
scheduled task for the current user via `Register-ScheduledTask … -RunLevel Limited` (no
elevation), runs it immediately, logs to `~/.windows-mcp/server.log`; `uninstall` ends and deletes
it (`__main__.py` `install()`/`uninstall()`).

**Ours.** Manual. Note `CLAUDE.md`/README: input, screenshot, window and UIA tools need the
**interactive session** — an at-logon task in the user's session is fine; a service or Session-0
task is not.

**Sketch.** `WindowsMcp.exe install --transport http --port … [--bind 127.0.0.1]` using the
already-referenced `TaskScheduler` NuGet (`LogonTrigger`, `TaskRunLevel.LUA`,
`ExecutionTimeLimit = 0`, stdout/stderr → `%LOCALAPPDATA%\WindowsMcp\logs`); requires an API key
unless loopback (reuse the S-6 config); `uninstall` stops + deletes; `status` prints the task
state and the running image path (the `Get-CimInstance Win32_Process` check from `CLAUDE.md`).

### S-8 — Unauthenticated `/health`  `P2 · S`
- [ ] Not started

**Upstream.** `/health` is exempt from auth and the IP allowlist (`_PUBLIC_PATHS`), used by
monitors and the `install` flow.

**Ours.** The bearer gate covers every path by design.

**Sketch.** `GET /health` → `{status:"ok", version, transport, uptimeSeconds}` mapped **before**
the gate; no tool list, no hostname; `HttpTransportTests` asserts 200 without a key and that
`/mcp` still 401s.

### S-9 — Claude Desktop Extension (`.mcpb`) and MCP registry `server.json`  `P3 · M`
- [ ] Not started

**Upstream.** `manifest.json` (manifest 0.4, `server.type: uv`, `user_config` toggles for
telemetry/profiling/backend/debug/watchdog mapped to env) packaged as an `.mcpb`, and
`server.json` for the official MCP registry (`io.github.CursorTouch/Windows-MCP`).

**Ours.** Claude Code plugin only (`.claude-plugin/plugin.json`, `bundle/WindowsMcp.exe`).

**Sketch.** `manifest.json` with `server.type: "binary"`, `entry_point: bundle/WindowsMcp.exe`,
`user_config` for `WINDOWSMCP_SCREENSHOT_SCALE`, `WINDOWSMCP_MAX_TREE_ELEMENTS`, backend, debug;
a `pack-mcpb` script; `server.json` naming `io.github.<owner>/Windows-mcp`; document both in
README.

### S-10 — Per-tool black-box tester skill  `P3 · S`
- [ ] Not started

**Upstream.** `.claude/skills/windows-mcp-tool-tester/SKILL.md`: one tool per run, cases derived
only from the MCP schema, correctness + latency, mandatory side-effect verification, PID-scoped
cleanup, VM/Sandbox recommendation for destructive tools.

**Ours.** `skills/windows/SKILL.md` is a usage playbook, not a tester.

**Sketch.** `skills/windows-tool-tester/SKILL.md` adapted to our 65 tools and `confirm:true`
gates; wire into the plugin manifest.

---

## X — Deliberately not porting

| Upstream feature | Reason |
|---|---|
| PostHog anonymous telemetry (`infrastructure/analytics.py`, `ANONYMIZED_TELEMETRY`, `POSTHOG_*`) | Privacy; no benefit to a self-hosted server. |
| SSE transport | Deprecated in the MCP spec; Streamable HTTP covers remote use. |
| UIA focus watchdog (`watchdog/`, `WINDOWS_MCP_WATCHDOG`) | Upstream itself documents it as debug-logging only and a crash risk. |
| `--stateless-http` toggle | Our HTTP transport is already stateless. |
| Desktop-relative path resolution in file tools | Ambiguity risk; absolute paths only (see C-1). |
| JSON-stringified-list coercion (`_as_loc`) | Our parameters are scalars; the Claude Desktop quirk does not apply. |
| `.mcpbignore`, `uv`/PyPI packaging | Python-specific. |

---

## Appendix A — Tool name map (upstream → ours)

| Upstream (20) | Ours (65) | Gap items |
|---|---|---|
| `Snapshot` | `snapshot`, `get_state`, `find_element`, `get_element`, `get_text`, `window` (list/active) | A-1..A-6, A-11..A-13 |
| `Screenshot` | `screenshot`, `ocr` | A-7..A-11 |
| `DisplayInventory` | `multi_monitor` | B-12 |
| `Click` | `click`, `interact_element` | D-2, B-4 |
| `Type` | `type`, `file_dialog` | B-1 |
| `Scroll` | `scroll` | B-3 |
| `Move` | `hover`, `drag` | B-2 |
| `Shortcut` | `shortcut`, `key` | D-1 |
| `Wait` | — | B-5 |
| `WaitFor` | `wait_for`, `assert_element` | B-6 |
| `MultiSelect`, `MultiEdit` | — | B-7 |
| `App` | `launch`, `switch_to_window`, `focus`, `window`, `start_process` | B-8..B-11 |
| `PowerShell` | `powershell`, `job` | C-6 |
| `FileSystem` | `file_read/write/manage/search/info/hash/streams`, `archive` | C-1 |
| `Scrape` | `scrape`, `http_request` | C-5 |
| `Clipboard` | `clipboard` | — |
| `Process` | `process`, `process_inspect` | C-3 |
| `Notification` | `notification` | C-4 |
| `Registry` | `registry_get`, `registry_set` | C-2 |
| _(none)_ | services, tasks, event log, disk, storage, network, firewall, security, startup report, integrity, USN, watch, env, power, audio, WMI, drivers, reliability, cert store, Defender, signatures | ours only |

## Appendix B — Upstream source map (`src/windows_mcp/`)

| Area | Files |
|---|---|
| CLI, transports, auth wiring, install/auth commands | `__main__.py` (`serve` ~447–745, `_apply_tool_filter` ~349, `_http_middleware` ~80, `install` ~856, `uninstall` ~905, `auth` ~925, `_gen_tls` ~747) |
| Config file | `infrastructure/config.py` |
| Bearer / OAuth / IP allowlist / SSRF | `infrastructure/auth.py`, `infrastructure/oauth.py`, `infrastructure/security.py` |
| Desktop facade (state, windows, app launch, input) | `desktop/service.py`, `desktop/views.py`, `desktop/utils.py` |
| Screenshot backends, flash | `desktop/screenshot.py`, `desktop/flash_overlay.py` |
| UI tree (classification, traversal, rendering, budget, cache, IA2) | `tree/config.py`, `tree/service.py`, `tree/views.py`, `tree/budget.py`, `tree/cache_utils.py`, `tree/ia2.py` |
| UIA wrapper (comtypes) | `uia/*.py` |
| Virtual desktops | `vdm/core.py` |
| Tools | `tools/*.py`, `tools/_snapshot_helpers.py` |
| Services | `filesystem/service.py`, `process/service.py`, `registry/service.py`, `notifications/service.py`, `powershell/service.py` |
| Packaging | `manifest.json`, `server.json`, `pyproject.toml` |
