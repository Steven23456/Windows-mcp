# D-9 — `job output`: decode a background job's CLIXML stderr

**Checklist item:** [D-9](../upstream-parity-checklist.md#d-9--job-output-still-returns-raw-clixml-on-stderr--p3--s) ·
**Status:** implemented 2026-09-04 (build clean, 446/446 tests green — see CHANGELOG [Unreleased]) ·
**Order:** the leftover from [D-8](D-8-powershell-clixml-stderr.md), which deliberately did not
widen into `JobService` (rule 4). Effort: ~2 hours.

## Problem

D-8 gave the foreground `powershell` tool a whole-stream `ClixmlStderr.Decode`, and put
`$ProgressPreference='SilentlyContinue'` into `PowerShellInvocation`'s preamble — which
`JobService` shares, so background jobs already lost the bulk of the noise. But the decoding did
not reach them, for a structural reason: `PowerShellService` reads stderr with one
`ReadToEndAsync` and has the whole document in hand, whereas `JobService` **pumps** stderr into a
`BoundedTextBuffer` as chars arrive and serves a `Tail(n)` of it. There is no complete document to
parse at any given moment, and a tail can cut a CLIXML record in half.

So a job that wrote a warning, or whose script re-enabled progress, still returned raw `<Objs>` XML
from `job output` — the same tokens-the-model-ignores problem D-8 fixed everywhere else.

## Decision

Decode where a complete stream exists, and make the decoder tolerant enough to handle the case
where one does not.

### 1. Decode once, at job completion

`MonitorAsync` already awaits both pumps before recording the final state. That is the moment the
stream is complete, so the decode goes there — **before** `job.State` flips to a terminal value, so
no reader can ever observe a finished job together with raw XML:

```csharp
await Task.WhenAll(job.StdoutPump, job.StderrPump);
DecodeStderr(job.Stderr);        // rewrites the buffer in place
lock (_lock) { … job.State = … }
```

Rewriting the buffer rather than decoding on every read keeps `Tail()`, `Length` and
`TrimmedChars` telling the same story as what `job output` returns — `job status` reporting
`StderrChars: 5000` while `job output` hands back 40 characters would be its own small lie — and
costs nothing per read. It needs one new method, `BoundedTextBuffer.ReplaceAll`, which clamps to
capacity and deliberately does **not** reset `TrimmedChars`: that counter describes what was lost
from the raw stream, which stays true after the retained text is rewritten.

Stdout is left alone. It is never CLIXML — only the redirected non-stdout streams are wrapped.

### 2. Decode on read while a job is still running

A running job has no complete stream, but it usually has complete *documents*: PowerShell flushes
one `<Objs>…</Objs>` per write. So `GetOutput` decodes a copy for a running job:

```csharp
var stderrText = state == "running"
    ? TailOf(ClixmlStderr.Decode(job.Stderr.Snapshot()), tailChars)
    : job.Stderr.Tail(tailChars);          // already decoded by the monitor
```

Decode-then-tail, not tail-then-decode: tailing first would slice a record in half.

### 3. The decoder tolerates a trailing partial document

Point 2 only works if landing mid-flush does not throw the whole read away. `ClixmlStderr` now
retries on everything up to the last `</Objs>` when the full payload fails to parse, and drops the
trailing fragment:

| payload | result |
|---|---|
| parses whole | decoded |
| complete documents + a trailing fragment | the complete ones decoded, fragment dropped |
| no complete document at all | raw passthrough, exactly as before |

Dropping the fragment is safe in the case that motivates it — for a running job the record arrives
whole on the next read — and it is XML nobody could have read anyway. For a finished job a trailing
fragment means the child was killed mid-write; the alternative is emitting half a record as raw XML,
which is worse. Crucially, a stream with **no** complete document still passes through untouched, so
this can never silently swallow non-CLIXML output.

This also helps the foreground service: a `powershell` call whose child is killed by the backstop
mid-flush now decodes the records it did complete instead of dumping the lot as XML.

### Where it still does not apply

A buffer whose head was trimmed (a job that wrote more than ~1 MB to stderr) has lost the
`#< CLIXML` marker, so `Decode` passes it through raw. That is the correct outcome — without the
header we cannot know the rest is CLIXML — and the preamble means a job should not be producing
that much stderr in the first place.

## Changes (as landed)

- `Services/ClixmlStderr.cs` — `TryParseRecords` split into a `TryParsePayload` helper plus the
  last-`</Objs>` retry.
- `Services/BoundedTextBuffer.cs` — `ReplaceAll(string)`.
- `Services/JobService.cs` — `DecodeStderr` called from `MonitorAsync`; `GetOutput` decodes for a
  running job; `TailOf` helper.

## Tests

**Pure** (`ClixmlStderrTests`): a trailing partial document keeps the complete records and drops the
fragment; a stream with no complete document stays raw.

**Pure** (`BoundedTextBufferTests`): `ReplaceAll` swaps the text, keeps `TrimmedChars`, and honours
capacity.

**Real-process** (`JobServiceTests`, `Category=Integration`): a job that writes a warning returns
`careful` and no `<Objs` / `_x000D_`; an ordinary job's stderr is empty (the preamble); a job that
re-enables `$ProgressPreference` still gets its progress records dropped while its warning survives;
and `job status`'s `StderrChars` equals the length of what `job output` returns, which pins the
decode-before-state-flip ordering.

## A test-suite fix that came with it

Four `UIAutomation` tests began failing **only in a full parallel suite run** (they passed alone).
Root cause was D-5's default scope change, not D-9: `GetStateAsync` and the default find scope both
root at the **foreground** window, so when another app held the foreground the helper resolved
*VS Code's* document and the test asserted against it (`Expected … "vscode-file://…workbench.html"
to contain "d2-…"`). Fixed in the tests, two ways:

- `UIAutomationServiceTests`' constructor calls `NotepadFixture.BringToForeground()`, so every test
  in the class starts with Notepad in front. Keyboard input goes to the foreground window whatever
  element id was resolved, so the typing tests need this regardless.
- `FindNotepadDocumentIdAsync` resolves the editor with `scope=window, window:"Notepad"` instead of
  trusting the foreground — which is exactly what D-5 added the scope for, and it makes the helper
  say what it means.

## Done when

`job output` on a finished job that emitted warnings returns prefixed text, not `<Objs`; a running
job's tail is no worse than before (and usually decoded too); `job status` and `job output` agree
on the size of stderr.
