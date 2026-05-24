# Windows-mcp Python → C# Conversion — Design Spec

**Date:** 2026-05-24
**Status:** Approved by user; ready for implementation plan
**Author:** Claude (Opus 4.7) + Daniel

## Goal

Convert the existing Python Windows-mcp server (FastMCP, ~6038 LOC, 45 tools)
to a single-binary C# MCP server using the official `ModelContextProtocol`
NuGet SDK, expanding the tool surface to **50 tools** that cover the full
range of local Windows OS automation, admin, and diagnostics tasks. Replace
the venv-launched Python process with a self-contained `WindowsMcp.exe` that
ships as one file the user references from `.mcp.json`.

This is the 4th and final repo in the Python→modern-SDK MCP conversion
project. dropbox-mcp, time-mcp, and gmail-mcp were converted to TypeScript
on `@modelcontextprotocol/sdk`. Windows-mcp is converted to C# instead
because the dominant dependency surface — UI Automation, WinRT, WMI,
registry, services, event log — is first-party in .NET and second-rate (or
absent) in Node.

## Background — why C#, not TypeScript

A feasibility exploration of the Python source identified ~13 tools that
depend on Windows UI Automation via `uiautomation` (COM) and `comtypes`.
These have no mature Node.js equivalent — `robotjs` and `@nut-tree-fork/nut-js`
can click coordinates and image-match but cannot walk the a11y tree, query
controls by ControlType, or drive Toggle/Selection/Grid patterns.

Three architectures were evaluated:
- **(A)** Node MCP server with hidden C# stdio helper using a custom JSON
  protocol — rejected: custom IPC contract on top of two SDKs is more
  surface than necessary.
