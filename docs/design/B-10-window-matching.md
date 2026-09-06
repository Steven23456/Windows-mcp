# B-10 — fuzzy window matching and a bring-to-foreground that works from the background

**Checklist item:** [B-10](../upstream-parity-checklist.md#b-10--fuzzy-window-matching-and-robust-bring-to-foreground--p1--m) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 1, second item — it builds the `WindowMatcher`
and `FuzzyMatch` that B-8, B-9 and B-6 reuse (C5, C6) ·
**Status:** implemented 2026-09-05 (build clean, headless suite green, the B-10 desktop tests
green — see CHANGELOG [Unreleased]) ·
**Effort:** ~3 h including the RED/GREEN passes and the Notepad-fixture repair they forced.

## Problem

`switch_to_window` and `focus` did `FindWindow(null, exactTitle)` and one bare
`SetForegroundWindow`. Two failures on every real desktop: a title has to be exact
(`"notepad"` never found `"Untitled - Notepad"`), and Windows refuses `SetForegroundWindow` to a
process that is not itself in the foreground — the normal state of an MCP server — so the call
returned `false` for a window that exists. `window(action:…)` had the same exact-title lookup.
Upstream fuzzy-matches over its window list and climbs a ladder of foreground tricks.

## Decision

- **`FuzzyMatch`, in-repo and package-free** (C6): `Ratio` = `round(200·LCS/(|a|+|b|))` away
  from zero (the indel ratio `thefuzz` computes with python-Levenshtein), `PartialRatio` = the
  best `Ratio` of the shorter string against every same-length window of the longer, and
  `TokenSetRatio` = `thefuzz`'s token-set (lower-case, split on non-alphanumeric runs, the
  sorted intersection against each side's intersection-plus-remainder, best of three). All
  case-insensitive, symmetric, 0–100; two empty strings score 100, one empty string 0. The
  thirteen-row score table in the tests was computed from those definitions before the code
  existed — the code was written to the table, not the table to the code.
- **`WindowMatcher`, pure** (C5): an `hwnd` wins over a title and never fuzzes (not in the
  inventory → `KeyNotFoundException` naming it in decimal and hex); a title is matched exact
  (ordinal, ignoring case) → substring → fuzzy with `max(PartialRatio, TokenSetRatio) ≥ 70`;
  ties inside one strategy go to the lowest `ZOrder`, the frontmost; minimised windows are
  candidates; neither argument is an `ArgumentException`; nothing matched is a
  `KeyNotFoundException` in A-2's wording — the open titles, at most fifteen — plus the nearest
  fuzzy candidate and its score, so a miss is diagnosable. `UIAutomationService.MatchWindows`
  (the snapshot's exact-then-substring) is deliberately **not** replaced: a walk must not fuzz.
- **The ladder, behind a seam** (C11): `ForegroundLadder.Bring(match, IForegroundNative)` —
  restore first when `IsIconic` (reported as `Restored`), then `SetForegroundWindow`, then
  `AttachThreadInput` to the window's thread + `BringWindowToTop` + `SetForegroundWindow` +
  detach (attached once, detached once, whatever happens; a refused attach — an elevated
  target — skips the rung entirely), then the ALT nudge (`keybd_event(VK_MENU)` down/up) +
  `SetForegroundWindow`. `GetForegroundWindow` is re-read after every rung and is the **only**
  source of `Success`; user32's `SetForegroundWindow` return value is never consulted, because
  it says "request accepted", not "window in front". `Strategy` names the rung that worked, or
  is null. `IForegroundNative` is the seven user32 calls; `Win32ForegroundNative` is the only
  caller of user32, and a recording fake drives the ladder in the unit tests.
- **Surface.** `IWindowService.SwitchToAsync(title) → bool` is gone; `BringToFrontAsync(title?,
  hwnd?) → ForegroundResult(Window, MatchStrategy, Score, Restored, Strategy, Success)` replaces
  it, and `ExecuteAsync(action, title?, hwnd?)` resolves through the same matcher and returns the
  matched window's `Title`, `Hwnd`, `MatchStrategy` and `Score` on the `WindowAction`. The action
  is validated before the inventory is read. `switch_to_window` and `focus` return the whole
  result as JSON; `window` gains `hwnd`. **Behaviour change:** a title that matches nothing is a
  `KeyNotFoundException` listing the open windows, on all three tools, where `window(action:…)`
  used to answer `Success:false` and the other two a "not found" string.

## Changes

- `Abstractions`: `ForegroundResult` (new); `WindowAction +MatchStrategy, +Score, +Hwnd`;
  `IWindowService` — `BringToFrontAsync` in, `SwitchToAsync` out, `ExecuteAsync +hwnd`.
- `Services/FuzzyMatch.cs`, `WindowMatcher.cs`, `ForegroundLadder.cs` (+ `IForegroundNative`),
  `Win32ForegroundNative.cs` (new); `Services/WindowService.cs` (internal ctor seam, the two
  members); `NativeMethods.txt` (`AttachThreadInput`, `BringWindowToTop`, `GetCurrentThreadId`,
  `keybd_event`, `VIRTUAL_KEY`).
- `Tools/WindowTools.cs` — `switch_to_window`/`focus` re-signed, `window` gains `hwnd`, the
  descriptions say exact → substring → fuzzy and name the `Strategy` field.
- Tests: `NotepadFixture` now tracks the window it opened and closes **that** window, because
  modern Notepad is one process hosting every window and the launched process exits after
  handing its window over (the desktop had twelve leftover fixture windows before this).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | The three scorers against the thirteen-row table, symmetry, full marks for equality, 0–100, the empty-string rules, punctuation as a token separator, subset → 100, Unicode scored not stripped | `FuzzyMatchTests` (10 methods, 60+ cases) | Unit |
| R2 | hwnd wins and never fuzzes, unknown hwnd named; exact > substring > fuzzy, case-insensitive, score 100 for the first two, the better of partial/token-set, the highest fuzzy score wins over a frontmost weaker one, below 70 is no match; ties by z-order; minimised candidates; neither argument; the no-match message (A-2 wording, ≤ 15 titles, nearest candidate and score, `(none with a title)`) | `WindowMatcherTests` (25) | Unit |
| R3 | The ladder on a recording fake: rung 1 stops the ladder; rung 2 attaches, raises, sets, detaches exactly once; a refused attach skips to the nudge with no detach; the nudge is named; all refused → `Success:false`, `Strategy:null`, three attempts and three re-reads; user32's return value never trusted; `Restored` iff `IsIconic`, restore first, independent of the outcome; the matcher's verdict passed through | `ForegroundLadderTests` (12) | Unit |
| R4 | Real service: the foreground window by hwnd and by title reports success at the first rung with a live inventory entry; no target, a stale hwnd and a nonsense title refuse as specified; the old not-found tests rewritten to the exception contract | `WindowServiceForegroundTests` (6), `WindowServiceTests` (4 rewritten) | Integration |
| R5 | Tools: `switch_to_window`/`focus` serialise every field, accept hwnd alone, refuse neither, report a refused change as data, describe the ladder; `window` acts on an hwnd, names both in its refusal, reports the matched window, advertises `hwnd` | `WindowToolsTests` (20+) | Unit |
| R6 | Notepad parked behind another window comes forward by title and by hwnd; a minimised Notepad is restored; the tool layer agrees; `window(action:"close", title:"notepad")` closes the matched window | `WindowForegroundDesktopTests` (4), `WindowCloseDesktopTests` (1) | UIAutomation |

Coverage and bite check: `FuzzyMatch`, `WindowMatcher`, `ForegroundLadder` 100 % line;
`Win32ForegroundNative` desktop-only. Breaks caught: truncated rounding, threshold 69, ties by
highest z-order, the ladder trusting `SetForegroundWindow`'s return, the detach skipped,
`ExecuteAsync` reporting the requested title instead of the matched one.

## Deviations and follow-ups

- **Many windows, one title.** With several `Untitled - Notepad` windows open, the title path
  picks the frontmost; that is the documented rule, and `hwnd` from `window list` is the precise
  form. The desktop tests target the fixture's own window by handle for that reason.
- **`AttachThreadInput` to an elevated target** is refused by Windows and the ladder skips to the
  nudge; whether the nudge lifts the lock on a given build is reported, not assumed.
- **`PartialRatio` rewards a one-character title.** A window titled `"a"` scores 100 against
  `"notepad"` (the letter is a window of the longer string), so on a desktop where nothing
  matches exactly or by substring the fuzzy rung can pick it. That is `thefuzz`'s documented
  `partial_ratio` behaviour and C6 says the scorers are `thefuzz`'s, so it is pinned by a test
  that must be rewritten deliberately if a minimum-length guard is ever added.
- **`ExecuteAsync` still reports `Success:true` unconditionally**: `ShowWindow` and
  `PostMessage` return values are discarded, so a window that dies between the enumeration and
  the post is reported as acted on. The ladder's re-read rule (C11) was not extended to the
  actions; tightening it later will not fight the tests, which pin success only where the
  action really happened.
- **`Restored` means "SW_RESTORE was sent"**, not "the window is no longer minimised";
  `IForegroundNative.Restore`'s return is not consulted.
- The roadmap's `AllowSetForegroundWindow(-1)` step was not added: it only helps when *our*
  process already holds the foreground, which is the case the first rung covers.
