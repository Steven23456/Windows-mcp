# Changelog

## [Unreleased]

## [0.4.0] - 2026-06-26

Codebase-audit sweep: fixes every defect a 3-agent audit found, restores the thin-tool pattern
across the last hold-outs, closes the service test-coverage gaps, and adds 8 inspection tools
(tool count 52 → 60). Highlights below.

### Added
- **`file_streams` tool** — NTFS alternate data streams (e.g. `Zone.Identifier`, hidden payloads) on a
  file plus the reparse target if the path is a symlink/junction — forensic checks `file_info` can't
  surface. Reparse target via native `FileSystemInfo.LinkTarget`; ADS via `Get-Item -Stream`. New
  `IFileStreamService`. **Tool count 55 → 60 across this expansion batch** (verify_signature,
  file_hash, defender_status, cert_store, reliability, driver_list, process_inspect, file_streams,
  plus `network ports` enrichment); interfaces/singletons 28 → 32; tool classes 15.
- **`driver_list` tool** — installed PnP device drivers (`Win32_PnPSignedDriver`) with version,
  date, manufacturer, signed-state, and INF name; nameless bus/enumerator stubs filtered out. Old or
  unsigned drivers are a real attack surface (BYOVD). New `IDriverService` (in `SystemTools`).
- **`reliability` tool** — system stability snapshot: crash minidumps in `C:\Windows\Minidump`
  (name/size/time) plus recent `Win32_ReliabilityRecords` failure entries (app/OS/hardware), for
  BSOD/instability investigation. New `IReliabilityService` (in `SystemTools`).
- **`cert_store` tool** — enumerate a Windows certificate store (LocalMachine/CurrentUser × Root/CA/My/…)
  via native `X509Store`; each cert reports subject/issuer/thumbprint/expiry and self-signed + expired
  flags. Surfaces rogue/MITM root CAs (a self-signed cert in Root). New `ICertStoreService`.
- **`defender_status` tool** — Microsoft Defender posture via `Get-MpComputerStatus`: real-time
  protection, tamper protection, behavior monitoring, signature version + last-updated, and last
  quick/full scan times. Null fields + a `Note` when Defender is disabled/replaced. (`SecurityTools`.)
- **`process_inspect` tool** — deep per-process detail: parent PID and command line (via WMI),
  start time, and the **loaded-module (DLL) inventory** (via the live process) — the core signal for
  spotting injected/sideloaded DLLs and tracing process lineage. The module list degrades gracefully
  (`ModulesError` set) for protected/higher-integrity processes. `ProcessService` gained an
  `IWmiService` dependency. Tool count 54 → 55.
- **`verify_signature` tool** (new `SecurityTools` class) — exposes the existing catalog-aware
  `AuthenticodeInspector` standalone, so any file path (a suspicious process binary, an unknown
  autostart entry) can be checked for code-signing trust, not just files surfaced by `startup_report`.
  Returns `{trusted, signer}`.
- **`file_hash` tool** — SHA256 (default), SHA1, or MD5 hex digest of a file, for integrity checks
  and IOC lookups (the internal hasher was MD5-only and unexposed). Tool count 52 → 54.

### Changed
- **`network ports` now reports the owning process (PID + name)** — `PortInfoDto` gained
  `OwningPid`/`ProcessName`, the single most useful field for "who owns this connection". The
  managed `IPGlobalProperties` API doesn't expose it, so `ListPortsAsync` now uses
  `Get-NetTCPConnection` (uniform IPv4/IPv6, with state), kept as a single-pipeline script so it
  survives the `-Command -` stdin path (a multi-statement version returns empty — the storage_health
  failure class). `ParsePorts` is white-box unit-tested.
- **Test coverage added for previously-untested services** — `WebService` SSRF guard
  (`IsPrivateAddress` widened to internal for white-box testing: loopback / RFC1918 / link-local /
  unique-local / IPv4-mapped-IPv6 evasion + public-address allow, plus loopback-URL and malformed-URL
  rejection through `ScrapeAsync`), and `NetworkService` (adapters, ports, loopback ping, DNS,
  unresolvable-host failure, wifi placeholder). `ProcessService`/`WmiService` coverage landed with
  the disposal fixes above.
- **`firewall` logic extracted into `IFirewallService`/`FirewallService`** — `NetworkTools.Firewall`
  built inline PowerShell for list/add/remove and returned raw stdout, so it was untestable. Now
  behind a service: `list` returns typed `FirewallRuleDto[]` (enum fields rendered as strings,
  handling both the single-object and array shapes `ConvertTo-Json` produces), and add/remove throw
  on cmdlet failure. `NetworkTools` no longer depends on `IPowerShellService`; the `network` action
  now also forwards a `CancellationToken`. This also closes the R4 empty-output-guard item (the
  reclaimable guard landed with the disk refactor; the firewall path is now typed + failure-checked).
  Unit-tested via `InternalsVisibleTo` (`ParseRules` single/array/blank) + mocked list/add paths.
