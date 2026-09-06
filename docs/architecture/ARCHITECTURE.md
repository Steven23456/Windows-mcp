# Windows-MCP System Architecture

## Architectural Overview

Windows-MCP follows a four-layer architecture built on .NET 10 with dependency injection throughout. The system is organized into: MCP Protocol, Tool, Service Abstraction, and Service Implementation layers — each with clearly defined responsibilities and interfaces.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        MCP Protocol Layer                                    │
│                    (ModelContextProtocol SDK)                                │
│  Stdio / Streamable HTTP ◄──► JSON-RPC ──► WithToolsFromAssembly() discovery │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                           Tool Layer                                         │
│                 (19 [McpServerToolType] classes, 66 tools)                   │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐  │
│  │InputTools  │ │UIAutoTools │ │ FileTools  │ │SystemTools │ │WindowTools │  │
│  │  9 tools   │ │  9 tools   │ │  9 tools   │ │  9 tools   │ │  5 tools   │  │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘  │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐  │
│  │ProcessTools│ │ScreenTools │ │  WebTools  │ │RegistryTls │ │NetworkTls  │  │
│  │  6 tools   │ │  2 tools   │ │  2 tools   │ │  2 tools   │ │  2 tools   │  │
│  └────────────┘ └────────────┘ └────────────┘ └────────────┘ └────────────┘  │
│  ShellTools(1) · JobTools(1) · DiskTools(1) · StorageTools(1)                │
│  SecurityTools(3) · StartupTools(1) · IntegrityTools(1)                      │
│  UsnTools(1) · WatchTools(1)                                                 │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ constructor injection
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                      Service Abstraction Layer                               │
│                    (WindowsMcp.Abstractions assembly)                        │
│  IInputService · IScreenshotService · IOcrService · IClipboardService        │
│  IAudioService · IPowerShellService · IUIAutomationService · IFileSystemSvc  │
│  IRegistryService · IServiceControlService · IEventLogService                │
│  ITaskSchedulerService · IProcessService · IWindowService · IWmiService      │
│  IEnvService · IPowerService · INotificationService · INetworkService        │
│  IWebService · IVirtualDesktopService · IFlashOverlay  (38 interfaces total) │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │ implemented by
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    Service Implementation Layer                              │
│                     (WindowsMcp.Services namespace)                          │
│  InputService · ScreenshotService · OcrService · ClipboardService            │
│  AudioService · PowerShellService · UIAutomationService · FileSystemService  │
│  RegistryService · ServiceControlService · EventLogService                   │
│  TaskSchedulerService · ProcessService · WindowService · WmiService          │
│  EnvService · PowerService · NotificationService · NetworkService            │
│  WebService · VirtualDesktopService · FlashOverlay                           │
│               (38 singletons — registered in Hosting/WindowsMcpHost)         │
└──────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Windows Platform Layer                                │
│  ┌──────────────┐ ┌───────────────────┐ ┌──────────────────────────────────┐ │
│  │  FlaUI.UIA3  │ │ H.InputSimulator  │ │ CsWin32 / Win32 APIs             │ │
│  │ (UI Automat.)│ │  (keyboard/mouse) │ │ DPI, WinRT, WMI, COM, P/Invoke   │ │
│  └──────────────┘ └───────────────────┘ └──────────────────────────────────┘ │
│  ┌──────────────┐ ┌───────────────────┐ ┌──────────────────────────────────┐ │
│  │   SkiaSharp  │ │  TaskScheduler    │ │ System.Management / EventLog     │ │
│  │  (images)    │ │     (COM)         │ │ (WMI, Windows event logs)        │ │
│  └──────────────┘ └───────────────────┘ └──────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Layer Descriptions

### 1. MCP Protocol Layer

The MCP SDK (`ModelContextProtocol.Server`) handles all protocol concerns:

- **Transport** — selected by `Hosting/ServerOptions` from the command line (`WINDOWSMCP_*` env fallbacks):
  - **stdio** (default, no arguments): `WithStdioServerTransport()` — JSON-RPC on stdin/stdout, for hosts that spawn the exe.
  - **Streamable HTTP** (`--transport http`): `WithHttpTransport(o => o.Stateless = true)` + `MapMcp("/mcp")` on Kestrel, built by `Hosting/WindowsMcpHost.BuildHttpApp`. `--port`/`--bind` choose the endpoint, `--cert-thumbprint` (resolved by `Hosting/CertificateLocator`) makes it HTTPS-only, and `--api-key` installs a constant-time bearer gate ahead of every route. Stateless because no tool issues server→client requests, and a restart then stays invisible to clients.
