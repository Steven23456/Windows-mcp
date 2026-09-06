# B-1 — `type`: target, clear, caret, press_enter, and a paste path for long text

**Checklist item:** [B-1](../upstream-parity-checklist.md#b-1--type-target-clear-caret-press_enter-long-text-paste--p1--m) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 2, second item — it builds the `TypePlanner` (C8) ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, desktop tests green on a
quiet desktop — see CHANGELOG [Unreleased]) ·
**Effort:** ~3 h including the RED/GREEN passes and the per-key pacing the desktop forced.

## Problem

`type(text)` was one `TextEntry` into whatever had focus: no way to aim it, to replace a
field's content, to submit, or to move the caret first, so filling one field was five calls
(`click`, `shortcut("ctrl+a")`, `key("backspace")`, `type`, `key("enter")`). Long text went in
as one keystroke burst, which drops or garbles characters in apps that fall behind the input
queue. Upstream clicks the target, clears with Ctrl+A/Backspace, types short text per key with
pacing, and pastes long plain text through the clipboard, restoring what was there.

## Decision

- **A pure planner** (`TypePlanner.Plan(text, TypeOptions)`, C8) turns the request into an
  ordered list of steps — `shortcut`, `key`, `text`, `paste` — and names the method. `Clear` is
  `ctrl+a` then `backspace` first; `Caret` `start`/`end` is `ctrl+home`/`ctrl+end` next (a chord,
  because `PressKeyAsync` resolves one token); then the text; `PressEnter` last. **Paste** when
  the text is 200 characters or longer and contains no control character other than `\n`/`\t`
  (a CR counts as a control character, so CRLF text is typed); otherwise **keys**, with every
  LF, CR or CRLF as one Enter and every tab as Tab between literal chunks. `PaceMs` must be 0 or
  more. The threshold sits at exactly 200 and is pinned there.
- **An executor behind a seam.** `InputService.TypeAsync(text, options)` runs the plan against
  an `IKeyboardSink` — the simulator in production, a recorder in the unit tests, which is what
  makes the order of keystrokes provable without injecting input (C10). `PressKeyAsync` and
  `PressShortcutAsync` go through the same sink. The paste path borrows `IClipboardService`
  (an optional constructor parameter; every existing `new InputService()` keeps compiling and
  cannot paste, so it types): read the previous text, set the text, `ctrl+v`, wait for the
  target to take it, put the previous text back. `TypeResult` now says `Method` (`keys` or
  `paste`) and `ClipboardRestored` (true; false when the clipboard held no text or the restore
  failed; null when nothing was pasted). A clipboard that cannot be set (another app holds it)
  falls back to keys for that text and reports `keys` — the response tells the truth about
  which path ran.
- **Per-key pacing is real.** The desktop showed why upstream paces: with the whole chunk in
  one `SendInput` call, a Notepad that falls behind reads the last injected character for every
  queued key, and "abc" arrives as "c". The simulator sink therefore sends one character per
  call with `PaceMs` between them (default 5 ms); the pace also separates plan steps. 199
  characters cost about a second; 200 go by paste in one keystroke.
- **The tool.** `type(text, x?, y?, element_id?, clear, caret, press_enter, pace_ms)`: with a
  target, a physical click there first (C1), then the plan; with none, the focus as before.
  `caret` is parsed case-insensitively and an unknown value lists the three. Response
  `{typed, method, clipboardRestored?, x?, y?, elementId?, name?}`. The old single-argument
  `TypeAsync(text)` now means the options default — so `type(text)` and `interact_element(type)`'s
  keyboard fallback split newlines into Enter presses instead of one raw entry. That is C8's
  intent and a behaviour change; CHANGELOG carries it.

## Changes

- `Abstractions`: `CaretPosition`, `TypeOptions`; `TypeResult +Method, +ClipboardRestored`;
  `IInputService.TypeAsync(text, options)`.
- `Services/TypePlanner.cs`, `Services/IKeyboardSink.cs` (new); `Services/InputService.cs`
  (`SimulatorKeyboardSink`, the executor, `PasteAsync`, the clipboard ctor parameter).
- `Tools/InputTools.cs` — `Type` re-signed and re-described.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Planner: defaults; short text one chunk; empty text nothing; newline/tab split, CRLF and lone CR as one Enter, no empty chunk; the threshold at exactly 200; paste verbatim; any other control character forces keys; non-ASCII pastes; clear first, caret next, text, enter last; a negative pace refused by name | `TypePlannerTests` (20+ methods) | Unit |
| R2 | Executor on a recorder: the plan in order; keys never touch the clipboard; the caret chord resolvable; paste = get → set → ctrl+v → restore with `ClipboardRestored:true`; a non-text clipboard → false and no restore; a clipboard that cannot be set → keys, no ctrl+v; no clipboard service → keys; clear/enter wrap the paste; pace honoured; cancellation before the first key; the single-argument overload = defaults; both overloads on the interface; one public ctor with an optional clipboard | `InputServiceTypeTests` (16 methods) | Unit |
| R3 | Tool: no target types at the focus and never clicks; method/clipboard flag reported and omitted when null; a target is clicked first, by point or by id; options forwarded verbatim; caret parsing; negative pace refused; empty text still reaches the service; `text` first; description; schema | `InputToolsTypeTests` (16 methods), `HttpTransportTests` (1) | Unit / Integration |
| R4 | Notepad: `clear` replaces the content; `press_enter` leaves the caret on a new line; `caret:"end"` appends; 5 000 characters arrive intact by paste and the clipboard is given back | `InputToolsDesktopTests` (4) | UIAutomation |

## Deviations and follow-ups

- **Long text with CRLF is typed, not pasted**, by the literal reading of C8. Normalising CRLF
  before the decision would let it paste; not done, so the pasted payload never carries a CR.
- **The clipboard restore waits 150 ms** after Ctrl+V on the real desktop (the target reads the
  clipboard on its own schedule); the unit tests run the recorder without the wait.
- The desktop's typing corruption under load is the reason for per-key pacing, not something
  the pacing proves absent: a very busy target can still fall behind. The paste path is the
  robust one for anything long.
