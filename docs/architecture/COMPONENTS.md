# Windows-MCP Component Reference

## Component Overview

This document provides detailed documentation for each component in the Windows-MCP C# architecture, including tool classes, service interfaces, service implementations, and data models.

---

## Program.cs — Host and DI Entry Point

### Purpose
Parses the command line (`Hosting/ServerOptions`, with `WINDOWSMCP_*` env fallbacks) and starts the MCP server over stdio (default) or Streamable HTTP (`--transport http`, built by `Hosting/WindowsMcpHost.BuildHttpApp`). The service registrations and MCP wiring both transports share live in `Hosting/WindowsMcpHost.AddWindowsMcp`.

### Location
`src/WindowsMcp/Program.cs`

### Startup Sequence

```csharp
public static async Task<int> Main(string[] args)
{
    // 1. Register AppUserModelID for WinRT toast notifications
    PInvoke.SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server");

    // 2. Per-Monitor DPI Awareness V2 — physical pixel coordinates on HiDPI
    PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((nint)(-4)));

    // 3. Command line / WINDOWSMCP_* env → ServerOptions (exit 2 + usage on a bad option; --help)
    var options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
    return options.IsHttp ? await RunHttpAsync(options) : await RunStdioAsync(args);
}

static async Task<int> RunStdioAsync(string[] args)
{
    // Force UTF-8 to prevent JSON-RPC response buffering on Windows (cp1252 default)
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding  = System.Text.Encoding.UTF8;

    var builder = Host.CreateApplicationBuilder(args);
    WindowsMcpHost.ConfigureStderrLogging(builder.Logging, http: false);
    builder.AddWindowsMcp()             // Hosting/WindowsMcpHost: all singletons + AddMcpServer(ServerInfo)
        .WithStdioServerTransport();    //   + ToolErrors call-tool filter + WithToolsFromAssembly()
    await builder.Build().RunAsync();
    return 0;
}

static async Task<int> RunHttpAsync(ServerOptions options)
{
    // Refuse off-loopback without an API key; resolve --cert-thumbprint via CertificateLocator.
    var app = WindowsMcpHost.BuildHttpApp(options, cert);   // Kestrel Listen(bind, port[, UseHttps])
    await app.RunAsync();                                    //   + bearer gate + MapMcp("/mcp"), stateless
    return 0;
}
```

### Registered Services

| Interface | Implementation |
|-----------|---------------|
| `IInputService` | `InputService` |
| `IScreenshotService` | `ScreenshotService` |
| `IOcrService` | `OcrService` |
| `IClipboardService` | `ClipboardService` |
| `IAudioService` | `AudioService` |
| `IPowerShellService` | `PowerShellService` |
| `IUIAutomationService` | `UIAutomationService` |
| `IFileSystemService` | `FileSystemService` |
| `IRegistryService` | `RegistryService` |
| `IServiceControlService` | `ServiceControlService` |
| `IEventLogService` | `EventLogService` |
| `ITaskSchedulerService` | `TaskSchedulerService` |
| `IProcessService` | `ProcessService` |
| `IWindowService` | `WindowService` |
| `IWmiService` | `WmiService` |
| `IStorageService` | `StorageService` |
| `IDiskService` | `DiskService` |
| `ISecurityService` | `SecurityService` |
| `IFirewallService` | `FirewallService` |
| `ICertStoreService` | `CertStoreService` |
| `IReliabilityService` | `ReliabilityService` |
| `IDriverService` | `DriverService` |
| `IFileStreamService` | `FileStreamService` |
| `IEnvService` | `EnvService` |
| `IPowerService` | `PowerService` |
| `INotificationService` | `NotificationService` |
| `INetworkService` | `NetworkService` |
| `IWebService` | `WebService` |
| `IAuthenticodeInspector` | `AuthenticodeInspector` |
| `ILspEnumerator` | `LspEnumerator` |
| `IShortcutResolver` | `ShortcutResolver` |
| `IStartupReportService` | `StartupReportService` |
| `IIntegrityService` | `IntegrityService` |
| `IUsnService` | `UsnService` |
| `IWatchService` | `WatchService` |
| `IJobService` | `JobService` |