- **Tool Discovery**: `WithToolsFromAssembly()` — discovers all `[McpServerTool]` methods, registering them with their parameter schemas automatically
- **Server Info**: `ServerInfo = new() { Name = "Windows-mcp", Version = Program.ServerVersion }` — the version comes from `<Version>` in `Directory.Build.props`
- **Shared wiring**: `WindowsMcpHost.AddWindowsMcp(options)` holds the service registrations, server identity, caller-facing error filter and tool discovery, so both transports are configured identically; only the transport call differs.

**Critical startup requirements** (handled in `Program.cs` before host build):
```csharp
// First thing in Main: fill in a host-stripped environment (PATHEXT, ProgramData, ...) so every
// child process we spawn inherits a usable one. Host-set values are never overwritten.
EnvironmentRepair.Apply();

// stdio mode only: prevent JSON-RPC response buffering on Windows (cp1252 default encoding)
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// Both modes: Per-Monitor DPI Awareness V2 — physical pixel coordinates on multi-monitor
PInvoke.SetProcessDpiAwarenessContext(new DPI_AWARENESS_CONTEXT((nint)(-4)));
```

---

### 2. Tool Layer

Tool classes are `[McpServerToolType]`-annotated sealed classes that group related MCP tools. They receive services via constructor injection — they contain no business logic themselves, only parameter validation and delegation.

**Pattern:**
```csharp
[McpServerToolType]
public sealed class InputTools
{
    private readonly IInputService _input;
    private readonly IClipboardService _clipboard;

    public InputTools(IInputService input, IClipboardService clipboard)
    {
        _input = input;
        _clipboard = clipboard;
    }

    [McpServerTool, Description("Click at screen coordinates. Coordinates are physical pixels on the virtual desktop: origin = top-left of the primary monitor, so monitors left of / above it have negative values (see multi_monitor for each monitor's bounds).")]
    public async Task<string> Click(int x, int y, string button = "left", int clicks = 1)
        => JsonSerializer.Serialize(await _input.ClickAsync(x, y, ParseButton(button), clicks));
}
```

**Tool class inventory:**

| Tool Class | Tools | Services Injected |
|------------|-------|------------------|
| `InputTools` | 9 | `IInputService`, `IClipboardService` |
| `UIAutomationTools` | 9 | `IUIAutomationService` |
| `FileTools` | 9 | `IFileSystemService`, `IInputService`, `IFileStreamService` |
| `SystemTools` | 9 | `IWmiService`, `IEnvService`, `IPowerService`, `INotificationService`, `IAudioService`, `ISecurityService`, `IReliabilityService`, `IDriverService` |
| `WindowTools` | 5 | `IWindowService`, `IVirtualDesktopService` |
| `ProcessTools` | 6 | `IProcessService`, `IServiceControlService`, `ITaskSchedulerService`, `IEventLogService` |
| `ScreenTools` | 2 | `IScreenshotService`, `IOcrService`, `IWindowService`, `IInputService`, `IUIAutomationService`, `IFlashOverlay` (+ the `ScreenshotOptions` record) |
| `WebTools` | 2 | `IWebService` |
| `RegistryTools` | 2 | `IRegistryService` |
| `NetworkTools` | 2 | `INetworkService`, `IFirewallService` |
| `ShellTools` | 1 | `IPowerShellService`, `IJobService` |
| `JobTools` | 1 | `IJobService` |
| `DiskTools` | 1 | `IDiskService` |
| `StorageTools` | 1 | `IStorageService` |
| `SecurityTools` | 3 | `IAuthenticodeInspector`, `ISecurityService`, `ICertStoreService` |
| `StartupTools` | 1 | `IStartupReportService` |
| `IntegrityTools` | 1 | `IIntegrityService` |
| `UsnTools` | 1 | `IUsnService` |
| `WatchTools` | 1 | `IWatchService` |

---

### 3. Service Abstraction Layer (`WindowsMcp.Abstractions`)

A separate assembly (`WindowsMcp.Abstractions.csproj`) containing:
- **38 `IXxxService` interfaces** — define the contract for each domain
- **Model DTOs** in `WindowsMcp.Abstractions.Models` — records/classes shared between tools and services

The abstraction layer exists so tool classes compile against interfaces, not concrete types. This enforces the dependency inversion principle and makes services independently testable.

**Example interface:**
```csharp
public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button, int clicks);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button);
    Task HoverAsync(int x, int y, int durationMs);
    Task<TypeResult> TypeAsync(string text);
    Task PressKeyAsync(string key);
    Task PressShortcutAsync(string shortcut);
    Task ScrollAsync(int x, int y, string direction, int amount);
    Task<CursorPosition> GetCursorPositionAsync();
}
```

