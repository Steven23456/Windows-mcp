# D-8 — `powershell`: suppress progress, decode the CLIXML stderr stream

**Checklist item:** [D-8](../upstream-parity-checklist.md#d-8--powershell-ships-the-clixml-progress-stream-to-the-model-on-every-call--p2--s) ·
**Status:** implemented 2026-09-04 (build clean, tests green incl. the real-process PowerShell set — see CHANGELOG [Unreleased]) ·
**Order:** independent of D-5/D-6/D-7 — different files, no shared code, can run in parallel.
Do it **before** [C-6](../upstream-parity-checklist.md#c-6--powershell-per-call-timeout-environment-rebuild-from-registry),
which touches the same service. Effort: ~half a day.

## Problem

Windows PowerShell 5.1 with redirected stderr wraps every non-stdout stream in a CLIXML document.
Two things then happen on essentially every call:

- **Progress records.** A cold start emits `Obj S="progress"` "Preparing modules for first use.";
  `Invoke-WebRequest`, `Invoke-RestMethod` and module autoload each add more.
- **`PSResult.Stderr` carries the raw blob.** `PowerShellService.RunAsync` stores the stream
  verbatim and `ShellTools.Powershell` serialises the whole `PSResult`, so the model reads
  ~0.6–3 KB of XML per call and ignores all of it.

Measured 2026-09-04 through the service's exact process setup: a one-liner with one
`Write-Progress` → **596 characters** of CLIXML on stderr; the same script with
`$ProgressPreference='SilentlyContinue'` in the preamble → **0**.

`Errors[]` is already clean — since `6c96350` (2026-08-24) `ExtractErrors` decodes only
`<S S="Error">` records, covered by `RunAsync_progress_records_on_stderr_do_not_fail_the_command` —
so the blob is carried once, in `Stderr`, not twice. The exception is the `XElement.Parse` failure
path, where `RawLines()` puts the raw XML lines into `Errors[]` as well.

This is not an upstream-parity item. It is logged here (checklist rule 4) because it is the highest
token-ROI fix available on the most-used tool.

## Decision

Two independent layers, because neither alone is enough: suppression stops the common case at the
source, and decoding handles everything suppression cannot reach.

### 1. Suppress progress at the source

`PowerShellInvocation.BuildArgumentsAsync` already prepends an encoding preamble. Add one line:

```csharp
const string Preamble =
    "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{}\n" +
    "$ProgressPreference='SilentlyContinue'\n";
```

- It is set in the **script scope**, so a caller's script can set it back to `'Continue'` if it
  genuinely wants progress — layer 2 then drops the records anyway.
- There is no console to draw a progress bar on; suppressing it is not a loss of information.
- Welcome side effect: `Invoke-WebRequest` / `Invoke-RestMethod` are markedly faster without the
  progress bar.
- It lands in `PowerShellInvocation`, which **`JobService` also uses**, so background jobs get the
  suppression too (see the limitation below).

### 2. Decode what is left

`PSResult.Stderr` becomes text a model can read, using the parser `ExtractErrors` already has:

| stderr | result |
|---|---|
| empty | `""` |
| not CLIXML (a native child writing raw bytes) | unchanged, raw |
| CLIXML that fails to parse | unchanged, raw (same fallback `ExtractErrors` uses) |
| CLIXML | one line per non-empty line of each `Error` / `Warning` / `Verbose` / `Debug` record, prefixed `ERROR: ` / `WARNING: ` / `VERBOSE: ` / `DEBUG: `, escapes decoded; `<Obj>` records (progress, information) dropped entirely |

So `Write-Warning 'careful'` arrives as `WARNING: careful`, and a script that re-enables progress
still yields `Stderr: ""`.

Prefixing rather than merging the streams into bare text is deliberate: an agent reading `Stderr`
needs to know whether it is looking at a warning or a verbose trace, and the prefix is what the
PowerShell host itself prints.

### 3. One parser, two consumers

`ExtractErrors` and the new decoder must not drift, so the CLIXML handling moves into an
`internal static class ClixmlStderr` (new file, `Services/ClixmlStderr.cs`):

```csharp
internal static bool TryParseRecords(string stderr, out IReadOnlyList<(string Stream, string Text)> records);
internal static string Decode(string stderr);      // -> PSResult.Stderr
```

`PowerShellService.ExtractErrors` keeps its name, signature and semantics and calls
`TryParseRecords`, filtering to `Error`. `PSResult`'s shape (`Success, Stdout, Stderr, ExitCode,
Errors`) is untouched, so `job` output and every existing test keep working, and `Success` still
means "exit code 0 and no `Error` records".

A real error therefore appears twice: decoded in `Stderr` as `ERROR: boom`, and in `Errors[]`.
That is intended — `Stderr` is the human/model-readable stream, `Errors[]` the structured list — and
both are now small.

### Background jobs — deferred here, closed by D-9

`JobService` pumps stderr incrementally into a `BoundedTextBuffer` and serves a `Tail(n)` of it, so
it has no whole document to parse and a tail can cut a CLIXML record in half. Layer 1 applies to
jobs (same `PowerShellInvocation`), which removes the bulk; layer 2 did not. That was a real
remaining gap, so it became its own checklist item rather than widening D-8 (rule 4: "do not
silently expand an item's scope") — and shipped the same day:
[D-9](D-9-job-clixml-stderr.md). It decodes at job completion, decodes on read while a job runs,
and made the decoder tolerate a trailing partial document (which helps this service too: a child
killed mid-flush now decodes the records it completed).

## Plan

1. New `Services/ClixmlStderr.cs` — `TryParseRecords` + `Decode`, moving `ClixmlEscape` /
   `DecodeClixmlEscapes` and the `#< CLIXML` header handling out of `PowerShellService`.
2. `Services/PowerShellService.cs` — `ExtractErrors` delegates to `TryParseRecords`;
   `RunAsync` stores `ClixmlStderr.Decode(stderr)` in `PSResult.Stderr`.
3. `Services/PowerShellInvocation.cs` — the `$ProgressPreference` preamble line, and update the
   preamble comment to say why.
4. `Tools/ShellTools.cs` — the `powershell` description: progress output is suppressed (there is no
   console to draw it on) and the warning/verbose/debug streams arrive as prefixed text, not CLIXML.

## Tests

**Pure** (`tests/.../Services/PowerShellErrorExtractionTests.cs`, or a sibling
`ClixmlStderrTests.cs`) on captured CLIXML samples — no process spawn, so these are the fast
regression net:
- progress-only document → `""`;
- warning record → `WARNING: careful`, no `<Objs`;
- error record → `ERROR: boom`, escapes (`_x000D__x000A_`) decoded;
- mixed progress + warning + error → the three prefixed lines, progress gone;
- non-CLIXML stderr → returned byte-for-byte;
- unparseable CLIXML → returned raw (matches `ExtractErrors`' fallback);
- concatenated `<Objs>` documents (one per stream flush) → all records found;
- `ExtractErrors` output is unchanged for every sample above.

**Real-process** (`PowerShellServiceTests`, already integration-slow — see `CLAUDE.md`):
- rename `RunAsync_progress_records_on_stderr_do_not_fail_the_command` →
  `RunAsync_progress_output_is_suppressed`; its precondition
  `Stderr.Should().Contain("progress")` **flips** to `Stderr.Should().BeEmpty()`. The CLIXML path it
  used to exercise is now covered by the pure tests;
- a script that re-enables `$ProgressPreference='Continue'` and calls `Write-Progress` → `Stderr`
  still empty (proves layer 2 independently of layer 1);
- `Write-Warning 'careful'` → `Stderr` contains `careful` and not `<Objs`; `Success` still true;
- `Write-Error 'boom'` → `Errors[]` and `Success` unchanged from today.

## Docs / CHANGELOG

- `CHANGELOG.md [Unreleased] → Fixed`, with the measured before/after (596 → 0 characters).
- `docs/architecture/COMPONENTS.md` — the `PowerShellService` bullets (preamble contents, stderr
  decoding) and the new `ClixmlStderr.cs` entry in the services table.
- `skills/windows/SKILL.md` — only if the shell playbook mentions reading `Stderr`.

## Done when

A `powershell` call that triggers module first-use returns `Stderr: ""`; a `Write-Warning` arrives
as `WARNING: careful`; `Errors[]` and `Success` semantics are unchanged; the whole response for
`'hi'` is under 200 bytes.
