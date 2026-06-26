# Windows-mcp — todo

Cross-session task tracker. Done items kept briefly for context; see `CHANGELOG.md` for the
full record.

## ✅ Recently done

- [x] **`startup_report` + `storage_health` released.** `v0.3.0` (`ecafe9d`) shipped both;
  `v0.3.1` (`3f1e75f`, 2026-06-26) is the storage_health live-fix — temp-`.ps1` MCP path +
  opt-in SMART/physical (`include_usage`). **Both storage_health paths E2E-verified against the
  live server** (fast default never wakes devices; deep path returns real SMART + free space).

## 🔧 Audit backlog (2026-06-26 — 3-agent codebase audit; full sweep approved)

Ordered for safety/atomicity. Each is its own dev-workflow task + atomic commit.

### Batch 1 — clear defects
- [x] **D1 ProcessService handle leak** (`ProcessService.cs:13-21,29,74`) — `Process` objects from
  `GetProcesses`/`GetProcessById`/`Start` never disposed → native handle leak per `process list` /
  `startup_report`. Wrap in `using`/dispose after projecting DTO.
- [x] **D2 WmiService COM leak** (`WmiService.cs:20-26`) — `ManagementObjectCollection` + each
  `ManagementObject` not disposed. Dispose collection + per-row objects.
- [x] **D3 WindowService Process leak** (`WindowService.cs:73`) — `Process.Start` result not disposed.
- [x] **D4 get_table empty headers** (`UIAutomationService.cs:272-283`) — `headers[]` allocated, never
  populated; table always returns null headers. Populate from header cells.
- [x] **D5 PowerAction false-success** (`PowerService.cs:16-22`) — `SE_SHUTDOWN_NAME` never enabled,
  `ExitWindowsEx` bool ignored → unelevated no-op reported as "executed". Enable privilege; throw on false.
- [x] **D6 HashFile aborts find_duplicates** (`FileSystemService.cs:105-119`) — locked/denied file throws
  out of the grouping and kills the whole search. Guard per-file, skip failures.
- [x] **D7 PowerShell orphan-on-cancel** (`PowerShellService.cs:57-66`) — `ct.Register(kill)` installed
  after stdin write; cancel during write orphans the child. Register kill before the write.

### Batch 2 — cross-cutting (both agents flagged)
- [x] **X1 PowerShellService default timeout** — the no-timeout `SemaphoreSlim` gate lets one runaway
  script wedge ALL PS-backed tools. Add a default per-call timeout (the storage budget pattern).
- [x] **X2 Plumb CancellationToken through tools** — services accept `ct`; most tools drop it
  (`ShellTools`, `DiskTools`, `NetworkTools`, `ProcessTools`, `FileTools`). Add `ct` params + forward.

### Batch 3 — service refactors (restore thin-tool pattern)
- [x] **R1 IDiskService** — extract aggregation + reclaimable script out of `DiskTools.cs:28-107` into a
  service + typed DTOs (`DiskUsageEntry`…); white-box test helpers via InternalsVisibleTo.
- [x] **R2 ISecurityService** — move `SystemTools.SecurityAudit` inline PS (`:103-121`) behind a service
  + `SecurityAuditDto`; replace hardcoded JSON fallback literal.
- [x] **R3 IFirewallService** — move `NetworkTools.Firewall` inline PS (`:66-116`) behind a service.
- [x] **R4 empty-output guards** — `DiskTools.reclaimable` + `NetworkTools` raw-`Stdout` returns lack
  the empty-output guard that hid the storage bug; add guard or stage-to-file.
- [x] **R5 StorageService temp-path quote** (`StorageService.cs:49`) — `& '{tempScript}'` breaks if the
  profile path contains a `'`. Escape or use `-File`.

### Batch 4 — missing tests (10 services have none)
- [x] **T1 WebService tests** — SSRF/private-IP guard + HTML→markdown (highest value).
- [x] **T2 NetworkService tests**, **T3 ProcessService tests** — pure-logic paths.

