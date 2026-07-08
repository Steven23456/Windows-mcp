# Process tool: lineage, orphans, root-grouping, and fleet-aware kill — Design

## Goal

Extend the windows-mcp **process tool** so a caller can enumerate process **lineage**
(parent chain, start time, command line), detect **orphaned** processes with a
recycle-aware test, **group** processes under their root ancestor, and **kill a process
tree** — all as single declarative tool calls, instead of hand-scripting WMI + PowerShell.

## Motivation

Answering "list orphan processes" today required three throwaway PowerShell scripts and an
84K-character raw `wmi_query` dump, because `process action:list` returns only
`(Pid, Name, Path, MemoryMb)` — no parent PID, no start time, no command line, and no
filter. The correct orphan test is also non-obvious (Windows never reparents to init, and
recycles PIDs), so every caller would reinvent it, wrong. Encapsulating the algorithm once
turns a scripting exercise into one call. The tool **describes** lineage and **never judges**
what is reapable — mirroring the existing `confirm:true` kill-guard philosophy.

## Verified facts (current code)

- `Tools/ProcessTools.cs` → `Process(action, name?, pid?, confirm)` dispatches `list|kill`.
  `list` → `IProcessService.ListAsync` (fast `Process.GetProcesses()`), `kill` → `KillAsync(pid)`
  or loop-kill by name; `kill` requires `confirm:true`.
- `Services/ProcessService.cs`: `ListAsync` projects `ProcessDto(Pid, Name, Path, MemoryMb)`
  and disposes every wrapper (handle-leak guard). `InspectAsync(pid)` is the **only** path that
  reads parent PID + command line, via `IWmiService.QueryAsync("Win32_Process", null,
  "ProcessId={pid}", ct)` returning rows as `IDictionary<string,object>`.
- `Models/ProcessDtos.cs`: `ProcessDto(Pid, Name, Path, MemoryMb)` and
  `ProcessDetailDto(Pid, Name, ParentPid, CommandLine, StartTimeUtc, ModulesError, Modules[])`.
- `IProcessService`: `ListAsync`, `KillAsync`, `StartDetachedAsync`, `InspectAsync`.
- Tests: `IWmiService` is mockable (Moq); `WmiService` used real for lineage. Conventions:
  DTOs are `record`s, services `sealed`, tools `async Task<string>` returning JSON,
  `TreatWarningsAsErrors=true`.

## The orphan algorithm (recycle-aware) — the core primitive

One bulk `Win32_Process` WMI enumeration (no WHERE) → build `map: pid → { ParentProcessId,
Name, CreationDate, CommandLine, WorkingSetSize }`. With an injected `nowUtc`, for each
process **P** with parent id **Q**:

- **`parentAlive`** = `map.ContainsKey(Q)` **AND** `map[Q].CreationDate <= P.CreationDate`.
  The second clause is the recycle guard: if the live "parent" started *after* the child, its
  PID was reused and the true parent is gone.