- **`security_audit` logic extracted into `ISecurityService`/`SecurityService`** — `SystemTools`
  embedded the audit PowerShell inline and returned raw stdout (with a hardcoded JSON fallback
  literal), so the success path was untestable. Now behind a service that parses into a typed
  `SecurityAuditDto` (firewall/Defender/UAC/BitLocker, plus a `Note` when all probes fail). Tool is
  a thin wrapper; `SystemTools` no longer depends on `IPowerShellService`. Unit-tested via a mocked
  shell (parse, empty-output note, partial results).
- **`disk_inspect` logic extracted into `IDiskService`/`DiskService`** — the aggregation (top-dir
  usage, file-type grouping, stale-file filtering) and the reclaimable-space PowerShell lived
  directly in `DiskTools`, making it untestable. Now behind a service returning typed DTOs
  (`DiskUsageEntry`, `FileTypeEntry`, `StaleFileEntry`, `ReclaimableSpace`); the tool is a thin
  serialize wrapper that forwards a `CancellationToken`. Fixes a latent bug along the way: the
  reclaimable script used PowerShell 7 `??` null-coalescing, which is a parse error under the
  `powershell.exe` (5.1) the server invokes — rewritten 5.1-safe — and the result is now parsed
  into a typed DTO with an empty-output guard (the storage_health failure class). Unit-tested via
  `InternalsVisibleTo` (FormatBytes/GetTopLevelDir) + mocked aggregation/parse paths.
- **`CancellationToken` now plumbed from tools into the service layer** for `powershell`, the
  process/service/scheduled-task/event-log tools, and the file tools — the services already
  accepted a token but the tools dropped it, so MCP-framework cancellation never reached a running
  operation (a long `powershell`/`file_search` couldn't be cancelled). (`DiskTools`/`NetworkTools`/
  `SystemTools` get the same treatment as part of their service refactors.)

### Fixed
- **`storage_health` temp-script invocation broke on a username containing an apostrophe** — the
  staged `.ps1` path was interpolated into a single-quoted PowerShell string (`& '{path}'`) without
  escaping, so a profile path like `C:\Users\O'Brien\…` corrupted the command. Now doubles `'`.
- **PowerShell gate could be held forever by a runaway script, wedging every PS-backed tool** — the
  shared no-timeout `SemaphoreSlim` serializes all PowerShell calls, so one stuck child (e.g. an
  accidental infinite loop) blocked audio/notification/firewall/disk/system/storage indefinitely.
  `PowerShellService` now runs every call under a linked CTS with a generous 10-minute backstop
  (longer than any legitimate caller budget) that tears the child down. Also fixed an orphan window:
  the process-kill cancellation callback is now registered **before** the stdin write, so a cancel
  mid-write still kills the child instead of leaking it. New tests cover both the backstop and
  caller-token cancellation.
- **`file_search find_duplicates` aborted entirely on one locked/denied file** — `HashFile` threw
  `IOException`/`UnauthorizedAccessException` out of the LINQ grouping, killing the whole search.
  It now returns null for unreadable files and they're skipped from dedup (covered by a new test
  that holds a duplicate open exclusively).
- **`power_action` (shutdown/reboot) reported success on a privilege-blocked no-op** — `ExitWindowsEx`
  silently fails unless `SeShutdownPrivilege` is *enabled* in the process token, and the return value
  was ignored, so the tool returned "executed" while nothing happened. Now enables the privilege via
  `AdjustTokenPrivileges` before shutdown/reboot, and **every** power action checks its native return
  value and throws a `Win32Exception` (or a clear "privilege not held" error) on failure.
- **`get_table` always returned empty column headers** — `UIAutomationService.GetTableAsync`
  allocated `headers[cols]` but never populated it (GridPattern has no header concept). Now reads
  column headers from the element's TablePattern (`ColumnHeaders`) when supported.
- **Native handle / COM-object leaks in the process and WMI paths** (found by a codebase audit):
  - `ProcessService.ListAsync` never disposed the `Process` wrappers returned by
    `Process.GetProcesses()` — and touching `WorkingSet64`/`MainModule` opens a kernel handle per
    wrapper. Since `startup_report` also walks this path, handles accumulated on every call. Now
    disposed in a `try/finally` after projecting to DTOs. `KillAsync`/`StartDetachedAsync` and
    `WindowService.LaunchAsync` likewise `using`-dispose their `Process` wrappers (the detached
    child keeps running; only our handle is released).
  - `WmiService.QueryAsync` disposed only the `ManagementObjectSearcher`, leaking the
    `ManagementObjectCollection` and every COM-backed `ManagementObject` row on each
    `wmi_query`/`system_info` call. Now disposes the collection and each row.
  - Added `ProcessServiceTests` + `WmiServiceTests` (these services previously had no direct
    coverage) as regression guards for the behavior across the disposal change.

