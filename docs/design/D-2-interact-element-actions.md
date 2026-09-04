# D-2 — `interact_element`: implement click / focus / type, fix select, report what happened

**Checklist item:** [D-2](../upstream-parity-checklist.md#d-2--interact_element-advertises-click--focus--type-but-only-implements-toggle--select--invoke--p1--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) · **Order:** **after D-3** — the physical-click fallback clicks element
bounds, which only land on a second monitor once D-3 is in. Effort: one day including the
UIAutomation-category tests.

## Problem

`src/WindowsMcp/Tools/UIAutomationTools.cs:68` advertises `click, toggle, select, focus, type`.
`src/WindowsMcp/Services/UIAutomationService.cs:237` `InteractAsync` implements `toggle`, `select`,
`invoke` and throws `Unknown interact action` for everything else. Worse, the three it has use
`PatternOrDefault?.` so an unsupported pattern is a **silent no-op** that still answers
`"interacted"`. `select` demands a `value` it never reads. `FocusAsync` (`:312`) exists but
`interact focus` cannot reach it.

## Decision

One action table. Every branch either does the thing or throws `NotSupportedException` naming the
pattern and the control (`"TogglePattern not supported on Document 'Text editor'"`), and the call
returns what actually happened.

| action | behaviour | `Method` reported |
|---|---|---|
| `click` | `InvokePattern.Invoke()` → else `SelectionItemPattern.Select()` → else `TogglePattern.Toggle()` → else physical left click at the centre of `BoundingRectangle` via `IInputService.ClickAsync` (throws if the element is offscreen or its bounds are empty) | `InvokePattern` / `SelectionItemPattern` / `TogglePattern` / `PhysicalClick` (`Detail: "(x,y)"`) |
| `invoke` | `InvokePattern` only, else throw — kept as the explicit form | `InvokePattern` |
| `toggle` | `TogglePattern`, else throw | `TogglePattern` (`Detail`: resulting `ToggleState`) |
| `select` | **no `value`:** `SelectionItemPattern.Select()` on the element itself, else throw. **with `value`:** treat the element as a container — `ExpandCollapsePattern.Expand()` if present, `FindFirstDescendant(cf => cf.ByName(value))`, then that item's `SelectionItemPattern.Select()` (fallback `Invoke`); `KeyNotFoundException` naming `value` if absent | `SelectionItemPattern` (`Detail: "item 'value'"`) |
| `focus` | `el.Focus()` — same code as `FocusAsync` | `Focus` |
| `type` | requires `value`; `el.Focus()`; if `ValuePattern` is present and `!IsReadOnly` → `SetValue(value)` (**replaces** the whole value); else keyboard `IInputService.TypeAsync(value)` (**inserts at the caret**). A `clear` option is deliberately left to B-1 so both `type` tools get the same semantics at once | `ValuePattern` / `Keyboard` |

**Return type.** New DTO `InteractResult(string ElementId, string Action, string Method, string? Detail)`
in `src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`; the tool serialises it in place of the
constant string.

**Threading.** Pattern calls stay on the STA worker (`OnStaAsync`). The two paths that inject input
(`PhysicalClick`, `Keyboard`) return a small "pending input" marker from the STA step, and the async
wrapper then awaits `IInputService` **off** the STA thread, so a slow or blocked `SendInput` can
never stall the UIA queue.

**Dependency.** `IInputService` is constructor-injected into `UIAutomationService` (both are
singletons, `Hosting/WindowsMcpHost.cs:60` and `:66`; DI resolves it). Tests that write
`new UIAutomationService()` pass `new InputService()` (UIAutomation category) or a Moq mock (unit).

**Rejected:** doing the physical-click fallback in the tool. It splits one action's logic across
two layers, and the tool would need `IInputService` as well.

## Changes

- `src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`: add `InteractResult`.
- `src/WindowsMcp.Abstractions/IUIAutomationService.cs`: `Task<InteractResult> InteractAsync(...)`.
- `src/WindowsMcp/Services/UIAutomationService.cs`: ctor `(IInputService input)`; rewrite
  `InteractAsync` per the table; a helper `NotSupported(el, "TogglePattern")` that formats the
  control type and name; centre from `el.BoundingRectangle` (FlaUI `System.Drawing.Rectangle`,
  physical pixels under PMv2 — same space D-3 establishes for `ClickAsync`).
- `src/WindowsMcp/Tools/UIAutomationTools.cs`: description lists exactly the six actions and the
  `value` contract for `select` / `type`; return `JsonSerializer.Serialize(result)`.
- `tests/WindowsMcp.Tests/Services/UIAutomationServiceTests.cs` (four `new UIAutomationService()`)
  and `UIAutomationServiceUnitTests` (one): pass an `IInputService`.
- `docs/architecture/COMPONENTS.md:451`: update the `InteractAsync` line.

## Tests

- Unit, `tests/.../Tools/UIAutomationToolsTests.cs` (Moq): `InteractElement("el-1", "type", "hi")`
  forwards the three arguments and the response JSON contains `"Method"`.
- Unit, `UIAutomationServiceUnitTests`: the existing dispose test, now with a mock `IInputService`.
- `[Trait("Category","UIAutomation")]` with `NotepadFixture` (interactive desktop, Notepad in the
  foreground — headless runs must exclude the category, as `CLAUDE.md` says):
  - `type` into the document element → `GetTextAsync` contains the text. Modern Notepad's document
    has no writable `ValuePattern`, so this also proves the `Keyboard` path; assert `Method` is one
    of `ValuePattern` / `Keyboard` rather than pinning which.
  - `focus` on the document → `el.Properties.HasKeyboardFocus` is true.
  - `click` on the document → `Method == "PhysicalClick"` (a Document supports none of
    Invoke / SelectionItem / Toggle).
  - `toggle` on the document → `NotSupportedException` whose message contains `TogglePattern` and
    the control type.

**Neighbour gap found, not fixed here** (checklist rule 4): `assert_element` advertises `value` and
`focused`, but `AssertElementAsync` (`:220`) throws `Unknown assertion state` for both, and
`docs/architecture/COMPONENTS.md:454` claims they work. Logged as checklist item **D-4**.

## Docs / CHANGELOG

One bullet under `### Fixed` (the interface change is internal; no tool-count change).
`skills/windows/SKILL.md` §4: add that `interact_element` now reports the pattern it used, and to
prefer it over a coordinate `click` for named controls. Tick D-2 in the checklist and board.

## Done when

Checklist bar: every action named in the tool description works or returns a specific
"X not supported on <controlType>" message; description and implementation agree; the response
says which pattern or fallback fired.