- **`Orphaned`** = `!parentAlive`.
- **`ParentName`** = `parentAlive ? map[Q].Name : null` (null when orphaned — the real parent
  is gone, so we don't report a misleading recycled name).
- **`RootPid`** = walk `P → parent → …` while `parentAlive`, with a visited-set cycle guard and
  a 64-hop depth cap; the top of the chain is the root. An orphaned process is its own root.

**Null `CreationDate`** (System PID 4 / Idle PID 0 and some protected procs expose none):
`AgeMinutes` = null; treat a null-dated parent as *alive* for the recycle comparison (cannot
prove recycling), so we do not spuriously mark boot processes orphaned.

**Realism, documented in the tool description:** orphaned is **common and by-design** on
Windows — `userinit.exe` spawns `explorer.exe` then exits, so the shell and most user apps are
orphaned. Orphaned ≠ leak. The signals below let a caller rank without the tool judging.

### Per-row signals (annotate, return all — no keep/kill verdict)

- **`AgeMinutes`** — `(nowUtc − StartTimeUtc)` in whole minutes; null if start time unknown.
- **`RuntimeKind`** — deterministic classification from the process name (lowercased), static
  map: `node|python|dotnet|shell|browser|native|other`
  (`node.exe→node`, `python*.exe→python`, `dotnet.exe→dotnet`,
  `pwsh.exe/powershell.exe/cmd.exe→shell`, `chrome.exe/msedge.exe/firefox.exe→browser`,
  known native system names → `native`, else `other`).
- **`IsSystemAdjacent`** — descriptive boolean: process name is in a known session/boot set
  (`System, Registry, smss, csrss, wininit, winlogon, services, lsass, svchost, fontdrvhost,
  dwm, userinit, explorer`) OR its parent id is 0/4. Flags "orphaned-by-design" rows so a
  caller can de-emphasize them. Not a verdict.

## Scope

### Part A — DTOs (`Models/ProcessDtos.cs`)
- Add `ProcessLineageDto(int Pid, string Name, int? ParentPid, string? ParentName,
  string? CommandLine, DateTime? StartTimeUtc, int? AgeMinutes, bool Orphaned,
  string RuntimeKind, bool IsSystemAdjacent, int RootPid, long MemoryMb)`.
- Add `ProcessGroupDto(int RootPid, string RootName, DateTime? RootStartTimeUtc,
  int DescendantCount, int[] ChildPids)`.
- `ProcessDto` unchanged (plain `list` fast path must not regress).

### Part B — Classifier (pure, testable)
- A pure static (e.g. `ProcessLineage.Classify(IReadOnlyDictionary<int, Win32ProcRow> rows,
  DateTime nowUtc)`) that produces `ProcessLineageDto[]` implementing the algorithm above.
  No I/O — fed the parsed WMI rows and a clock. This is where the unit tests live.
- Grouping helper `ProcessLineage.GroupByRoot(ProcessLineageDto[])` → `ProcessGroupDto[]`.

### Part C — Service (`ProcessService`, `IProcessService`)
- `Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter,
  CancellationToken ct)` — one bulk WMI query → parse rows → `Classify(rows, DateTime.UtcNow)`
  → filter (`orphansOnly` ⇒ `Orphaned==true`; `nameFilter` ⇒ case-insensitive substring match
  on `Name` **or** `CommandLine`) → return.
- `Task<ProcessGroupDto[]> GroupByRootAsync(CancellationToken ct)` — same enumeration →
  `Classify` → `GroupByRoot`.
- **Recycle-safe kill + tree:**
  - Extend `KillAsync(int pid, DateTime? expectedStartUtc = null, CancellationToken ct = default)`:
    when `expectedStartUtc` is supplied, verify the live process's `StartTime` matches (within a
    small tolerance) before killing; abort with a clear error on mismatch (guards against a
    recycled PID from stale list data). Existing callers pass nothing → unchanged behavior.
  - Add `Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct)`:
    take a fresh WMI snapshot, compute the recycle-aware descendant set of `pid`
    (only following parent links validated by the same start-time rule, so we never chase a
    recycled PID into unrelated processes), then kill **leaves-first** and finally the root;
    return the count killed. Verifies the root's `expectedStartUtc` first when supplied.

### Part D — Tool surface (`ProcessTools.Process`)
- New params: `bool includeLineage = false`, `bool groupByRoot = false`, `bool tree = false`,
  `string? startTime = null` (ISO-8601 guard for kill). `name` now also serves as the
  list/orphans filter.
- New action verb: **`orphans`**.
- Dispatch:
  - `action=list`, `groupByRoot=true` → `GroupByRootAsync`.
  - `action=list`, `includeLineage=true` → `ListLineageAsync(false, name)`.
  - `action=orphans` → `ListLineageAsync(true, name)`.
  - `action=list` (plain) → `ListAsync` (unchanged).
  - `action=kill`: parse `startTime` (if given) to `DateTime?`; `tree=true` →
    `KillTreeAsync(pid, start)`; else `KillAsync(pid, start)` / existing name-loop. `confirm:true`
    still required.
