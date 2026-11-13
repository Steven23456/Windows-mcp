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

  Popup text (not controls, no ids):          <- only when a popup said something
  <popup label>: <text>
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
  could not be determined. Use observe:foreground.` — all before any input. The liveness check is
  `ClosedNotHung`, so a timeout on it — like a timeout in the walk (a hung provider) — propagates
  and refuses `observe:window` before any input, rather than reading as a destroyed element.

The snapshot then takes that one window **by handle**: `SnapshotRequest.WindowHwnd` (only with
`Scope == Window`, never with a title). The handle need not be in the window inventory, which
filters out untitled and tool windows (`WindowFilter`): the service builds a stand-in row
(`UnlistedWindow`) and takes the title from UIA's name for the window. A window missing from the
inventory is not a window that closed; whether it exists is asked of the handle itself
(`ElementFromHandle`, the second seam), and one that has gone is a `KeyNotFoundException` naming
the handle. The observation catches exactly that and observes the foreground window instead.

**A timeout is not "closed".** A `TimeoutException` while resolving the handle or probing the
resolved root (`ClosedNotHung`: a process id of 0, or an element-not-available / Win32 / COM
failure, means closed; a timeout means a hung provider) propagates out of `SnapshotAsync`. The
observation does not catch it as the window being gone, so the result carries the failure note
and no other window is shown in its place. (Review fix on PR #43: before it, a hung dialog read as
closed and the caller was shown the foreground window under a "gone" header.) A timeout for a
window not asked for by handle is still logged and skipped.

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

### Owned windows

UI Automation nests an **owned** top-level window — a dialog, an owned popup — under its owner, so
the owner's walk sees it clipped to the owner's rectangle, under the owner's title, and not at all
where it reaches outside. With `IncludePopups` and a scope other than `desktop` (and not for a
window walked as a DOM page), each walked window that has a native handle gets its owned windows
from **Win32's owner relation**, not from a search of the UI tree (`OwnedWindowsUnder`): the
`OwnedWindowsOf` seam, by default `Win32OwnedWindows` — `EnumWindows`, keeping every visible
top-level window whose `GetWindow(GW_OWNER)` chain (up to 32 links) reaches the handle, nearest
first, then in z-order, so a dialog opened by a dialog is included and a hidden owned window is
not (`NativeMethods.txt` gains `GetWindow`). Each handle is resolved with `ElementFromHandle`; one
that does not resolve or is gone (`ClosedNotHung`) is logged and skipped, and a failure of the
enumeration itself (other than a timeout) is logged and yields none. They are found **before** the owner is walked, and
their handles go to `UiTraverser.Walk` as `skipHandles`: the owner's walk does not enter an element
whose native handle (read from the cache request, so no extra provider call) is in the set. Each is
then walked as an independent root — clipped to its own rectangle, labelled with its inventory
title, else its UIA name, else `popup` — right after its owner and before the desktop-level
popups, under the same budget, with the same set so a window nested in another owned one is walked
in its own turn, not inside it. A handle already walked is not walked again. So an owned window's
elements are visited, and **charged to the element budget, once**: `ElementCount` is what the
result holds, and with a short budget the owner's rows come first, then the owned window's. An
owned dialog lying beside or reaching outside its owner is observed whole, once, under its own
title.

The per-window walk is one local function, `WalkWindow`, used for every target window **and** for
the foreground fallback (no inventory window flagged active — an untitled or tool window in
front), so that window's owned dialogs are lifted out too; `ForegroundRoot` is the internal test
seam for the fallback's root. In an observation that root comes from
`GetForegroundRoot(timeoutsPropagate: true)`, whose ladder stops at a timeout (see Failure rules).
The root's handle and process id are read only in an observation, so a plain
`snapshot(scope:"foreground")` cannot be failed by a provider that times out on them.

History (review fixes on PR #43): the first fix walked the owner whole and then removed the owned
subtrees from its rows (`WithoutSubtreesOf`, since deleted). That charged an owned window's
elements to the budget twice, so with an exact budget a dialog's controls were dropped; the
second fix replaced it with `skipHandles`. The third replaced discovery itself: the first two
searched the owner's UI tree (`FindAllDescendants` with a handle condition, else a 5 000-element
manual descent, each match checked with `GetAncestor(GA_ROOT)`) — a second pass over the whole
window on the one UI Automation thread whatever the element limit — and now the owner relation is
asked natively; `RootWindowOf` is left to `GetElementWindowAsync`. The `snapshot` tool does none of this: in desktop scope
it still lists an owned dialog's rows twice (D-43).

### Popup text

A tooltip, a message box's question or a dialog's explanatory line is plain text: the snapshot has
no row for it (rows are controls), so before this an observation walked the text and dropped it,
and the one thing the caller wanted — what the popup said — was missing. With `IncludePopups`,
`SnapshotResult.PopupText` collects it for every window walked **as a popup** (an owned window
lifted out of its owner, a desktop-level popup — never the target window or the
foreground-fallback root, whose labels the caller reaches with `find_element kind:text`): each walked node of a popup that is not an interactive row and has a name gives one line `<popup label>: <name>`, in walk order (owned windows first), each name once per popup, line breaks collapsed, the text clipped to 200 characters and the label folded to one line and clipped to 80; the popup's own root name counts only when nothing inside it spoke; at most 50 lines, the 50th then `... (more popup text not shown)`.
`Observation` appends it after the rendered snapshot as `Popup text (not controls, no ids):` and the
lines, or nothing. It is null for every non-observation snapshot, so `snapshot`'s JSON and text
are unchanged. Hover-and-observe (`click` with `clicks:0`, `observe`) therefore returns a
tooltip, and observing the window that opened a message box returns its question.

## Failure rules

- **Refusals come first.** `observe` and `settle_ms` are parsed and range-checked, and the window
  looked up, before the action's own argument checks and before any input — a bad `observe`
  never costs a click. `settle_ms` is checked even with `observe:none`.
- **An action that throws is observed by nobody:** its exception propagates unchanged.
- **A snapshot that fails afterwards never fails the action.** Any exception but a cancellation
  becomes one line after the result: `Observation failed: <why> The action above was performed, so
  do not repeat it; call snapshot to see the result.` `<why>` is the caller-facing message
  (newlines folded) or `an unexpected fault (<TypeName>).` for anything `ToolErrors` would mask.
- **A hung provider is a failure, not a smaller snapshot.** With `IncludePopups` (every
  observation), a `TimeoutException` from **any** step of the STA walk propagates out of
  `SnapshotAsync`: resolving a target window (by handle, the active foreground window, or a
  title match — `ElementFromHandle`), the foreground fallback's root ladder
  (`UiRoots.FirstAvailable(timeoutsPropagate: true, …)` via `GetForegroundRoot`: a hung
  foreground window ends the ladder instead of quietly becoming the focused element or the
  desktop root), probing it, the name read that labels a window with no inventory title — the
  fallback root, an unlisted window by handle, an unlisted owned window, a popup (`Label` →
  `NameUnlessHung`; a non-timeout failure still gives "", so `popup`), owned-window discovery, the pid/handle bookkeeping, any read in the
  walk (`UiTraverser.Walk(…, timeoutsPropagate: true)` — the subtree cache fetch, every node
  property and pattern read through `Try(...)`, `ReadBounds` and around `ReadNode`, the skip-set
  handle read, and the children read; a `[ThreadStatic]` flag carries the mode to the static read
  helpers for the length of the walk), and the desktop-level popup pass — listing the desktop's
  top-level windows and, for one of the observed process's own popups, its offscreen flag, bounds,
  label and walk. **The one exception is a popup candidate:** until a desktop window's native
  handle and process id have both answered and show it belongs to a walked process, a timeout on
  either read skips that window (logged as a skipped popup window) and the scan goes on. Why: the
  popup pass reads every top-level window on the desktop, most of them other applications', and a
  hung window of an unrelated application must not fail the observation of a healthy one — it
  could not have held the rows the caller asked for. (Decided with the user after review; D-47
  would remove the reads altogether.) The observation
  then returns the failure note instead of a snapshot under an "Observed …" header with rows
  missing. There is no property-read exception in an observation. Non-timeout failures keep their
  behaviour: a window or popup that cannot be read is logged and skipped, and a desktop window
  list that cannot be read costs the popups, not the snapshot. Without `IncludePopups` — the
  `snapshot` tool, `wait_for`, `screenshot` annotate, `scrape` dom — nothing changes: a window
  that times out is logged and skipped, the foreground ladder falls to its next rung, an
  unreadable label is "" (`TryGetName`), the foreground fallback reads no native handle or process
  id of its root at all, and the walk swallows everything a dying element does (D-5).
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
| Owned windows skipped by their owner's walk and walked once under their own label, the foreground fallback included; charged to the budget once, owner first under a short budget; owned windows from a fake owner relation (`OwnedWindowsOf`); a by-handle timeout, and with `IncludePopups` a timeout in discovery, bookkeeping or the walk, propagates and an observation turns it into the failure note, not the foreground fallback | `UIAutomationSnapshotOwnedWindowsTests`, `UIAutomationSnapshotOwnedWindowBudgetTests`, `ObserveWindowTimeoutTests`, `UIAutomationSnapshotObservationTimeoutTests` | Unit |
| Strict walk: a timeout from the cache fetch, any property or pattern read, the skip-set handle read or the children read propagates with `timeoutsPropagate`, and is swallowed without it; the root ladder stops at a timeout only in strict mode | `UiTraverserTimeoutTests`, `UiRootsTests` | Unit |
| A window label read times out → propagates in an observation, "" without one; a plain foreground-fallback snapshot reads no root handle or process id | `UIAutomationSnapshotObservationLabelTests` | Unit |
| Popup text: which windows and nodes contribute, order, once per popup, collapse and clip, the root-name rule, the 50-line cap, null outside observations; the appended section | `UIAutomationSnapshotPopupTextTests`, `ObservePopupTextTests`, Z3 rows in `ObservationToolsTests` | Unit |
| A real comctl32 tracking tooltip owned by its host, shown with no input, comes back as popup text | `ObservePopupTextIntegrationTests` (`Fixtures/OwnedTrackingTooltip.cs`) | Integration |
| On real windows with the real `WindowService`: an element in an owned dialog answers the dialog; an owned dialog observed whole under its own title (two owner/owned pairs), a window owned by an owned window, a hidden owned window left out — through the real owner relation; untitled, tool and popup windows observed by handle | `UIAutomationServiceElementWindowIntegrationTests`, `UIAutomationServiceObserveWindowsIntegrationTests` (`Fixtures/OwnedWindowFamily.cs`) | Integration |
| The done-when: a real WinForms `ContextMenuStrip` (opened from code in a child PowerShell process, no input injected) is listed item by item by a by-handle snapshot with popups, and not without | `ObserveRealContextMenuIntegrationTests` | Integration |

## Deviations and follow-ups

- Same-text append instead of a second block (above).
- The done-when is met in a test, not live: the menu is opened from code and observed through the
  service; the click-through-the-tool path is covered by the mocked unit tests.
- Neighbour gaps logged in the checklist and not fixed: D-39 (`snapshot` without popups), D-40
  (five tools without a token), D-41 (a cancelled snapshot still evicts ids), D-42 (no
  `max_elements`/`use_dom` on an observation), D-43 (an owned dialog listed twice by `snapshot` in
  desktop scope; observations no longer do it), D-44 (`FlashOverlayTests` outside the desktop collection), D-45 (the takeover hotkey does
  not interrupt an observation), D-46 (an owned window shown after the owner relation was
  enumerated stays nested, clipped, in its owner's rows), D-47 (the popup pass reads every desktop
  window's handle and process id through UI Automation instead of natively), D-48 (a dialog or
  message box that is itself the foreground window is the target, so `observe:"foreground"` does
  not return its question; observing the window that opened it does).
