# D-5 — `find_element` / `wait_for`: survive stale elements, scope the walk, retry the wait

**Checklist item:** [D-5](../upstream-parity-checklist.md#d-5--find_elementkindany-and-wait_for-fail-on-the-first-stale-element--p1--s) ·
**Status:** implemented 2026-09-04 (build clean, 438/438 tests green incl. the live UIAutomation set — see CHANGELOG [Unreleased]) ·
**Order:** first of the find-path trio. D-5 rebuilds the walker; [D-6](D-6-interactive-control-types.md)
swaps the control-type set it consumes and [D-7](D-7-offscreen-filter.md) adds a filter inside it,
so both are small once this lands and neither is worth doing first. Reuses `IsElementGone` from
[D-4](D-4-assert-element-states.md). Effort: ~1 day including the UIAutomation-category tests.

## Problem

`Services/UIAutomationService.FindElementAsync` is one LINQ chain over
`_automation.GetDesktop().FindAllDescendants()`:

```csharp
var all = root.FindAllDescendants();
var matches = all
    .Where(el => MatchesKind(el, kind))
    .Where(el => string.IsNullOrEmpty(text) || (el.Name?.Contains(text, ...) ?? false))
    .Take(20).Select(ToInfo).ToArray();
```

Four defects in five lines:

- **Every property read is unguarded.** `el.Name`, `el.ControlType` (inside `MatchesKind`),
  `el.Patterns.Scroll.IsSupported` and `ToInfo`'s `el.BoundingRectangle` all read straight through
  to the provider. A desktop always contains elements that are about to die — a tooltip fading, a
  menu closing, a virtualised list row scrolling out — and any one of them raises
  `UIA_E_ELEMENTNOTAVAILABLE` **between the walk and the read**, which fails the whole call.
  Observed 2026-09-04 on two machines: `find_element("zzqxv", kind="any")` → "An error occurred
  invoking 'find_element'", while `kind="text"` on the same desktop returned `{"Matches":[]}`.
  `kind=any` is worst because `MatchesKind` short-circuits to `true`, so `el.Name` is read on
  *every* element until 20 match; `text` / `interactive` narrow on `ControlType` first and usually
  get lucky, but that read is just as unguarded.
- **The walk is the whole desktop, always.** One cross-process `FindAll` materialises every element
  of every process, with no `CacheRequest` and no cap (`Take(20)` runs *after* the walk). It holds
  the single STA worker for its whole duration, so every other UIA call queues behind it.
- **`wait_for` has no retry.** `WaitForAsync` hard-codes `FindKind.Any`, calls `FindElementAsync`
  with no `try`, and lets the first transient failure end the wait — the one thing a wait exists to
  absorb. It also repeats the whole-desktop walk every `interval_ms`, and its
  `while (UtcNow < deadline)` never polls at all when `timeout_ms` is 0.
- **`wait_for` cannot express what to wait for.** No `kind`, so waiting for a button by a name that
  also appears in a tooltip is a coin flip.

Upstream (`tree/service.py`) has none of these: it walks per window, active window first, reads
through a `CacheRequest` (`tree/cache_utils.py`) and stops at the `TreeElementBudget`. A dead
element costs one node, never the call.

## Decision

**One rule: a single element may never fail the call.** Plus three structural changes that follow
from it.

### 1. Guarded reads, per-element and per-root

Every read on the find path goes through a `TryGet*` helper (the existing `TryGetName`,
`TryGetControlType`, `TryGetIsOffscreen` plus new `TryGetBounds` and `TryIsScrollable`), and the
whole per-element evaluation sits in a `catch`:

```csharp
private ElementInfo? TryEvaluate(AutomationElement el, string text, FindKind kind)
{
    try
    {
        if (kind == FindKind.Scrollable && !TryIsScrollable(el)) return null;
        var name = TryGetName(el);
        if (text.Length > 0 && !name.Contains(text, StringComparison.OrdinalIgnoreCase)) return null;
        return ToInfo(el);          // ToInfo becomes fully guarded — TryGetBounds
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
}
```

The catch is deliberately **broad**. `IsElementGone` (D-4) names the failures we expect —
`ElementNotAvailableException`, `UIA_E_ELEMENTNOTAVAILABLE`, the RPC failures — but a provider can
also throw `PropertyNotSupportedException`, `PatternNotSupportedException`, or a bare `COMException`
we have not seen, and none of those is worth failing a search over. `IsElementGone` is still used,
for observability: the per-root catch logs at Debug when a failure is **not** a recognised
stale-element failure, so a genuinely new failure mode shows up on stderr instead of vanishing.
That needs an `ILogger<UIAutomationService>? log = null` ctor parameter (optional, so the existing
`new UIAutomationService(new InputService())` in tests keeps compiling; DI supplies the real one).

**No ProcessId liveness probe on this path.** D-4 probes `Properties.ProcessId <= 0` because it
resolves an id issued minutes ago, and the Win32 HWND proxy answers for a dead window with defaults
rather than an exception. A walk returns elements the provider *just* enumerated, so the probe would
buy little and cost one extra cross-process read per element on a walk of thousands. The exception
net plus guarded reads carry this path; if a dead Win32 proxy does leak through it yields a
`ControlType Pane` with an empty name, which the text filter drops anyway.

### 2. `scope`: foreground, a named window, or the whole desktop

New `FindScope { Foreground, Window, Desktop }` (in `Models/UIAutomationDtos.cs`, beside
`FindKind`), default `Foreground`, plus a `windowTitle` that is **only** valid with
`FindScope.Window`.

- `Foreground` → `GetForegroundRoot()`, the root `get_state` already uses: the window the agent is
  acting on. One window's subtree, not every process on the machine.
- `Window` → the top-level window whose name matches `windowTitle`. **Deterministic**: it does not
  matter what stole focus between two calls.
- `Desktop` → the desktop's **top-level children, one at a time**, each in its own try/catch, so a
  window closing mid-walk drops that window from the results instead of killing the search. This is
  what the tool does implicitly today.

```csharp
private IEnumerable<AutomationElement> RootsFor(FindScope scope, string? windowTitle)
{
    if (scope == FindScope.Foreground) { yield return GetForegroundRoot(); yield break; }

    AutomationElement[] windows;
    try { windows = _automation.GetDesktop().FindAllChildren(); } catch { yield break; }

    if (scope == FindScope.Desktop) { foreach (var w in windows) yield return w; yield break; }

    // FindScope.Window: exact name first, then case-insensitive substring — titles carry volatile
    // decoration ("Untitled - Notepad" gains a leading '*' after one keystroke).
    var named = windows.Where(w => string.Equals(TryGetName(w), windowTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (named.Length == 0)
        named = windows.Where(w => TryGetName(w).Contains(windowTitle!, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (named.Length == 0)
        throw new KeyNotFoundException(
            $"No top-level window matching '{windowTitle}'. Open windows: {string.Join(", ", windows.Select(w => $"'{TryGetName(w)}'").Where(n => n != "''").Take(15))}");
    foreach (var w in named) yield return w;
}
```

Three decisions inside that:

- **Why not reuse `WindowService`.** `WindowService` addresses windows with
  `PInvoke.FindWindow(null, title)` — an **exact, whole-title** match, and it hands back an `HWND`
  we would then have to convert. The desktop's UIA children are already enumerated here for
  `Desktop` scope, so matching on the element's own `Name` costs nothing extra, gives substring
  matching for free, and keeps the find path off Win32 entirely.
  [B-10](../upstream-parity-checklist.md#b-10--fuzzy-window-matching-and-robust-bring-to-foreground)
  will add real fuzzy matching (score ≥ 70) and an `hwnd` accepted for precision; when it lands it
  replaces the substring step here and this method is the single place to change. Until then,
  **a not-found window lists the open window titles in the error** so the caller can retry with one
  that exists — the thing an agent cannot otherwise discover, because no tool returns a window list
  ([A-1](../upstream-parity-checklist.md#a-1--whole-desktop-window-inventory) is not started).
- **Ambiguity yields all matches, not an error.** Two windows can legitimately share a title (two
  Explorer windows, two documents). Searching both and returning up to `MaxMatches` hits is more
  useful than refusing; the caller narrows by passing a longer title.
- **`windowTitle` without `scope="window"` is an error**, not a silently ignored argument — the same
  rule D-4 applies to `expected`, for the same reason ("silently ignoring an argument is how the D-2
  `select` bug happened").

**Why `Foreground` stays the default.** Requiring a window on every call is friction on the common
case: the agent has just run `launch` or `switch_to_window` and is acting on what is in front of it.
But the tool description must say the default is *whatever is foreground at call time* and point at
`scope:"window"` for anything multi-step, so the raciness is stated rather than discovered.

This is still a **behaviour change** — today's implicit scope is the whole desktop — and must be
called out in the CHANGELOG and the tool description, not slipped in.

**Overlap with B-6.** [B-6](../upstream-parity-checklist.md#b-6--wait_for-conditions-and-window-filter)
covers "`wait_for` conditions **and window filter**". This item delivers the window filter for both
`find_element` and `wait_for`; B-6 keeps the `condition` enum and `use_dom` and drops the filter.
Cross-referenced in the checklist so it is not built twice.

### 3. Push `kind` into the UIA condition

`FindAllDescendants(Func<ConditionFactory, ConditionBase>)` lets the provider do the control-type
filtering, so far fewer elements are marshalled across the process boundary:

| kind | condition |
|---|---|
| `Text` | `new OrCondition(cf.ByControlType(Text), cf.ByControlType(Edit), cf.ByControlType(Document))` |
| `Interactive` | `new OrCondition(InteractiveControlTypes.Select(cf.ByControlType))` — the set D-6 defines |
| `Any`, `Scrollable` | no condition (`FindAllDescendants()`); `Scrollable` is a pattern test, not a property, and stays client-side in `TryEvaluate` |

(Verified against FlaUI 5.0.0: `AutomationElement.FindAllDescendants(Func<ConditionFactory,
ConditionBase>)` and `OrCondition(IEnumerable<ConditionBase>)` both exist.)

`Name` still has to be matched client-side: UIA property conditions are exact-match and
`find_element` is documented as a *contains* search. `MatchesKind` shrinks to the condition builder
plus the `Scrollable` branch.

### 4. `wait_for` forwards everything and retries across polls

New signature, and a pure loop that is unit-testable without UIA:

```csharp
Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs,
    FindKind kind = FindKind.Any, FindScope scope = FindScope.Foreground,
    string? windowTitle = null, CancellationToken ct = default);

internal static async Task<ElementInfo?> PollAsync(
    Func<CancellationToken, Task<ElementInfo?>> poll, int timeoutMs, int intervalMs, CancellationToken ct);
```

`scope:"window"` matters most here: a `wait_for` runs for seconds while the UI is changing, which is
exactly when focus moves. The window is resolved **on every poll**, not once — a window that has not
appeared yet is a failed poll that gets retried, so `wait_for(scope:"window", window:"Notepad")` is
also a usable "wait for that app to open".

`PollAsync` rules: poll **at least once** (so `timeout_ms:0` means "check now", not "do nothing");
a poll that throws is recorded and retried, never fatal; the sleep is clamped to the remaining
budget so the call cannot overshoot the deadline by up to `interval_ms`;
`OperationCanceledException` propagates. On the deadline:

- at least one poll ran cleanly and found nothing → **return `null`** (today's contract; the tool
  still answers `"null"`);
- **every** poll threw → **throw** `TimeoutException("wait_for: every poll failed within {n} ms;
  last error: {message}", last)`. Silently answering "not found" when we never actually managed to
  look is the failure mode this whole item is about.

### 5. One thing the plan missed, found by the tests

Pushing `kind` into the UIA condition filters **descendants only** — the walk's own roots are not
matched by it. The first implementation evaluated each root unconditionally, so with
`scope=desktop, kind=interactive` every top-level window `Pane` (the taskbar first) counted as a
match and filled the 20-result cap before any real control was reached. Caught by
`FindElementAsync_interactive_finds_the_editor`. Fixed with `RootMatchesKind`, a small client-side
twin of `KindCondition` applied to roots only, and pinned by
`FindElementAsync_interactive_never_returns_a_window_root_pane`.

### 6. Shaped for A-4

The walker is one method per root (`CollectFrom(root, …)`) and the cap is a named
`MaxMatches = 20`, so [A-4](../upstream-parity-checklist.md#a-4--element-budget-truncation-note-uia-caching)
can wrap it with a `CacheRequest` and a real element budget without another rewrite. `CacheRequest`
and the budget stay **out** of this item.

## Plan

1. `Models/UIAutomationDtos.cs` — add `public enum FindScope { Foreground, Window, Desktop }`.
2. `Abstractions/IUIAutomationService.cs` — add `scope` and `windowTitle` to `FindElementAsync`; add
   `kind`, `scope` and `windowTitle` to `WaitForAsync`. All after the existing parameters and before
   `ct`, with defaults, so no caller breaks. (D-7 inserts `includeOffscreen` into the same block.)
3. `Services/UIAutomationService.cs` —
   - add `TryGetBounds`, `TryIsScrollable`; make `ToInfo` use `TryGetBounds`;
   - add the optional `ILogger<UIAutomationService>? log = null` ctor parameter;
   - replace the `FindElementAsync` LINQ chain with `FindOnSta` → `RootsFor` → `CollectFrom` →
     `TryEvaluate`, capped at `MaxMatches`;
   - replace `MatchesKind` with `KindCondition(ConditionFactory, FindKind)` plus the `Scrollable`
     branch inside `TryEvaluate`;
   - rewrite `WaitForAsync` over `PollAsync`, resolving the window root on every poll.
4. `Tools/UIAutomationTools.cs` — `ParseScope`; `scope` + `window` on `find_element` and `wait_for`;
   `kind` on `wait_for`; validation that `window` is present iff `scope="window"`; descriptions that
   state the default scope and that it is resolved at call time.
5. Docs, per checklist rule 3 (below).

## Tests

**Unit** (`tests/.../Tools/UIAutomationToolsTests.cs`, `Category=Unit`, Moq):
- `find_element` forwards `scope:"desktop"` → `FindScope.Desktop`; an unknown scope →
  `ArgumentException` naming the token (mirrors the existing
  `FindElement_rejects_unknown_kind_with_clear_message`).
- `find_element` forwards `scope:"window"` + `window:"Notepad"` as `(FindScope.Window, "Notepad")`.
- `scope:"window"` without `window` → `ArgumentException`; `window` with any other scope →
  `ArgumentException` (the D-4 `expected` precedent).
- `wait_for` forwards `kind`, `scope` and `window`.

**Unit** (`UIAutomationServiceUnitTests` — no desktop needed) against `PollAsync` with a fake poll
delegate:
- a poll that throws twice then returns a hit → returns the hit (the D-5 headline);
- every poll throws → `TimeoutException` whose message carries the last error;
- clean polls, no hit → `null`;
- `timeoutMs: 0` → polls exactly once;
- a hit on the first poll → returns without sleeping.

**UIAutomation category** (Notepad fixture):
- `FindElementAsync("", FindKind.Any)` ten times in a row, in **all three** scopes, without
  throwing — the regression this item was filed for;
- `FindElementAsync("", Any, Foreground)` returns only elements from the Notepad window;
- `FindElementAsync("", Any, Window, "Notepad")` returns the same elements as `Foreground` while
  Notepad is in front — the substring match resolves `"Notepad"` against `"Untitled - Notepad"`;
- an unmatched `windowTitle` throws `KeyNotFoundException` whose message lists open window titles;
- `WaitForAsync` for text typed into the document after ~500 ms returns it.

Also **delete** the D-5 workaround in `UIAutomationServiceTests.FindNotepadDocumentIdAsync` — the
`catch (COMException) when (attempt < 5)` retry loop added because this defect broke unrelated
tests. Removing it is part of the acceptance.

## Docs / CHANGELOG

- `CHANGELOG.md [Unreleased] → Fixed` — the stale-element fix, with the default-scope change called
  out explicitly as a behaviour change.
- `docs/architecture/COMPONENTS.md` — the `FindElement` / `WaitFor` signature rows (l.135, l.141),
  the `IUIAutomationService` row (l.376), the `UIAutomationDtos.cs` row (l.416, add `FindScope`),
  and the `FindElementAsync` / `WaitForAsync` bullets (l.454–456).
- `docs/architecture/DATAFLOW.md` — the WaitFor polling diagram (l.305–330) gains the per-poll
  catch; the summary-table row (l.522) gains "a failed poll is retried".
- `skills/windows/SKILL.md` §4 — `find_element` searches the **foreground window** by default;
  `scope:"desktop"` is the opt-in for cross-window searches.

## Done when

`find_element(kind=any)` and `wait_for` return on a busy desktop; a stale element is skipped, not
fatal; the default scope is the foreground window, with `scope=desktop` and `scope=window` opt-in;
a workflow can pin its searches to one window by title and is unaffected by focus moving; an
unmatched window title names the open windows; the STA worker is not held for a whole-desktop walk
unless asked; `wait_for` accepts `kind`, retries across polls, and distinguishes "looked and did not
find it" from "never managed to look".