- **(B)** Two sibling MCP servers (Node + C#) — rejected by user: splits
  tools across two prefixes.
- **(C)** All-C# MCP server using the official `ModelContextProtocol` C#
  SDK — **chosen**. Single binary, single .mcp.json entry, native access to
  every Windows API category.

The mode is **enhancement** (matching gmail-mcp's precedent, not the strict
1:1 parity of dropbox-mcp/time-mcp): drop dead/redundant tools, consolidate
overlap, rename to snake_case for consistency with the other three Node
servers, and add coverage where the Python source has gaps.

## Architecture

Single .NET 9 solution at repo root with three projects.

### Repo layout

```
Windows-mcp/
├── Windows-mcp.sln
├── src/
│   ├── WindowsMcp/                       ← Main MCP server (executable)
│   │   ├── WindowsMcp.csproj             ← <OutputType>Exe</OutputType>
│   │   ├── Program.cs                    ← Entry: McpServerBuilder().AddStdio()
│   │   ├── Tools/                        ← Tool method classes
│   │   │   ├── InputTools.cs
│   │   │   ├── ScreenTools.cs
│   │   │   ├── WindowTools.cs
│   │   │   ├── UIAutomationTools.cs
│   │   │   ├── ProcessTools.cs
│   │   │   ├── ShellTools.cs
│   │   │   ├── FileTools.cs
│   │   │   ├── DiskTools.cs
│   │   │   ├── SystemTools.cs
│   │   │   ├── NetworkTools.cs
│   │   │   ├── RegistryTools.cs
│   │   │   └── WebTools.cs
│   │   ├── Services/                     ← Concrete implementations
│   │   │   ├── UIAutomationService.cs    ← FlaUI.UIA3 on dedicated STA thread
│   │   │   ├── InputService.cs           ← P/Invoke SendInput via CsWin32
│   │   │   ├── ScreenshotService.cs      ← System.Drawing + SkiaSharp encode
│   │   │   └── PowerShellService.cs      ← System.Management.Automation runspace
│   │   └── Models/                       ← Result DTOs serialized to JSON
│   ├── WindowsMcp.Abstractions/
│   │   └── WindowsMcp.Abstractions.csproj ← Interfaces + shared DTOs
│   └── WindowsMcp.Sidecar/                ← Placeholder; deleted if mainline AOT not split
├── tests/
│   └── WindowsMcp.Tests/
│       └── WindowsMcp.Tests.csproj        ← xUnit + FluentAssertions + Moq
├── docs/superpowers/
│   ├── specs/2026-05-24-windows-mcp-csharp-conversion-design.md
│   └── plans/2026-05-24-windows-mcp-csharp-conversion.md
├── dist/                                  ← Publish output, gitignored
├── .python-snapshot-2026-05-24/           ← Phase 0 snapshot; deleted at retirement
├── .gitignore
├── global.json                            ← Pins .NET SDK 9.0.x
├── Directory.Build.props                  ← Shared csproj settings
├── README.md
├── CHANGELOG.md
└── LICENSE
```

### Why three projects, not one

- **`WindowsMcp`** — the executable, holds tool method classes and concrete services.
- **`WindowsMcp.Abstractions`** — interfaces (`IInputService`, `IUIAutomationService`,
  `IPowerShellService`, `IScreenshotService`, `IFileSystemService`, `IRegistryService`,
  `IClipboardService`, `IAudioService`, `IServiceControlService`, `IEventLogService`,
  `ITaskSchedulerService`) plus shared DTOs. Tests reference this and mock the
  concretes — test code never depends on the executable project.
- **`WindowsMcp.Sidecar`** — placeholder for an emergency split if AOT and WinRT
  OCR turn out to be incompatible in one binary. Deleted from the .sln in Phase 1
  if not needed.

### Tool registration

Use `ModelContextProtocol` SDK's `[McpServerToolType]` (on the tool class) and
`[McpServerTool]` (on each method). The SDK's source generator discovers tools
at compile time. No reflection at runtime → AOT-friendly path stays open for
non-FlaUI tools.

### Threading

UI Automation requires single-threaded apartment (STA). The
`UIAutomationService` holds one dedicated STA thread plus a
`BlockingCollection<Action>` work queue. All UA calls marshal onto that thread
via `Task.Run` + `TaskCompletionSource`. WinRT calls (OCR, ToastNotification)
get their own STA worker if testing reveals COM apartment conflict with FlaUI.

### Python source retirement

Phase 0 of the implementation plan moves the existing Python sources
(`main.py`, `src/desktop/`, `src/tree/`, `pyproject.toml`, `requirements.txt`,
`windows_mcp_entry.py`) into `.python-snapshot-2026-05-24/` for the duration
of development. The cutover commit at end deletes that snapshot.

## Tool Inventory — 50 tools across 11 categories

Naming: `snake_case`, no `windows_` prefix. Server name `Windows-mcp` already
namespaces the tools in the MCP layer (they appear to clients as
`mcp__Windows-mcp__click`, etc.).

### Input (8) — `Tools/InputTools.cs`, backed by `InputService` (SendInput via CsWin32)

| New | ← was | Notes |
|---|---|---|
| `click` | Click-Tool | `(x, y, button?, clicks?)`. Drops `humancursor` dependency — straight SendInput. |
| `drag` | Drag-Tool | `(from_x, from_y, to_x, to_y, button?)` |
| `hover` | Hover-Tool + Move-Tool | Merged. `duration_ms: 0` = bare cursor move. |
| `type` | Type-Tool | `(text)`. Unicode-clean via KEYEVENTF_UNICODE. |
| `key` | Key-Tool | Single keys (Enter, Tab, F1–F12, arrows). |
| `shortcut` | Shortcut-Tool | Combos like `ctrl+c`, `alt+tab`. |
| `scroll` | Scroll-Tool | `(x, y, direction, amount?)` |
| `clipboard` | Clipboard-Tool | `(action: "get"|"set", text?)`. Backed by TextCopy NuGet. |

### Screen (2) — `Tools/ScreenTools.cs`

| New | ← was | Notes |
|---|---|---|
| `screenshot` | Screenshot-Tool | `(region?, format?: "png"|"jpeg")`. `Graphics.CopyFromScreen` + SkiaSharp encode. |
| `ocr` | OCR-Tool | `(region?)`. WinRT `Windows.Media.Ocr` direct from .NET 9. |

### Window (5) — `Tools/WindowTools.cs`

| New | ← was | Notes |
|---|---|---|
| `window` | Window-Tool | `(action, title?)` — minimize/maximize/restore/close/move/resize |
| `switch_to_window` | Switch-Tool | Brings window to foreground (User32 SetForegroundWindow) |
| `launch` | Launch-Tool | App from Start Menu by name (PowerShell search) |
| `focus` | Focus-Tool | UA-bound; sets keyboard focus by element_id |
| `multi_monitor` | Multi-Monitor-Tool | Display count, resolutions, positions |

### UI Automation (8) — `Tools/UIAutomationTools.cs`, backed by `UIAutomationService` (FlaUI.UIA3, dedicated STA thread)

| New | ← was | Notes |
|---|---|---|
| `get_state` | State-Tool | Full a11y tree snapshot + optional annotated screenshot |
| `find_element` | Find-Element-Tool | By text/control type; returns element_ids |
| `get_element` | Get-Element-Property-Tool | Properties: ControlType, IsEnabled, Bounds, Value, IsChecked, … |
| `get_text` | Get-Text-Tool | Text content of element (faster than OCR) |
| `assert_element` | Assert-Element-Tool | Verify state (exists, enabled, checked, visible) |
| `interact_element` | Checkbox-Toggle + Select-Option | Merged: `(element_id, action: "toggle"|"select"|"invoke", value?)` |
| `get_table` | Get-Table-Tool | GridPattern → markdown table |
| `wait_for` | Wait-For-Tool | Poll for element/text with timeout |

### Process / Shell (6) — `Tools/ProcessTools.cs` + `Tools/ShellTools.cs`

| New | ← was | Notes |
|---|---|---|
| `process` | Process-Tool | list/kill by name/PID |
| `start_process` | Start-Process-Tool | Detached spawn |
| `powershell` | Powershell-Tool | Persistent `PowerShellService` runspace; ~200ms→<5ms per call vs spawn |
| `service` | NEW | `(action: "start"|"stop"|"restart"|"status"|"list", name?)` — `ServiceController` |
| `scheduled_task` | NEW | `(action: "list"|"get"|"run"|"create"|"delete", name?, …)` — `Microsoft.Win32.TaskScheduler` |
| `event_log` | NEW | `(log, level?, source?, since?, max?)` — `EventLog` class |

### File (7) — `Tools/FileTools.cs`

| New | ← was | Notes |
|---|---|---|
| `file_search` | File-Search + Duplicate-Finder | Merged: `(pattern?, size?, modified_since?, find_duplicates?)` |
| `file_manage` | File-Manage-Tool | copy/move/rename/delete; respects blocked-paths config |
| `file_dialog` | File-Dialog-Tool | Type path into Open/Save dialogs |
| `file_read` | NEW | `(path, max_bytes?: 1048576, encoding?: "utf-8"|"utf-16"|"ascii"|"auto")`. Binary returned base64. |
| `file_write` | NEW | `(path, content, encoding?: "utf-8", confirm: true)`. Atomic via tempfile + rename. |
| `file_info` | NEW | Size, dates, attributes, ACL summary via `FileInfo` |
| `archive` | NEW | `(action: "zip"|"unzip", src, dst)` via `System.IO.Compression.ZipFile` |

### Disk (1) — `Tools/DiskTools.cs`

| New | ← was | Notes |
|---|---|---|
| `disk_inspect` | Disk-Analysis + Disk-Cleanup + Storage | 3→1 merge. `(mode: "usage"|"reclaimable"|"file_types"|"stale", path?)` |

### System (7) — `Tools/SystemTools.cs`

| New | ← was | Notes |
|---|---|---|
| `system_info` | System-Info-Tool | `(category: "cpu"|"ram"|"disk"|"gpu"|"battery"|"all")` |
| `audio` | Audio-Tool | PowerShell-based (drops COM IMMDeviceEnumerator). `(action: "get"|"set"|"mute"|"unmute", level?)` |
| `notification` | Notification-Tool | WinRT `ToastNotification` direct from .NET 9 |
| `security_audit` | Security-Audit-Tool | Defender + Firewall + UAC + BitLocker status |
| `wmi_query` | NEW | `(class, namespace?, where?)` via `System.Management.ManagementObjectSearcher` |
| `env` | NEW | `(action: "get"|"set"|"unset"|"list", name?, value?, scope?: "user"|"machine")` |
| `power_action` | NEW | `(action: "shutdown"|"restart"|"sleep"|"hibernate"|"lock"|"sign_out", confirm: true)` |

### Network (2) — `Tools/NetworkTools.cs`

| New | ← was | Notes |
|---|---|---|
| `network` | Network-Tool | adapters, ports, ping, DNS, WiFi |
| `firewall` | NEW | `(action: "list"|"add"|"remove", name?, direction?, action_type?, ..., confirm: true)` for write ops |

### Registry (2) — `Tools/RegistryTools.cs`

| New | Notes |
|---|---|
| `registry_get` | `(hive, path, value?)` — read registry. `Microsoft.Win32.Registry`. |
| `registry_set` | `(hive, path, value, data, type, confirm: true)` — write. `confirm: true` required by schema. |

### Web (2) — `Tools/WebTools.cs`

| New | ← was | Notes |
|---|---|---|
| `scrape` | Scrape-Tool | `HttpClient` + ReverseMarkdown. SSRF rules ported. |
| `http_request` | NEW | `(url, method, headers?, body?, json?)`. Full HTTP client beyond HTML scraping. |

### Tools dropped (5) with rationale

| Dropped | Reason |
|---|---|
| Move-Tool | Merged into `hover` (duration_ms: 0 = bare cursor move) |
| Compare-Screenshot-Tool | Niche QA tool; LLM can compare two screenshots by reading both |
| Wait-Tool | Pure sleep; LLM can space its calls. `wait_for` (element-poll) is kept. |
| Record-Replay-Tool | LLM is the orchestrator; replay is an odd capability for an agent |
| Command-History-Tool | Session-scoped PowerShell history; cross-session value near zero |

### Safety rails baked into schemas

Destructive actions require a `confirm: { const: true }` parameter at the
JSON Schema level. The LLM cannot omit it. Action-discriminated tools
express this via JSON Schema `anyOf` (one branch per action enum value,
with `confirm` in the `required` list of destructive branches only).

**Always required (tool itself is destructive):**

- `registry_set`
- `power_action`
- `file_write` — required on every call to prevent accidental writes,
  even to new paths

**Required for specified action values (schema `anyOf`-discriminated):**

| Tool | `confirm: true` required when `action` is |
|---|---|
| `firewall` | `"add"`, `"remove"` |
| `file_manage` | `"delete"` |
| `service` | `"stop"`, `"restart"` |
| `scheduled_task` | `"delete"` |
| `process` | `"kill"` |

Read-only / non-destructive actions on those tools (e.g.
`service status`, `process list`, `file_manage copy`) do **not** require
`confirm`.

## NuGet Dependencies

### Main project (`WindowsMcp`)

```xml
<!-- Core MCP -->
<PackageReference Include="ModelContextProtocol" Version="0.4.*" />

<!-- UI Automation -->
<PackageReference Include="FlaUI.UIA3" Version="5.0.0" />

<!-- Input + Screen + Audio -->
<PackageReference Include="H.InputSimulator" Version="1.*" />
<PackageReference Include="SkiaSharp" Version="3.*" />
<PackageReference Include="System.Drawing.Common" Version="9.*" />
<PackageReference Include="TextCopy" Version="6.*" />

<!-- Windows-specific (source generator + runtime libs) -->
<PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*" PrivateAssets="all" />
<PackageReference Include="Microsoft.Win32.TaskScheduler" Version="2.*" />
<PackageReference Include="System.Management" Version="9.*" />
<PackageReference Include="System.ServiceProcess.ServiceController" Version="9.*" />
<PackageReference Include="System.Diagnostics.EventLog" Version="9.*" />

<!-- Web -->
<PackageReference Include="ReverseMarkdown" Version="4.*" />

<!-- Logging -->
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="9.*" />
```

WinRT projections (OCR + ToastNotification) come via the target framework
declaration:

```xml
<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
```

### Test project (`WindowsMcp.Tests`)

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
<PackageReference Include="FluentAssertions" Version="7.*" />
<PackageReference Include="Moq" Version="4.*" />
```

### Built-in (no NuGet)

- `Microsoft.Win32.Registry` — registry access
- `System.Net.Http.HttpClient` — HTTP requests
- `System.IO.Compression.ZipFile` — archive
- `System.Drawing` — screenshot capture (`Graphics.CopyFromScreen`)
- `System.Management.Automation` — PowerShell host runspace
- `System.Net.NetworkInformation` — ping, adapters

### Native AOT decision

**For v0.2.0: ship as self-contained single-file, NOT native AOT.** FlaUI
uses reflection internally for COM marshaling that the AOT trimmer cannot
see, and NAudio (if it gets pulled in transitively) does the same.

Build flags:
```xml
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<PublishTrimmed>false</PublishTrimmed>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
```

Result: ~70 MB `WindowsMcp.exe` — one file. No .NET runtime install required
by user. AOT deferred to v0.3.0; CsWin32 keeps the AOT path open for the
non-FlaUI tools when that work resumes.

## Build, distribution, and dev workflow

### `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>
</Project>
```

### `global.json`

```json
{ "sdk": { "version": "9.0.100", "rollForward": "latestFeature" } }
```

### Build commands

| Task | Command |
|---|---|
| Restore | `dotnet restore` |
| Build | `dotnet build` |
| Run server (stdio) | `dotnet run --project src/WindowsMcp` |
| Unit tests | `dotnet test --filter "Category=Unit"` |
| All tests | `dotnet test` |
| Watch + test | `dotnet watch test --project tests/WindowsMcp.Tests` |
| Publish | `dotnet publish src/WindowsMcp -c Release -o dist` |

### .gitignore additions

```
bin/
obj/
*.user
*.suo
.vs/
dist/
src/WindowsMcp/Properties/launchSettings.json
```

### No CI for v0.2.0

Matches the precedent of the other three conversions. Documented as v0.3.0
backlog.

## Testing strategy

### Three test categories with xUnit `[Trait]`

```csharp
[Trait("Category", "Unit")]          // default; ~80% of tests
[Trait("Category", "Integration")]   // real Windows APIs (not UA)
[Trait("Category", "UIAutomation")]  // real apps + real a11y tree
```

Local dev defaults to `dotnet test --filter "Category=Unit"` for tight loop;
full suite runs all three.

### Mocking discipline — interfaces define the seams

Every tool method depends on a service interface, never a concrete. Tool
methods do nothing but: validate args → call service → format result. Unit
tests use `Mock<IInputService>` etc. for the handler, and a separate
integration test exercises the concrete service against the real API.

### Integration test fixtures (xUnit `IClassFixture`)

| Fixture | Purpose | Cleanup |
|---|---|---|
| `TempDirectoryFixture` | Per-class temp dir; used by File / Disk / Archive tests | Recursive directory delete |
| `CalculatorFixture` | Launches `calc.exe`, exposes FlaUI Application handle | `app.Close()` |
| `NotepadFixture` | Launches `notepad.exe`; used by Input + UA integration | Ctrl+W, decline save |
| `RegistryNamespaceFixture` | Creates `HKCU\Software\WindowsMcp.Tests\{Guid}` namespace | `DeleteSubKeyTree` |
| `EnvScopeFixture` | Sets transient User env vars; restores prior values | Restore-or-delete |
| `FirewallSandboxFixture` | Creates rule name prefix `WindowsMcp.Test.{Guid}.*` | PowerShell remove matching |
| `TaskSchedulerSandboxFixture` | Tasks under `\WindowsMcp.Tests\` folder | DeleteFolder |
| `LocalHttpServerFixture` | In-process `HttpListener` on random port | StopListener |

### Per-tool test plan summary

| Tool group | Unit | Integration | UA | Total |
|---|---|---|---|---|
| Input (8) | 16 | 8 | 0 | 24 |
| Screen (2) | 4 | 4 | 0 | 8 |
| Window (5) | 10 | 5 | 0 | 15 |
| UI Automation (8) | 16 | 0 | 16 | 32 |
| Process / Shell (6) | 12 | 12 | 0 | 24 |
| File (7) | 28 | 6 | 0 | 34 |
| Disk (1) | 2 | 2 | 0 | 4 |
| System (7) | 14 | 14 | 0 | 28 |
| Network (2) | 4 | 4 | 0 | 8 |
| Registry (2) | 4 | 4 | 0 | 8 |
| Web (2) | 4 | 6 | 0 | 10 |
| **Subtotal** | **114** | **65** | **16** | **~195** |

Plus concurrency/safety tests:

- `UIAutomationService_concurrency_test`: 50 parallel GetStateAsync calls, no
  deadlock, STA thread stays alive
- `PowerShellService_concurrency_test`: 50 parallel echo calls with distinct
  args, no output cross-talk
- `PowerShellService_restart_on_crash_test`: kill runspace mid-call, assert
  next call rebuilds it cleanly
- `FileWrite_atomic_test`: concurrent overwrite + read loop, no torn writes

Total target: ~200 tests.

### Tools not directly tested (and why)

| Skipped | Reason |
|---|---|
| `power_action` real shutdown/reboot | Would destroy the test machine. P/Invoke wrapper is mocked. |
| `audio` actual volume change | Test asserts PowerShell command was issued with correct args. |
| `notification` visual toast | Test asserts WinRT API returned success. |
| Clipboard interleaving with user's real clipboard | Tests save+restore the user's clipboard at start/end; race accepted. |
| `file_dialog` against arbitrary apps' dialogs | Tested against Notepad's Save As only. |

### Test execution budget

| Bucket | Target |
|---|---|
| Unit only | <5 sec |
| Unit + Integration | <30 sec |
| Full (incl. UA) | <2 min |

## Cutover & retirement plan

### Phase 0 — Python snapshot (first commit)

Move existing Python tree into `.python-snapshot-2026-05-24/`:

```
.python-snapshot-2026-05-24/
├── main.py
├── pyproject.toml
├── requirements.txt
├── windows_mcp_entry.py
└── src/
    ├── desktop/
    └── tree/
```

Commit message: `chore: snapshot Python sources before C# rewrite`.

Python `.mcp.json` entry stays live during development.

### Phase 1–3 — Scaffolding, services, tool handlers

Each implemented TDD via subagent-driven development. Atomic commits per
task.

### Phase 4 — Build + smoke

Publish to stable location:

```powershell
dotnet publish src/WindowsMcp -c Release -o dist
```

Output: `C:/Users/danie/Dropbox/Github/Windows-mcp/dist/WindowsMcp.exe`.

Smoke test:

```powershell
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}' | dist/WindowsMcp.exe
```

Expected: JSON-RPC `initialize` response on stdout; server logs on stderr.

### Phase 5 — `.mcp.json` cutover

Backup the live config first:

```powershell
Copy-Item C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json `
          C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json.bak-2026-05-24-pre-windows-mcp-cs-cutover
```

Replace the entry:

```json
"Windows-mcp": {
  "type": "stdio",
  "command": "C:/Users/danie/Dropbox/Github/Windows-mcp/dist/WindowsMcp.exe",
  "args": [],
  "env": { "_RETRY": "2026-05-24-windows-mcp-cs-cutover" }
}
```

The `_RETRY` value changes the config hash to evict Claude Code's per-session
MCP failure cache.

User runs: `/kill-plugins` → `/reload-plugins`.

### Phase 6 — Live verification (4 representative tools)

| Tool | Why this one |
|---|---|
| `system_info(category: "cpu")` | Fast, read-only, exercises WMI path |
| `find_element(text: "Start")` | Exercises FlaUI on dedicated STA thread |
| `file_read(path: "C:/Users/danie/.claude/CLAUDE.md", max_bytes: 1024)` | Exercises file I/O + encoding |
| `network(action: "ping", host: "127.0.0.1")` | Exercises native Ping |

If all four return expected shape: proceed to Phase 7. If any fails: stop,
diagnose, fix forward.

### Phase 7 — Final retirement (separate commit)

After live verification holds:

1. Delete `.python-snapshot-2026-05-24/`
2. Delete `.mcp.json.bak-2026-05-24-*`
3. Update `README.md` for C# build/install
4. Add `CHANGELOG.md` entry `[0.2.0] - 2026-05-24`
5. User runs out of band: `Remove-Item -Recurse -Force C:/Users/danie/.venvs/windows-mcp`

Commit message: `chore: complete C# conversion; retire Python sources`.

### Rollback path

If post-cutover discovers a showstopper:

1. `Copy-Item .mcp.json.bak-* .mcp.json -Force`
2. `/kill-plugins` + `/reload-plugins`
3. Python is live again in ~30 seconds

This is why retirement is a separate commit — only runs after live
verification holds for the dev session.

## Open risks (documented, not yet mitigated)

| Risk | Likelihood | Mitigation plan |
|---|---|---|
| FlaUI + WinRT COM apartment conflict | Medium | UA on dedicated STA thread; if WinRT calls conflict, they get their own STA worker |
| First-run cold-start exceeds Claude Code's 30s timeout | Low–Medium | Add Defender exclusion for `dist/` (matches existing pattern in CLAUDE.md for `~/.venvs/`). Document in README. |
| WinRT ToastNotification requires registered AppUserModelID | High | Call `SetCurrentProcessExplicitAppUserModelID` at Program.cs startup with `org.windows-mcp.server` |
| PowerShell runspace memory growth on long sessions | Medium | Restart runspace every 1000 commands or 30 min |
| `calc.exe` on Windows 11 is UWP (different a11y tree than W10's classic calc) | High | Test fixture defaults to `notepad.exe` (Win32, stable a11y across versions). CalculatorFixture is fallback. |
| AOT path deferred to v0.3.0 | Accepted | Documented in CHANGELOG backlog. CsWin32 keeps non-FlaUI tools AOT-ready. |
| FlaUI v5 API surface still in flux | Low | Pin to specific 5.x minor (`Version="5.0.0"`); no floating |
| `file_dialog` tested only against Notepad's Save As | Accepted | Other apps work in practice; documented as limitation |

## Explicit non-goals for v0.2.0

- Cross-platform support (macOS/Linux) — Windows-only by design
- Touch / pen / gesture input — SendInput-class only
- Multi-user / RDP session traversal — single interactive session
- Driver-level keyboard hooks — user-mode SendInput only
- OCR for languages without an installed Windows language pack
- Image-template-based UI finding — `claude-in-chrome` territory
- Browser automation — covered by `claude-in-chrome`
- Running as a Windows Service (background) — stdio-driven, parent-process lifetime only
- CI / GitHub Actions — deferred to v0.3.0
- Native AOT compilation — deferred to v0.3.0; FlaUI is the blocker
- Auto-update / installer / MSIX packaging — portable .exe; user manages updates

## Implementation handoff

After this spec is approved, the implementation plan will decompose this
into 18–22 tasks following the `superpowers:writing-plans` pattern:
bite-sized steps (2–5 min each), TDD discipline, atomic commits. Execution
will use `superpowers:subagent-driven-development` — fresh implementer
subagent per task, two-stage review (spec compliance, then code quality),
final reviewer at end.
