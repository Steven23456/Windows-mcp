# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Overview

Windows-mcp is a **C# / .NET 10** Model Context Protocol (MCP) server for Windows desktop
automation and system inspection. It exposes 64 tools over MCP — **stdio** by default, or
**Streamable HTTP/HTTPS** with `--transport http` for clients on other machines (README: "Run
over HTTP/HTTPS") — covering UI automation, input, screen/OCR, windows, files, disk,
processes/shell, services, scheduled tasks, registry, network, web, system, and a
HiJackThis-style startup report.

## Architecture (4 layers)

```
MCP protocol  →  Tool classes  →  Service abstractions  →  Service implementations
(stdio/HTTP)     Tools/*.cs        WindowsMcp.Abstractions   Services/*.cs
```

- **`src/WindowsMcp/`** — the server exe. `Program.cs` parses `Hosting/ServerOptions` and starts
  the MCP server over stdio (default) or HTTP. `Hosting/WindowsMcpHost.cs` registers every
  service as a singleton and holds the MCP wiring both transports share (`AddWindowsMcp`) plus
  the Kestrel host factory (`BuildHttpApp`); `Hosting/CertificateLocator.cs` resolves
  `--cert-thumbprint`; `Hosting/EnvironmentRepair.cs` runs first in `Main` and repairs a
  host-stripped environment (`PATHEXT`, `ProgramData`, missing `Path`) before anything spawns a
  child. `Tools/*.cs` are the tool surface; `Services/*.cs` are the implementations.
- **`src/WindowsMcp.Abstractions/`** — `IXxxService` interfaces (one per file) and DTO records
  under `Models/`. Tools and services depend on these interfaces (testability).
- **`tests/WindowsMcp.Tests/`** — xUnit + Moq + FluentAssertions.
- Tools are **source-generated/discovered** via `.WithToolsFromAssembly()` — you do **not**
  register tool classes manually, only the services they inject.
- Detailed, current design docs live in `docs/architecture/` (ARCHITECTURE / OVERVIEW /
  COMPONENTS / DATAFLOW). The feature backlog against the upstream Python server is
  `docs/upstream-parity-checklist.md`; design notes for items taken from it go in `docs/design/`.

## Build / run / test

```powershell
dotnet build                      # debug build (first build ~3 min cold; incremental ~30s)
dotnet test                       # full suite
dotnet test --filter "Category!=UIAutomation"   # headless-safe subset

# Publish the single-file exe (end users need no .NET runtime) — ONE file, bundle/WindowsMcp.exe:
.\scripts\build-release.ps1
# = dotnet publish src/WindowsMcp -c Release -o bundle -r win-x64 --self-contained `
#     -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
#     -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
#   then deletes the stray libSkiaSharp.pdb. bundle/ is gitignored — never commit binaries.

# Serve over HTTP instead of stdio (remote clients; README "Run over HTTP/HTTPS"):
$env:WINDOWSMCP_API_KEY = "<16+ chars>"
bundle/WindowsMcp.exe --transport http --port 8765 [--bind <ip>] [--cert-thumbprint <sha1>]
bundle/WindowsMcp.exe --help
```

- **`[Trait("Category","UIAutomation")]` tests need an interactive desktop** with the target
  app (Notepad fixture) in the **foreground**; they fail under headless/background runs and on
  Win11's modern Notepad (documented in `NotepadFixture.cs`). Exclude them when running headless.
  `ClipboardServiceTests` is similarly environment-flaky (TextCopy `OpenClipboard` access-denied
  when another app holds the clipboard) — a lone clipboard failure under `Category!=UIAutomation`
  is environmental, not a regression.
- Other tests are unit (`Category=Unit`, mocked) or read-only integration (`Category=Integration`).
- **`PowerShellServiceTests` are real-process integration tests** — each `RunAsync` spawns a full
  `powershell.exe` cold-start, which under Defender scanning on a loaded box is ~15–75 s *each*, so
  the serialized-calls test can take many minutes. That slowness is environmental (excluding system
  PowerShell from Defender would be a bad security trade), not a regression. The service's backstop
  starts **after** the serialization gate is acquired so it bounds execution, not queue-wait time
  (a queued caller must not burn its runaway-script budget waiting) — do not move it back.

### Testing a change against the LIVE MCP server (Claude Code)

The server Claude Code talks to is whatever its MCP registration points at — **never** the build
output by itself. Before trusting any live result, verify which image is running:

```powershell
Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'" | Select ProcessId, ExecutablePath, CreationDate
```

**Redeploy a rebuilt exe:**
1. `.\scripts\build-release.ps1` → `bundle/WindowsMcp.exe`. Keep `IncludeNativeLibrariesForSelfExtract`:
   without it `libSkiaSharp.dll` is left loose beside the exe and the exe alone is not portable.
2. Make sure the registration points at that exe: `claude mcp add --transport stdio Windows-mcp
   -- <repo>\bundle\WindowsMcp.exe`, or the `command` in the relevant `.mcp.json` (README
   "Register with Claude Code"). For a remote host, copy the exe over and restart it there
   (README "Run over HTTP/HTTPS").
3. Reconnect (`/mcp`) or start a new session — a running `WindowsMcp.exe` keeps serving the old
   image until its process exits. Instances from earlier sessions can accumulate; if the
   `ExecutablePath`/`CreationDate` above is not the build you just made, kill them and reconnect.
4. Bump `<Version>` in `Directory.Build.props` **and** `.claude-plugin/plugin.json` together
   (`ServerInfoTests` fails when they drift) and record the change in `CHANGELOG.md`.

The repo-root `.mcp.json` is the **plugin** manifest: it launches
`${CLAUDE_PLUGIN_ROOT}/bundle/WindowsMcp.exe`, which only resolves when the repo is installed as
a Claude Code plugin **and** a locally built `bundle/WindowsMcp.exe` exists. `bundle/` is
gitignored by decision (2026-09-04): **no binaries in the repo**, so a fresh clone has no bundle
and the plugin entry shows as disconnected — expected, not a bug. Register `bundle/WindowsMcp.exe`
directly (README "Register with Claude Code"); plugin delivery from a clone still needs either a
build step before install or a changed manifest (see `todo.md`).

## Conventions (enforced)

`Directory.Build.props` sets `Nullable=enable`, `ImplicitUsings=enable`,
**`TreatWarningsAsErrors=true`**, `LangVersion=latest`, and `NoWarn=CA1416`. So:
- Warnings fail the build. Suppress an unavoidable obsolete/analyzer warning **narrowly** with
  `#pragma warning disable <ID>` + a comment naming the reason (see `AuthenticodeInspector.cs`
  for the `SYSLIB0057` precedent), or add to `NoWarn` only for a genuinely global case.