---

### 4. Service Implementation Layer

All 38 services are registered as **singletons** in `Hosting/WindowsMcpHost.AddWindowsMcp(ServerOptions)`, which both transports call; the parsed options enter the container alongside them as two options records — `ScreenshotOptions` (read by the screen tools) and `UiTreeOptions` (injected into `UIAutomationService`):

```csharp
// --screenshot-scale, --flash, --profile-snapshot, --screenshot-backend
services.AddSingleton(new ScreenshotOptions(options.ScreenshotScale, options.Flash,
    options.ProfileSnapshot, options.ScreenshotBackend));
// --max-tree-elements, --profile-snapshot
services.AddSingleton(new UiTreeOptions(options.MaxTreeElements, options.ProfileSnapshot));
services.AddSingleton<IFlashOverlay, FlashOverlay>();   // always registered; the tool gates on ScreenshotOptions.Flash
services.AddSingleton<IInputService, InputService>();
services.AddSingleton<IScreenshotService, ScreenshotService>();
// ... (38 registrations)
```

Services contain all business logic and directly call Windows APIs through platform packages. They are constructed once at host startup and shared across all tool invocations.

---

### 5. Windows Platform Layer

| Package | Windows API | What It Does |
|---------|-------------|-------------|
| `FlaUI.UIA3` | UI Automation COM | Walk the accessibility tree, find/inspect/interact with elements |
| `H.InputSimulator` | `SendInput` Win32 | Inject keyboard and mouse events at driver level |
| `SkiaSharp` | GDI+/DirectX | Hold either capture backend's frame, downscale (Mitchell cubic), annotate, encode PNG/JPEG |
| `Windows.Graphics.Capture` (no package — the `net10.0-windows10.0.19041` WinRT projection) | WGC + D3D11 | The `wgc` screenshot backend: compositor frames for the GPU-accelerated and DRM surfaces GDI returns black for |
| `CsWin32` | P/Invoke gen | Auto-generates interop for `SetProcessDpiAwareness`, `SetCurrentProcessExplicitAppUserModelID`, etc. |
| `TaskScheduler` | Task Scheduler COM | Create, read, update, delete scheduled tasks |
| `System.Management` | WMI | Query hardware, driver, and configuration data |
| `System.Diagnostics.EventLog` | Event Log API | Read Windows event log entries |
| `TextCopy` | Clipboard API | Cross-platform clipboard read/write |

---

## Design Patterns

### 1. Dependency Injection via `Microsoft.Extensions.Hosting`

All services follow the DI pattern — no static state, no singletons instantiated outside the container:

```csharp
// Hosting/WindowsMcpHost.cs — shared by both transports
public static IMcpServerBuilder AddWindowsMcp(this IHostApplicationBuilder builder, ServerOptions options)
{
    builder.Services.AddSingleton(new ScreenshotOptions(options.ScreenshotScale, options.Flash,
        options.ProfileSnapshot, options.ScreenshotBackend));
    builder.Services.AddSingleton<IInputService, InputService>();
    // ...
    return builder.Services.AddMcpServer(...).WithRequestFilters(...).WithToolsFromAssembly(...);
}

// Program.cs — stdio
var builder = Host.CreateApplicationBuilder(args);
builder.AddWindowsMcp(options).WithStdioServerTransport();
await builder.Build().RunAsync();

// Program.cs — HTTP (WindowsMcpHost.BuildHttpApp)
var web = WebApplication.CreateBuilder();
web.AddWindowsMcp(options).WithHttpTransport(o => o.Stateless = true);
app.MapMcp("/mcp");
```

### 2. Interface Segregation

Each service interface covers exactly one domain. Tool classes declare only the interfaces they actually use:

```csharp
// InputTools only needs input + clipboard — not screenshot, not filesystem
public InputTools(IInputService input, IClipboardService clipboard)
```

### 3. Source-Generated Tool Discovery

`WithToolsFromAssembly()` uses a Roslyn source generator that runs at compile time. It emits a registration method that lists all `[McpServerTool]` methods with their `[Description]`-derived JSON schemas. There is no runtime reflection and no decorator registration step.

### 4. Record-Based DTOs

Model types in `WindowsMcp.Abstractions.Models` use C# records for immutability:

```csharp
public record AudioState(int Level, bool Muted);
public record ClickResult(int X, int Y, string Button, int Clicks);
```

### 5. Async-First API Surface

Every service method is `async Task<T>` or `async Task`. No blocking calls on tool dispatch threads.

---

## Project Structure