## [0.3.1] - 2026-06-26

### Fixed
- **`storage_health` returned empty / timed out against the live MCP server** — two defects that
  only end-to-end testing could surface (unit tests mock the shell):
  1. The large generated script silently produced **no output over `powershell -Command -`
     (stdin)**, the path `IPowerShellService` uses — though it ran fine as a `.ps1` file (proven
     head-to-head: 0 bytes via stdin vs 4714 bytes via `-File`). Fix: `StorageService` now stages
     the script to a temp `.ps1` and invokes it as a file (a reliable one-liner over stdin),
     cleaning up after.
  2. `Get-PhysicalDisk` + per-disk SMART **wake sleeping USB/SD devices** and can take minutes
     (or wedge the storage stack under repeated aborts), blowing any fixed timeout. Fix: physical
     disks + SMART reliability are now **opt-in** under `include_usage`; the **default** path stays
     on fast storage-stack metadata that never wakes a device — `Get-Disk` (now also carrying
     **bus type**), `Get-Partition`, `MSFT_Volume`, and the event log. Default budget 30→45s.
- Both paths are now **end-to-end verified against the live MCP server**: the fast default
  returns disks/volumes/events without waking devices (`PhysicalDisks: []`, `UsageProbed:false`),
  and `include_usage:true` adds real SMART (per-disk temperature/power-on-hours/uncorrected
  errors) and per-volume free space (`UsageProbed:true`).

## [0.3.0] - 2026-06-25

### Added
- **`storage_health` MCP tool** — disk/drive HEALTH diagnostics (distinct from `disk_inspect`'s
  usage analysis): physical disks (model, bus/media type, SMART `HealthStatus` + reliability
  counters — temperature, power-on-hours, uncorrected read/write errors), per-disk online/offline
  + health, the volume→disk/partition map (filesystem, label, health), and recent disk-stack
  Error/Warning events. **Metadata-first and hang-safe:** free space (which stalls on
  slow/sleeping/USB drives) is only probed with `include_usage:true`, each probe time-boxed in an
  in-process PowerShell runspace, under an overall `CancellationToken` budget that tears down a
  wedged shell. Backed by `IStorageService`/`StorageService` (the embedded PowerShell was
  validated live on real hardware first). `drive_letter` scopes the volumes section.
  - **Docs:** architecture counts refreshed — 51→52 tools across 13→14 tool classes; corrected the
    stale OVERVIEW interface/singleton count (20→25) to match the 25 registered services.
  - Added `InternalsVisibleTo("WindowsMcp.Tests")` so pure helpers (e.g. `StorageService.BuildScript`)
    can be white-box unit-tested.
- **`startup_report` Control Panel parity + `summary` format** (the two follow-ups from the
  HiJackThis comparison):
  - Control Panel applets are now also discovered by scanning `System32` / `SysWOW64` for
    `*.cpl` files (deduped against the `Cpls` registry key), catching vendor applets dropped
    straight into the system dirs that the registry key omits (e.g. Xerox `xrxscn`). The DTO
    field `ControlPanelAppletEntry.Hive` is renamed `Source` (registry hive or directory).
  - New `format=summary` (now the **default**): section counts + only the flagged entries
    (untrusted code-signing or missing target) + proxy/trusted-zone — a compact, inline-able
    triage view instead of the full report spilling to a file. `format=json|text|both` still
    return the complete report. COM-handler scheduled tasks (no exec action) are not flagged
    as missing/untrusted (they have no executable to verify).
- **`startup_report` coverage expansion** (from a HiJackThis-vs-tool gap comparison) — new
  sections, each signer-annotated where file-backed: **DNS servers**, **per-user `HKU\<SID>`
  Run/RunOnce** entries, **Control Panel applets**, **Accessibility-AT `StartExe` hooks**,
  **Image File Execution Options** (`Debugger`/`VerifierDlls`), **Winlogon hooks**
  (`Shell`/`Userinit`/`Taskman`/`VmApplet`), **AppInit_DLLs**, **Active Setup `StubPath`**
  (HKLM + HKCU), **browser proxy** (ProxyEnable/ProxyServer/PAC), and **Trusted/zoned sites**.
  Header now also reports boot mode, signed-in user, and default browser. IFEO/Winlogon/
  AppInit/Active Setup are persistence vectors neither HiJackThis nor the prior report covered.
  - **Size controls:** `startup_report(includeProcesses=false, format=json|text|both)`. The
    process inventory (largest, least persistence-relevant section) is now opt-in, and the
    default `json` format avoids the duplicated text rendering — keeping the default response
    small instead of spilling to a file.
  - `IRegistryService.EnumerateSubKeysAsync`/`EnumerateValuesAsync` now explicitly enumerate
    the hive root for an empty path (used for `HKU\<SID>` discovery) without disposing the
    predefined base key.
