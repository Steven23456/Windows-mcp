# startup_report — design & implementation plan

## Goal
Add a `startup_report` MCP tool to Windows-mcp that produces a HiJackThis-style
**boot/persistence report** as structured JSON plus a readable text rendering,
returned to the caller (no file I/O). Pure native C# — no PowerShell, no new
NuGet dependencies.

## Why
HiJackThis dumps raw registry *presence* but does not decode whether a Run entry
is actually **enabled** (that lives in a separate `StartupApproved` binary blob),
does not flag **missing target files**, and labels Microsoft-signed components as
"Unknown". This tool encodes those joins as first-class columns.

## Scope (Core + persistence)
Sections: header (machine/OS/elevated/timestamp), processes, Run/RunOnce entries
(HKCU/HKLM/WOW6432Node, joined with enabled state + target-exists + signer),
Startup-folder entries, scheduled tasks (logon/boot-relevant, action path +
target-exists), auto-start services, hosts file, Winsock LSP providers (with
signer), shell icon-overlay / context-menu handlers (CLSID→DLL + signer).

Out of scope (v1): file output, diff mode, legacy IE browser sections (BHO/
toolbar/search-scope/zones).

## Architecture (fits Service → Abstraction → Tool → DTO layering)
- `StartupTools` (`[McpServerToolType]`) — one `startup_report` tool method;
  returns `JSON + "\n\n" + text rendering`.
- `IStartupReportService` / `StartupReportService` — orchestrates sections,
  assembles `StartupReportDto`; per-section try/catch so one failure attaches an
  error note to that section only.
- `ILspEnumerator` / `LspEnumerator` — `ws2_32` P/Invoke (`WSCEnumProtocols` +
  `WSCGetProviderPath`) behind an interface (fakeable).
- `IAuthenticodeInspector` / `AuthenticodeInspector` — `path → {signed, signer}`
  via `X509Certificate.CreateFromSignedFile`.
- `StartupReportRenderer` — pure `StartupReportDto → string`.

## Changes to existing code
1. `IRegistryService`: add `EnumerateValuesAsync(hive, path)` →
   `RegistryValueDto[]` and `EnumerateSubKeysAsync(hive, path)` → `string[]`.
2. `ITaskSchedulerService`: add `ListDetailedAsync()` → `ScheduledTaskDetailDto[]`
   (Name, Path, State, ActionPath, ActionArguments, TriggerTypes) using the
   already-referenced `Microsoft.Win32.TaskScheduler` library. Existing
   `ScheduledTaskDto` and `scheduled_task` tool are untouched.
3. `Program.cs`: register `IRegistryService` already present; add singletons for
   `ILspEnumerator`, `IAuthenticodeInspector`, `IStartupReportService`.

## DTOs (`Models/StartupReportDtos.cs`)
- `StartupReportDto(StartupHeader Header, RunEntry[] RunEntries, StartupFolderEntry[] StartupFolders, StartupTaskEntry[] ScheduledTasks, StartupServiceEntry[] Services, ProcessEntry[] Processes, HostsEntry[] Hosts, LspProviderEntry[] Lsp, ShellExtensionEntry[] ShellExtensions, string[] Errors)`
- `StartupHeader(string Machine, string OsVersion, bool Elevated, DateTime TimestampUtc)`
- `RunEntry(string Hive, string KeyPath, string Name, string Command, bool Enabled, bool TargetExists, string? Signer)`
- `StartupFolderEntry(string Scope, string FileName, string Target, bool Enabled, bool TargetExists)`
- `StartupTaskEntry(string Path, string State, string? ActionPath, string? ActionArguments, string[] Triggers, bool TargetExists)`
- `StartupServiceEntry(string Name, string DisplayName, string Status, string StartType, string? BinaryPath)`
- `ProcessEntry(int Pid, string Name, string? Path, long MemoryMb, string? Signer)`
- `HostsEntry(string Ip, string Host)`
- `LspProviderEntry(int CatalogId, string Description, string? ProviderPath, string? Signer)`
- `ShellExtensionEntry(string Category, string Clsid, string? Dll, string? Signer)`

## Helpers / rules
- `StartupApproval.IsEnabled(byte[]? flag)`: `flag is null || (flag[0] & 1) == 0`.
  (StartupApproved byte0 even=enabled, odd=disabled; absence = enabled.)
- `targetExists`: resolve the exe out of a command string (handles quotes,
  rundll32 wrappers best-effort) and `File.Exists`.

## Commit plan (atomic, TDD each)
0. docs: this spec.
1. `IRegistryService` enumeration (+impl, integration tests).
2. `ITaskSchedulerService.ListDetailedAsync` (+DTO, +impl, read-only integration test).
3. `IAuthenticodeInspector` (+impl, integration test vs kernel32 + missing file).
4. `ILspEnumerator` (+impl P/Invoke, integration test asserts MSAFD providers).
5. Report DTOs + `StartupApproval` decode + `StartupReportRenderer` (unit tests).
6. `StartupReportService` orchestration (unit tests with faked gatherers:
   enabled-join, target-exists, autostart/service filter, task trigger filter,
   per-section error isolation).
7. `StartupTools` tool + DI registration (unit test mocks `IStartupReportService`).
8. docs (architecture COMPONENTS/OVERVIEW) + CHANGELOG + version bump + final
   full-suite gate.

## Error handling
Per-section try/catch; failures append to `StartupReportDto.Errors` and that
section returns empty rather than failing the whole report. Unelevated runs lose
some HKLM/task detail; the header `Elevated` flag explains thin sections.

## Testing
- Unit (fast, faked/pure): StartupApproval decode, renderer, StartupReportService
  composition + error isolation, StartupTools serialization.
- Integration (read-only, real OS): registry enumerate, task ListDetailed,
  Authenticode vs kernel32, LSP enumerate.
- Gate per commit: `dotnet test --filter` on affected classes; full suite before
  final push.

## Risk
LSP P/Invoke is the riskiest piece. Native fallback if it misbehaves: read the
Winsock catalog from `HKLM\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\
Protocol_Catalog9\Catalog_Entries` (registry, already enumerable).