- DTOs are `record`s; services are `sealed`; tool methods are `async Task<string>` returning
  JSON (and/or text) and carry a `[McpServerTool, Description(...)]` attribute.

## Adding a tool

1. Add a `sealed [McpServerToolType]` class in `Tools/`, constructor-inject the service
   interfaces, add `[McpServerTool, Description("…")]` methods.
2. Put real logic behind an `IXxxService` in `Abstractions` + impl in `Services/` (keeps the
   tool thin and the logic unit-testable with Moq).
3. Register any **new** service singleton in `Hosting/WindowsMcpHost.AddWindowsMcp` (tools
   auto-register; both transports pick it up from there).
4. Update `docs/architecture/*` counts and `CHANGELOG.md` under `## [Unreleased]`.

## Key technical notes (still true in C#)

- **MCP stdio:** stdout is JSON-RPC, logs go to **stderr only** (in HTTP mode too — one logging
  config, `WindowsMcpHost.ConfigureStderrLogging`). `Program.RunStdioAsync` forces
  `Console.OutputEncoding/InputEncoding = UTF8` **before** host startup — without it, Windows'
  cp1252 default buffers responses and they never flush. Do not remove that. HTTP mode sets them
  best-effort only (the setters throw with no attached console, e.g. Task Scheduler).
- **HTTP transport** (`--transport http`, opt-in): `ServerOptions.Parse` is the *only* reader of
  the command line (no `IConfiguration` binding; the web builder is created with **no args**);
  Kestrel endpoints are explicit `Listen()` calls so `ASPNETCORE_URLS` cannot add a listener;
  the transport is stateless; the bearer gate is an `app.Use` **before** `MapMcp` so it covers
  every path. Binding off-loopback without an API key is a startup **refusal**, not a warning —
  keep it that way. `HttpTransportTests` drives the real host in-process on an ephemeral port.
- **COM vtable gaps:** when declaring COM interfaces, use `_VtblGap1_N()` to skip unused slots,
  or declare only the leading methods you call (an `InterfaceIsIUnknown` interface binds declared
  methods from vtable slot 3). Never stub later methods with guessed signatures — silent stack
  corruption. See `ShortcutResolver.cs` (IShellLink/IPersistFile) — currently the only COM
  interface declarations in `src/`.
- **Native interop** for the startup report: `AuthenticodeInspector` (WinVerifyTrust, catalog-
  aware), `LspEnumerator` (`WSCEnumProtocols`), `ShortcutResolver` (IShellLink). Catalog-aware
  trust matters — most Windows components are catalog-signed, not embedded-signed.
- **WinRT async in PowerShell** (PowerShellService): never call `GetResults()` directly; resolve
  via `AsTask().Wait(-1)`.

## Windows Defender / cold-start

If `/mcp` times out at 30s on a fresh machine, add a Defender exclusion for the repo / exe path
(heavy first-touch scanning of the self-contained exe). Re-apply after an AV reset.

## Tooling under `tools/`

Node-based dev utilities (`create-dependency-graph` supports C# via `--lang=csharp`,
`chunking-for-files`, `compress-for-context`). Dependabot tracks their JS deps.