- **`startup_report` MCP tool** — a HiJackThis-style, read-only boot/persistence report
  (structured JSON + a readable text rendering). Covers running processes, Run/RunOnce keys
  with effective enabled/disabled state, Startup folders, startup-relevant scheduled tasks,
  auto-start services, the hosts file, Winsock LSP providers, and shell extensions — every
  file-backed entry annotated with a catalog-aware code-signing trust flag. Built from these
  composable pieces:
  - `IRegistryService.EnumerateValuesAsync` / `EnumerateSubKeysAsync` — enumerate all
    values under a key (with data + kind, including binary blobs like `StartupApproved`)
    and immediate sub-key names; return an empty array for a missing key.
  - `ITaskSchedulerService.ListDetailedAsync` + `ScheduledTaskDetailDto` — list tasks
    across all folders with exec-action path/arguments and trigger types; tolerant of
    protected/corrupt task definitions.
  - `IAuthenticodeInspector` + `AuthenticodeInfo` — catalog-aware code-signing check via
    WinVerifyTrust (correctly trusts catalog-signed Windows/driver components, not just
    embedded signatures) plus the embedded signer subject when present.
  - `ILspEnumerator` + `LspProviderDto` — enumerate the Winsock 2 service-provider catalog
    (base providers + layered service providers / LSPs) via `WSCEnumProtocols` /
    `WSCGetProviderPath`, with provider DLL paths resolved.
  - Report DTOs (`StartupReportDto` + section records) and pure helpers: `StartupApproval`
    (decodes the StartupApproved enabled/disabled flag), `CommandTarget` (resolves an exe
    from a command line incl. unquoted paths with spaces, and tests existence), and
    `StartupReportRenderer` (section-grouped text rendering).
  - `IShortcutResolver` + `ShortcutResolver` — resolve `.lnk` targets via `IShellLink` COM
    (for the Startup-folder section).
  - `IStartupReportService` + `StartupReportService` — orchestrates all sections into a
    `StartupReportDto`: Run-key/Startup-folder enabled-state joins (StartupApproved),
    file-missing detection, auto-start service filtering (with ImagePath signer), logon/boot
    or missing-target task filtering, hosts parsing, LSP + shell-extension (CLSID→DLL)
    enumeration, all signer-annotated. Per-section failures are isolated into an errors list.
  - `StartupTools.startup_report` MCP tool wiring + DI registration of the new services
    (`IAuthenticodeInspector`, `ILspEnumerator`, `IShortcutResolver`, `IStartupReportService`).

### Changed
- **Docs**: added `todo.md` (cross-session tracker: ready/deferred items, out-of-scope decisions,
  known environmental test flakes); added a "testing a change against the live MCP server" section
  to `CLAUDE.md` (rename-running-exe to republish; bump `.mcp.json` `_RETRY` to force a reload);
  refreshed the `startup_report` description in `docs/architecture/OVERVIEW.md` + `COMPONENTS.md`
  to list all sections, the `summary` default, and the `includeProcesses`/`format` parameters.
- **Docs**: refreshed for `startup_report` — tool/class/service counts (50→51 tools, 12→13
  tool classes, 20→24 services) across `docs/architecture/*` and `README.md`; added the new
  tool, services, interfaces, and DTOs; updated the `IRegistryService` / `ITaskSchedulerService`
  method lists. Rewrote the root `CLAUDE.md`, which still described the retired Python
  architecture, to reflect the current C#/.NET 9 design, build/test commands, and conventions.
- **`tools/create-dependency-graph`**: Added C# language support (auto-detects via `.sln` at
  project root). New functions: `detectProjectLanguage`, `getAllCsFiles`, `parseCsFile`,
  `categorizeCsFiles`, `buildCsDependencyMatrix`, `inferCsProjectNames` (auto-discovers
  namespace roots from `.csproj` files — no longer hardcoded). Fixed `detectUnused` to
  return empty results for C# projects (namespace-based imports cannot be resolved to file
  paths). Fixed `Microsoft.Extensions.*` and `Microsoft.Win32.TaskScheduler` misclassified
  as system deps (added `CS_MICROSOFT_NUGET_PREFIXES` blocklist). Added `--lang=auto|typescript|csharp`
  CLI flag with validation. Renamed `Statistics.totalTypeScriptFiles` → `totalSourceFiles`.
- **`docs/architecture/OVERVIEW.md`**: Rewritten for C# architecture — 50 MCP tools, 12 tool
  classes, .NET 9, FlaUI/H.InputSimulator/SkiaSharp dependency table; removed all Python references.
