# A-2 — `snapshot`: the desktop-wide labelled element list (with A-4's budget and A-3's scroll data)

**Checklist items:** [A-2](../upstream-parity-checklist.md#a-2--desktop-wide-labeled-interactive-element-snapshot--p1--l),
[A-3](../upstream-parity-checklist.md#a-3--scrollable-regions-with-scroll-percentages--p2--s),
[A-4](../upstream-parity-checklist.md#a-4--element-budget-truncation-note-uia-caching--p1--m) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 3 — the centre of the section; the one new tool (C4) ·
**Status:** implemented 2026-09-05 in two test-first cycles (build clean, 1463/1463 headless tests
green, 17/17 Notepad-fixture tests green — see CHANGELOG [Unreleased]) ·
**Effort:** ~2 days including four test-agent passes.

## Problem

Upstream's agent loop starts with one `Snapshot` call: the windows, the cursor, and a labelled
list of every interactive element with centre coordinates and an action hint, cheap enough in
tokens to call every turn. Ours needed `get_state` (foreground only, three levels, a JSON tree,
no centres, no classification, unbounded on a big grid) plus a `find_element` per control, and
still had no scroll positions. The element cache also grew forever.

## Decision

Built as two cycles so the pure logic was proven before any UIA read existed.

**Cycle A — the pure core** (`Services/UiTree/`, all 100 % line/branch):
- `UiNode`: every fact a traversal reads from one element (type, name, bounds, enabled,
  off-screen, focus, password, value, range, toggle, expand, access/accelerator key, legacy role,
  scroll, depth, window).
- `UiClassifier`: **owns D-6's interactive set** — `find_element` now references the same array,
  so the two cannot drift; upstream's LegacyIAccessible role fallback for `Custom` elements
  (`pushbutton`, `link`, … and `text` only when the node carries a value — static text has that
  role too); the informative set (Text, Image, StatusBar, ProgressBar, ToolTip, Header — not
  HeaderItem, a column header sorts); the action map (`Edit`/`Document` → fill, CheckBox →
  toggle, ComboBox → select, Slider/Spinner → slide, ScrollBar → scroll, else click; **Document →
  fill** deviates from upstream's `document→scroll` because a Document is the thing you type into
  and scrolling it is advertised in the scrollable list); `IsScrollable`, `CenterOf`, `ShortcutOf`.
- `ElementBudget` (A-4): `TryTake` per admitted node, `Truncated` on the first refusal, one
  `NoteFor(limit)` sentence the renderer prints verbatim.
- `SnapshotRenderer` (C6): the compact text form — cursor, active window, z-ordered window list,
  interactive rows grouped by window (stable `GroupBy`, first-appearance order) with a fixed tag
  order (action, focused, password, value, toggle, expand, shortcut, range), scrollable rows with
  percentages and `[reached top]`/`[reached bottom]`, the budget note. A password never prints a
  value; values clip at 80 chars; CR/LF/tab/backslash are escaped so **one element is one row** —
  the GREEN pass showed a Document's value (a whole multi-line file) splitting the block.
- DTOs: `ScrollInfo` (additive on `ElementInfo`), `SnapshotElement`, `SnapshotScrollable`,
  `SnapshotResult`, `SnapshotRequest`, `SnapshotScope`, `UiTreeOptions`; `ElementTree` gains
  `Truncated`/`ElementLimit` that serialise only when set, so `get_state`'s JSON is unchanged
  until a walk is cut short.

**Cycle B — the traversal, the service, the tool, the option, the back-port:**
- `UiTraverser.Walk(root, title, budget)`: re-fetches the root under one FlaUI `CacheRequest`
  (`TreeScope.Subtree`, `AutomationElementMode.Full`) and walks `CachedChildren`, so a subtree is
  one cross-process fetch instead of one per property (A-4). Every read is guarded (a dead
  element is skipped, never fatal — D-5); names and values go through `UiText.Sanitize` (A-13);
  each node is clipped to the window rect (`Clip`, pure and unit-tested) and dropped when
  off-screen (an `Edit` with real bounds is kept — D-7) or zero-area; the budget is spent once
  per admitted node and the walk stops the moment it refuses. Pre-order, root first.
  **The defect the GREEN pass caught:** a cached *pattern* is not its *properties* — reading
  `Value`, `Role`, `ToggleState` or the scroll percentages inside the request threw
  `PropertyNotCachedException`, the guard turned it into null, and 200 walked nodes reported
  zero scrollables, zero values, zero roles while every mocked, integration and desktop test
  stayed green. Each pattern property id is now cached too; UIA's −1 for a non-scrolling axis is
  clamped to 0.
- `UIAutomationService.SnapshotAsync`: header from the A-1 inventory and the cursor (each
  collaborator once; the list is reused for the roots); roots by scope — `desktop` walks every
  non-minimised window topmost first, `foreground` the active entry (falling back to UIA's own
  foreground when no entry is flagged), `window` matches exact-then-substring against the
  inventory and names up to 15 open titles when nothing matches; one budget for the whole call
  on the STA thread; a window whose walk throws is logged and skipped. **Ids (C5):** one `el_N` per
  walked node, so the tree, the lists and `get_element` share a numbering; the ids the previous
  snapshot issued are evicted when the next one starts, and a `find_element` id issued between
  survives. `Project` is a pure `internal static` so the password rule (`Value` null in JSON too)
  is testable headless. `IncludeTree` hangs one window subtree per walked window under a
  synthetic `desktop` root.
- `--max-tree-elements <n>` / `WINDOWSMCP_MAX_TREE_ELEMENTS` (both transports, positive whole
  numbers only, `NumberStyles.None` so `+5` and ` 5` are rejected), registered as a
  `UiTreeOptions` singleton (C7) and injected into `UIAutomationService`, whose constructor now
  takes `IWindowService` too.
- `get_state` keeps its foreground root and three-level shape but is budgeted: descent stops when
  the budget refuses and the root carries `Truncated`/`ElementLimit`. `find_element` is unchanged
  (its own 20-match cap and UIA-side conditions already bound it; the cache request there is a
  follow-up).
- Tool `snapshot(scope, window, include_tree, max_elements, format, use_dom)`: validates scope →
  window rule → `max_elements` → `format` → `use_dom` (refused with "A-5 … not implemented"),
  calls the service once, returns `SnapshotRenderer.Render` for `text` (default) or the
  serialised result for `json`. 64 → 65 tools.

## Tests (test-agent RED → GREEN, four passes)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| A.R1–R5 | DTO shapes and JSON invisibility of the budget fields; every classifier rule (17 types, 6 informative, 10 structural, 16 roles, the text-needs-value rule, type-beats-role, action map, non-interactive → click); budget arithmetic and note; every renderer rule with golden strings (layout, empty desktop, tags, password, clip at 80/81, quotes/CR/LF/backslash escaping, grouping incl. case-sensitive titles, percentages and half-rounding, reached-top gate, footer = the budget's own sentence) | `SnapshotDtosTests`, `UiNodeTests`, `UiClassifierTests`, `ElementBudgetTests`, `SnapshotRendererTests` (≈ 360 cases) | Unit |
| B.R1–R2 | `--max-tree-elements` parsing (11 rejected forms), Usage, DI singleton; constructor change | `ServerOptionsTests`, `WindowsMcpHostTests` | Unit |
| B.R3 | Argument rules before any collaborator; header reads once each; active = flagged entry; window matching and the open-titles error; per-call limit; a ghost window skipped; tree root; `Project` password/split/no-bounds | `UIAutomationSnapshotArgumentTests`, `UiClassifierTests.Project_*` | Unit |
| B.R4 | `get_state` budgeted, root reports it | `UIAutomationSnapshotIntegrationTests`, `UIAutomationSnapshotDesktopTests` | Integration / UIAutomation |
| B.R5–R6 | Tool validation order, mapping, text/json, description; discovered over HTTP with defaults; json/text over HTTP with a mocked service | `UIAutomationToolsTests`, `HttpTransportTests` | Unit / Integration |
| B.R7 | Notepad fixture: editor listed with `fill` and a centre inside its bounds; `max_elements:5` → 5, truncated, note rendered; ids work with `get_element`/`interact_element`; a second snapshot evicts them but not a `find_element` id; window scope by substring and the error listing open windows; desktop scope includes Notepad; tree root and resolvable ids; scroll percentages in range; under 10 s; the traverser's pre-order, clipping and budget stop; a minimised window listed but not walked | `UIAutomationSnapshotDesktopTests` (17) | UIAutomation |
| B.R8–R11 | Through the real `WindowService`/`InputService` on this session: the walk happens, layout parses, budget honoured, foreground = the active entry, fallback when none, minimised skipped, ids evicted, tree reproduces every walked node; **the live oracle rows**: the legacy role and the scroll pattern a direct read returns are what the walk reports | `UIAutomationSnapshotIntegrationTests` (10) | Integration |
| B.R9 | `Clip`: inside, each edge, enclosing, 1 px, touching, outside, zero-area, unknown window | `UiTraverserClipTests` (20) | Unit |

Bite checks across the two cycles: eighteen one-line breaks, all caught after the GREEN passes
added the rows that were missing (clip-at-81, reached-top on a horizontal-only region, the note
shared with the budget, `NumberStyles.None`, the id eviction, the foreground fallback, the
minimised filter, `ElementLimit` reporting the effective limit).

## Deviations and follow-ups

- **`ElementInfo.Scroll` is populated only by the snapshot.** `find_element(kind:scrollable)`
  still returns no scroll data; the field is there, the read is a small follow-up (A-3's
  checklist text names both).
- **`find_element` keeps its own walk** (conditions pushed into UIA, 20-match cap); the cache
  request and budget apply to `snapshot` and `get_state`. A-4's checklist mentions `find_element`
  too — deferred, it is already bounded.
- **`scope=window` resolves against the inventory**, so a window `WindowFilter` drops (tool
  window, cloaked, untitled) is unreachable by `snapshot` while `find_element(scope:window)` can
  still reach it through UIA's own child list.
- **Vacuous invariants on a quiet desktop:** two `OnlyContain` checks on scrollables prove nothing
  when nothing scrolls; their non-vacuous sibling is the live-oracle scroll test, which fails if
  the walk reports fewer scrollables than a direct read finds.
- **Modern Notepad's window lives in another process** than the one `Application.Launch`
  started, so `GetMainWindow` can return null; the fixture helper falls back to the A-1 inventory
  by title. Two live-oracle tests pick the first listed window that still resolves, because other
  tests in the same run open and close windows.
- Labels: the roadmap's C5 imagined numeric labels plus `el_N`; the `el_N` ids **are** the labels
  — one identifier the model reads and every tool accepts.
