# C-3 — Process list CPU %, sort, limit; graceful kill

**Checklist item:** [C-3](../upstream-parity-checklist.md#c-3--process-list-cpu--sort-limit-graceful-kill--p2--m) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 2, last item — decisions R4 (two-sample CPU,
substring filter kept, `limit` default all) and R5 (graceful kill is our definition, default
off) ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]; the Notepad graceful close is a desktop test) ·
**Effort:** ~3 h including the RED/GREEN passes.

## Problem

`process(list)` has memory, path and lineage but no CPU column, no order and no cap, so "what
is eating the CPU" is a `powershell` call, and a 300-row list is the only size. `kill` is
always `TerminateProcess`, which loses an editor's unsaved work with no chance to answer a
"save changes?" prompt. Upstream lists CPU % with `sort_by` and `limit`; its `terminate()` is
`TerminateProcess` on Windows, so the graceful path is defined here.

## Decision

- **CPU % is two samples ~250 ms apart, normalised across all cores** (R4): for every process,
  `TotalProcessorTime` before and after an injected delay; a pure
  `CpuSample.Percent(before, after, elapsed, cores)` returns
  `clamp((after − before) / elapsed / cores × 100, 0, 100)` rounded to one decimal, `0` when
  `elapsed ≤ 0` or the delta is negative. A process that exits between the samples reads `0`.
  A process at 100 % of one core on eight reads `12.5`, as Task Manager shows. `ProcessDto`
  gains a trailing `CpuPercent (double)`; the lineage/group rows do not (follow-up).
- **`ListAsync(ProcessListOptions)`** beside the old overload, which is
  `new ProcessListOptions(nameFilter)`: `NameFilter` (substring, unchanged), `SortBy`
  (`memory` default, `cpu`, `name`, `pid`) and `Limit` (`0` = all). Sorting is pure
  (`CpuSample.SortAndLimit`): the two numbers descending, `name` ordinal-ignore-case ascending
  then pid, `pid` ascending; `limit` applies after the filter and the sort. Every plain list
  pays the 250 ms; `orphans`, `includeLineage` and `groupByRoot` do not.
- **`process(list, sort_by?, limit = 0)`**: `sort_by` is validated against the four names;
  `sort_by` or a `limit` with `includeLineage`/`groupByRoot`/`orphans` is refused (they have
  their own shapes) rather than silently ignored — which is why `sort_by` is nullable at the
  tool: null means `memory` for the plain list and "not given" everywhere else.
- **Graceful kill (R5).** `KillOptions(Graceful, GraceMs, ExpectedStartUtc?)` and
  `IProcessService.KillAsync(pid, options)` → `KillResult(Pid, Name, Graceful,
  ExitedGracefully, Forced, WaitedMs)`. Sequence: the start-time guard first when given (the
  existing rule: mismatch aborts, nothing killed); then, when `Graceful`: `CloseMainWindow()`,
  and when the process reports no main window, `WM_CLOSE` posted to every top-level window
  whose owner pid matches (`EnumWindows` + `GetWindowThreadProcessId` + `PostMessage`, behind an
  internal `IProcessWindowNative` seam so the unit test sees the posts); then
  `WaitForExitAsync` bounded by `GraceMs`; still alive → `Kill()` and `Forced:true`. When
  nothing could be sent (no window at all — a console child, a service) the kill is forced at
  once with `WaitedMs:0` and `ExitedGracefully:false`, honestly. `Graceful:false` is today's
  hard kill. `KillGuardedAsync` and the old `KillAsync(pid)` stay and delegate.
- **`process(kill, …, graceful = false, grace_ms = 3000)`**: `grace_ms` in `0…60000`;
  `graceful` with `tree:true` is refused (descendants are killed leaves-first and forcibly).
  The pid and name kills return JSON `{killed:[KillResult…]}` (camelCase, like the section-B
  verbs) instead of the text lines; the tree kill keeps its text count. A name kill runs the
  same options for every exact-name match. Contract change → CHANGELOG *Changed*.