- **`docs/architecture/ARCHITECTURE.md`**: Rewritten with 4-layer C# diagram (MCP Protocol /
  Tool / Service Abstraction / Service Implementation); DI wiring, source-generated tool
  discovery, `Program.cs` startup sequence.
- **`docs/architecture/COMPONENTS.md`**: Rewritten — all 12 tool classes with tool counts and
  injected services; all 20 service interfaces with key methods; model DTO files; NuGet
  package reference table.
- **`docs/architecture/DATAFLOW.md`**: Rewritten with C# sequence diagrams — `GetState` via
  FlaUI UIA3 tree walk, `Click` via `H.InputSimulator.SendInput`, `Powershell` via
  `System.Diagnostics.Process`, `Screenshot`/`Ocr` via SkiaSharp + `Windows.Media.Ocr`,
  `WaitFor` polling loop, DI resolution at startup, `AssertElement` state checks.

### Fixed
- **`startup_report` signer resolution for bare-name targets** (found via e2e): entries whose
  command is a bare program name (e.g. Winlogon `Shell = explorer.exe`) reported `Trusted=false`
  because the signature check ran against a relative path. Added `CommandTarget.ResolveFullPath`
  (PATH-resolve to an absolute path) and use it before every signature check; `CommandTarget.Exists`
  is now a thin wrapper over it.
- **`startup_report` accessibility section noise** (found via e2e): `Accessibility\ATs` on Win11
  contains ~26 feature-setting subkeys whose `StartExe` is a numeric code, not a program. The
  section now reports only entries whose `StartExe` is an actual executable path (the real AT
  tools), matching HiJackThis's behavior.
- **`AuthenticodeInspector` catalog verification** (found via `startup_report` end-to-end
  validation): catalog-signed Microsoft components (e.g. `cscui.dll`, `ntshrui.dll`, most
  shell-extension DLLs and many services) were reported `Trusted=false` — the exact
  false-positive the catalog-aware check exists to prevent. Root cause: `WINTRUST_CATALOG_INFO.hCatAdmin`
  was left null, so WinVerifyTrust assumed SHA-1 and failed to match SHA-256 catalog members.
  Now passes the catalog-admin handle. Added a catalog-only test (`cscui.dll`) that exercises
  the path embedded-signed files like `kernel32.dll` never reach.
- **`CommandTarget.Exists` PATH resolution** (found via `startup_report` e2e): a bare program
  name in a task action (e.g. `powershell.exe`, which lives in `System32\WindowsPowerShell\v1.0`,
  not `System32`) was reported as a missing target. `Exists` now resolves bare names against
  the `PATH` directories, not just `System32`.
- **`UIAutomationService.GetStateAsync`** now roots the element tree at the **foreground
  top-level window** (falling back to the focused element, then the desktop) instead of the
  focused element directly. A focused leaf control (a text box, a button) has no children, so
  the previous behavior returned an empty, near-useless tree — and made
  `GetStateAsync_returns_tree_with_notepad_root` non-deterministic (it only passed when focus
  happened to land on a container). The `NotepadFixture` now also foregrounds Notepad. The full
  test suite is green with no category exclusions (79/79).

### Security
- **`tools/` dev dependencies**: `npm audit fix` in `chunking-for-files` and
  `create-dependency-graph` resolved 3 high-severity transitive advisories (`tar`,
  `picomatch` via `tinyglobby`). Both packages now report 0 vulnerabilities; the
  dependency-graph tool still builds (`tsc`) and runs. Lockfiles only — no `package.json`
  changes.

## [0.2.0] - 2026-05-26

### Changed
- **Complete rewrite from Python to C# on the official ModelContextProtocol SDK (1.0.0).**
  Same server identity (`Windows-mcp` in `.mcp.json`); single self-contained
  `dist/WindowsMcp.exe` replaces the venv-launched `python main.py`.
- Tool names normalized to snake_case (e.g. `Click-Tool` → `click`,
  `Find-Element-Tool` → `find_element`).
