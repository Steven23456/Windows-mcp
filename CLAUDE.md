# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Overview

Windows-mcp is a **C# / .NET 10** Model Context Protocol (MCP) server for Windows desktop
automation and system inspection. It exposes 60 tools over MCP — **stdio** by default, or
**Streamable HTTP/HTTPS** with `--transport http` for clients on other machines (README: "Run
over HTTP/HTTPS") — covering UI automation, input, screen/OCR, windows, files, disk,
processes/shell, services, scheduled tasks, registry, network, web, system, and a
HiJackThis-style startup report.

> History: v0.x–0.8.5 were Python; **v0.2.0 (2026-05-26) is a full C# rewrite**. The Python
> tree is archived in `legacy/`. Do not edit `legacy/`. See `CHANGELOG.md` for versions.

## Architecture (4 layers)

```
MCP protocol  →  Tool classes  →  Service abstractions  →  Service implementations
(stdio/HTTP)     Tools/*.cs        WindowsMcp.Abstractions   Services/*.cs
```

- **`src/WindowsMcp/`** — the server exe. `Program.cs` parses `Hosting/ServerOptions` and starts
  the MCP server over stdio (default) or HTTP. `Hosting/WindowsMcpHost.cs` registers every
  service as a singleton and holds the MCP wiring both transports share (`AddWindowsMcp`) plus
  the Kestrel host factory (`BuildHttpApp`); `Hosting/CertificateLocator.cs` resolves
  `--cert-thumbprint`. `Tools/*.cs` are the tool surface; `Services/*.cs` are the implementations.
- **`src/WindowsMcp.Abstractions/`** — `IXxxService` interfaces (one per file) and DTO records
  under `Models/`. Tools and services depend on these interfaces (testability).
- **`tests/WindowsMcp.Tests/`** — xUnit + Moq + FluentAssertions.
- Tools are **source-generated/discovered** via `.WithToolsFromAssembly()` — you do **not**
  register tool classes manually, only the services they inject.
- Detailed, current design docs live in `docs/architecture/` (ARCHITECTURE / OVERVIEW /
  COMPONENTS / DATAFLOW). Feature specs live in `docs/superpowers/specs/`.

## Build / run / test

```powershell
dotnet build                      # debug build (first build ~3 min cold; incremental ~30s)
dotnet test                       # full suite
dotnet test --filter "Category!=UIAutomation"   # headless-safe subset

# Publish the single-file exe (end users need no .NET runtime):
dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
# → dist/WindowsMcp.exe

# Serve over HTTP instead of stdio (remote clients; README "Run over HTTP/HTTPS"):
$env:WINDOWSMCP_API_KEY = "<16+ chars>"
dist/WindowsMcp.exe --transport http --port 8765 [--bind <ip>] [--cert-thumbprint <sha1>]
dist/WindowsMcp.exe --help
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

**This plugin is url-sourced** (registered in `~/Github/skills/.claude-plugin/marketplace.json`
as `source: url → github.com/danielsimonjr/Windows-mcp`). Claude Code does **not** run
`dist/WindowsMcp.exe` from this repo, nor `~/.claude/local-marketplace/windows-mcp/` (a dead
pre-2026-07-02 location). It runs a **committed bundle from a per-version cache clone**:
`~/.claude/plugins/cache/local-marketplace/windows-mcp/<version>/bundle/WindowsMcp.exe`. So a
`dotnet publish -o dist` + `_RETRY` bump deploys **nothing** — verify the running image path
before believing a deploy landed (`Get-CimInstance Win32_Process -Filter "Name='WindowsMcp.exe'"
| Select ProcessId, ExecutablePath, CreationDate`).

**Correct redeploy of a rebuilt exe:**
1. `dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained
   -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`.
2. Copy `dist/WindowsMcp.exe` over the **committed** `bundle/WindowsMcp.exe` (it is git-tracked;
   a plain `git add bundle/WindowsMcp.exe` works — it is not gitignored).
3. Bump the version in **both** `.claude-plugin/plugin.json` **and** the `windows-mcp` entry in
   `~/Github/skills/.claude-plugin/marketplace.json` (an unchanged marketplace version
   short-circuits `/plugin marketplace update`, so the re-clone never happens).
4. Commit + push this repo (bundle + plugin.json + CHANGELOG) and `~/Github/skills`
   (marketplace.json).
5. User runs **`/plugin marketplace update local-marketplace`** (re-clones into a fresh
   `<version>/` cache dir with the new bundle) → **`/kill-plugins`** (clears the accumulated
   stale `WindowsMcp.exe` instances — one per prior session) → **`/reload-plugins`** (binds the
   new process). Confirm via the running process `ExecutablePath` pointing at the new `<version>`
   cache dir and `CreationDate` later than the update.

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
  corruption. See `ShortcutResolver.cs` (IShellLink/IPersistFile) and the audio service.
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
