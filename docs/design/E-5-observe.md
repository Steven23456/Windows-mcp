# E-5 — `observe`: action tools return the state they produced

**Checklist item:** [E-5](../upstream-parity-checklist.md#e-5--observe-action-tools-return-the-state-they-produced--p1--m) ·
**Depends on:** D-11 (the bounded element cache; snapshot ids evicted as a set) ·
**Status:** implemented 2026-11-12 (see CHANGELOG [Unreleased]; not verified live — the
`UIAutomation` category and a third-party app were not run) ·
**Effort:** M, three RED/GREEN rounds plus a review round.

## Problem

`click`, `type`, `key`, `shortcut`, `scroll`, `drag` and `interact_element` return an echo of what
they did. Learning what happened costs a `wait_for` or `wait` and then a `snapshot`: one UI step
is three calls and three model turns, and the step that matters most — clicking a menu and
reading what it opened — is the one where the extra round trip is always needed.

## Contract as built

- **Parameters.** All seven tools gain `observe` (`none` | `foreground` | `window`, any case;
  default `none`) and `settle_ms` (0-5000, default 250). `key` and `shortcut` advertise only
  `none | foreground` (`Observation.ObserveParameterNoElement`) and refuse `window`.
- **`observe:none` is byte-identical** to the tool's output before E-5.
- **Otherwise**, after the action the tool waits `settle_ms` (`Observation.Delay`, a seam so the
  tests do not sleep), takes a snapshot, and returns:

  ```
  <the action's result, unchanged>

  Observed the foreground window after 250 ms (a new snapshot: el_N ids from earlier snapshots are no longer valid):
  <SnapshotRenderer.Render(snapshot)>
  ```

  For `window` the header reads `Observed the element's window after N ms (…)`; when that window
  is gone it reads `Observed after N ms: the element's window is gone, so this is the foreground
  window instead (…)`. A truncated walk adds `; truncated at the element limit, so call snapshot
  with scope, window or max_elements for the rest` to the header (the renderer's own footer names
  `snapshot`'s parameters; the header says whose they are).
- **It is a real snapshot.** It goes through `IUIAutomationService.SnapshotAsync`, so it evicts
  the previous snapshot's ids and issues new ones; the descriptions, the `snapshot` description,
  `ElementCache`'s expired-id message and the server instructions all say "any action with
  observe" replaces snapshot ids. It uses the server's default element budget
  (`--max-tree-elements`) and no DOM mode.
- **One helper.** `Tools/Observation.cs` — `AroundAsync(observe, settleMs, elementId, ct,
  action)` — is shared by every tool. Each tool method is now a thin wrapper
  `=> Observation.AroundAsync(...)` over a private `XxxCore` method that holds the old body
  unchanged. `InputTools` and `UIAutomationTools` each construct their own `Observation` from the
  `IUIAutomationService` they already inject; it is not a registered service.

### Why the observation is appended to the same text, not a second block

The checklist sketch said "a second block after the action's JSON". These seven tools return
`Task<string>`, which the SDK wraps as one text block, and hundreds of tests plus every existing
client read that string. Switching them to `CallToolResult` to carry two blocks would change the
return type of all seven for every caller, observe or not. Appending keeps the first line of the
result exactly the old output (a client that parses it still can) and keeps `none` free. This is
a deliberate departure, recorded in the checklist's "As landed".

## Which window `observe:window` means

`window` is the top-level window **the target element belongs to**, resolved **before** the action
by the new `IUIAutomationService.GetElementWindowAsync(elementId, ct)`: the click may close the
dialog the element is in, and a destroyed element cannot say which window it was part of.

- The element itself, else the nearest ancestor with a native handle (at most 200 levels up the
  control view; an ancestor counts only while it has a parent of its own, so the desktop root is
  never the answer), is asked for its Win32 **root** window with `GetAncestor(GA_ROOT)`
  (`NativeMethods.txt` gains `GetAncestor`; the `RootWindowOf` delegate is the test seam).
- **Why not the top of the UIA tree:** UI Automation nests an owned window (a dialog) under its
  owner, so walking UIA parents to the top answers the owner. `GA_ROOT` follows the parent chain,
  not the owner chain, and stops at the dialog.
- **The target element:** `element_id` on `click`, `type`, `scroll` and `interact_element`;
  `drag` uses its destination `element_id`, else `from_element_id`. A call with no element — a
  coordinate, cursor or focus target, and always `key` and `shortcut` — refuses `window` with
  `observe:window observes the window the target element belongs to, and this call has no
  element_id. Use observe:foreground.`
- An unknown or expired id gets the element cache's refusal, a destroyed element `UiRefusals.Gone`,
  and an element whose window cannot be determined `The window that <element> (el_N) belongs to
  could not be determined. Use observe:foreground.` — all before any input. A timeout in the walk
  (a hung provider) propagates.

The snapshot then takes that one window **by handle**: `SnapshotRequest.WindowHwnd` (only with
`Scope == Window`, never with a title). The handle need not be in the window inventory, which
filters out untitled and tool windows (`WindowFilter`): the service builds a stand-in row
(`UnlistedWindow`) and takes the title from UIA's name for the window. A window missing from the
inventory is not a window that closed; whether it exists is asked of the handle itself
(`ElementFromHandle`, the second seam), and one that has gone is a `KeyNotFoundException` naming
the handle. The observation catches exactly that and observes the foreground window instead.

## Popups

An open menu, a combo box's dropdown and a tooltip are their own untitled top-level windows, not
children of the window that opened them, and the inventory does not list them — so a snapshot of
the window alone never shows the menu a click just opened. `SnapshotRequest.IncludePopups`, set by
every observation, makes `SnapshotAsync` (for any scope but `desktop`) walk, after the target
windows and under the same budget, every top-level window of the desktop that

- belongs to a process already walked,
- is not in the inventory and was not already walked,
- is not off-screen and has a non-empty rectangle,

labelled with its UIA name, else `popup`. The `snapshot` tool does not set it, so its output is
unchanged (parity D-39 logs that gap).

## Failure rules

- **Refusals come first.** `observe` and `settle_ms` are parsed and range-checked, and the window
  looked up, before the action's own argument checks and before any input — a bad `observe`
  never costs a click. `settle_ms` is checked even with `observe:none`.
- **An action that throws is observed by nobody:** its exception propagates unchanged.
- **A snapshot that fails afterwards never fails the action.** Any exception but a cancellation
  becomes one line after the result: `Observation failed: <why> The action above was performed, so
  do not repeat it; call snapshot to see the result.` `<why>` is the caller-facing message
  (newlines folded) or `an unexpected fault (<TypeName>).` for anything `ToolErrors` would mask.
- **Cancellation.** Only `click` and `key` take the request's token (E-3 added it); cancelling
  them during the settle wait or the snapshot drops the result although the action was performed,
  and their descriptions say so (`Observation.CancelNote`). The other five pass
  `CancellationToken.None` (D-40).

## Deliberately left out

- **No `max_elements` / `use_dom` on the observation** — seven tools would each gain more schema
  rows; the truncation header points at `snapshot` instead (D-42).
- **No popups in the plain `snapshot`** — a behaviour change to the most-called tool, kept out of
  this item's scope (D-39).
- **No diff against the previous snapshot** — that is E-11, which waits on a measurement of what
  observations cost.
- **No token on the five token-less tools**, and the takeover hotkey does not interrupt an
  observation (D-40, D-45).

## Tests (test-agent RED → GREEN, review round)

| Requirement | Test(s) | Category |
|---|---|---|
| `none` unchanged on every tool; parse, bounds and refusal order; the header, the appended text, the snapshot after the input; the window fallback; the failure note; cancellation on `click`/`key` | `ObservationToolsTests` | Unit |
| The seven schemas and descriptions carry the parameters and notes; `key`/`shortcut` advertise no `window` | `ObservationSurfaceTests` | Unit |
| `GetElementWindowAsync`: own handle, ancestor handle, desktop root never the answer, unknown/expired/gone refused, a timeout propagates | `UIAutomationServiceElementWindowTests` | Unit |
| `WindowHwnd`: the scope/title rules, an unlisted window walked, a closed one a `KeyNotFoundException`; `IncludePopups` order, filters and budget | `UIAutomationSnapshotWindowHandleTests`, `UIAutomationSnapshotPopupsTests` | Unit |
| On real windows with the real `WindowService`: an element in an owned dialog answers the dialog; untitled, tool and popup windows observed by handle | `UIAutomationServiceElementWindowIntegrationTests`, `UIAutomationServiceObserveWindowsIntegrationTests` (`Fixtures/OwnedWindowFamily.cs`) | Integration |
| The done-when: a real WinForms `ContextMenuStrip` (opened from code in a child PowerShell process, no input injected) is listed item by item by a by-handle snapshot with popups, and not without | `ObserveRealContextMenuIntegrationTests` | Integration |

## Deviations and follow-ups

- Same-text append instead of a second block (above).
- The done-when is met in a test, not live: the menu is opened from code and observed through the
  service; the click-through-the-tool path is covered by the mocked unit tests.
- Neighbour gaps logged in the checklist and not fixed: D-39 (`snapshot` without popups), D-40
  (five tools without a token), D-41 (a cancelled snapshot still evicts ids), D-42 (no
  `max_elements`/`use_dom` on an observation), D-43 (an owned dialog listed twice in desktop
  scope), D-44 (`FlashOverlayTests` outside the desktop collection), D-45 (the takeover hotkey does
  not interrupt an observation).