- Annotations unchanged (`process` is destructive, not idempotent).

## Changes

- `Abstractions/Models/ProcessDtos.cs` — `ProcessDto +CpuPercent = 0`, `ProcessSort`,
  `ProcessListOptions`, `KillOptions`, `KillResult`; `Abstractions/IProcessService.cs` — the
  two overloads.
- `Services/CpuSample.cs` (new, pure); `Services/IProcessWindowNative.cs` +
  `Services/Win32ProcessWindowNative.cs` (new); `Services/ProcessService.cs` — the sampler
  (internal constructor takes the delay and the native seam), the sort, the graceful path.
- `Tools/ProcessTools.cs` — `sort_by`, `limit`, `graceful`, `grace_ms`, the JSON kill result,
  the description.
- `NativeMethods.txt` — unchanged: `EnumWindows`, `GetWindowThreadProcessId`, `PostMessage` and
  `IsWindowVisible` were already declared, and `WM_CLOSE` is a local const in the seam.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `CpuSample.Percent`: the normalisation table (one core saturated on 8 = 12.5; half a core on 4 = 12.5; zero elapsed = 0; negative delta = 0; above 100 clamped; one decimal); `SortAndLimit` for the four keys, both directions, ties by pid, `limit` 0 = all, `limit` after filter | `CpuSampleTests` | Unit |
| R2 | The service on the live box: every row's `CpuPercent` in 0–100 and their sum ≤ 100; a thread spinning in the test process for the sample window puts the current process above 0 and first under `cpu` among processes named like the host; the old overload still lists; `limit` and `sort_by` honoured | `ProcessServiceTests` | Integration |
| R3 | Graceful kill, unit (fake native seam, a fake delay): `WM_CLOSE` posted to every window of the pid when there is no main window; nothing posted and forced at once when there are no windows; the guard runs first and a mismatch kills nothing; `Graceful:false` never touches the seam | `ProcessServiceKillTests` | Integration (real children, fake window seam) |
| R4 | Graceful kill, live: a detached `powershell -c Start-Sleep 30` (no window) reports `forced:true, exitedGracefully:false, waitedMs:0` and is gone; an unmodified Notepad window closes with `exitedGracefully:true, forced:false` | `ProcessServiceKillTests` (Integration), `ProcessToolsKillDesktopTests` (UIAutomation, `DesktopCollection`, Notepad fixture) | Integration / UIAutomation |
| R5 | The tool: `sort_by` validated and forwarded with `limit`; both refused with lineage/group/orphans; `graceful`+`tree` refused; `grace_ms` range; the JSON kill result for pid and name kills, the text for the tree kill; the schemas over HTTP | `ProcessToolsTests`, `HttpTransportTests` | Unit / Integration |

## Deviations and follow-ups

- **The old `ListAsync(nameFilter)` overload does not sample CPU.** The decision said it would
  become `ListAsync(new ProcessListOptions(nameFilter))`; the name kill and the startup report
  enumerate through it and must not pay the 250 ms window, so it keeps today's shape and only
  the options overload measures.
- **Each reading is timestamped per process.** Walking a few hundred processes takes tens of
  milliseconds at either end of the window; one shared elapsed time credited the processes read
  first with more CPU time than it divided by, and the whole box summed to 155 %. Per-process
  `Stopwatch` timestamps bring the sum back to about 100.
- **`CloseMainWindow()` is not used.** The graceful path posts `WM_CLOSE` through the seam to
  every *visible* top-level window of the pid (`IsWindowVisible`, so a hidden helper window
  cannot make a windowless process look like one that was asked to close); the main window is
  one of them. Modern Notepad hosts every window in one process, so a graceful kill of that pid
  asks every Notepad window to close.
- `KillGuardedAsync` stays on the interface but the tool no longer calls it: a `startTime` kill
  rides in `KillOptions.ExpectedStartUtc` so it gets the same JSON result.
- CPU on the lineage and group rows is a follow-up; only the plain list carries it.
