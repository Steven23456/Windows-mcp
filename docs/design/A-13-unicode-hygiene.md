# A-13 — Unicode hygiene: sanitise UI text before the model sees it

**Checklist item:** [A-13](../upstream-parity-checklist.md#a-13--unicode-hygiene--p2--s) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 1 ("anywhere; before A-1" — A-1's window titles go
through the same helper) ·
**Status:** implemented 2026-09-04 (build clean, 1041/1041 headless + the two Notepad-fixture
tests green — see CHANGELOG [Unreleased]) ·
**Effort:** ~½ day including the RED/GREEN passes.

## Problem

Element names flowed straight from UIA into `JsonSerializer`. Two things go wrong with that.
VS Code (and any icon-font UI) puts Private Use Area glyphs in names (`" Explorer"`), which
reach the model as token noise it cannot read. And a lone UTF-16 surrogate — an emoji cut in half
by a truncating control — is, **measured on .NET 10**, *not* a serialiser exception: `System.Text.Json`
silently writes U+FFFD, so the model receives a value that differs from the UI with nothing in the
response saying so. Upstream strips PUA and repairs surrogates before anything is encoded. The
checklist's open question ("throws vs. U+FFFD") is answered: U+FFFD, silently.

## Decision

- **One pure helper**, `UiText.Sanitize(string?) → string` (`Services/UiText.cs`): strips PUA
  (U+E000–U+F8FF and the supplementary planes U+F0000–U+FFFFD, U+100000–U+10FFFD), replaces a
  lone high or low surrogate with U+FFFD, drops C0 controls except tab/LF/CR and U+007F–U+009F,
  then `Trim()`s (Unicode whitespace, so an NBSP-padded name trims too). Valid pairs, ZWJ
  sequences, variation selectors, combining marks and RTL text are untouched. Null → `""`. A
  single pass with a lazily allocated `StringBuilder`: a string that needs nothing comes back as
  the same instance, which the tests pin, because it is what makes the call free on the hot path.
- **Applied at every read site that feeds a DTO**: `TryGetName`, `TryGetValue`, `GetTextAsync`,
  `GetTableAsync` (through a new `internal static BuildTable(string?[] headers, string?[][]
  cells)` so the projection is unit-testable — a grid cannot be faked headless; it also fixes a
  pre-existing null in `TableData.Headers` for columns without a header element), and
  `assert_element state=value`, which the GREEN pass caught comparing the **raw** value against
  an `expected` the model had read back sanitised from `find_element`/`get_text` — a check that
  could never pass on a VS Code element. `TryGetControlType` is an enum name and is left alone.
- **Order matters**: strip → drop → repair → trim, so a codicon followed by a space at the start
  becomes nothing, and a repaired U+FFFD at the edge survives the trim.

## Changes

- `Services/UiText.cs` (new); `Services/UIAutomationService.cs` — the four read sites,
  `BuildTable`, the `value` assertion.
- No interface or DTO change.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Every rule with exact boundaries (49 cases: null/empty, PUA bounds and planes, lone high/low in every position, valid pairs/flags/ZWJ/VS, C0/C1/DEL, tab/LF/CR, NBSP mid vs edge, trim-after-strip, preserved scripts, mixed), idempotence, output invariant, same-instance fast path | `UiTextTests` (7 methods, 203 cases) | Unit |
| R2 | Platform measurement pinned (STJ substitutes U+FFFD, does not throw); sanitised text serialises and round-trips for every row | `UiTextTests.Platform_JsonSerializer_*`, `Sanitized_text_serialises_*` (49) | Unit |
| R3 | Wiring, end to end: type a PUA glyph + an emoji into Notepad, read back through `get_state`/`find_element` names and values and through `get_text`: no PUA, emoji intact | `UIAutomationServiceTests.Element_name_and_value_carry_no_private_use_glyph_and_keep_the_emoji`, `GetTextAsync_strips_private_use_glyphs_and_keeps_the_emoji` | UIAutomation |
| R3.3 | `get_table` headers and cells sanitised, null header → `""`, shape and order kept, empty grid → empty arrays, round-trips | `UIAutomationBuildTableTests` (9) | Unit |
| R4 | `ElementInfo` / `ElementTree` / `TableData` built from sanitised text serialise without the glyph and round-trip | `UiTextTests.ElementInfo_*`, `ElementTree_*`, `BuildTable_result_round_trips_*` | Unit |

Coverage: `UiText` and `BuildTable` 100 % line and branch. Bite check: six one-line breaks
(supplementary-PUA bounds, lone-low handling, tab exemption, trim ordering, the fast path, cells
unsanitised); the fast-path break was caught by nothing until a same-instance test was added.

Two test-authoring traps worth recording. A lone surrogate **cannot** travel in `[InlineData]`:
attribute arguments are UTF-8 in metadata, so the compiler rewrites `"\uD83D"` into two U+FFFD
chars and a naïve theory asserts about the wrong thing (and passes against a no-op) — the tests
use a case table keyed by ASCII ids. And the Notepad tests originally slept 400 ms after
`TypeAsync`; on a loaded box the read raced the keystrokes and the text ended at the marker, which
looked like a dropped surrogate. They now poll `get_text` for the last typed word.

## Deviations and follow-ups

- **`GetTableAsync`'s pattern reads have no live test** — Notepad has no grid. Logged in
  `todo.md` for the e2e sweep (Explorer details view or Task Manager).
- **`assert_element state=value` sanitising has no live test either**; same `AutomationElement`
  constraint, same fixture. The unit-level rule is shared with `get_text`, and the Notepad tests
  cover that path.
- A-1's window titles and A-2's snapshot names must call `UiText.Sanitize` too; the helper is
  the contract, the design notes for those items should say so.
