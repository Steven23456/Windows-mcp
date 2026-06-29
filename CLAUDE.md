# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Overview

Windows-mcp is a **C# / .NET 9** Model Context Protocol (MCP) server for Windows desktop
automation and system inspection. It exposes 60 tools over the MCP **stdio** transport
(UI automation, input, screen/OCR, windows, files, disk, processes/shell, services,
scheduled tasks, registry, network, web, system, and a HiJackThis-style startup report).

> History: v0.x–0.8.5 were Python; **v0.2.0 (2026-05-26) is a full C# rewrite**. The Python
> tree is archived in `legacy/`. Do not edit `legacy/`. See `CHANGELOG.md` for versions.

## Architecture (4 layers)

```
MCP protocol  →  Tool classes  →  Service abstractions  →  Service implementations
(stdio SDK)      Tools/*.cs        WindowsMcp.Abstractions   Services/*.cs
```

- **`src/WindowsMcp/`** — the server exe. `Program.cs` configures the Generic Host, registers
  every service as a singleton, and starts the MCP server. `Tools/*.cs` are the tool surface;
  `Services/*.cs` are the implementations.
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
```

- **`[Trait("Category","UIAutomation")]` tests need an interactive desktop** with the target
  app (Notepad fixture) in the **foreground**; they fail under headless/background runs and on
  Win11's modern Notepad (documented in `NotepadFixture.cs`). Exclude them when running headless.
  `ClipboardServiceTests` is similarly environment-flaky (TextCopy `OpenClipboard` access-denied
  when another app holds the clipboard) — a lone clipboard failure under `Category!=UIAutomation`
  is environmental, not a regression.
- Other tests are unit (`Category=Unit`, mocked) or read-only integration (`Category=Integration`).

### Testing a change against the LIVE MCP server (Claude Code)

The plugin runs `dist/WindowsMcp.exe`. Two gotchas when re-deploying a rebuilt exe:
1. **The running server locks `dist/WindowsMcp.exe`**, and Claude Code auto-restarts a killed
   server — so you can't just overwrite it. Windows allows **renaming a running image**: rename
   `WindowsMcp.exe` aside (e.g. `WindowsMcp.old1.exe`), then `dotnet publish -o dist`.
2. **`/reload-plugins` does NOT restart a server whose `.mcp.json` is unchanged** — it keeps the
   existing process (serving the OLD exe). To force a fresh process, bump the `_RETRY` env value
   in `~/.claude/local-marketplace/windows-mcp/.mcp.json`, then `/reload-plugins`. Confirm via the
   server process `StartTime` being later than the publish time.
- Orphaned `WindowsMcp.exe` instances + `dist/WindowsMcp.old*.exe` accumulate across reloads;
  `/kill-plugins` then `/reload-plugins` clears them.

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
3. Register any **new** service singleton in `Program.cs` (tools auto-register).
4. Update `docs/architecture/*` counts and `CHANGELOG.md` under `## [Unreleased]`.

## Key technical notes (still true in C#)

- **MCP stdio:** stdout is JSON-RPC, logs go to **stderr only**. `Program.cs` forces
  `Console.OutputEncoding/InputEncoding = UTF8` **before** host startup — without it, Windows'
  cp1252 default buffers responses and they never flush. Do not remove that.
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
