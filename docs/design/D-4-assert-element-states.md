# D-4 — `assert_element`: implement `value` and `focused`, report the observed state

**Checklist item:** [D-4](../upstream-parity-checklist.md#d-4--assert_element-advertises-value-and-focused-but-implements-neither--p2--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) ·
**Order:** landed before D-5; the stale-element helpers this note introduces (`IsElementGone`, the
ProcessId liveness probe) live in `UIAutomationService.cs` for D-5 to reuse. Effort: half a day
including the UIAutomation-category tests.

## Problem

`src/WindowsMcp/Tools/UIAutomationTools.cs` `AssertElement` advertised
`exists, enabled, checked, value, visible, focused` and promised `'PASS' or 'FAIL: <reason>'`.
`src/WindowsMcp/Services/UIAutomationService.cs` `AssertElementAsync` implemented four of the six
and threw `Unknown assertion state` for `value` and `focused`. Three smaller things were wrong in
the same method:

- **`FAIL:` carried no reason.** The tool returned `FAIL: {state}` — the state the caller asked
  for, not what was observed. An agent that got `FAIL: enabled` learned nothing it did not know.
- **`exists` was always `true`.** It never touched the element, so a control whose window closed
  since `find_element` still "existed". An unknown id threw `KeyNotFoundException` instead of
  failing the assertion.
- **`enabled` / `visible` read `el.IsEnabled` / `el.IsOffscreen` unguarded.** A stale element
  surfaced as a `COMException` from the tool rather than a `FAIL`, so the caller could not tell
  "the button is disabled" from "the button is gone"; and a provider that omits an optional
  property (modern Notepad's document has no `IsOffscreen`) made `visible` throw
  `PropertyNotSupportedException`.
- **`value` had nothing to compare against.** The tool had no `expected` parameter.

## Decision

One state table, every row implemented, and the result says what was observed.

| state | passes when | `Observed` on FAIL |
|---|---|---|
| `exists` | the element is in the cache **and** alive (see the liveness probe below) | `unknown element id` / `element no longer available` |
| `enabled` | `IsEnabled` (UIA's default `true` when a provider omits it) | `disabled` |
| `checked` | `TogglePattern.ToggleState == On` | `toggle state Off` / `toggle state Indeterminate` / `no TogglePattern on <ControlType> '<Name>'` |
| `visible` | `!IsOffscreen` (unsupported ⇒ not offscreen) **and** `BoundingRectangle` is non-empty | `offscreen` / `empty bounds` |
| `focused` | `Properties.HasKeyboardFocus`, or the element **is** `_automation.FocusedElement()` (some frameworks report focus only through the latter) | `focus is on <ControlType> '<Name>'`, or `nothing has focus` |
| `value` | **requires `expected`**; `ValuePattern.Value` (or `Name` when there is no ValuePattern — the same fallback `get_text` uses) equals `expected`, ordinal | `value is '<actual>' (from ValuePattern\|Name)` |

Rules that apply to every row:

- **Liveness probe first, for every state.** Two things happen to a destroyed element, depending
  on the provider. XAML / UWP / browser providers raise `UIA_E_ELEMENTNOTAVAILABLE` (0x80040201) on
  any read — FlaUI converts it to `ElementNotAvailableException`, and RPC failures
  (`RPC_E_DISCONNECTED`, `RPC_S_SERVER_UNAVAILABLE`, `RPC_S_CALL_FAILED`) appear once the process is
  gone; `IsElementGone(Exception)` recognises all of those. The Win32 HWND proxy instead keeps
  answering with **defaults**: a killed Character Map window still reads as `ControlType Pane`,
  empty `Name`, `IsEnabled false`, `ProcessId 0`, `FindAllChildren` empty — no exception at all
  (measured 2026-09-04). So the probe is `Properties.ProcessId.ValueOrDefault <= 0` **plus** the
  exception net; either yields `Pass=false, Observed="element no longer available"`.
- **Unknown id → FAIL for `exists`, throw for everything else.** `exists` is the one question
  where "I don't know that id" is the answer; for the other states an unknown id is a caller bug
  (ids only come from `find_element` / `get_state`) and stays `KeyNotFoundException`.
- **`expected` is only meaningful with `value`.** `value` without it → `ArgumentException("'value'
  requires expected: the text to compare against.")`; any other state with it →
  `ArgumentException("expected is only used with state=value.")`. Silently ignoring an argument is
  how the D-2 `select` bug happened.
- **Unknown state** → `ArgumentException("Unknown assertion state '<state>'; expected
  exists|enabled|checked|value|visible|focused.")` — same shape as `interact_element`.
- **Reads never throw for an omitted property.** Every read goes through `ValueOrDefault` /
  `TryGetValue` with UIA's documented default where it matters (`IsEnabled` → true). `.Value`
  would throw `PropertyNotSupportedException` for a provider that leaves a property out.
- **Comparison for `value` is exact and ordinal.** No trimming, no case folding: an agent that
  wants looser matching can read the value with `get_text` and compare itself. `Observed` always
  quotes the actual value so the next call can be exact.

**Return type.** New DTO
`AssertResult(string ElementId, string State, bool Pass, string Observed)` in
`src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`; the interface is
`Task<AssertResult> AssertElementAsync(string elementId, string state, string? expected = null, CancellationToken ct = default)`.
The tool keeps its text contract — `PASS`, or `FAIL: <state> — observed <Observed>` — because the
tool description promises `'PASS' or 'FAIL: <reason>'` and it is the cheapest possible response
for a confirm step. `Observed` is also filled on PASS (e.g. `value is 'hello' (from Name)`) so a
caller of the service can find it; the tool prints it only on FAIL.

**Threading.** All reads happen on the STA worker inside one `OnStaAsync`; `FocusedElement()` is
a second cross-process call, made only when `HasKeyboardFocus` is false. `CompareElements` itself
fails with 0x80040201 when either side is stale, so the identity check is guarded (`SameElement`)
and a failure means "not the same", never "gone".

**Rejected.**
- Returning JSON from the tool. Breaks the documented `PASS`/`FAIL:` contract for no gain; the
  observed state fits in the text.
- Accepting `SelectionItemPattern.IsSelected` as `checked` (radio buttons, list items). Useful but a
  new state (`selected`), not one of the six advertised — log it as a checklist item if wanted.
- A `contains` / case-insensitive `value` mode. See the ordinal rule above.
- Checking `Process.GetProcessById` as a second liveness signal. UIA already reports `ProcessId 0`
  for a dead HWND; the extra process enumeration bought nothing in the measurements.

## Changes (as landed)

- `src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`: `AssertResult`.
- `src/WindowsMcp.Abstractions/IUIAutomationService.cs`: new signature (adds `expected`, returns
  `AssertResult`).
- `src/WindowsMcp/Services/UIAutomationService.cs`: `AssertElementAsync` → `AssertOnSta` per the
  table; helpers `IsElementGone` (internal, shared with D-5), `TryGetFocusedElement`,
  `SameElement`.
- `src/WindowsMcp/Tools/UIAutomationTools.cs`: `AssertElement` gains
  `[Description("Expected value; only with state=value")] string? expected = null`; the
  description lists the six states, the `expected` rule, and the `FAIL: <state> — observed <what>`
  shape.
- `tests/WindowsMcp.Tests/Tools/UIAutomationToolsTests.cs`: forwarding + rendering tests.
- `tests/WindowsMcp.Tests/Services/UIAutomationServiceTests.cs`: live tests below; unit tests for
  the argument rules, the unknown-id rule and `IsElementGone`.
- `tests/WindowsMcp.Tests/Fixtures/NotepadFixture.cs`: `BringToForeground()` extracted from the
  constructor so a test that opens another window can hand the desktop back.
- `docs/architecture/COMPONENTS.md`, `DATAFLOW.md`, `OVERVIEW.md`, `skills/windows/SKILL.md` §4,
  `CHANGELOG.md [Unreleased]`, and the checklist (D-4 ticked; D-5's sketch now reuses
  `IsElementGone` and the ProcessId probe).

## Tests

- Unit, `UIAutomationToolsTests`: `AssertElement("el-1", "value", "hi")` forwards all three
  arguments and a mocked `Pass=false, Observed="value is 'ho' (from ValuePattern)"` renders as
  `FAIL: value — observed value is 'ho' (from ValuePattern)`; `Pass=true` renders exactly `PASS`.
- Unit, `UIAutomationServiceUnitTests` (mock `IInputService`, no desktop): `value` without
  `expected`, `enabled` with `expected`, and an unknown state throw `ArgumentException` naming the
  rule; an unknown id with `exists` returns `Pass=false, Observed="unknown element id"`; an unknown
  id with `enabled` throws `KeyNotFoundException`; `IsElementGone` recognises 0x80040201 and the
  RPC HRESULTs and rejects `E_FAIL`. These paths never reach UIA, so they run headless.
- `[Trait("Category","UIAutomation")]` with `NotepadFixture` (foreground Notepad; excluded
  headless, as `CLAUDE.md` says):
  - `exists`, `enabled`, `visible` PASS on the document (the `visible` case is what exposed the
    missing `IsOffscreen` on modern Notepad).
  - `focused` PASS on the document after `FocusAsync`; `focused` on a title-bar button FAILs with
    `Observed` starting `focus is on`. (The top-level Window of a XAML app *does* report
    `HasKeyboardFocus`, so it is not usable as the negative case.)
  - `value` PASS with `expected` = what `GetTextAsync` returns after typing a stamp; FAIL with a
    different `expected` quotes the stamp in `Observed`.
  - `checked` FAIL on the document: `Observed` starts `no TogglePattern on`.
  - stale element: start `charmap.exe` (classic Win32, multi-instance, on every edition), bring it
    to the foreground, take the `get_state` root's id, kill the process, then `exists` and
    `enabled` both FAIL with `element no longer available`; the fixture re-foregrounds Notepad.

**Known interference:** `FindNotepadDocumentIdAsync` falls back to the desktop-wide
`FindElementAsync`, which is the D-5 defect (dies on the first stale element anywhere on the
desktop). The helper retries that call up to five times as a stopgap; remove the retry when D-5
lands.

## Docs / CHANGELOG

One bullet under `### Fixed`: every advertised state implemented, `expected` parameter, FAIL names
the observed state, stale elements fail instead of throwing, omitted optional properties no longer
throw. No tool-count change.

## Done when

Checklist bar: every state named in the description is implemented; `FAIL:` names the observed
state. Plus: a stale element yields `FAIL` with `element no longer available` for every state, and
the description, `COMPONENTS.md`, and `SKILL.md` all describe the `expected` parameter. All met
2026-09-04 (26/26 in the `UIAutomation` filter: 14 headless, 12 live).
