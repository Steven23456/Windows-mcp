# B-7 — `multi_select` and `multi_edit`

**Checklist item:** [B-7](../upstream-parity-checklist.md#b-7--multi_select--multi_edit-batch-tools--p2--sm) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 4, last item — decision C3 (two new tools, 66 → 68)
settled in section 7 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, 3/3 desktop tests green
— see CHANGELOG [Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

Selecting three files or filling four fields was three or four round-trips, each with its own
snapshot-to-click latency, and Ctrl-clicking several items was not expressible at all: no tool
could hold a modifier across calls. Upstream's `MultiSelect` holds Ctrl while clicking a list
of points or labels and `MultiEdit` clicks and types a list of `(target, text)` pairs; both
accept a JSON-stringified list because one client sends arrays that way.

## Decision

- **One parser, pure** (`BatchTargets.ParseTargets` / `ParseEntries`): a JSON array of objects,
  each `{x, y}` (virtual-desktop pixels) or `{element_id}`, and for `multi_edit` a required
  `text` with optional `clear` and `press_enter`; a JSON string holding that array is unwrapped
  once. Every refusal — both forms, neither, half a pair, a non-object item, a non-array root,
  malformed JSON, an empty array, a missing or non-string text — names the parameter and the
  entry's index, so the caller fixes one entry instead of guessing.
- **Resolve everything before touching anything** (C1): every target goes through the input
  verbs' resolver first, so an off-screen element refuses the whole batch with nothing clicked;
  argument and resolution refusals throw. Then the batch runs in order and **stops at the first
  failure** during input, returning the results so far with `failedIndex` and `error` rather
  than throwing, so the caller knows exactly how far it got; the batch is not atomic and the
  descriptions say so.
- **`multi_select(targets_json, ctrl = true)`**: Ctrl down before the first click, one left
  click per target, Ctrl up after the last — in a `finally`, so a click that throws never leaves
  the modifier down (the one failure here that would damage the user's session; the desktop
  test checks `GetAsyncKeyState` afterwards). `ctrl: false` clicks without the modifier.
  `IInputService` gains `KeyDownAsync`/`KeyUpAsync` through the keyboard sink, so the order is
  provable on the recorder.
- **`multi_edit(entries_json)`**: per entry, click the point then B-1's `TypeAsync(text,
  options)` with the entry's `clear` and `press_enter`; each result carries `typed` and `method`
  from the type path. No Ctrl.
- Neither tool is read-only or idempotent; the results are camelCase objects like the other
  verbs' (`{count, ctrl, results:[…], failedIndex?, error?}`), with `count` the size of the
  batch that was asked for.

## Changes

- `Abstractions/IInputService.cs` — `KeyDownAsync`, `KeyUpAsync`; `Services/IKeyboardSink.cs`
  — `KeyDown`, `KeyUp` (the simulator sink maps through the shortcut parser).
- `Services/BatchTargets.cs` (new, pure); `Tools/InputTools.cs` — the two tools.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `KeyDownAsync`/`KeyUpAsync` pass the key name to the sink verbatim; a hold brackets what happens between; a cancelled token sends nothing; typing never uses the hold | `InputServiceKeyHoldTests` (4), `InputServiceTypeTests` | Unit |
| R2 | The parser: points, ids, negative coordinates, options untouched, padded and stringified JSON, every refusal naming the parameter and the index; entries with text and defaults, missing or non-string text | `BatchTargetsTests` (16 methods, 30+ cases) | Unit |
| R3 | `multi_select`: Ctrl first and last with the clicks in order, `ctrl: false`, ids resolved to centres and echoed, everything resolved before any click, a malformed batch refused before input, a failing click reported with the index and Ctrl released, Ctrl released when the very first click throws | `InputToolsBatchTests` (7) | Unit |
| R4 | `multi_edit`: click then type with each entry's options in order, never Ctrl, resolve-all-first, stop at the first failing entry, an entry without text refused before input | `InputToolsBatchTests` (5) | Unit |
| R5 | Both tools: neither read-only nor idempotent, descriptions, upstream's parameter names, 68 tools with both named, the schemas and a refusal over HTTP | `InputToolsBatchTests` (4), `ToolInventoryTests` (2), `HttpTransportTests` (2) | Unit / Integration |
| R6 | Notepad: `multi_edit` runs two entries in one call with `clear` on the second; `multi_select` with Ctrl leaves no modifier stuck; Ctrl released on a mid-batch failure | `InputToolsBatchDesktopTests` (3) | UIAutomation |

## Deviations and follow-ups

- **An empty batch is refused**, not a no-op: nothing to do is more likely a wrong parameter
  than an intent.
- The two tools are separate rather than list parameters on `click`/`type`, per decision 1 in
  the roadmap's section 7.
- `multi_select` clicks with the left button only; a right-click batch has no use case yet.
