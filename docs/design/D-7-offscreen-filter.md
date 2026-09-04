# D-7 — `find_element` / `wait_for`: drop off-screen elements by default

**Checklist item:** [D-7](../upstream-parity-checklist.md#d-7--find_element-and-wait_for-return-off-screen-elements-by-default--p2--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green — see CHANGELOG [Unreleased]) ·
**Order:** last of the find-path trio — the filter belongs inside D-5's `TryEvaluate`, and D-6
decides which control types reach it. Effort: ~3 hours.

## Problem

`FindElementAsync` has no visibility filter at all. `ToInfo` reports `IsOffscreen` and leaves the
judgement to the caller. Measured 2026-09-04: **18 of 21** `kind=text` hits on a normal desktop were
`IsOffscreen: true` — collapsed panes, virtualised list rows, minimised windows.

The count is not the real problem. `Take(20)` runs **before** any filtering a caller could do, so
off-screen hits **crowd out** on-screen ones: an on-screen match can simply be absent from the
twenty returned, and no amount of client-side filtering gets it back. And `wait_for` inherits the
same walk, so it can "find" an element that has not been shown yet and return early — the wait
succeeds while the UI is still not ready.

Upstream never has this problem: `tree/service.py` `tree_traversal` computes
`is_visible = area > 0 and not is_offscreen` and off-screen nodes never reach the output. The
[A-2](../upstream-parity-checklist.md#a-2--desktop-wide-labeled-interactive-element-snapshot) sketch
already says "off-screen dropped"; this item is that rule applied to the tools that exist today.

**Not the same thing as negative coordinates.** A monitor to the left of or above the primary has
negative bounds and is perfectly on-screen (see [D-3](D-3-cursor-virtual-desktop.md)). The filter is
`IsOffscreen` plus non-empty bounds, never a sign test on `X`/`Y`.

## Decision

### The filter

Inside D-5's `TryEvaluate`. **As landed it runs *after* the text match, not before** — the plan had
this backwards: the name test is one property read and the visibility test is two, so when the
caller passed text the cheaper test should narrow first. With empty text the order makes no
difference, since visibility is then the only filter:

```csharp
private static bool IsVisibleEnough(AutomationElement el)
{
    var b = TryGetBounds(el);
    if (b is null || b.Width <= 0 || b.Height <= 0) return false;   // upstream's `area > 0`
    if (!TryGetIsOffscreen(el)) return true;
    // Chromium/WebView2 and some XAML providers report IsOffscreen on edit fields that are
    // scrolled in a container but still the correct target for `type`. Upstream keeps the same
    // exception (EditControl, and browser ListItemControl). A real rectangle is the guard.
    return TryGetControlType(el) == nameof(ControlType.Edit);
}
```

Two calls that the checklist left open, decided:

- **Adopt upstream's `Edit` exception.** An edit control with a real rectangle that reports
  off-screen is still the right target for `type` — that is precisely the case that browsers get
  wrong — and edit fields are the highest-value find result there is. The non-empty-bounds guard
  keeps it from readmitting genuinely destroyed controls. It is stated in the tool description, not
  left as a surprise.
- **Do *not* extend the exception to `Document` or to browser `ListItem`.** Upstream's ListItem
  carve-out exists to keep browser result lists in its whole-page snapshot; `find_element` is a
  targeted search and a list row nobody can see is not a target. Narrower is easier to explain, and
  `include_offscreen:true` covers anyone who disagrees.

### The parameter

`include_offscreen` (`bool`, default `false`) on `find_element` **and** `wait_for`, threaded to
`FindElementAsync` / `WaitForAsync` beside D-5's `scope`:

```csharp
Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any,
    FindScope scope = FindScope.Foreground, string? windowTitle = null,
    bool includeOffscreen = false, CancellationToken ct = default);
```

`true` restores today's behaviour exactly (no visibility test at all, including the empty-bounds
test — "give me everything" means everything).

### The cap

`MaxMatches` (20) applies **after** kind, visibility and text — that is the half of this item that
actually changes which elements a caller can reach. D-5 already structures the walker to
short-circuit once the cap is reached, so the filter costs nothing extra: it runs on candidates the
provider has already narrowed by control type.

`IsOffscreen` stays on `ElementInfo` and `get_element` is untouched — asking about a specific
element is a different question from searching for one.

## Plan

1. `Abstractions/IUIAutomationService.cs` — `includeOffscreen` on `FindElementAsync` and
   `WaitForAsync`, after `scope`, before `ct`.
2. `Services/UIAutomationService.cs` — `IsVisibleEnough`; call it from `TryEvaluate` unless
   `includeOffscreen`; thread the flag through `FindOnSta` and `WaitForAsync`.
3. `Tools/UIAutomationTools.cs` — `include_offscreen` on both tools; descriptions state the default,
   the `Edit` exception, and that the 20-result cap is applied after filtering.

## Tests

**Unit** (`Category=Unit`, Moq): both tools forward `include_offscreen` (default `false`, and `true`
when passed).

**UIAutomation category** (Notepad fixture):
- `FindElementAsync("", FindKind.Text)` returns only `IsOffscreen == false` results, *or* `Edit`
  controls with non-empty bounds — asserted as that exact disjunction so the documented exception is
  the only way an off-screen element can appear;
- the same call with `includeOffscreen: true` returns at least as many results;
- with a menu open (so the desktop holds a fresh crop of off-screen elements), the default call
  still returns the Notepad editor — the crowding-out regression.

## Docs / CHANGELOG

- `CHANGELOG.md [Unreleased] → Fixed` — a behaviour change; say so, and say `include_offscreen:true`
  restores the old results.
- `docs/architecture/COMPONENTS.md` l.135 / l.141 (signature rows) and l.454 (the
  `FindElementAsync` bullet).
- `skills/windows/SKILL.md` §4 — results are on-screen elements only; `include_offscreen:true` is
  for diagnosing why something is missing.

## Done when

Default results contain no `IsOffscreen: true` element other than an `Edit` with real bounds; the
20-result cap applies after the filter, so an on-screen match is never crowded out by off-screen
ones; `include_offscreen:true` restores today's behaviour; `wait_for` no longer returns on an
element that has not been shown.
