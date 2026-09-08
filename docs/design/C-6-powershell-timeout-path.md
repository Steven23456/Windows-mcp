# C-6 — `powershell`: a per-call timeout that returns; `Path` repaired at startup

**Checklist item:** [C-6](../upstream-parity-checklist.md#c-6--powershell-per-call-timeout-environment-rebuild-from-registry--p2--sm) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 3, first item — decision R9 (a timeout that
*returns* a result; the environment repaired once, process-wide, in `EnvironmentRepair`) ·
**Status:** implemented 2026-09-08 (build clean, headless suite green, the real-process
PowerShell set green including the timeout rows; two review rounds — see CHANGELOG [Unreleased]) ·
**Effort:** ~3 h plus the real-process `Integration` runs (a `powershell.exe` cold start is
15–75 s under Defender).

## Problem

`powershell(command)` has one clock: the service's 15-minute execution backstop, which
**throws** `OperationCanceledException` when it fires. A caller who wants "give this script five
seconds" has nothing shorter, and when the backstop does fire the stdout the script wrote before
it hung — usually the diagnosis — is thrown away with the exception. Upstream has
`PowerShell(command, timeout=30)`.

Upstream also rebuilds the child's environment from the registry on every spawn because MCP
hosts launch servers with a stripped block, so `git`, `node`, `winget` are "not found" inside a
tool call. Our `Hosting/EnvironmentRepair` already runs first in `Main` and already fixes
`PATHEXT`, `ProgramData` and every *missing* variable from the registry — but a `Path` the host
*did* set is never touched, even when it is `C:\nothing`.

## Decision

### The timeout

- **`powershell(command, background = false, timeout_seconds = 0)`.** `0` (the default) means
  the backstop only — today's behaviour. `1…900` bounds this call; outside that range is an
  `ArgumentException` naming `timeout_seconds` and the range. `timeout_seconds` with
  `background:true` is refused: a job has `job(cancel)` and its own 60-minute backstop.
- **`IPowerShellService.RunAsync(command, TimeSpan? timeout, ct)`** beside the old overload,
  which is `timeout: null`. `null` = backstop only; a non-positive `timeout` is an
  `ArgumentException`; a `timeout` longer than the backstop is not an error — the earlier of the
  two fires, the result says which.
- **The timer starts after the gate.** Same rule as the backstop (`CLAUDE.md`): a queued caller
  must not burn its budget waiting behind another caller's script. Two queued calls, the second
  with a 2-second timeout and a 5-second wait in front of it, both complete.
- **Expiry returns, it does not throw.** The child tree is killed
  (`Process.Kill(entireProcessTree: true)`), the stdout and stderr already read are kept, and
  `RunAsync` returns `PSResult` with the new trailing field **`TimedOut: true`**, `Success:
  false`, `ExitCode: -1`, `Errors: ["timed out after 5s"]` (the seconds as given, invariant, up
  to three decimals) and `Stderr` decoded as always. The backstop is folded into the same path:
  `TimedOut: true`, `Errors: ["timed out after 900s (execution backstop)"]`. Every other `PSResult`
  carries `TimedOut: false`, so a pre-C-6 result serialises with one extra `false` and nothing
  else changes. Only the **caller's** cancellation still throws `OperationCanceledException`.
- **Partial output is harvested, not cancelled.** The two stream reads are pumps (a chunked
  read into a buffer that can be inspected before the read has finished) that the clocks do not
  cancel; after the kill the pumps drain what the pipes still hold (the tree kill closes the
  write ends) and the result carries it. A pump that does not finish within a 2-second grace
  after the kill — a grandchild that survived the tree kill still holding the pipe — is
  abandoned and the result carries what was read so far. A caller's cancellation cancels the
  pumps as before.
- **`JobService` is unchanged.** It has `job(cancel)`, its own backstop and its own buffers.

### The environment

