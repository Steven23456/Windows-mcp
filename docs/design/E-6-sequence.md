# E-6 — `sequence`: an ordered batch of UI steps in one call

Checklist item: `docs/upstream-parity-checklist.md` § E-6. Depends on E-5 (`Observation`) and S-11
(the control indicator and its takeover record).

## What it is

A new tool, `sequence(steps_json, observe = "none", settle_ms = 250)`, that runs up to
**20** steps in order in one call and stops at the first one that does not succeed. A menu path, a
wizard page or a keyboard-driven dialog becomes one round trip instead of one per step.

```json
[
  {"tool": "shortcut", "args": {"shortcut": "ctrl+shift+s"}},
  {"tool": "wait_for", "args": {"text": "File name", "condition": "element_exists"}},
  {"tool": "type",     "args": {"text": "report.txt", "press_enter": true}}
]
```

## Steps

- `steps_json` is a JSON array of step objects, or a JSON string holding that array (the B-7
  convention). A step is `{"tool": "<name>", "args": {…}}`; `args` may be omitted (= `{}`). Any other
  property on a step is refused.
- **Allowlist** (fixed, ordinal, lower-case): `click`, `type`, `key`, `shortcut`, `scroll`, `drag`,
  `interact_element`, `wait`, `wait_for`, `assert_element`. Nothing else — no `powershell`, file,
  registry, process, window or screenshot tool, and not `sequence` itself. The refusal names the
  tool and lists the allowlist.
- `args` carries the step tool's own parameters under their wire names (`element_id`, `press_enter`,
  `timeout_ms`, …). The dispatcher binds them to the real tool method by reflection, so a step runs
  exactly the code the standalone tool runs, with the same defaults and the same refusals.
- A step may **not** carry `observe` or `settle_ms`: the observation belongs to the sequence (one,
  at the end). Both are refused by name.

### Everything checkable is checked before step 1 runs

The whole array is parsed and validated first; any of these refuses the call (`ArgumentException`)
with **nothing done**, naming the step index:

- not an array / empty / more than 20 steps / a step that is not an object;
- `tool` missing, not a string, or not on the allowlist;
- `args` not an object; an argument name the tool does not have (the message lists the ones it has);
  `observe` / `settle_ms` in a step;
- a required argument missing (`type` without `text`, `wait` without `seconds`, …);
- an argument of the wrong JSON type: a string where a number is expected, a fractional or
  out-of-`int`-range number for an integer, `null` for a parameter that is not nullable. No coercion
  ("5" is not 5).

What only the tool itself can decide (an unknown button, an expired `el_N`, `seconds` out of range)
is decided when that step runs, and is that step's failure.

## Running

Steps run strictly in order; targets are resolved per step, because each step changes the screen.
An `el_N` id is only useful for an element that survives the steps before it — the sequence takes no
snapshot between steps, so later steps normally address by coordinates, by keyboard, or wait with
`wait_for`. One exception the description must state: a `wait_for` step with `text_exists` or
`focused_element` takes a snapshot itself, which invalidates the ids of every earlier snapshot, so
an `el_N` step after it fails ("no longer available").

A string that System.Text.Json cannot materialise (an escaped lone surrogate, `"\ud800"`) in a
tool name, property name, argument name or argument value is refused like any other malformed step,
as an `ArgumentException` naming `steps_json[i]`.

The batch stops at the first of:

| stop | `stoppedBy` | `failedIndex` |
|---|---|---|
| a step throws (anything but cancellation) | `"error"` | that step |
| a `wait_for` step returns `Satisfied:false` | `"wait_for"` | that step |
| an `assert_element` step does not return `PASS` | `"assert_element"` | that step |
| the user took over (below) | `"takeover"` | `null` — no step failed |

**Cancellation.** The sequence's token is checked before every step, the first included: once the
caller has cancelled, no further step starts — whether or not that step's tool takes a token (seven
of the ten do not) — and the call ends with `OperationCanceledException`. An
`OperationCanceledException` from a step **while the caller's token is not cancelled** (a timeout
inside a service) is an ordinary step failure: `stoppedBy:"error"`, masked like any unexpected
fault, with the results so far kept. A step already in progress whose tool takes no token runs to
its end first (D-40 family).

### Result

```json
{"count": 3, "completed": 2,
 "results": [
   {"index": 0, "tool": "shortcut", "ok": true,  "result": {…}},
   {"index": 1, "tool": "wait_for", "ok": false, "result": {"Satisfied": false, …}}],
 "failedIndex": 1, "error": "wait_for was not satisfied: …", "stoppedBy": "wait_for"}
```