---

## Tool Classes

Tool classes are `[McpServerToolType]`-annotated, sealed, and stateless (except for injected service references). All tool methods are `async Task<string>` and return JSON-serialized results or plain strings.

---

### `InputTools` — 8 tools
`src/WindowsMcp/Tools/InputTools.cs`

**Injected:** `IInputService`, `IClipboardService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Click` | `(int x, int y, string button="left", int clicks=1)` | Click at coordinates |
| `Drag` | `(int from_x, int from_y, int to_x, int to_y, string button="left")` | Drag between two points |
| `Hover` | `(int x, int y, int duration_ms=0)` | Hover or move cursor |
| `Type` | `(string text)` | Type text into focused input |
| `Key` | `(string key)` | Press a named key |
| `Shortcut` | `(string shortcut)` | Press a key combo (e.g., `ctrl+c`) |
| `Scroll` | `(int x, int y, string direction, int amount=3)` | Scroll mouse wheel |
| `Clipboard` | `(string action, string? text=null)` | `get` or `set` clipboard text |

---

### `UIAutomationTools` — 8 tools
`src/WindowsMcp/Tools/UIAutomationTools.cs`

**Injected:** `IUIAutomationService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `GetState` | `()` | Full UI element tree of the foreground app |
| `FindElement` | `(string text, string kind="any")` | Find elements by text; kind: any/interactive/text/scrollable |
| `GetElement` | `(string element_id)` | Properties of a specific element by ID |
| `GetText` | `(string element_id)` | Extract text content (faster than OCR) |
| `AssertElement` | `(string element_id, string state)` | Assert element state; returns `PASS` or `FAIL: <reason>` |
| `InteractElement` | `(string element_id, string action, string? value=null)` | click/toggle/select/focus/type on element |
| `GetTable` | `(string element_id)` | Extract grid/table data via `GridPattern` |
| `WaitFor` | `(string text, int timeout_ms=10000, int interval_ms=500)` | Poll until element appears |

---

### `WindowTools` — 5 tools
`src/WindowsMcp/Tools/WindowTools.cs`

**Injected:** `IWindowService`, `IProcessService`

| Method | Description |
|--------|-------------|
| `SwitchToWindow` | Focus a window by title pattern |
| `Window` | Get window position, size, and state |
| `MultiMonitor` | Enumerate all monitors with resolution and DPI |
| `Launch` | Launch an application by name |
| `StartProcess` | Start a detached process |

---

### `FileTools` — 9 tools
`src/WindowsMcp/Tools/FileTools.cs`

**Injected:** `IFileSystemService`, `IInputService`, `IFileStreamService`

| Method | Description |
|--------|-------------|
| `FileRead` | Read file contents (text or binary) |
| `FileWrite` | Write or append to a file |
| `FileManage` | Copy, move, delete, or create files/directories |
| `FileInfo` | Get file or directory metadata |
| `FileSearch` | Search for files by glob pattern |
| `FileHash` | Compute a file's SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse (symlink/junction) target |
| `FileDialog` | Interact with a native open/save dialog |
| `Archive` | Create, extract, or list zip/tar archives |

---

### `SecurityTools` — 3 tools
`src/WindowsMcp/Tools/SecurityTools.cs`

**Injected:** `IAuthenticodeInspector`, `ISecurityService`, `ICertStoreService`

| Method | Description |
|--------|-------------|
| `VerifySignature` | Catalog-aware Authenticode trust verdict for a file |
| `DefenderStatus` | Microsoft Defender posture (real-time/tamper protection, signature age, last scans) |
| `CertStore` | Enumerate a certificate store; flags self-signed (rogue-root) and expired certs |

---

### `SystemTools` — 9 tools
`src/WindowsMcp/Tools/SystemTools.cs`

**Injected:** `IWmiService`, `IEnvService`, `IPowerService`, `INotificationService`, `IAudioService`, `ISecurityService`, `IReliabilityService`, `IDriverService`

| Method | Description |
|--------|-------------|
| `SystemInfo` | WMI system info by category (os/memory/disk/gpu/battery) |
| `Audio` | Get/set volume or mute/unmute |
| `Notification` | Show a Windows toast notification |
| `SecurityAudit` | Firewall/Defender/UAC/BitLocker posture snapshot |
| `Reliability` | Crash minidumps + recent reliability failure records |
| `DriverList` | Installed PnP drivers with version/date/signer/signed-state (BYOVD surface) |
| `WmiQuery` | Execute arbitrary WMI queries |
| `Env` | Get, set, or list environment variables (secret-name redaction) |
| `PowerAction` | Shutdown, reboot, logoff, lock, sleep, hibernate |

---

### `ProcessTools` — 6 tools
`src/WindowsMcp/Tools/ProcessTools.cs`

**Injected:** `IProcessService`, `IServiceControlService`, `ITaskSchedulerService`, `IEventLogService`

| Method | Description |
|--------|-------------|
| `Process` | actions `list\|orphans\|kill`. `list` is plain by default; `includeLineage:true` adds recycle-aware parent lineage + signals (age, runtime kind, system-adjacency, root PID); `groupByRoot:true` collapses processes under their nearest-live root ancestor. `orphans` lists lineage rows whose parent is gone (recycle-aware). `kill` by PID or name (`confirm:true` required); `tree:true` kills descendants leaves-first; `startTime` guards against PID reuse. Data path: WMI bulk query → CIM datetime parse → pure lineage classifier. |
| `ProcessInspect` | Deep per-process detail: parent PID, command line, start time, loaded modules |
| `StartProcess` | Start a detached process; returns the PID |
| `Service` | List/status/start/stop/restart Windows services |
| `ScheduledTask` | List/get/run/create/delete scheduled tasks |
| `EventLog` | Query the Windows Event Log |

---

### `ScreenTools` — 2 tools
`src/WindowsMcp/Tools/ScreenTools.cs`

**Injected:** `IScreenshotService`, `IOcrService`

| Method | Description |
|--------|-------------|
| `Screenshot` | Capture full screen or a region; returns base64 PNG |
| `Ocr` | Extract text from a screen region |

---

### `ShellTools` — 1 tool
`src/WindowsMcp/Tools/ShellTools.cs`

**Injected:** `IPowerShellService`, `IJobService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Powershell` | `(string command, bool background)` | Execute PowerShell; returns `{stdout, stderr, exitCode}` JSON. Foreground calls emit MCP progress heartbeats every 10s (via an SDK-injected `IProgress<ProgressNotificationValue>`, excluded from the tool schema) so spec-compliant clients reset their request timeout; the foreground execution backstop is 15 min. `background:true` starts a `JobService` job and returns its `JobInfo` immediately |

---

### `JobTools` — 1 tool
`src/WindowsMcp/Tools/JobTools.cs`

**Injected:** `IJobService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Job` | `(string mode, string? id, int tail)` | modes `status\|output\|cancel\|list` over background PowerShell jobs. Unknown ids are forgiving (`found:false` / `cancelled:false`). `tail` limits `output` to the last N chars per stream |

---

### `RegistryTools` — 2 tools
`src/WindowsMcp/Tools/RegistryTools.cs`

**Injected:** `IRegistryService`

| Method | Description |
|--------|-------------|
| `RegistryGet` | Read a registry key or named value |
| `RegistrySet` | Write a registry value (REG_SZ, DWORD, etc.) |

---

### `NetworkTools` — 2 tools
`src/WindowsMcp/Tools/NetworkTools.cs`

**Injected:** `INetworkService`, `IFirewallService`

| Method | Description |
|--------|-------------|
| `Network` | Get adapter info, IP, gateway, DNS |
| `HttpRequest` | Make an HTTP request (GET/POST/PUT/DELETE) |

---

### `WebTools` — 2 tools
`src/WindowsMcp/Tools/WebTools.cs`

**Injected:** `IWebService`

| Method | Description |
|--------|-------------|
| `Scrape` | Fetch a URL and convert HTML to Markdown |
| `Shortcut` | Create or read a Windows .lnk shell shortcut |

---

### `DiskTools` — 1 tool
`src/WindowsMcp/Tools/DiskTools.cs`

**Injected:** `IDiskService`

| Method | Description |
|--------|-------------|
| `DiskInspect` | Disk usage analysis: usage (top dirs), reclaimable, file_types, stale |

---

### `StorageTools` — 1 tool
`src/WindowsMcp/Tools/StorageTools.cs`

**Injected:** `IStorageService`

| Method | Description |
|--------|-------------|
| `StorageHealth` | Diagnose disk/drive health (not usage): physical disks (model, bus/media type, SMART `HealthStatus` + reliability counters), per-disk online/offline + health, the volume→disk/partition map (filesystem, label, health), and recent disk-stack Error/Warning events. Metadata-first and hang-safe: free space is only probed when `include_usage:true`, each probe time-boxed in an in-process runspace, with an overall `CancellationToken` budget. `drive_letter` limits the volumes section. |

---

### `StartupTools` — 1 tool
`src/WindowsMcp/Tools/StartupTools.cs`

**Injected:** `IStartupReportService`

| Method | Description |
|--------|-------------|
| `StartupReport` | HiJackThis-style boot/persistence report. Sections: Run/RunOnce (all hives + per-user SIDs, enabled-state decoded), Startup folders, scheduled tasks, auto-start services, hosts, DNS, Winsock LSP, shell extensions, Control Panel applets (registry + `System32`/`SysWOW64` `*.cpl`), accessibility ATs, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, browser proxy, trusted-zone. Catalog-aware code-signing trust on every file-backed entry. `format`: `summary` (default, inline) / `json` / `text` / `both`; `includeProcesses` opt-in |

---

## Service Interfaces (`WindowsMcp.Abstractions`)

Located in `src/WindowsMcp.Abstractions/`. Each interface is a separate file.

| Interface | Key Methods |
|-----------|-------------|
| `IInputService` | `ClickAsync`, `DragAsync`, `HoverAsync`, `TypeAsync`, `PressKeyAsync`, `PressShortcutAsync`, `ScrollAsync` |
| `IScreenshotService` | `CaptureAsync(region?)`, `CaptureRegionAsync` |
| `IOcrService` | `RecognizeAsync(region)` |
| `IClipboardService` | `GetTextAsync`, `SetTextAsync` |
| `IAudioService` | `GetAsync`, `SetVolumeAsync`, `SetMutedAsync` |
| `IPowerShellService` | `RunAsync(command)` |
| `IJobService` | `StartAsync(command)`, `GetStatus(id)`, `GetOutput(id, tailChars)`, `Cancel(id)`, `List()` |
| `IUIAutomationService` | `GetStateAsync`, `FindElementAsync`, `GetElementAsync`, `GetTextAsync`, `AssertElementAsync`, `InteractAsync`, `GetTableAsync`, `WaitForAsync` |
| `IFileSystemService` | `ReadAsync`, `WriteAsync`, `ManageAsync`, `InfoAsync`, `SearchAsync`, `ArchiveAsync` |
| `IRegistryService` | `GetAsync`, `SetAsync`, `EnumerateValuesAsync`, `EnumerateSubKeysAsync` |
| `IServiceControlService` | `ListAsync`, `StartAsync`, `StopAsync`, `RestartAsync` |
| `IEventLogService` | `QueryAsync` |
| `ITaskSchedulerService` | `ListAsync`, `ListDetailedAsync`, `GetAsync`, `CreateAsync`, `DeleteAsync`, `RunAsync` |
| `IProcessService` | `ListAsync`, `InspectAsync`, `StartDetachedAsync`, `KillAsync`, `ListLineageAsync`, `GroupByRootAsync`, `KillGuardedAsync`, `KillTreeAsync` |
| `IWindowService` | `ListAsync`, `FocusAsync`, `GetAsync`, `LaunchAsync` |
| `IWmiService` | `QueryAsync(wql)` |
| `IStorageService` | `GetHealthAsync(driveLetter?, includeUsage, timeoutSeconds)` → `StorageHealthReport` |
| `IDiskService` | `GetUsageAsync`, `GetFileTypesAsync`, `GetStaleAsync`, `GetReclaimableAsync` |
| `ISecurityService` | `AuditAsync()` → `SecurityAuditDto` |
| `IFirewallService` | `ListAsync`, `AddAsync`, `RemoveAsync` |
| `IEnvService` | `GetAsync`, `SetAsync`, `ListAsync` |
| `IPowerService` | `SleepAsync`, `HibernateAsync`, `LockAsync`, `SignOutAsync` |
| `INotificationService` | `ShowAsync` |
| `INetworkService` | `GetAdaptersAsync`, `GetConnectionsAsync`, `GetFirewallRulesAsync` |
| `IWebService` | `FetchMarkdownAsync`, `RequestAsync` |
| `IAuthenticodeInspector` | `Inspect(path)` → catalog-aware trust + signer |
| `ILspEnumerator` | `Enumerate()` → Winsock catalog providers |
| `IShortcutResolver` | `ResolveTarget(lnk)` → `.lnk` target via IShellLink |
| `IStartupReportService` | `BuildAsync()` → aggregated `StartupReportDto` |

---

## Data Models (`WindowsMcp.Abstractions.Models`)

Located in `src/WindowsMcp.Abstractions/` alongside the interfaces (one DTOs file per domain):

| File | Key Types |
|------|-----------|
| `InputDtos.cs` | `ClickResult`, `DragResult`, `TypeResult`, `MouseButton` (enum) |
| `ScreenDtos.cs` | `ScreenshotResult`, `OcrResult`, `Region` |
| `UIAutomationDtos.cs` | `UiState`, `UiElement`, `FindKind` (enum), `TableData` |
| `ProcessDtos.cs` | `ProcessInfo`, `ProcessStartResult` |
| `WindowDtos.cs` | `WindowInfo`, `MonitorInfo` |
| `NetworkDtos.cs` | `AdapterInfo`, `ConnectionInfo`, `FirewallRule` |
| `FileSystemDtos.cs` | `FileEntry`, `ArchiveEntry`, `SearchResult` |
| `SystemDtos.cs` | `ServiceInfo`, `ScheduledTaskInfo`, `EventLogEntry`, `SystemInfoResult` |
| `PowerShellDtos.cs` | `PowerShellResult(string Stdout, string Stderr, int ExitCode)` |
| `WebDtos.cs` | `HttpResponse`, `ShortcutInfo` |
| `SecurityDtos.cs` | `AuthenticodeInfo`, `LspProviderDto` |
| `StartupReportDtos.cs` | `StartupReportDto` + section records (`RunEntry`, `StartupTaskEntry`, `StartupServiceEntry`, `LspProviderEntry`, `ShellExtensionEntry`, …) |

**Model pattern** — all DTOs are C# records:
```csharp
// Example from IAudioService.cs
public record AudioState(int Level, bool Muted);