```
Windows-mcp.slnx
├── src/
│   ├── WindowsMcp/                        ← Main project
│   │   ├── WindowsMcp.csproj              (targets net10.0-windows10.0.19041)
│   │   ├── Program.cs                     (entry: env repair, AUMID + DPI setup, parse options, pick transport)
│   │   ├── Hosting/                       (ServerOptions, WindowsMcpHost, CertificateLocator, EnvironmentRepair)
│   │   ├── Tools/                         (19 tool classes)
│   │   │   ├── InputTools.cs
│   │   │   ├── UIAutomationTools.cs
│   │   │   ├── FileTools.cs
│   │   │   ├── SystemTools.cs
│   │   │   ├── SecurityTools.cs
│   │   │   ├── WindowTools.cs
│   │   │   ├── ProcessTools.cs
│   │   │   ├── ScreenTools.cs
│   │   │   ├── ShellTools.cs
│   │   │   ├── JobTools.cs
│   │   │   ├── RegistryTools.cs
│   │   │   ├── NetworkTools.cs
│   │   │   ├── WebTools.cs
│   │   │   ├── DiskTools.cs
│   │   │   ├── StorageTools.cs
│   │   │   ├── StartupTools.cs
│   │   │   ├── IntegrityTools.cs
│   │   │   ├── UsnTools.cs
│   │   │   └── WatchTools.cs
│   │   ├── Services/                      (38 service implementations + helpers)
│   │   │   ├── InputService.cs
│   │   │   ├── UIAutomationService.cs
│   │   │   ├── ScreenshotService.cs       (+ WgcCaptureBackend, Annotator, FlashOverlay, FlashGlow)
│   │   │   ├── UiTree/                    (snapshot core: UiNode, UiClassifier, ElementBudget,
│   │   │   │                               UiTraverser, SnapshotRenderer, DomCorrection)
│   │   │   ├── WindowService.cs           (+ FuzzyMatch, WindowMatcher, ForegroundLadder and
│   │   │   │                               Win32ForegroundNative — B-10's matcher and ladder)
│   │   │   ├── ProcessService.cs          (+ ArgvJson — B-11's args_json parser)
│   │   │   └── ...
│   │   └── Startup/                       (startup-report renderer + approval decoding)
│   └── WindowsMcp.Abstractions/           ← Contracts assembly
│       ├── WindowsMcp.Abstractions.csproj
│       ├── IInputService.cs
│       ├── IUIAutomationService.cs
│       ├── ... (38 interfaces)
│       └── Models/                        (21 DTO files)
│           ├── InputDtos.cs
│           └── ...
├── tests/WindowsMcp.Tests/                (xUnit + Moq + FluentAssertions)
└── docs/
    ├── architecture/
    └── upstream-parity-checklist.md
```

---

## Entry Point

```
dotnet run --project src/WindowsMcp
```

The `Program.cs` static `Main` returns `Task<int>`. It parses `ServerOptions` first (exit code 2 with usage on a bad option; `--help` prints usage). In **stdio** mode the host runs until the MCP client closes the stdin pipe (EOF), at which point `RunAsync()` returns and the process exits with code 0. In **HTTP** mode (`--transport http`) Kestrel runs until Ctrl+C / SIGTERM; startup is refused (exit 2) when binding off-loopback without an API key, or when the `--cert-thumbprint` certificate cannot be found or its private key opened.

---

## Security Considerations

1. **Transport exposure** — by default (stdio) no network port is opened; only the MCP client process can communicate. `--transport http` deliberately opens one, and every tool is reachable through it, so: the server refuses to start on a non-loopback bind without `--api-key`/`WINDOWSMCP_API_KEY` (constant-time bearer check applied to every path, 401 otherwise); `--cert-thumbprint` makes the port HTTPS-only; plain HTTP off-loopback is allowed but warned about at startup. Kestrel endpoints are configured explicitly, so `ASPNETCORE_URLS` cannot add an unauthenticated listener.
2. **PowerShell execution guards** — there is no command blocklist. `PowerShellService` serializes foreground calls through a gate, kills the process tree at a 15-minute execution backstop, redirects and closes stdin, and passes scripts whole via `-EncodedCommand`. Destructive *tools* are gated by `confirm:true` (README "Safety rails"); `scrape`/`http_request` reject private address ranges.
3. **DPI-aware coordinates** — `SetProcessDpiAwarenessContext` ensures coordinates are in physical pixels, preventing misclicks on HiDPI displays
4. **Concurrency** — services are singletons shared across concurrent tool calls; the ones that hold state (`UIAutomationService` STA queue, `PowerShellService` gate, `JobService`/`WatchService` registries) synchronize internally