- `count` = steps given; `completed` = steps that ran and succeeded.
- `results` has one entry per step that was **attempted**, in order. `result` is what the step's
  tool returned — embedded as JSON when it is a JSON object or array, otherwise the string
  (`"PASS"`). A step that threw has `error` instead of `result`.
- `error` for a thrown step is `ToolErrors.MessageFor(ex)` when `ToolErrors.IsCallerFacing(ex)`,
  otherwise `An unexpected fault (<TypeName>).` — the same masking the call-tool filter applies, so
  a sequence leaks nothing a single call would not.
- All ran: `completed == count`, `failedIndex`, `error` and `stoppedBy` are `null`.
- A failed batch is a normal result, not `isError` (B-7): the steps before it happened and the
  caller needs them.

## Takeover (S-11) aborts the remaining steps

S-11 is information, never control, for a single tool call — and stays that way. A sequence is
different in kind: it is the server, not the model, that decides to send step 3 after step 2, so
the server is the only party that can honour a takeover that happens in between.

**Mechanism.** `IControlIndicator` gains two read-only members, neither of which consumes the
record, both inert (false / 0, never throwing) when the indicator is off or disposed:

- `bool TakeoverPending` — a takeover is recorded that the AI has not been told about;
- `long TakeoverCount` — how many takeovers have been recorded since the process started. It only
  grows. The pending record is one slot that **any** call's filter consumes (a concurrent
  `snapshot`, a second `sequence`), so "pending" alone can be emptied by someone else between two
  steps; the count cannot.

`sequence` is in `DrivingTools.Names`, so the filter's `BeginAction` covers the whole batch and the
hotkey is live throughout. The dispatcher reads `TakeoverCount` once when the batch starts; before
**each** step — the first included — the batch stops when `TakeoverPending` is true **or**
`TakeoverCount` has **grown** since that first reading (a count that reads lower later is a
disposed indicator at shutdown, not a takeover):

- true → no further step runs. `stoppedBy:"takeover"`, `failedIndex:null`, `error` =
  `The user took over the mouse and keyboard; steps <i>..<count-1> were not run.` (`step <i> was
  not run` for one).
- The record is left pending, so the call-tool filter appends its usual takeover note (who, when)
  as the last content block, exactly as for any other tool.
- Before step 0 matters: a takeover made while the indicator was fading after the previous call is
  still pending when the sequence arrives. A single tool would act once and then be told; a
  sequence would act up to twenty times. It runs nothing.
- A fault reading either member counts as "no takeover" — the indicator can never fail a tool.
- When another call's filter consumed the record first, that call's result carried the note; this
  one still stops, with the same `stoppedBy` and `error`.
- Not covered (D-53): a takeover that was recorded **and** consumed by another call before this
  sequence started running. Normally the model has been told and honouring it is the model's part.
  The case where that is not true is **parallel tool calls**: a fast tool and a `sequence` sent
  together just after a takeover — the fast tool's result carries the note, but the model had
  already committed to the sequence, and all of its steps run. Nothing inside the tool can tell
  that from a deliberate resume; closing it needs per-session delivery tracking in the filter.

Not covered (the D-29 / D-45 family): a takeover **during** a step does not interrupt that step —
a long `type`, a `wait`, a `wait_for` runs to its end and the batch stops before the next one.

## Observation

`observe` is `none` (default) or `foreground`; `window` is refused before any step runs (a sequence
has no single target element), through the same `Observation.AroundAsync` the other tools use with
no element id. With `foreground`, after the batch ends — complete, failed or taken over — the call
waits `settle_ms` and appends the E-5 observation to the JSON result: where the screen is now is
exactly what the caller needs after a batch that stopped early. A refused batch (validation) is
never observed. `settle_ms` is range-checked always (0–5000).

## Shape in the code

- `Tools/SequenceTools.cs` — `sealed [McpServerToolType]`; constructor takes `IInputService`,
  `IClipboardService`, `IUIAutomationService`, `IControlIndicator`; builds one `InputTools` and one
  `UIAutomationTools` (internal properties, so tests reach their `Delay` seams) and an
  `Observation`.
- `Tools/SequenceSteps.cs` — the pure part: parse `steps_json`, validate against the allowlist and
  the methods' `ParameterInfo`s, bind arguments. No service access; unit-testable alone.
- Annotations (C-7): `Title = "Sequence"`, `ReadOnly = false`, `Destructive = false`,
  `Idempotent = false`, `OpenWorld = false`. Tool count 69 → 70.