// Example from IPowerShellService.cs
public record PowerShellResult(string Stdout, string Stderr, int ExitCode);
```

---

## Key Service Implementations

### `UIAutomationService`

Uses **FlaUI.UIA3** to walk the Windows Accessibility (UIA3) tree:
- `GetStateAsync()` — enumerates all elements in the foreground window's UIA3 tree
- `FindElementAsync()` — searches by name/value with optional kind filter
- `WaitForAsync()` — polls at `interval_ms` until element appears or `timeout_ms` elapses
- `GetTableAsync()` — reads cells via `IGridPattern`
- `AssertElementAsync()` — checks element properties: exists / enabled / checked / value / visible / focused

### `InputService`

Uses **H.InputSimulator** (`WindowsInput` namespace) which calls `SendInput` directly:
- Mouse events: `MoveMouse`, `LeftButtonClick`, `RightButtonClick`, `MiddleButtonClick`
- Keyboard events: `KeyPress`, `KeyDown`, `KeyUp`, `TextEntry`
- Note: `MouseButton` enum disambiguation required — `H.InputSimulator` also exports `WindowsInput.MouseButton`; the abstractions define `WindowsMcp.Abstractions.Models.MouseButton` to avoid ambiguity

### `PowerShellService`

Executes foreground PowerShell via `System.Diagnostics.Process` (system `powershell.exe`):
- Serializes all calls through a `SemaphoreSlim(1,1)` gate; a 15-minute execution backstop
  (started **after** the gate is acquired, so it bounds execution rather than queue-wait)
  tears down runaway scripts by killing the whole process tree
- Builds the invocation via the shared `PowerShellInvocation` helper: `-EncodedCommand`
  (base64 UTF-16LE, UTF-8 console-encoding preamble) with a temp-`.ps1` `-File` fallback for
  oversized scripts; stdin is redirected and closed so the child cannot eat MCP protocol bytes
- Returns `PSResult(Success, Stdout, Stderr, ExitCode, Errors)` to callers

### `JobService`

Background PowerShell jobs (`powershell background:true` + the `job` tool):
- Spawns children via the same `PowerShellInvocation` helper as the foreground service, but
  **outside** the foreground serialization gate — jobs run concurrently (cap: 8 running; new
  starts are rejected when full)
- Per-job 60-minute backstop kills the process tree and marks the job `timedOut`; `Cancel`
  marks it `cancelled`; a per-job monitor task is the single writer of the final state
  (`completed`/`failed` by exit code otherwise)
- Stdout/stderr are pumped into `BoundedTextBuffer`s (~1 MB/stream, oldest chars trimmed,
  trim counters surfaced); the ~32 most recent finished jobs are retained, oldest evicted
- Registry pattern mirrors `WatchService`: `Dictionary` + lock, sequential ids (`j1`, `j2`…),
  forgiving unknown-id semantics

### `BoundedTextBuffer`

Thread-safe bounded text accumulator (lock + `StringBuilder`): keeps the most recent tail once
capacity is exceeded and counts trimmed chars — the unit-testable core of job output capture
(sibling of `EventRingBuffer`).

### `ScreenshotService`

Uses **SkiaSharp** for capture and encoding:
- `CaptureAsync()` — captures the full virtual screen (all monitors) via `BitBlt` + `SkiaSharp` PNG encode
- `CaptureRegionAsync(Region)` — clips to specified bounding box before encode
- Returns base64-encoded PNG strings

### `OcrService`

Uses the **Windows.Media.Ocr** WinRT API:
- Calls `OcrEngine.TryCreateFromUserProfileLanguages()` for language detection
- Returns word-level bounding boxes and recognized text

### `AudioService`

v0.2.0 limitation — uses PowerShell + `SendKeys` as a backend:
- `GetAsync()` — reads volume via `Get-AudioDevice` PowerShell module or WMI fallback
- `SetVolumeAsync()` — sends `VK_VOLUME_UP`/`VK_VOLUME_DOWN` key presses
- `SetMutedAsync()` — sends `VK_VOLUME_MUTE` toggle (cannot set absolute mute state)
- Tracked for v0.3.0 to switch to NAudio/CoreAudio COM for accurate read/write

---

## NuGet Package Reference

| Package | Version | Purpose |
|---------|---------|---------|
| `ModelContextProtocol` | 1.4.x | MCP SDK — stdio transport, `[McpServerTool]` discovery, request filters |
| `ModelContextProtocol.AspNetCore` | 1.4.x (lockstep) | Streamable HTTP transport (`WithHttpTransport`, `MapMcp`) on Kestrel via the `Microsoft.AspNetCore.App` framework reference |
| `FlaUI.UIA3` | latest | Windows UI Automation API (UIA3 COM wrapper) |
| `H.InputSimulator` | latest | `SendInput`-based keyboard and mouse simulation |
| `SkiaSharp` | latest | Screenshot capture, image encode/decode |
| `CsWin32` | latest | Source-generated P/Invoke for Win32 APIs (DPI, AUMID, etc.) |
| `Microsoft.Extensions.Hosting` | latest | Generic Host, DI container, configuration |
| `ReverseMarkdown` | latest | HTML → Markdown conversion for `Scrape` tool |
| `TaskScheduler` | latest | Windows Task Scheduler COM automation |
| `TextCopy` | latest | Cross-platform clipboard read/write |
| `System.Diagnostics.EventLog` | latest | Windows Event Log querying |
| `System.Drawing.Common` | latest | GDI+ image support (legacy compat) |
| `System.Management` | latest | WMI query execution (`ManagementObjectSearcher`) |