### Batch 5 — expansions
- [x] **E1 network ports → owning PID/name/path** (`PortInfoDto` completeness defect; `Get-NetTCPConnection -OwningProcess`).
- [x] **E2 verify_signature** — expose existing catalog-aware `AuthenticodeInspector` as a standalone tool.
- [x] **E3 file_hash (SHA256/SHA1/MD5)** — upgrade `FileSystemService.HashFile` (MD5-only) + expose.
- [x] **E4 process_inspect** — parent PID / cmdline / owner / loaded modules (WMI `Win32_Process` + `Process.Modules`).
- [ ] **E5 defender_status** [DONE] (`Get-MpComputerStatus`/`Get-MpThreat`), **E6 cert_store** [DONE] (rogue root CAs),
  **E7 reliability/minidump list**, **E8 driver_list** (BYOVD), **E9 NTFS ADS + reparse**.

## 🟢 Ready / candidates (none blocking)

- [ ] **OVERVIEW.md tool-catalog reconciliation** — per-tool tables have pre-existing drift (SystemTools lists ProcessTools tools; WindowTools lists StartProcess; missing Disk/Storage/Security/Network/Registry/Web sections; ARCHITECTURE ServerInfo "0.2.0"). Counts are correct now; do a full pass against COMPONENTS.md.

- [ ] **`startup_report` — scheduled-task COM-handler resolution.** ComHandler tasks (NGEN,
  CertificateServicesClient, …) expose a CLSID, not an exec path; currently reported with no
  action path (and excluded from summary flags). Could resolve the CLSID → handler DLL for
  fuller coverage. Low priority.
- [ ] **`startup_report` — summary severity tiers.** The `summary` flagged list could rank
  untrusted-third-party vs missing-target vs MS-file-missing, instead of a flat list. Nice-to-have.
- [ ] **Dependabot dev-dep advisories** in `tools/*` (JS). Banner 12→4 after `npm audit fix`;
  remaining need major bumps — let Dependabot PRs handle them.

## ⚪ Deliberately out of scope (decisions, not todos)

- `startup_report` skips IE-era sections (BHO / toolbars / IE search scopes / IE MenuExt) —
  obsolete on Win11; they'd add noise, not signal.
- Full `format=json|text|both` reports are large (~110 KB) and spill to a file by design; the
  default `format=summary` is the inline path. Not worth shrinking the full dump.

## 🔴 Known environmental test flakes (NOT code defects — do not "fix" by disabling)

- `UIAutomationServiceTests.GetStateAsync_returns_tree_with_notepad_root` — needs an interactive
  foreground desktop with Notepad; fails headless. (Fixture documents this.)
- `ClipboardServiceTests.SetTextAsync_then_GetTextAsync_roundtrips` — TextCopy `OpenClipboard`
  access-denied when another app holds the clipboard; transient. Gate headlessly with
  `dotnet test --filter "Category!=UIAutomation"` and treat a lone clipboard failure as environmental.
- `ScreenshotServiceTests.CaptureAsync_returns_non_empty_png_with_dimensions` — fails only under
  full-suite contention (no/contended desktop surface during a parallel run); **passes in isolation**
  (`--filter FullyQualifiedName~ScreenshotServiceTests`). Same screen-capture environmental class as
  the UIAutomation tests — not a regression.

## ✅ Done (shipped in v0.3.0 / v0.3.1 — see CHANGELOG)

- `startup_report` MCP tool: HiJackThis-style boot/persistence report, catalog-aware code-signing
  trust, enabled-state decode, file-missing detection — meets/beats HiJackThis on every actionable
  persistence category, plus IFEO / Winlogon / AppInit_DLLs / Active Setup that HJT lacks.
- Coverage expansion + `format=summary` (default, inline) + `includeProcesses`; Control-Panel
  `System32`/`SysWOW64` `*.cpl` scan; per-SID `HKU` Run; DNS; proxy/trusted-zone.
- All e2e-found bugs fixed (catalog `hCatAdmin`, full-path signer resolution, accessibility noise
  filter, ComHandler-task flagging). `npm audit fix` on `tools/*`.
