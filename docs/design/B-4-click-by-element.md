# B-4 — `click` by element id, `clicks:0` as hover, and the one target resolver

**Checklist item:** [B-4](../upstream-parity-checklist.md#b-4--click-by-element-id-clicks0-hover--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 2, first item — it builds the element-target
resolver (C1) every input verb reuses ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

`snapshot` hands out `el_N` ids with centre coordinates, but `click` only took coordinates: the
agent had to copy numbers out of one response into the next call, and nothing stopped it from
clicking the centre of an element that had scrolled off-screen since the snapshot. `hover` was a
separate tool for what upstream expresses as `clicks: 0`.

## Decision

- **One resolver, in the tool layer** (`InputTools.ResolveTargetAsync`, C1): exactly one of
  (`x` and `y`) or `element_id`. Both given, one coordinate without the other, or neither where a
  target is required are `ArgumentException`s that name the parameters in play, so `drag`'s
  refusals say `from_x`/`from_element_id` and `to_x`/`element_id`. An id goes through
  `IUIAutomationService.GetElementAsync` (the id cache A-2 and D-4 already keep) and the pure
  `ElementTarget.CentreOf`: integer-division centre of the bounds; an off-screen element, one with
  no bounds, or empty bounds is refused with an `InvalidOperationException` naming the id and the
  reason, before any input is sent. Off-screen is reported before missing bounds because both
  usually hold at once and the first is the actionable one. Where a verb allows no target at all
  (`scroll`, `drag`'s origin) the live cursor is used and the response says `cursor` (C2).
- **`click(x?, y?, element_id?, button, clicks)`**: `clicks: 0` moves the pointer and presses
  nothing (`HoverAsync`), reported as `action: "hover"`; 1–3 as before; negative refused. The
  response is JSON — `{action, x, y, button, clicks, elementId?, name?}` — with the resolved
  point, so a model that clicked by id learns where that was, and `button` is the parsed name
  (`"R"` → `right`). `x` and `y` stay the first two parameters, so positional callers keep
  working. `hover` stays as a tool.

## Changes

- `Services/UiTree/ElementTarget.cs` (new, pure); `Tools/InputTools.cs` — `IUIAutomationService`
  injected, `ResolveTargetAsync`, `Click` re-signed and re-described.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Centre by integer division, negative coordinates fine, the three refusals naming the id and the reason, off-screen before no-bounds, the id echoed is the one given | `ElementTargetTests` (7 methods) | Unit |
| R2 | The exclusivity and half-pair rules on every verb, the cursor fallback where allowed, an id resolved through the lookup, an unreachable id refused before any input, an unknown id surfacing as the lookup threw it, a coordinate call never touching UI Automation | `InputTools{Click,Type,Scroll,Drag}Tests` (shared rows) | Unit |
| R3 | `click`: coordinates clicked and echoed, button forwarded and named, 1–3 clicks through, `clicks:0` hovers (by point and by id) and never clicks, negative refused, `x`/`y` first, description, schema over HTTP | `InputToolsClickTests` (10 methods), `HttpTransportTests` (1) | Unit / Integration |
| R4 | `click(element_id)` focuses the Notepad editor; `clicks:0` parks the pointer on the element's centre | `InputToolsDesktopTests` (2) | UIAutomation |

## Deviations and follow-ups

- The roadmap's "an id goes through D-2's `focus` first and clicks only if focus did not land"
  was not built for `type`: an id is a physical click at the centre on every verb, one rule.
  `interact_element(click)` remains the pattern-first path.
- **The verbs echo the id they were given, not the lookup's.** `UIAutomationService.GetElementAsync`
  goes through `ToInfo`, which mints and caches a fresh `el_N` on every call, so the first
  desktop run of `click(element_id:"el_0")` read back `el_8`. The resolver now carries the
  caller's id into the response; the minting itself (one never-evicted cache entry per
  `get_element` or input-verb call) is pre-existing and stays a follow-up for the service.
- `hover` was kept alongside `clicks:0` (the roadmap allowed either); removing a tool is a
  bigger break than a duplicate alias.