- Rewrite the `[Description]` to document actions, the recycle-aware orphan definition, the
  "orphaned is common/by-design" caveat, and the signal fields.

### Part E — Docs + CHANGELOG + version
- Update `docs/architecture/COMPONENTS.md` + `DATAFLOW.md` where the process tool is described
  (new actions/params; tool **count unchanged** — still the one `Process` tool). No version/date
  in architecture docs.
- `CHANGELOG.md` under `## [Unreleased]`.
- Feature → bump plugin version (`plugin.json` + the `windows-mcp` entry in the marketplace
  `marketplace.json`, per the version-gating rule) at release; the binary is rebuilt on redeploy.

## Non-Goals (explicit follow-ups, not this change)

- Spawn enhancements: working directory, environment vars, spawn-and-wait, output capture,
  run-as/elevation. (`StartProcess` stays detached-only.)
- Process **owner/user** (`Win32_Process.GetOwner`), window titles.
- Graceful `CloseMainWindow` termination (kill stays a hard `Kill()`).
- Bulk "kill all orphans matching filter" (too destructive for one flag).
- Any GUI.

## Delivery

Per repo `CLAUDE.md`: `dotnet test` (headless subset `Category!=UIAutomation`) green → rename
the running `dist/WindowsMcp.exe` aside → `dotnet publish src/WindowsMcp -c Release -o dist -r
win-x64 --self-contained -p:PublishSingleFile=true` → bump `_RETRY` in
`~/.claude/local-marketplace/windows-mcp/.mcp.json` → user runs `/reload-plugins` → confirm the
server `StartTime` is later than publish. Then verify live (below).

## Success Criteria

1. `process action:list includeLineage:true` returns `ProcessLineageDto[]` with correct
   `ParentPid/ParentName/CommandLine/StartTimeUtc/AgeMinutes/Orphaned/RuntimeKind/IsSystemAdjacent/RootPid`.
2. `process action:orphans` returns only `Orphaned==true` rows, annotated; `name:"node"` filters
   by name **or** command-line substring.
3. `process action:list groupByRoot:true` returns `ProcessGroupDto[]` that reproduces today's
   finding — the ~5 `claude.exe` roots each with their child fleet — in **one call**.
4. Recycle-aware correctness: a synthetic row set where a parent PID is alive but younger than
   its child marks the child `Orphaned==true` (unit test); a genuinely-parented child is not.
5. `process action:kill pid:<p> tree:true confirm:true` kills `<p>` and its live descendants
   (leaves-first) and returns the count; `startTime:` mismatch aborts without killing.
6. Plain `list` output and existing kill behavior are byte-for-byte unchanged (no regression).
7. `dotnet test` green (headless subset); build clean under `TreatWarningsAsErrors=true`.
8. Live server (post-redeploy) answers `orphans` / `groupByRoot` correctly.

## Verification (tests + live)

- **Unit (pure classifier, mocked `IWmiService`):** dead-parent orphan; recycled-parent orphan
  (alive-but-younger); normal child not orphan; multi-level `RootPid` walk; cycle guard;
  null-`CreationDate` boot process (age null, not spuriously orphaned); each `RuntimeKind`
  mapping; `IsSystemAdjacent`; `GroupByRoot` counts/children; `nameFilter` on name and cmdline;
  kill-tree descendant-set computation (pure) + start-time guard mismatch.
- **Integration:** `ListLineageAsync` includes the current process with a real parent;
  kill-tree against a spawned `cmd.exe` that launches a child, asserting both die.
- **Live:** call `orphans` and `groupByRoot` on the reloaded server; confirm the 5-session /
  episodic-memory-pair picture from this session reproduces in one call.
