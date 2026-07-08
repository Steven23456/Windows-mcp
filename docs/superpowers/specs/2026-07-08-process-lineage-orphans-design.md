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
- `IProcessService`: `ListAsync`, `KillAsync`, `StartDetachedAsync`, `InspectAsync`. **Consumers
  (dependency-graph + grep):** injected by `ProcessTools` **and `StartupReportService`**;
  implemented only by `ProcessService` (sealed); doubled only by Moq mocks (`ProcessToolsTests`,
  `StartupReportServiceTests`) — so interface *additions* are safe, but existing *signatures*
  must stay byte-stable. `IWmiService` is shared by five services → do not modify it.
- **`WmiService.QueryAsync`** builds `SELECT * FROM <class>` (null WHERE ⇒ bulk enumeration,
  all columns) and returns each row as `Dictionary<string, object>` of raw `PropertyData.Value`.
  **Critical:** `Win32_Process.CreationDate` comes back as a raw **CIM_DATETIME string**
  (`yyyyMMddHHmmss.ffffff±ooo`, e.g. `20260708070935.590000-300`) — *not* a parsed `DateTime`
  (confirmed: the `system_info` OS query returns `InstallDate` in this exact form). Numeric
  columns like `WorkingSetSize` box as `ulong`/`string`. This is precisely why the existing
  `InspectAsync` reads `Process.StartTime` instead of the WMI date — a per-PID luxury the bulk
  path cannot afford (484 live `Process` objects = handle cost), so the bulk path **must parse
  CIM_DATETIME itself**.
- Tests: `IWmiService` is mockable (Moq); `WmiService` used real for lineage. Conventions:
  DTOs are `record`s, services `sealed`, tools `async Task<string>` returning JSON,
  `TreatWarningsAsErrors=true`.

## The orphan algorithm (recycle-aware) — the core primitive

One bulk `Win32_Process` WMI enumeration (no WHERE). The service first **parses each raw WMI
dictionary into a typed row** `Win32ProcRow(int Pid, int ParentPid, string Name,
DateTime? CreationUtc, string? CommandLine, long MemoryMb)` — this is the I/O boundary where the
messy coercions live: `CreationUtc` = parse the **CIM_DATETIME** string via
`ManagementDateTimeConverter.ToDateTime(...).ToUniversalTime()` (null on missing/unparseable —
e.g. System PID 4 / Idle PID 0 expose none); `MemoryMb` = `Convert.ToInt64(WorkingSetSize)/1MiB`
tolerating `ulong`/`string`. The pure classifier then operates on `Win32ProcRow[]` + an injected
`nowUtc` only — **no string dates, no WMI types** — so tests feed real `DateTime`s. For each
process **P** (typed row) with parent id **Q**:

- **`parentAlive`** = `map.ContainsKey(Q)` **AND** *not provably recycled*, where "provably
  recycled" = both `CreationUtc` values are non-null **and** `parent.CreationUtc > P.CreationUtc`
  (the live "parent" started *after* the child ⇒ its PID was reused, true parent gone). If
  either date is null we **cannot prove** recycling, so treat the parent as alive (avoids
  spuriously orphaning boot processes whose parent has no CIM date).