- **One more rule in `EnvironmentRepair.Apply`.** After the existing fills, when the process's
  `Path` is empty, missing, or has no `System32` entry, the registry machine `Path` and then
  the user `Path` are **appended** after the host's own entries by a pure
  `Hosting/PathMerge.Merge(host, machine, user)`: entries compared ordinal-ignore-case after
  trimming whitespace, surrounding double quotes and trailing `\`/`/`; the first spelling wins
  and is kept (trimmed and unquoted, otherwise as written — `C:\Tools\` stays `C:\Tools\`);
  empty entries are dropped. Nothing the host set is removed or reordered.
  When the registry contributed no `Path` at all and `SystemRoot` is known, the stock four
  (`<SystemRoot>\System32`, `<SystemRoot>`, `<SystemRoot>\System32\Wbem`,
  `<SystemRoot>\System32\WindowsPowerShell\v1.0`) are appended instead, so a box whose registry
  read failed still resolves `where.exe` and `powershell.exe`. A registry `Path` that exists but
  still lacks `System32` after the merge is left as merged — the stock four are the fallback for
  an *empty* registry only. The `changed` list — and the startup line on stderr — names `Path`
  once.
- **"Has `System32`"** is an entry equal to `<SystemRoot>\System32` when `SystemRoot` is known
  (the process block, then the registry, then the folder default), and any entry whose last
  segment is `System32` when it is not (`PathMerge.HasSystem32(path, systemRoot)`).
- **The policy sentence changes.** `EnvironmentRepair` never overwrote a host-set variable except
  `PATHEXT`; `Path` is now the second exception, and only by appending: a `Path` that already has
  `System32` — even one with nothing else on it — is left alone, because a short `Path` may be
  deliberate and a `Path` without `System32` cannot be.
- **`PowerShellInvocation.CreateStartInfo` stays environment-free.** Foreground calls, jobs,
  `start_process` and `launch` all inherit the repaired block; there is no per-spawn rebuild.

## Changes

- `Abstractions/Models/PowerShellDtos.cs` — `PSResult +TimedOut` (trailing, default `false`), and
  round 2's `+StdoutTrimmedChars` / `+StderrTrimmedChars` (trailing, default `0`).
- `Abstractions/IPowerShellService.cs` — `RunAsync(string command, TimeSpan? timeout,
  CancellationToken ct = default)`.
- `Services/PowerShellService.cs` — the linked timer after the gate, the bounded pumps, the
  harvest, the backstop folded into the result (and, on the one-argument overload, rethrown as a
  `TimeoutException`).
- `Services/PowerShellInvocation.cs` — a cancelled temp-script write deletes its partial `.ps1`
  (round 2, F14).
- `Hosting/PathMerge.cs` (new, pure) — `Merge`, `HasSystem32`, `Split`.
- `Hosting/EnvironmentRepair.cs` — the `Path` rule; the policy comment.
- `Tools/ShellTools.cs` — `timeout_seconds`, its range, the `background` refusal, the description.
- `skills/windows/SKILL.md` — the "long jobs" paragraph gains the parameter.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `timeout_seconds` default 0 after `background`; 1…900 → `TimeSpan`, both ends; −1 / 901 / `int.MaxValue` refused naming the parameter and 900 with nothing spawned; with `background:true` refused before the job starts (0 with `background` allowed; a bad value with `background` refused for the range); the description names it; `TimedOut` and the trimmed-char counts in the JSON both ways; the production constructor still works | `ShellToolsTests` (21 in all) | Unit |
| R2 | Expiry returns: `TimedOut:true`, `Success:false`, `ExitCode:-1`, `Errors == ["timed out after 2s"]`, the stdout written before the hang present, the child pid gone; `1.5s` as written; `null` and a generous timeout run to completion; a non-positive timeout refused before the gate and any spawn; the clock starts after the gate (B queued behind a 5 s A with a 2 s budget succeeds); a timeout past the backstop lets the backstop win and says so, one equal to it is the caller's clock and one millisecond past it the backstop's; the backstop itself returns `(execution backstop)` on the two-argument overload and throws a `TimeoutException` on the one-argument one, which the tool-error filter passes through; the timeout path decodes stderr (never the `#< CLIXML` header) and keeps the errors the script wrote first; a flooding script and a successful oversized one are both bounded with the tail kept; an oversized script whose clock fires during (or before) the temp-script write returns a timeout and leaves no `.ps1` behind; the caller's cancellation still throws; `CreateStartInfo` builds no environment of its own | `PowerShellServiceTests` (36 in all) | Integration |
| R3 | `HarvestAsync`: both pumps awaited, the grace expiring against a pump that never finishes, a faulted pump, a pump cancelled while the caller was not, the caller's cancellation rethrown before and during the grace; `Pump.Text` returns the partial buffer of a read that has not finished, an already-closed pipe is empty text, and multi-byte text split across a chunk boundary is reassembled | `PowerShellServiceHarvestTests` (12) | Unit |
| R4 | `PathMerge`: host first and as written, machine then user, no leading separator, nothing to append → unchanged, duplicates by case / trailing `\` / trailing `/` / quotes dropped with the first spelling kept, registry values de-duplicated against each other, empties dropped, whitespace trimmed; `HasSystem32` with and without a known root (22 rows); `Split` trims, unquotes, drops empties, keeps the rest | `PathMergeTests` (41) | Unit |
| R5 | `EnvironmentRepair`: `C:\nothing` → machine then user appended, named once; a `Path` with `System32` (even alone) untouched; empty `Path` takes the registry without a leading separator; missing `Path` filled and named once — also when filled and then repaired; empty registry + known `SystemRoot` → the stock four; nothing anywhere → left alone; `SystemRoot` precedence process → registry → defaults; `StockPath` order and separators; the policy sentence (`PATHEXT` and `Path` the only exceptions) | `EnvironmentRepairTests` (+16) | Unit |
| R6 | Against the real registry, a host-stripped `Path` is repaired into one that carries `System32` and resolves `where.exe` (through the pure core, never the live `Apply()`) | `EnvironmentRepairPathIntegrationTests` | Integration |
| R7 | `skills/windows/SKILL.md` names `timeout_seconds` | `ToolInventoryTests.The_skill_names_the_powershell_timeout` | Unit |
| R8 | Round 2, F8: the five internal-caller families never read a backstop expiry as data — `AudioService` does not answer 50 %, `NetworkService` not an empty port list, `SecurityService` not "probes failed", `DiskService` does not swallow it, `FileStreamService` does not answer "no alternate streams"; each surfaces the `TimeoutException` instead | `PowerShellBackstopPropagationTests` (5) | Unit |

Coverage: `PathMerge`, `ShellTools`, `PowerShellService` and its `Pump` 100 % line and branch; the
generic `catch (Exception)` and the temp-script delete failure in `RunAsync` are the only lines
without a test. Bite check: the clock created before the gate (caught by the queued-caller
test), `HasSystem32` ignoring the root (three tests), the harvest dropped from the timeout result
(two tests); two breaks that changed nothing — the pumps cancelled by the linked token, and
`Pump.Text` empty until the read completes — are what R3's unit tests exist for, since no real
process can hold a pipe past the tree kill on demand.

## Round 2 — what the review-agent found (2026-09-08)

The REVIEW step read the finished diff cold and reported seven C-6 findings; each went back to
`test-agent` as a RED row and was fixed with the family, not the case.

| # | Input → wrong outcome | Decision |
|---|---|---|
| F1 | `$s='x'*1000; while($true){$s}` with `timeout_seconds:6` → 100 MB of stdout handed to the client (the pre-C-6 backstop cancelled the reads and delivered nothing) | Each stream is a `BoundedTextBuffer` of 1 000 000 chars, the tail kept, exactly like a job's; `PSResult +StdoutTrimmedChars, +StderrTrimmedChars` (trailing, default 0) say what was dropped, on every path |
| F6 | `cmd /c "echo DISK FAILURE 1>&2"; 'still alive'; Start-Sleep 60` with a timeout → `Errors: ["timed out after 5s"]` only; the native child's stderr (unbuffered, it does arrive) dropped from `Errors` | `Errors` on the timeout path = the extracted errors, then the reason last |
| F7 | `timeout_seconds:900` — the documented maximum, equal to the backstop — reported as `(execution backstop)` | `timeout <= backstop` is the caller's clock; one millisecond past it is the backstop's |
| F8 | Every internal caller (`AudioService` answering "volume 50" from a partial stdout, the JSON parsers in disk, network, security, file streams) now got a *returned* failure where the backstop used to throw, and none of them read `Success` or `TimedOut` | The one-argument `RunAsync(command, ct)` throws a caller-facing `TimeoutException` whose message is the reason when the backstop fires; only the two-argument overload — the `powershell` tool's — returns `TimedOut:true`. A deviation from R9, which was written for the tool |
| F14 | A command over ~11 000 characters (the `-File` path) with a short timeout that fired during the temp-script write → `OperationCanceledException` out of `RunAsync` (masked for the client) and a partial `.ps1` orphaned in `%TEMP%` | The invocation is built under the caller's token only; a cancelled write deletes its partial file; and `timedOut` is decided from the clock after the wait, because a clock that fires before the wait begins has already killed the child and the wait then returns normally |
| — | A script that floods, a timeout equal to the backstop, an error before the hang: none had a test | Every row above is pinned in `PowerShellServiceTests` and `ShellToolsTests`; the five internal-caller families are pinned in `PowerShellBackstopPropagationTests` |

Examined and found clean by the review: the gate under a queued caller's cancellation, the
disposal order of the kill registration and the process, `Kill` on an exited child, the
`when` filters in both directions, `ClixmlStderr` on the bare header, `PathMerge` on every
spelling it could think of, `Environment.SetEnvironmentVariable` on a 100 000-char `Path`.

## Deviations and follow-ups

- **On expiry only stdout survives the kill.** Measured 2026-09-08 through the service's exact
  invocation: Windows PowerShell 5.1 writes the `#< CLIXML` header as soon as the first
  non-stdout record exists but buffers the `<Objs>` records until host shutdown, so a warning or
  error written before the hang is lost with the kill, while stdout is flushed per statement and
  survives. The harvest still decodes what arrived, so the bare header never reaches the model
  (`Stderr: ""`). The design note's "the partial stdout and stderr already read are kept" is
  true of the bytes; the diagnosis has to be on stdout.
- **The harvest grace cannot be provoked from a real process.** A grandchild that inherits the
  pipe is inside the tree-kill snapshot, and one created outside the tree does not inherit it;
  the branch is pinned at the unit level (R3) on hand-made tasks instead.
- **Two overloads on `IPowerShellService`, two backstop behaviours.** Every other caller (disk,
  storage, security, firewall, network, audio, file streams …) keeps the one-argument overload
  and the backstop only; on a backstop expiry it now throws a caller-facing `TimeoutException`
  (the reason as the message) where it used to throw a masked `OperationCanceledException` —
  louder, and never a plausible parse of partial output (round 2, F8). Only the two-argument
  overload, the `powershell` tool's, folds the backstop into a returned `TimedOut:true`.
- **Foreground output is capped at 1 000 000 characters per stream** (round 2, F1) — a
  contract change for a successful script that wrote more: the tail is kept and
  `StdoutTrimmedChars` says how much went, as a job's output already did.
