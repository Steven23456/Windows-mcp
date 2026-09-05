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
    // 1. Repair a host-stripped environment (PATHEXT, ProgramData, …) before anything spawns a
    //    child; repaired names are logged to stderr once. Host-set values are never overwritten
    EnvironmentRepair.Apply();

    // 2. Register AppUserModelID for WinRT toast notifications
    PInvoke.SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server");

    // 3. Per-Monitor DPI Awareness V2 — physical pixel coordinates on HiDPI
    PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((nint)(-4)));

    // 4. Command line / WINDOWSMCP_* env → ServerOptions (exit 2 + usage on a bad option; --help)
    var options = ServerOptions.Parse(args, Environment.GetEnvironmentVariable);
    return options.IsHttp ? await RunHttpAsync(options) : await RunStdioAsync(args, options);
}

static async Task<int> RunStdioAsync(string[] args, ServerOptions options)
{
    // Force UTF-8 to prevent JSON-RPC response buffering on Windows (cp1252 default)
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding  = System.Text.Encoding.UTF8;

    var builder = Host.CreateApplicationBuilder(args);
    WindowsMcpHost.ConfigureStderrLogging(builder.Logging, http: false);
    builder.AddWindowsMcp(options)      // Hosting/WindowsMcpHost: all singletons + AddMcpServer(ServerInfo)
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

Tool classes are `[McpServerToolType]`-annotated, sealed, and stateless (except for injected service references). All tool methods are `async Task<string>` and return JSON-serialized results or plain strings — except `screenshot`, which returns `async Task<CallToolResult>` so it can carry an image content block.

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
| `Key` | `(string key)` | Press one key: a character (`a`, `7`, `/`), `f1`–`f24`, or a name (enter, tab, esc, win, printscreen, …) |
| `Shortcut` | `(string shortcut)` | Press a chord (`ctrl+c`, `ctrl+shift+s`, `win+r`); a single part such as `win` is a bare key press |
| `Scroll` | `(int x, int y, string direction, int amount=3)` | Scroll mouse wheel |
| `Clipboard` | `(string action, string? text=null)` | `get` or `set` clipboard text |

---

### `UIAutomationTools` — 9 tools
`src/WindowsMcp/Tools/UIAutomationTools.cs`

**Injected:** `IUIAutomationService`

| Method | Signature | Description |
|--------|-----------|-------------|
| `Snapshot` | `(string scope="desktop", string? window=null, bool include_tree=false, int max_elements=0, string format="text", bool use_dom=false)` | One call for the whole desktop: window list (z-order, topmost first), active window, cursor, every interactive element with its centre coordinates and an action hint (click/fill/toggle/select/slide/scroll), and the scrollable regions with their percentages. `scope`: desktop (default) / foreground / window (needs `window`, exact-then-substring). `format="text"` (default) is the compact render, `"json"` the serialised `SnapshotResult` (`include_tree` adds the element tree). `max_elements` caps the walk (0 = `--max-tree-elements`, default 500); a truncated result says so. Element ids (`el_N`) work with `click`/`interact_element`/`get_element` and are valid until the next snapshot. `use_dom` is refused until A-5 |
| `GetState` | `()` | UI element tree of the foreground app, three levels deep and bounded by the element budget; the root carries `Truncated`/`ElementLimit` when the walk was cut short |
| `FindElement` | `(string text, string kind="any", string scope="foreground", string? window=null, bool include_offscreen=false)` | Find elements whose name contains `text`; kind: any/interactive/text/scrollable; scope: foreground (default) / window (needs `window`) / desktop; off-screen elements dropped unless `include_offscreen`; ≤20 matches, capped after filtering |
| `GetElement` | `(string element_id)` | Properties of a specific element by ID |
| `GetText` | `(string element_id)` | Extract text content (faster than OCR) |
| `AssertElement` | `(string element_id, string state, string? expected=null)` | exists / enabled / checked / visible / focused / value (needs `expected`); returns `PASS` or `FAIL: <state> — observed <what was found>` |
| `InteractElement` | `(string element_id, string action, string? value=null)` | click / invoke / toggle / select / focus / type via UIA patterns, with a physical click or keyboard fallback; returns `InteractResult` JSON naming what fired |
| `GetTable` | `(string element_id)` | Extract grid/table data via `GridPattern` |
| `WaitFor` | `(string text, int timeout_ms=10000, int interval_ms=500, string kind="any", string scope="foreground", string? window=null, bool include_offscreen=false)` | Poll `find_element` until a match appears; same filters; a failed poll is retried, and if every poll failed it errors instead of reporting `null` |

---

### `WindowTools` — 5 tools
`src/WindowsMcp/Tools/WindowTools.cs`

**Injected:** `IWindowService`

| Method | Description |
|--------|-------------|
| `Window` | `list` every user-visible top-level window in z-order (`include_minimized` default true, `include_hidden` default false) / `active` the foreground one (`{"found":false}` when there is none) / `minimize`, `maximize`, `restore`, `close` a window found by exact title (`FindWindow`); the action is validated first, then the title the four acting actions require |
| `SwitchToWindow` | Bring a window to the foreground by exact title (`SetForegroundWindow`) |
| `Focus` | Alias of `SwitchToWindow` |
| `Launch` | Launch an application by name or path via ShellExecute; returns the PID |
| `MultiMonitor` | Enumerate monitors: index, device name, bounds, primary flag |

---

### `FileTools` — 9 tools
`src/WindowsMcp/Tools/FileTools.cs`

**Injected:** `IFileSystemService`, `IInputService`, `IFileStreamService`

| Method | Description |
|--------|-------------|
| `FileRead` | Read a file as text (`max_bytes`, `encoding` auto/utf-8/utf-16/ascii) |
| `FileWrite` | Write text to a file (`confirm:true`) |
| `FileManage` | `copy` / `move` / `delete` (`confirm:true`) / `list` |
| `FileInfo` | Get file or directory metadata |
| `FileSearch` | Search for files by glob pattern |
| `FileHash` | Compute a file's SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse (symlink/junction) target |
| `FileDialog` | Interact with a native open/save dialog |
| `Archive` | `zip` a directory or `unzip` an archive |

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

**Injected:** `IScreenshotService`, `IOcrService`, `IWindowService` (monitor inventory), `IInputService` (cursor position), `IUIAutomationService` (the element walk `annotate` draws and lists), plus the `ScreenshotOptions` record (`--screenshot-scale`)

| Method | Description |
|--------|-------------|
| `Screenshot` | Capture the primary display, selected monitors (`display="all"`/`"0,2"`) or an `x,y,w,h` region (virtual-desktop pixels, validated against the virtual screen). Returns a `CallToolResult`: a JSON metadata text block (encoded and original size, captured `region`, `displays`, `cursor`, `cursorDrawn?`, `coordinateScale?`/`note?`) plus an `ImageContentBlock` (`output="inline"`, default; `"base64"` is an alias). `output="file"` saves to `%TEMP%\WindowsMcp` and returns the path in the metadata instead. Downscaled to fit `max_width`×`max_height` (1920×1080), with `scale`/`quality` on top; jpeg inline, png to file. `annotate:true` adds one `SnapshotAsync(desktop)` before the capture, draws a 2 px coloured box and a label chip (the snapshot's `el_N` ids) around every interactive element overlapping the captured rect, inserts the rendered element list as a second text block, and adds `annotated`/`annotations` to the metadata; `grid_columns`/`grid_rows` (0–64, no walk needed) overlay guide lines captioned with virtual-desktop coordinates and add `grid` |
| `Ocr` | Extract text from the primary display, a `display` selection, or a region — same parser, always captured at full resolution |

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
| `RegistryGet` | Read a named value, or list the key's value names when `value_name` is omitted |
| `RegistrySet` | Write a value (String / DWord / QWord / Binary / MultiString / ExpandString); `confirm:true` |

---

### `NetworkTools` — 2 tools
`src/WindowsMcp/Tools/NetworkTools.cs`

**Injected:** `INetworkService`, `IFirewallService`

| Method | Description |
|--------|-------------|
| `Network` | Adapters, listening ports (with owning process), Wi-Fi, DNS lookup, ping |
| `Firewall` | `list` / `add` / `remove` firewall rules (`confirm:true` for add/remove) |

---

### `WebTools` — 2 tools
`src/WindowsMcp/Tools/WebTools.cs`

**Injected:** `IWebService`

| Method | Description |
|--------|-------------|
| `Scrape` | Fetch a URL and convert HTML to Markdown (private address ranges rejected, DNS-rebinding aware) |
| `HttpRequest` | HTTP request (GET/POST/PUT/DELETE/PATCH) with optional JSON headers and body; same private-range rejection |

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

### `IntegrityTools` — 1 tool
`src/WindowsMcp/Tools/IntegrityTools.cs`

**Injected:** `IIntegrityService`

| Method | Description |
|--------|-------------|
| `Integrity` | File-integrity tripwire over a curated watch-list (hosts file, user + machine Startup folders, `~/.claude/settings.json`, `~/.gitconfig`, the `C:\` governance files). `mode`: `baseline` (SHA-256 snapshot to `%LOCALAPPDATA%\windows-mcp\integrity`, survives plugin upgrades) / `check` (added / removed / modified vs. baseline) / `list` (default watch-list + current baseline). `paths` adds extra semicolon-separated paths on `baseline` |

---

### `UsnTools` — 1 tool
`src/WindowsMcp/Tools/UsnTools.cs`

**Injected:** `IUsnService`

| Method | Description |
|--------|-------------|
| `FsChanges` | NTFS USN change journal — whole-volume file-change tracking from the OS journal. `mode`: `status` (journal id + `FirstUsn` / `NextUsn` / `LowestValidUsn`; record `NextUsn` now, query `since` it later) / `since` (records from `start_usn` forward, `max` default 200). `volume` default `C`. Requires elevation |

---

### `WatchTools` — 1 tool
`src/WindowsMcp/Tools/WatchTools.cs`

**Injected:** `IWatchService`

| Method | Description |
|--------|-------------|
| `Watch` | Live directory watching (`FileSystemWatcher`) with server-side buffering. `mode`: `start` (`path`, `filter` glob, `subdirs`; returns a session id) / `poll` (drain buffered created/changed/deleted/renamed events, `max` default 500) / `stop` / `list` (sessions with buffered/dropped counts). Events sit in a bounded ring (`EventRingBuffer`) between polls; oldest dropped when full |

---

## Service Interfaces (`WindowsMcp.Abstractions`)

Located in `src/WindowsMcp.Abstractions/`. Each interface is a separate file.

| Interface | Key Methods |
|-----------|-------------|
| `IInputService` | `ClickAsync`, `DragAsync`, `HoverAsync`, `TypeAsync`, `PressKeyAsync`, `PressShortcutAsync`, `ScrollAsync`, `GetCursorPositionAsync` → `CursorPosition` |
| `IScreenshotService` | `CaptureAsync(region?, options?)` → `ScreenshotResult` (`CaptureOptions`: format, max size, scale, quality, cursor, annotations, grid) |
| `IOcrService` | `ExtractTextAsync(region?)` → text |
| `IClipboardService` | `GetTextAsync`, `SetTextAsync` |
| `IAudioService` | `GetAsync` → `AudioState`, `SetVolumeAsync`, `SetMutedAsync` |
| `IPowerShellService` | `RunAsync(command)` → `PSResult` |
| `IJobService` | `StartAsync(command)`, `GetStatus(id)`, `GetOutput(id, tailChars)`, `Cancel(id)`, `List()` |
| `IUIAutomationService` | `GetStateAsync`, `FindElementAsync(text, kind, scope, windowTitle, includeOffscreen)`, `GetElementAsync`, `GetTextAsync`, `AssertElementAsync` → `AssertResult`, `InteractAsync` → `InteractResult`, `GetTableAsync`, `WaitForAsync(text, timeoutMs, intervalMs, kind, scope, windowTitle, includeOffscreen)`, `FocusAsync`, `SnapshotAsync(SnapshotRequest)` → `SnapshotResult` |
| `IFileSystemService` | `ReadTextAsync`, `ReadBytesAsync`, `WriteTextAsync`, `CopyAsync`, `MoveAsync`, `DeleteAsync`, `ListAsync`, `SearchAsync`, `GetInfoAsync`, `HashFileAsync`, `ZipAsync`, `UnzipAsync` |
| `IFileStreamService` | `GetStreamsAsync(path)` → `FileStreamsDto` (alternate data streams + reparse target) |
| `IRegistryService` | `GetAsync`, `SetAsync`, `EnumerateValuesAsync`, `EnumerateSubKeysAsync` |
| `IServiceControlService` | `ListAsync`, `GetStatusAsync`, `StartAsync`, `StopAsync`, `RestartAsync` |
| `IEventLogService` | `QueryAsync` |
| `ITaskSchedulerService` | `ListAsync`, `ListDetailedAsync`, `GetAsync`, `CreateAsync`, `DeleteAsync`, `RunAsync` |
| `IProcessService` | `ListAsync`, `InspectAsync`, `StartDetachedAsync`, `KillAsync`, `ListLineageAsync`, `GroupByRootAsync`, `KillGuardedAsync`, `KillTreeAsync` |
| `IWindowService` | `ExecuteAsync(action, title)`, `SwitchToAsync(title)`, `LaunchAsync(app)`, `EnumerateMonitorsAsync`, `ListAsync(includeMinimized, includeHidden)` → `WindowInfo[]`, `GetActiveAsync()` → `WindowInfo?` |
| `IWmiService` | `QueryAsync(className, properties?, where?)` → rows |
| `IStorageService` | `GetHealthAsync(driveLetter?, includeUsage, timeoutSeconds)` → `StorageHealthReport` |
| `IDiskService` | `GetUsageAsync`, `GetFileTypesAsync`, `GetStaleAsync`, `GetReclaimableAsync` |
| `ISecurityService` | `AuditAsync()` → `SecurityAuditDto`, `GetDefenderStatusAsync()` → `DefenderStatusDto` |
| `ICertStoreService` | `ListAsync` → `CertInfoDto[]` (flags self-signed and expired) |
| `IReliabilityService` | `GetAsync(maxRecords)` → `ReliabilityReport` |
| `IDriverService` | `ListAsync()` → `DriverInfo[]` |
| `IFirewallService` | `ListAsync`, `AddAsync`, `RemoveAsync` |
| `IEnvService` | `GetAsync`, `SetAsync`, `ListAsync` |
| `IPowerService` | `ExecuteAsync(action)` — shutdown / reboot / logoff / lock / sleep / hibernate |
| `INotificationService` | `ShowAsync(title, message)` |
| `INetworkService` | `ListAdaptersAsync`, `ListPortsAsync`, `GetWifiAsync`, `DnsLookupAsync`, `PingAsync` |
| `IWebService` | `ScrapeAsync(url)`, `RequestAsync(url, method, headers, body)` → `HttpResponseDto` |
| `IIntegrityService` | `BaselineAsync`, `CheckAsync`, `GetBaseline`, `DefaultWatchList` |
| `IUsnService` | `StatusAsync(volume)` → `UsnStatus`, `ReadAsync(volume, startUsn, max)` → `UsnReadResult` |
| `IWatchService` | `Start(path, filter, subdirs)` → `WatchSession`, `Poll(id, max)` → `WatchEvent[]`, `Stop(id)`, `List()` |
| `IAuthenticodeInspector` | `Inspect(path)` → `AuthenticodeInfo` (catalog-aware trust + signer) |
| `ILspEnumerator` | `Enumerate()` → Winsock catalog providers |
| `IShortcutResolver` | `ResolveTarget(lnk)` → `.lnk` target via IShellLink |
| `IStartupReportService` | `BuildAsync()` → aggregated `StartupReportDto` |

---

## Data Models (`WindowsMcp.Abstractions.Models`)

Located in `src/WindowsMcp.Abstractions/Models/` (one DTOs file per domain, 21 files):

| File | Key Types |
|------|-----------|
| `InputDtos.cs` | `ClickResult`, `DragResult`, `TypeResult`, `CursorPosition`, `MouseButton` (enum) |
| `ScreenDtos.cs` | `ScreenRegion`, `CaptureOptions` (trailing `Annotations`/`Grid`), `AnnotationBox`, `GridSpec`, `ScreenshotResult` (trailing `AnnotationsDrawn`), `ScreenshotOptions`, `ImageFormat` (enum) |
| `UIAutomationDtos.cs` | `ElementInfo` (trailing `Scroll`), `Bounds`, `ScrollInfo`, `ElementTree` (trailing `Truncated`/`ElementLimit`, omitted from JSON when default), `FindElementResult`, `FindKind` (enum), `FindScope` (enum), `TableData`, `InteractResult`, `AssertResult`, `SnapshotScope` (enum), `SnapshotRequest`, `UiTreeOptions`, `SnapshotElement`, `SnapshotScrollable`, `SnapshotResult` |
| `WindowDtos.cs` | `WindowAction`, `MonitorInfo`, `WindowInfo`, `WindowProbe`, `WindowState` (enum, serialised by name) |
| `ProcessDtos.cs` | `ProcessDto`, `ProcessDetailDto`, `ModuleInfo`, `ProcessLineageDto`, `ProcessGroupDto` |
| `PowerShellDtos.cs` | `PSResult` (success, stdout, stderr, exit code, parsed errors) |
| `JobDtos.cs` | `JobInfo`, `JobOutput` |
| `FileSystemDtos.cs` | `FileInfoDto`, `FileSearchHit`, `AlternateStreamInfo`, `FileStreamsDto`, `RegistryValueDto`, `ServiceDto`, `ScheduledTaskDto`, `ScheduledTaskDetailDto`, `EventLogEntryDto` |
| `SystemDtos.cs` | `WmiResultDto` |
| `NetworkDtos.cs` | `NetworkAdapterDto`, `PortInfoDto`, `WifiInfoDto`, `PingResult` |
| `FirewallDtos.cs` | `FirewallRuleDto` |
| `WebDtos.cs` | `HttpResponseDto` |
| `DiskDtos.cs` | `DiskUsageEntry`, `FileTypeEntry`, `StaleFileEntry`, `ReclaimableSpace` |
| `StorageDtos.cs` | `StorageHealthReport`, `PhysicalDiskInfo`, `DiskInfo`, `VolumeInfo`, `ReliabilityInfo`, `DiskEventInfo` |
| `SecurityDtos.cs` | `AuthenticodeInfo`, `LspProviderDto`, `SecurityAuditDto`, `DefenderStatusDto`, `CertInfoDto` |
| `ReliabilityDtos.cs` | `ReliabilityReport`, `MinidumpInfo`, `ReliabilityRecord` |
| `DriverDtos.cs` | `DriverInfo` |
| `StartupReportDtos.cs` | `StartupReportDto`, `StartupHeader` + one record per section (`RunEntry`, `StartupFolderEntry`, `StartupTaskEntry`, `StartupServiceEntry`, `HostsEntry`, `DnsEntry`, `LspProviderEntry`, `ShellExtensionEntry`, `ControlPanelAppletEntry`, `AccessibilityToolEntry`, `IfeoEntry`, `WinlogonHookEntry`, `AppInitDllEntry`, `ActiveSetupEntry`, `BrowserProxyEntry`, `TrustedZoneEntry`, `ProcessEntry`) |
| `IntegrityDtos.cs` | `IntegrityItem`, `IntegrityBaseline`, `IntegrityChange`, `IntegrityCheckResult` |
| `UsnDtos.cs` | `UsnStatus`, `UsnChange`, `UsnReadResult` |
| `WatchDtos.cs` | `WatchSession`, `WatchEvent` |

**Model pattern** — all DTOs are C# records:
```csharp
// Models/ScreenDtos.cs
public record ScreenRegion(int X, int Y, int Width, int Height);
public record ScreenshotResult(byte[] Bytes, int Width, int Height, ImageFormat Format,
    int OriginalWidth, int OriginalHeight, double CoordinateScale, string? CursorDrawn = null,
    int AnnotationsDrawn = 0);

// IAudioService.cs (small result types may sit next to their interface)
public record AudioState(int Level, bool Muted);
```

---

## Key Service Implementations

### `UIAutomationService`

Uses **FlaUI.UIA3** to walk the Windows Accessibility (UIA3) tree:
- `GetStateAsync()` — builds a three-level `ElementTree` rooted at the foreground window (falls back to the focused element, then the desktop); every element gets a cached `el_N` id. The descent spends an `ElementBudget` (`UiTreeOptions.MaxElements`, from `--max-tree-elements`, default 500) per node and stops when it refuses; the **root** then carries `Truncated: true` and `ElementLimit`, which are omitted from the JSON otherwise
- `SnapshotAsync(request)` — the whole desktop in one call. Header from `IWindowService` (window list, active window, monitors) and `IInputService` (cursor), each read once; roots by scope — `desktop` walks every non-minimised window topmost first, `foreground` the active entry (falling back to UIA's own foreground window when the inventory flags none), `window` matches a title exact-then-substring and otherwise throws naming up to 15 open titles. One `ElementBudget` (per-call `MaxElements`, else `UiTreeOptions`) covers the whole call on the STA thread; a window whose walk throws is logged and skipped. Each walked node gets an `el_N` id, and the ids the *previous* snapshot issued are evicted from the element cache when the next one starts — a `find_element` id issued in between survives. The pure `internal static Project` splits one walked node into an interactive element and/or a scrollable region and never lets a password's value out
- `FindElementAsync()` — walks one window root at a time (foreground by default; `scope=window` resolves a title exact-then-substring against the top-level windows and names the open windows when nothing matches; `scope=desktop` walks them all). Every property read is guarded and each element is evaluated inside a catch, so an element that dies mid-walk is skipped rather than failing the call; the kind filter is pushed into a UIA `OrCondition` for descendants and applied client-side to the root. `kind=interactive` is upstream's control-type set plus `Document` (`InteractiveControlTypes`). Off-screen elements and empty bounds are dropped before the 20-result cap unless `includeOffscreen` — an `Edit` with real bounds is kept either way, because browsers over-report it as off-screen
- `InteractAsync()` — click / invoke / toggle / select / focus / type. Each acts through a UIA pattern (Invoke, SelectionItem, Toggle, Value) or a physical fallback via `IInputService` (a click at the element's centre; keyboard entry when there is no writable ValuePattern) and returns an `InteractResult` naming what fired; an unsupported pattern throws `NotSupportedException` with the control type — never a silent no-op. `FocusAsync()` sets keyboard focus
- `WaitForAsync()` — polls `FindElementAsync` (same kind/scope/window/off-screen filters, the window re-resolved each poll) via the pure `PollAsync` loop: polls at least once, retries a poll that throws, clamps the sleep to the remaining budget, returns `null` when clean polls found nothing, and throws `TimeoutException` when *every* poll failed
- `GetTableAsync()` — reads cells via `IGridPattern` and column headers via the `TablePattern`; the raw strings are projected by the unit-testable `BuildTable`, so every header and cell is sanitised and a column with no header element is `""` rather than null
- `AssertElementAsync()` — exists / enabled / checked / visible / focused / value (`expected`: ordinal match against the ValuePattern value, else the Name — the same read as `get_text`); returns `AssertResult` with the observed state (focus owner, actual value, toggle state). A stale element (ProcessId 0, or UIA_E_ELEMENTNOTAVAILABLE / an RPC failure on a read — `IsElementGone`) fails with `element no longer available` instead of throwing; optional properties a provider omits (modern Notepad's document has no `IsOffscreen`) fall back to UIA's defaults

### `InputService`

Uses **H.InputSimulator** (`WindowsInput` namespace) for `SendInput` button, wheel and key events, and Win32 `SetCursorPos` for cursor placement:
- Cursor: `SetCursorPos(x, y)` in physical virtual-desktop pixels (origin = the primary monitor's top-left; monitors left of / above it have negative coordinates), then a `GetCursorPos` read-back — a point Windows clamped (off any monitor) throws `ArgumentOutOfRangeException` instead of clicking somewhere else. Button and wheel events carry no position, so they act at that cursor
- Mouse events: `LeftButtonClick` / `RightButtonClick` / `MiddleButtonClick`, `…ButtonDown/Up` for drags, `VerticalScroll` / `HorizontalScroll`
- Keyboard events: `KeyPress`, `ModifiedKeyStroke`, `TextEntry`. Key names and chords are resolved by the pure `ShortcutParser`: named keys and aliases, `f1`–`f24`, numpad and media keys, single characters (`a`–`z` / `0`–`9` directly, anything else through `VkKeyScan` with the layout's implied Shift), `plus` for the `+` key, and bare keys such as `win`
- `GetCursorPositionAsync()` — `GetCursorPos` in those same virtual-desktop pixels; `screenshot` reads it once and hands it to the capture, so the reported position and the drawn mark cannot disagree
- Note: `MouseButton` enum disambiguation required — `H.InputSimulator` also exports `WindowsInput.MouseButton`; the abstractions define `WindowsMcp.Abstractions.Models.MouseButton` to avoid ambiguity

### `PowerShellService`

Executes foreground PowerShell via `System.Diagnostics.Process` (system `powershell.exe`):
- Serializes all calls through a `SemaphoreSlim(1,1)` gate; a 15-minute execution backstop
  (started **after** the gate is acquired, so it bounds execution rather than queue-wait)
  tears down runaway scripts by killing the whole process tree
- Builds the invocation via the shared `PowerShellInvocation` helper: `-EncodedCommand`
  (base64 UTF-16LE, two-line preamble — UTF-8 console encoding, then
  `$ProgressPreference='SilentlyContinue'`) with a temp-`.ps1` `-File` fallback for oversized
  scripts; stdin is redirected and closed so the child cannot eat MCP protocol bytes
- `Stderr` is decoded through `ClixmlStderr`: Windows PowerShell 5.1 wraps every non-stdout stream
  in CLIXML when stderr is redirected, so progress records are dropped and error/warning/verbose/
  debug records become stream-prefixed text (`WARNING: careful`). Non-CLIXML and unparseable CLIXML
  pass through raw — losing output is worse than a blob
- `Errors[]` still holds only the `<S S="Error">` records (they alone decide `Success`), extracted
  through the same `ClixmlStderr` parser so the two cannot drift
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
- Stderr is decoded from CLIXML like the foreground tool's: **once** when the job finishes (in the
  monitor, before the state flips to a terminal value, rewriting the buffer via
  `BoundedTextBuffer.ReplaceAll` so `Tail`/`Length`/`TrimmedChars` stay consistent), and on read
  while a job is still running — `ClixmlStderr` drops a trailing partial document, so a mid-flush
  read still decodes the records that are complete. Stdout is never CLIXML and is left alone
- Registry pattern mirrors `WatchService`: `Dictionary` + lock, sequential ids (`j1`, `j2`…),
  forgiving unknown-id semantics

### `BoundedTextBuffer`

Thread-safe bounded text accumulator (lock + `StringBuilder`): keeps the most recent tail once
capacity is exceeded and counts trimmed chars — the unit-testable core of job output capture
(sibling of `EventRingBuffer`).

### `ScreenshotService`

GDI capture + **SkiaSharp** downscale and encode:
- `CaptureAsync(region?, options?)` — `Graphics.CopyFromScreen` of the given `ScreenRegion` (null = the primary display). With `CaptureOptions.IncludeCursor` the pointer is composited onto the full-resolution GDI bitmap first (real cursor icon through `DrawIconEx`, else `CursorOverlay.DrawRing`); the buffer is then wrapped zero-copy into an `SKBitmap`, resized to `ScaleMath.Fit(...)` with a Mitchell cubic filter when that changes the size, and encoded as PNG or JPEG at `Quality`. Resize and encode both run before `UnlockBits` — the `SKBitmap` points into the GDI buffer
- `EncodeAnnotated(bmp, format, quality, boxes, captured, coordinateScale, grid)` — the encode step both paths route through. With no boxes and no grid it is byte-identical to `Encode`; otherwise it copies the bitmap first (the unscaled path's `SKBitmap` is a zero-copy view of a read-only GDI lock), hands the copy to `Annotator.Draw`, and reports how many boxes landed. Drawing happens **after** the downscale, so a 2 px box and an 11 px chip stay legible at the output size and map through the same `CoordinateScale` the metadata reports
- Returns `ScreenshotResult(Bytes, Width, Height, Format, OriginalWidth, OriginalHeight, CoordinateScale, CursorDrawn, AnnotationsDrawn)`; the `screenshot` tool turns that into an image content block plus a metadata text block (`output="file"` writes to `%TEMP%\WindowsMcp` and returns the path instead)
- Which rect to capture is the tool's decision (`RegionMath` over `IWindowService.EnumerateMonitorsAsync`); the service captures whatever rect it is handed

### Pure helpers (`ScaleMath`, `RegionMath`, `CursorMath`, `CursorOverlay`, `Annotator`, `UiText`, `WindowFilter`)

`internal static` classes in `Services/` with no Win32, no screen and no UIA dependency, so every
rule is unit-tested headless:
- `ScaleMath.Fit(origW, origH, maxW, maxH, userScale)` — fit inside the cap (cap ≤ 0 = ignored), apply the user scale, never upscale; returns the output size and `CoordinateScale` = origW / Width
- `RegionMath` — `ParseRegion("x,y,w,h")`, `ParseDisplays("all" | "0,2")`, `Union`, `VirtualScreen`, `Primary`, and `Validate`, which **rejects** a region outside the virtual screen rather than clipping it. Shared by `screenshot` and `ocr` so the two cannot drift
- `CursorMath.MonitorIndexOf(x, y, monitors)` — the monitor a virtual-desktop point sits on, `-1` for none
- `CursorOverlay` — `RingPoint` (cursor rebased onto the captured rect, null when outside) and `DrawRing` (white 3 px ring at radius 12, black 2 px at radius 8)
- `Annotator` — A-6's drawing core (SkiaSharp only, no screen): a twelve-colour opaque `Palette` indexed by list position via `ColorFor`, so a colour always means the same label even when an off-image box is skipped; `ToImage` maps virtual-desktop `Bounds` to image pixels (subtract the captured origin, divide by the coordinate scale, round half **away from zero**, widen a sub-pixel box to 1 px, clip — null when nothing is in the picture); `ChipRect` places the label chip just above the box's top-left, inside the box when there is no room, never off the image; `UseDarkText` picks black or white by luminance; `Draw` paints the grid first, then each box as a 2 px stroke plus a filled chip, and returns how many were drawn. Grid lines are translucent dark grey at every interior division, captioned with the **virtual-desktop** coordinate, not the image pixel
- `UiText.Sanitize` — strips Private Use Area code points, replaces lone UTF-16 surrogates with U+FFFD, drops C0/C1 controls except tab/LF/CR, trims; returns the same instance when nothing needed changing
- `WindowFilter` — A-1's judgement over the `WindowProbe` records `WindowService` gathers, so every rule is provable on hand-written probes with no desktop attached. `Keep` drops a window that is not visible, a `WS_EX_TOOLWINDOW` without `WS_EX_APPWINDOW`, a DWM-cloaked one (UWP ghosts, other virtual desktops), a zero-area one, the shell chrome classes (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `Progman`, `WorkerW`, `IME`, `MSCTFIME UI`), an untitled one unless `includeHidden` (the title is judged **after** `UiText.Sanitize`) and a minimized one unless `includeMinimized`; `StateOf` reads `Minimized` before `Maximized` (a minimized window keeps `WS_MAXIMIZE`); `IsBrowser` matches `chrome, msedge, firefox, brave, opera, vivaldi` with or without `.exe`; `Build` projects the survivors onto `WindowInfo`, renumbering `ZOrder` from 0 and taking `MonitorIndex` from the window's centre via `CursorMath.MonitorIndexOf`; `ActiveOf` picks the entry flagged `IsActive`, so `active` reports the list's real `ZOrder`

### Snapshot core (`Services/UiTree/`)

`internal` types the `snapshot` tool is built from, split out so everything except the traversal
itself is unit-tested headless:
- `UiNode` — the record of every fact one element contributes: control type, name, bounds, window
  title, depth, enabled, off-screen, focus, password, value, range min/value/max, toggle state,
  expand state, access key, accelerator key, legacy role, `ScrollInfo`
- `UiClassifier` — **the single home of D-6's interactive control-type set** (`UIAutomationService.InteractiveControlTypes` now forwards to it, so the find path and the snapshot cannot drift), plus upstream's LegacyIAccessible role fallback for `Custom` elements (`text` counts only when the node carries a value), the informative set, `ActionFor` (`Edit`/`Document` → fill, CheckBox → toggle, ComboBox → select, Slider/Spinner → slide, ScrollBar → scroll, else click), `IsScrollable`, `CenterOf`, `ShortcutOf`
- `ElementBudget` — `TryTake` per admitted node, `Truncated` on the first refusal, and one `NoteFor(limit)` sentence the renderer prints verbatim
- `UiTraverser` — walks one window: re-fetches the root under a single FlaUI `CacheRequest` (`TreeScope.Subtree`, `AutomationElementMode.Full`, with each pattern *property* id cached as well) and walks `CachedChildren`, so a subtree costs one cross-process fetch instead of one per property. Every read is guarded, names and values go through `UiText.Sanitize`, each node is clipped to the window rect by the pure `Clip` and dropped when off-screen or zero-area, and the budget is spent once per admitted node. Pre-order, root first
- `SnapshotRenderer` — the compact text form: cursor line, active window, z-ordered window list, interactive rows grouped by window in first-appearance order with a fixed tag order (action, focused, password, value, toggle, expand, shortcut, range), scrollable rows with percentages and `[reached top]`/`[reached bottom]`, then the budget note when truncated. A password never prints a value, values clip at 80 chars, and CR/LF/tab/backslash are escaped so one element is always one row

### `OcrService`

Uses the **Windows.Media.Ocr** WinRT API:
- Calls `OcrEngine.TryCreateFromUserProfileLanguages()` for language detection
- `ExtractTextAsync(region?)` returns the recognized text
- Captures through `IScreenshotService` with `MaxWidth`/`MaxHeight` of 0, so OCR always reads a full-resolution PNG whatever the `screenshot` defaults are

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
| `ModelContextProtocol` | 2.2.0 | MCP SDK — stdio transport, `[McpServerTool]` discovery, request filters |
| `ModelContextProtocol.AspNetCore` | 2.2.0 (lockstep) | Streamable HTTP transport (`WithHttpTransport`, `MapMcp`) on Kestrel via the `Microsoft.AspNetCore.App` framework reference |
| `FlaUI.UIA3` | 5.0.0 | Windows UI Automation API (UIA3 COM wrapper) |
| `H.InputSimulator` | 1.* | `SendInput`-based keyboard and mouse simulation |
| `SkiaSharp` | 4.151.1 | Screenshot capture, image encode/decode |
| `Microsoft.Windows.CsWin32` | 0.3.* (`PrivateAssets=all`) | Source-generated P/Invoke for Win32 APIs (DPI, AUMID, `SetCursorPos`, `VkKeyScan`, etc.) |
| `Microsoft.Extensions.Hosting` | shared framework | Generic Host, DI container, configuration (no `PackageReference`; comes with the runtime / `Microsoft.AspNetCore.App`) |
| `ReverseMarkdown` | 6.2.1 | HTML → Markdown conversion for `Scrape` tool |
| `TaskScheduler` | 2.12.2 | Windows Task Scheduler COM automation |
| `TextCopy` | 6.* | Cross-platform clipboard read/write |
| `System.ServiceProcess.ServiceController` | 10.* | Windows service control (`service` tool) |
| `System.Diagnostics.EventLog` | shared framework | Windows Event Log querying (no `PackageReference`; provided by the `net10.0-windows` framework) |
| `System.Drawing.Common` | 10.* | GDI+ image support (legacy compat) |
| `System.Management` | 10.* | WMI query execution (`ManagementObjectSearcher`) |