- **`Orphaned`** = `!parentAlive`.
- **`ParentName`** = `parentAlive ? map[Q].Name : null` (null when orphaned — the real parent
  is gone, so we don't report a misleading recycled name).
- **`RootPid`** = walk `P → parent → …` while `parentAlive`, with a visited-set cycle guard and
  a 64-hop depth cap; the top of the chain is the root. An orphaned process is its own root.
- **`AgeMinutes`** = `(nowUtc − P.CreationUtc)` whole minutes, or null when `CreationUtc` is null.

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

### Part B — Row parsing + classifier (pure, testable)
- `Win32ProcRow(int Pid, int ParentPid, string Name, DateTime? CreationUtc, string? CommandLine,
  long MemoryMb)` record + a pure `Win32ProcRow.From(IDictionary<string,object> wmiRow)` that does
  the CIM_DATETIME parse and numeric coercions (returns null/skips rows missing `ProcessId`).
- Pure static `ProcessLineage.Classify(IReadOnlyList<Win32ProcRow> rows, DateTime nowUtc)` →
  `ProcessLineageDto[]` implementing the algorithm above. No I/O, no WMI/string-date types.
- Grouping helper `ProcessLineage.GroupByRoot(ProcessLineageDto[])` → `ProcessGroupDto[]`.

### Part C — Service (`ProcessService`, `IProcessService`)
- `Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter,
  CancellationToken ct)` — one bulk WMI query → `Win32ProcRow.From` each row →
  `Classify(rows, DateTime.UtcNow)` → filter (`orphansOnly` ⇒ `Orphaned==true`; `nameFilter` ⇒
  case-insensitive substring on `Name` **or** `CommandLine`) → return. The filter is applied
  **after** classification so `RootPid` still points at the true (possibly filtered-out) root.
- `Task<ProcessGroupDto[]> GroupByRootAsync(CancellationToken ct)` — same enumeration →
  `From` → `Classify` → `GroupByRoot`.
- **Recycle-safe kill + tree — additive only (blast-radius constraint).** `IProcessService` is
  consumed by **both `ProcessTools` and `StartupReportService`**, implemented only by
  `ProcessService`, and doubled exclusively by Moq mocks (`ProcessToolsTests`,
  `StartupReportServiceTests`). Adding interface members is therefore safe (Moq auto-defaults),
  but **changing the existing `KillAsync(int, CancellationToken)` signature is not** — it would
  ripple into every `.Setup`/`.Verify` and alter a contract `StartupReportService` relies on. So:
  - **Do not touch `KillAsync`.** Add `Task KillGuardedAsync(int pid, DateTime expectedStartUtc,
    CancellationToken ct)`: verify the live process's `StartTime` matches `expectedStartUtc`
    (≈1 s tolerance — CIM_DATETIME resolves to the second) before killing; abort with a clear
    error on mismatch (guards a recycled PID from stale list data).
  - Add `Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct)`:
    take a fresh WMI snapshot, compute the recycle-aware descendant set of `pid` (only following
    parent links validated by the same start-time rule, so we never chase a recycled PID into
    unrelated processes), then kill **leaves-first** and finally the root; return the count killed.
    Verifies the root's `expectedStartUtc` first when supplied.

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
    `KillTreeAsync(pid, start)`; else if `start` given → `KillGuardedAsync(pid, start.Value)`;
    else the existing `KillAsync(pid)` / name-loop (unchanged). `confirm:true` still required.
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

- **Unit — `Win32ProcRow.From`:** parses a real CIM_DATETIME string (`20260708070935.590000-300`)
  to the correct UTC instant; missing/garbage `CreationDate` → null (not throw); `WorkingSetSize`
  as `ulong` and as `string` both coerce; row missing `ProcessId` is skipped.
- **Unit (pure classifier over `Win32ProcRow[]`):** dead-parent orphan; recycled-parent orphan
  (alive-but-younger `CreationUtc`); normal child not orphan; **null-dated parent NOT marked
  recycled**; multi-level `RootPid` walk; cycle guard; null-`CreationUtc` boot process (age null,
  not spuriously orphaned); each `RuntimeKind` mapping; `IsSystemAdjacent`; `GroupByRoot`
  counts/children; `nameFilter` on name and cmdline **preserves `RootPid` of a filtered-out root**;
  kill-tree descendant-set computation (pure) + start-time guard mismatch.
- **Integration:** `ListLineageAsync` includes the current process with a real parent;
  kill-tree against a spawned `cmd.exe` that launches a child, asserting both die.
- **Live:** call `orphans` and `groupByRoot` on the reloaded server; confirm the 5-session /
  episodic-memory-pair picture from this session reproduces in one call.
