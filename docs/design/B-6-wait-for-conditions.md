# B-6 — `wait_for` conditions, a structured result, and `use_dom`

**Checklist item:** [B-6](../upstream-parity-checklist.md#b-6--wait_for-conditions-and-window-filter--p2--m) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 4, first item — decision C4 (a result on timeout,
never `"null"`) settled in section 7 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, 7/7 desktop tests green
— see CHANGELOG [Unreleased]) ·
**Effort:** ~3 h including the RED/GREEN passes.

## Problem

`wait_for` waited for one thing, an element whose name contains a text, and answered with the
element or the string `"null"`. An agent that had just launched an app had no way to wait for
its window, for a control to become enabled, for the focus to land, or for a sentence to appear
on a web page, and a timeout told it nothing about what the desktop looked like meanwhile.
Upstream's `WaitFor` takes a condition, returns elapsed time, attempts and a detail string, and
raises on timeout.

## Decision

- **Five conditions, one pure evaluator** (`WaitConditions.Evaluate(condition, text,
  evidence)`): `element_exists` (any find-path match; detail `found 'name' (id)` or `no element
  matching 'text'`), `element_enabled` (a match with `IsEnabled`; a found-but-disabled match is
  reported as such), `focused_element` (the snapshot's focused interactive element whose name
  contains the text, projected to an `ElementInfo` so the agent can act on its id; "nothing has
  keyboard focus" and "'x' has focus, wanted 'y'" are different diagnoses), `text_exists` (the
  text anywhere in a snapshot of the scope: element names and values, scrollable regions, and
  with `use_dom` the page's words; the detail says where), `active_window` (the foreground
  window's title exact → substring → fuzzy 70+ with B-10's scorers; the detail names the strategy
  or what was in front instead). Aliases `element|enabled|focused|text|window`. Evidence a
  condition did not need, or a poll could not gather, is "not there yet", never a throw.
- **Each condition gathers only what it reads.** `active_window` reads the window inventory and
  walks nothing; `text_exists` and `focused_element` take a snapshot of the scope (no tree, the
  server's budget, `use_dom` through to A-5's `Pages`); the element conditions use D-5's guarded
  find path with today's kind/scope/window/off-screen filters, re-resolved every poll.
- **The result is always a result** (C4): `{Satisfied, Condition, ElapsedMs, Attempts, Detail,
  Element?}`. The loop (`WaitLoopAsync`, a seam like D-5's `PollAsync`) polls immediately, then
  every `interval_ms` (10 ms floor, clamped to the remaining budget), counts every poll, retries a
  poll that throws, and on the deadline reports `Satisfied: false` with the last evaluation's
  detail — or, when every poll threw, a detail that starts `every poll failed:` and names the
  error. D-5's rationale ("never managed to look" must not read as "not found") is met by that
  detail; the `TimeoutException` stays on the old overload, which D-2 and other callers keep.
- **The tool.** The seven parameters keep their names, order and defaults, so every existing
  call behaves as `element_exists`; `condition` and `use_dom` are appended. Ranges are 0–120 000
  and 0–5 000 ms; a blank text is refused naming the condition. The string `"null"` is gone: a
  behaviour change, in CHANGELOG under Changed with its migration.

## Changes

- `Abstractions`: `WaitCondition`, `WaitRequest`, `WaitForResult` (`Element` JSON-omitted when
  null); `IUIAutomationService.WaitForAsync(WaitRequest)` beside the old overload.
- `Services/UiTree/WaitConditions.cs` (new, pure); `Services/UIAutomationService.cs`
  (`WaitForAsync(WaitRequest)`, `WaitLoopAsync`, `SnapshotRequestFor`).
- `Tools/UIAutomationTools.cs` — `wait_for` re-described, the two parameters, the result.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | DTO shape and defaults; `Element` omitted when null; the old overload unchanged (null / `TimeoutException`); ranges and blank text at the service; ranges, blank text, `use_dom` accepted elsewhere at the tool before any call | `UIAutomationToolsWaitForTests`, `WaitForServiceTests`, `WaitForFindPathIntegrationTests` | Unit / Integration |
| R2 | The evaluator, every condition: found/not found details, first match, disabled skipped for enabled, focus present/wrong/absent, text in a name, a value, a scrollable region, a page line, nowhere, no `Pages`, no snapshot; active window exact/substring/fuzzy/below-70/not-active/none/no inventory; an unknown enum value throws | `WaitConditionsTests` (26 methods) | Unit |
| R3 | The loop: stops at the satisfying poll, attempts counted, `timeout_ms: 0` polls once, a timeout carries the last detail, a throwing poll retried, every-poll-failed named, a clean verdict outranks an earlier failure, the interval clamped, poll before sleep, cancellation, the canonical names; the snapshot request's scope/title/`use_dom` mapping; `active_window` reads the inventory and nothing else; the real find path end to end | `WaitForServiceTests` (20+), `WaitForFindPathIntegrationTests` (2) | Unit / Integration |
| R4 | Tool: the old call shape is `element_exists`; filters and budgets forwarded; scope/window rules kept; every name and alias; an unknown condition lists the five; `use_dom` forwarded; the whole result returned, never `"null"`; descriptions; the schema and a timeout as a result over HTTP | `UIAutomationToolsWaitForTests` (12+), `HttpTransportTests` (2) | Unit / Integration |
| R5 | Desktop: `active_window` after focusing Notepad, `text_exists` on a live on-screen name, `element_enabled` on the editor, `focused_element` after a click, a never-appearing text within 600 ms (attempts ≥ 2, elapsed ≥ 600); Edge: `text_exists` with `use_dom` finds the page's heading and without it does not | `UIAutomationToolsWaitForDesktopTests` (5), `UIAutomationToolsWaitForDomTests` (2) | UIAutomation |

## Deviations and follow-ups

- **Typed document content is not `text_exists` evidence.** The condition reads element names
  and values, scrollable regions and page text; a Notepad document's body is none of those, so
  waiting for a string typed into the editor needs `element_exists` with `kind: text`, or the
  document's value through `get_text`. Growing the evidence to a document's value is the
  follow-up if it turns out to matter.
- `Element` is set only when the condition is satisfied and is about one element;
  `text_exists` and `active_window` never carry one.
- The old `WaitForAsync(text, …)` overload keeps throwing `TimeoutException` when every poll
  failed; only the tool's contract changed.
