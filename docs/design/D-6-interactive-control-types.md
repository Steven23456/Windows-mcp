# D-6 — `find_element(kind=interactive)`: use the full interactive control-type set

**Checklist item:** [D-6](../upstream-parity-checklist.md#d-6--find_elementkindinteractive-excludes-edit-combobox-listitem-tabitem-radiobutton-slider-treeitem--p2--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) ·
**Order:** after [D-5](D-5-find-path-resilience.md), which replaces `MatchesKind` with the condition
builder this item feeds. Doing it first would mean writing the set twice. Effort: ~2 hours.

## Problem

```csharp
FindKind.Interactive => el.ControlType is ControlType.Button or ControlType.CheckBox
                        or ControlType.Hyperlink or ControlType.MenuItem,
```

Four control types. Excluded: `Edit`, `ComboBox`, `ListItem`, `TabItem`, `RadioButton`,
`SplitButton`, `TreeItem`, `DataItem`, `HeaderItem`, `Spinner`, `Slider`, `ScrollBar`, `Document`.

So a text box is not "interactive" — the Claude Code prompt box is an `Edit` and
`find_element(kind="interactive")` cannot see it. Neither can it see a dropdown, a list row, a tab,
or a radio button. `kind="text"` (`Text | Edit | Document`) is currently the only way to find an
input, which is backwards: `text` is for reading, `interactive` is for acting, and the controls you
act on are mostly missing from it.

Upstream `tree/config.py` `INTERACTIVE_CONTROL_TYPE_NAMES` is Button, ListItem, MenuItem, Edit,
CheckBox, RadioButton, ComboBox, Hyperlink, SplitButton, TabItem, TreeItem, DataItem, HeaderItem,
TextBox, Spinner, Slider, ScrollBar — plus `INTERACTIVE_ROLES`, a LegacyIAccessible-role fallback
for controls that misreport their type. `DocumentControl` is a separate class there
(`DOCUMENT_CONTROL_TYPE_NAMES`, action `scroll`) but is listed alongside the interactive elements in
the snapshot.

## Decision

One named set, stated in the tool description, pinned by a test.

```csharp
/// Upstream's INTERACTIVE_CONTROL_TYPE_NAMES (tree/config.py), plus Document.
/// Kept as one named set so `kind=interactive` and A-2's classifier cannot drift apart.
internal static readonly ControlType[] InteractiveControlTypes =
[
    ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
    ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
    ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
    ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
    ControlType.Document,
];
```

All seventeen exist in `FlaUI.Core.Definitions.ControlType` (verified against FlaUI 5.0.0). Three
judgement calls:

- **`Document` is in.** Upstream keeps it in a separate class because its snapshot has separate
  lists with separate actions. `find_element` has one flat `kind`, and a text area you type into is
  something you interact with — excluding it would reproduce the exact `Edit`-shaped hole this item
  fixes for XAML apps (modern Notepad's editor is a `Document`, not an `Edit`). When
  [A-2](../upstream-parity-checklist.md#a-2--desktop-wide-labeled-interactive-element-snapshot) adds
  a separate document list, it can split `Document` back out; the set being one named constant is
  what makes that a one-line change.
- **Upstream's `TextBox` is dropped.** There is no `TextBox` UIA control type — it is `Edit`, which
  is already in the set. Carrying the name over would be cargo-culting.
- **The `INTERACTIVE_ROLES` LegacyIAccessible fallback is *not* ported here.** It costs a second
  cross-process property read on every element that fails the control-type test, which is exactly
  the per-element cost D-5 is trying to remove, and it only matters for old MSAA-bridged controls.
  It belongs with A-2's classifier, where the budget and cache make it affordable. Noted, not
  silently dropped.

An array rather than a `HashSet`: D-5's condition builder consumes it as
`new OrCondition(InteractiveControlTypes.Select(cf.ByControlType))`, so the provider does the
matching and no client-side set lookup remains.

## Plan

1. `Services/UIAutomationService.cs` — add `InteractiveControlTypes`; point D-5's
   `KindCondition(cf, FindKind.Interactive)` at it. (If D-6 somehow lands before D-5, the same array
   backs a `Array.IndexOf` test in `MatchesKind` and the OR-condition swap comes with D-5.)
2. `Tools/UIAutomationTools.cs` — the `kind` parameter description spells the set out, so a caller
   can predict what `interactive` returns without reading the source.

## Tests

**Unit** (`UIAutomationServiceUnitTests`, no desktop): pin the set against the list above, exact
contents and order-independent, so a later edit shows up as a deliberate diff rather than a silent
behaviour change.

**UIAutomation category** (Notepad fixture): `FindElementAsync("", FindKind.Interactive)` in
foreground scope returns the editor — `Edit` on classic Notepad, `Document` on the modern one — the
case that fails today.

**As landed**, the "and at least one `MenuItem`" half of this was dropped: asserted against
`scope=desktop` it is really a statement about which window a 20-capped desktop walk reaches first
(it reached the taskbar), and modern Notepad's XAML command bar does not expose classic `MenuItem`s
in the foreground tree anyway. The editor assertion is the one that pins D-6. The desktop-scope test
that replaced it pins something more valuable — that no window root `Pane` is ever returned for
`kind=interactive` (see [D-5](D-5-find-path-resilience.md) §5).

## Docs / CHANGELOG

- `CHANGELOG.md [Unreleased] → Fixed`.
- `docs/architecture/COMPONENTS.md` l.135 — the `FindElement` row's kind description.
- `skills/windows/SKILL.md` §4 — `kind:"interactive"` is the way to find something to click **or
  type into**; `kind:"text"` is for reading.

## Done when

`find_element("", kind="interactive")` on Notepad lists the editor; the set
matches upstream's (minus `TextBox`, plus `Document`, both explained above) and is stated in the
tool description; A-2 has one named constant to take over.