- Version reset from 0.8.x (Python) to 0.2.0 (C#) to signal the platform break.
  The Python source tree is preserved in
  `legacy/python-pre-csharp-conversion-archive-2026-05-26.zip` for reference;
  the active codebase is C# under `src/WindowsMcp/` + `src/WindowsMcp.Abstractions/`.

### Added (9 new tools beyond the Python set)
- `file_read`, `file_write`, `file_info` — file content primitives
- `http_request` — REST/HTTP client (beyond HTML scraping)
- `wmi_query` — structured WMI queries
- `env` — environment variable get/set/list (secret-named values redacted
  by default; `include_secrets:true` opts out)
- `power_action` — shutdown/reboot/logoff/lock/sleep/hibernate
  (`confirm: true` required)
- `firewall` — list/add/remove Windows Firewall rules
- `archive` — zip/unzip
- `service` — Windows service control
- `scheduled_task` — Task Scheduler control
- `event_log` — Windows Event Log query
- `registry_get`, `registry_set` — registry access

### Consolidated
- `Checkbox-Toggle-Tool` + `Select-Option-Tool` → `interact_element`
- `File-Search-Tool` + `Duplicate-Finder-Tool` → `file_search`
- `Disk-Analysis-Tool` + `Disk-Cleanup-Tool` + `Storage-Tool` → `disk_inspect`
- `Move-Tool` + `Hover-Tool` → `hover` (with `duration_ms: 0`)

### Removed (4 tools dropped from Python set)
- `Wait-Tool` — pure sleep; LLM can space its own calls
- `Compare-Screenshot-Tool` — niche QA tool
- `Record-Replay-Tool` — LLM is the orchestrator
- `Command-History-Tool` — session-scoped PowerShell history

### Fixed (during cutover audit)
- **Stdout JSON-RPC responses lost under cp1252 default encoding.**
  `Console.OutputEncoding` now explicitly set to UTF-8 before
  `Host.CreateApplicationBuilder` so the SDK's `StreamWriter` has
  `AutoFlush=true` and JSON responses reach the pipe (commit `ed76faa`).
- **PowerShell SDK incompatible with `PublishSingleFile=true`** —
  `InitialSessionState.CreateDefault2` deep-called `Path.Combine(null)`
  because `Assembly.Location` returns "" in single-file mode. Replaced
  the `System.Management.Automation` runspace with a `Process.Start
  ("powershell.exe", ...)` shell-out. Same `IPowerShellService` API;
  ~30 MB smaller binary (commit `0e43215`).
- **`env(list)` leaked secret-named environment variables** verbatim
  into LLM transcripts. Now redacts vars matching
  `KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT` to
  `***REDACTED***` by default (commit `0897dbc`).
- **`firewall(list)` returned 187 KB / 5910 lines** by default,
  overflowing MCP token limits. Added `name_like` filter and `max`
  cap; defaults to enabled rules only (commit `0897dbc`).
- **`security_audit` returned empty string** when probes ran
  unelevated. Each probe now in its own try/catch; always emits a
  JSON object with `null` fields where probes failed (commit `0897dbc`).
- **DPI awareness** set to per-monitor-V2 at process start so
  screenshots capture physical pixels on HiDPI displays
  (commit `ed7ba42`).
- **UI Automation calls** properly marshaled to a dedicated STA thread
  via `BlockingCollection<Action>` work queue (Task 7).
- `humancursor` 3-second startup cost removed (uses straight
  `SendInput` via `H.InputSimulator`).

### Backlog (v0.3.0 candidates)
- Native AOT compilation (blocked on FlaUI reflection)
- CI / GitHub Actions
- Real `audio(get/set/mute)` via NAudio or AudioDeviceCmdlets
  (current SendKeys backend is ±2% imprecise and can't read mute state)
- `multi_monitor` device-name lookup via `EnumDisplayDevices`
  (current `MONITORINFOEXW` is internal in CsWin32 0.3.x; synthesizes
  `Monitor{N}` names)
- Real WiFi info via `Windows.Networking.Connectivity` (currently a
  placeholder)
- LRU eviction for `UIAutomationService` element-id cache
  (currently grows unbounded per session)

## [0.8.5] - 2026-05-01

### Changed
- **Replaced GPL-2.0-or-later `Levenshtein` dependency with MIT-licensed `rapidfuzz` (3.0+).** Drop-in replacement for fuzzy app-name matching in `src/desktop/__init__.py` (`launch_app`, `switch_app`, `manage_window`). The transitive `python-Levenshtein` package was the only GPL/copyleft contamination in the dependency graph; removing it eliminates distribution-friction risk for any future commercial redistribution. The score scale (0-100) is preserved between `fuzzywuzzy.process.extractOne` and `rapidfuzz.process.extractOne`, but the return shape changed from 2-tuple `(match, score)` to 3-tuple `(match, score, index)`; call sites updated to use index access (`matched[0]`) instead of unpacking. `fuzzywuzzy>=0.18.0` and `python-levenshtein>=0.27.1` removed from `pyproject.toml`; `rapidfuzz>=3.0` added.

### Tests
- Added `tests/test_fuzzy_matching.py` (9 passing) — characterization tests pinning the observable behavior of fuzzy matching across the migration. Covers `process.extractOne` API contract (return shape, list/dict_keys inputs, empty-input handling) plus method-level coverage of `Desktop.launch_app`, `Desktop.switch_app`, and `Desktop.manage_window` to verify the tuple-unpacking pattern survives the swap. Total passing tests: 207 → 216, 0 xfail.

### Documentation
- Add CycloneDX SBOM (sbom.json).

### Security
- **`_sanitize_name` regex hardened against glob and newline injection** (`main.py:2738`): the v0.8.4 sanitizer accepted `*`, `?`, `\n`, `\r`, and `\t`, leaving four documented gaps in the Process-Tool injection-defense surface. Glob wildcards reach PowerShell as literal stars (no expansion in `-Name`, but bypasses the intent of the allow-list) and newlines inside a double-quoted PS string can inject statements on a fresh line. Extended the rejection regex to also match `*`, `?`, `\n`, `\r`, `\t`, `\v`, `\f`, and NUL. Total enforced injection-payload coverage rises from 15 to 19; xfail count drops to 0.

### Tests
- Added `tests/test_process_tool_injection.py` (72 passing) — regression coverage for the v0.8.4 PowerShell-injection fix in Process-Tool. Battery of 15 injection payloads (quote-breakers, command chaining, subshell expansion, pipe-to-attacker, brace/paren smuggling, path-separator smuggling) plus 7 legitimate-name positive cases for both `action="list"` and `action="kill"`. Asserts via mock that `desktop.execute_command` is **not** invoked for any payload, and that legitimate names still produce the expected `Get-Process -Name "<n>"` / `Stop-Process -Name "<n>"` PowerShell strings. Includes direct unit tests on `_sanitize_name` plus 4 hardening-pass payloads (glob `*`, `*.exe`, `\n`, `\r\n`) that the v0.8.5 regex now rejects.

## [0.8.4] - 2026-04-30

### Security
- **High-severity PowerShell injection in Process-Tool** (`main.py:894`, `:915`): the `name` argument was interpolated into double-quoted PS strings (`Get-Process -Name "{name}"`, `Stop-Process -Name "{name}"`) without escaping. A name like `x"; Stop-Process -Name explorer; "` could close the quote and chain arbitrary PowerShell. `validate_text` only enforced length and `validate_command(..., trusted=True)` skipped the operator check. Fixed by passing `name` through the existing `_sanitize_name` helper (rejects `'` `"` `` ` `` `$` `;` `|` `&` `{}` `()` `\` `/`) before interpolation in both call sites.

## [0.8.3] - 2026-04-10

### Fixed
- **Critical**: `default_factory=[]` in `TreeState` dataclass passed a list instance instead of a callable — raises `TypeError` on Python 3.12+ and shares mutable state between instances on older versions
- `get_appwise_nodes` crashes with `KeyError` when Taskbar or Program Manager is not visible (e.g., auto-hide taskbar, Remote Desktop)
- Thread-unsafe `PIL.ImageDraw` in `annotated_screenshot()` — concurrent `draw.rectangle()`/`draw.text()` via `ThreadPoolExecutor` could corrupt annotated images; replaced with sequential loop
- Click-Tool: extra `pg.mouseDown()` before `pg.click()` produced double mouse-down (drag-like behavior); `pg.click()` already handles down+up internally
- `scrape_tool`: unhandled `requests.get()` exceptions (`ConnectionError`, `Timeout`, `HTTPError`, and other `RequestException` subclasses) produced raw tracebacks instead of user-friendly messages
- Redundant `if use_vision` ternary inside already-true `if use_vision:` block in `Desktop.get_state()`
- `get_monitors` return type annotation said `list[dict]` but method returns `dict`
- `get_appwise_nodes` return type annotation missing `ScrollElementNode` from 3-tuple

### Changed
- Added missing `ensure_com()` calls to `clipboard_tool`, `move_tool`, `wait_tool`, `disk_cleanup_tool`
- Renamed misleading variable `is_minimized` → `is_not_minimized` in `Desktop.is_app_visible()`
- Fixed typo `Cordinates` → `Coordinates` in State-Tool output and docs
- Updated stale docs: tool count (14/25 → 45), `FAILSAFE` (False → True), Click-Tool sequence diagram, annotation drawing (parallel → sequential), security section (added input sanitization), command length limit (2000 → 10000)
- Bumped version to 0.8.3 in `pyproject.toml` and `manifest.json`

## [0.8.2] - 2026-04-05

### Fixed
- `_check_allowed_path` failed on drive roots — `C:\` + `os.sep` produced double backslash `C:\\` that never matched
- `_sanitize_path` blocked parentheses, rejecting legitimate paths like `C:\Program Files (x86)`

## [0.8.1] - 2026-04-05

### Changed
- Added `C:\` to `ALLOWED_PATHS` — tools can now access the entire C drive

## [0.8.0] - 2026-04-01

### Added
- **Storage-Tool**: 8-action storage analysis and cleanup tool with preview/execute safety pattern
  - `breakdown`: File type breakdown by extension with sizes and percentages
  - `stale`: Find files untouched for N days, sorted by size
  - `compress` / `compress-run`: Preview then zip stale files into per-folder archives, originals to Recycle Bin
  - `dedup` / `dedup-run`: Preview then remove duplicate files (SHA-256, keep first by path), extras to Recycle Bin
  - `archive` / `archive-run`: Preview then move old files into `_archive/<year>-Q<quarter>/` folders
- Parameters: `path`, `days` (1-3650), `minSizeMB`, `extensions` filter
- 22 new tests in `test_storage_tool.py`, 135 total passing

### Security
- All destructive operations (`compress-run`, `dedup-run`) send files to Recycle Bin via Shell.Application COM — fully recoverable
- `archive-run` uses Move-Item (non-destructive, files are relocated not deleted)
- Preview/execute split: agents must explicitly choose `-run` actions after reviewing previews
- Days parameter clamped to 1-3650, extension validation, ALLOWED_PATHS enforcement

### Changed
- Semicolons inside `{}` now allowed in Powershell-Tool (brace-depth tracking) — enables calculated properties like `@{N='x';E={...}}`

## [0.7.0] - 2026-03-31

### Added
- **Security-Audit-Tool**: Comprehensive Windows security scan — Defender status, Firewall per-profile, UAC level, BitLocker, pending updates, PowerShell execution policy, open shares, Remote Desktop status. Quick boolean summary at top.
- **Network-Tool**: Network diagnostics with 5 actions — `status` (adapters/IPs/gateway/DNS/connectivity), `connections` (active TCP + listening ports with process names), `ping` (with SSRF protection), `dns` (hostname resolution with SSRF protection), `wifi` (SSID/signal/available networks).
- `_validate_hostname()` helper for hostname/IP input validation with SSRF protection
- 26 new tests in `test_security_network_tools.py`

### Security
- Network-Tool `target` parameter validated against `BLOCKED_IP_RANGES` to prevent SSRF
- Hostname input restricted to safe characters (alphanumeric, dots, hyphens, colons)

## [0.6.2] - 2026-03-31

### Fixed
- **Disk-Analysis, File-Search, Process-Tool, File-Manage, Duplicate-Finder**: all returned `Security Error: blocked operator` because `validate_command()` rejected legitimate `;` and `` ` `` in server-generated PS scripts. Added `trusted=True` parameter to skip operator checks for internal scripts while preserving injection protection for user-supplied commands (Powershell-Tool).
- **Disk-Cleanup-Tool**: now also passes through `validate_command(trusted=True)` instead of bypassing validation entirely.
- **Restore missing `mcp.run()` entry point** — accidentally removed in v0.6.0, preventing the MCP server from starting its stdio transport loop.
- Removed `validate_command` mock workarounds from tests (no longer needed with `trusted` parameter).

## [0.6.1] - 2026-03-31

### Fixed
- Fix 17 failing tests in `test_existing_tools.py` — `ensure_com()` (COM thread init) was not mocked, causing `comtypes.CoInitialize()` to crash in test context
- Added `autouse` pytest fixture to mock `ensure_com` across all existing-tool tests
- Synced version across `pyproject.toml`, `manifest.json`, and `CLAUDE.md` (were out of sync at 0.5.3/0.4.1)

## [0.6.0] - 2026-03-30

### Added
- **Disk-Analysis-Tool**: Analyze disk usage for any folder/drive — top subfolders by size, free/used space, file counts. Configurable depth (1-3) and minimum size threshold.
- **Disk-Cleanup-Tool**: Find reclaimable disk space — scans temp files, npm/pip/bun caches, node_modules, Chrome data, Recycle Bin, Windows Update cache. Reports only, does not delete.
- **File-Search-Tool**: Search files by name pattern, extension, size range, and date range. Returns sorted results with sizes and dates.
- **File-Manage-Tool**: File operations — copy, move, rename, info, list. Native PowerShell with `-LiteralPath` for safety.
- **Duplicate-Finder-Tool**: Find duplicate files by size + SHA-256 hash. Reports wasted space per duplicate set.
- Input sanitization helpers: `_sanitize_path`, `_sanitize_name`, `_validate_date`, `_validate_extension`, `_check_allowed_path`
- `validate_command()` security gate applied to all new tools (was previously only on Powershell-Tool)
- `ALLOWED_PATHS` enforcement on all path parameters
- 84 automated tests (46 for new tools, 38 for existing tools including security validation)

### Security
- All user-supplied parameters (path, pattern, dates, extensions, filenames) sanitized before PowerShell interpolation
- `validate_command()` called on assembled PS scripts before execution
- ALLOWED_PATHS checked for all file operation paths
- SHA-256 used for duplicate detection (not MD5)
- Generic error messages returned to callers (no raw PS output disclosure)
- node_modules scan bounded to depth 3 and 20 results max

### Changed
- Synced with origin/main (v0.5.3): includes 10 testing/inspection tools, Start-Process-Tool, Switch-Tool improvements
