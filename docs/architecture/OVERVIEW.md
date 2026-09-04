# Windows-MCP Overview

## Introduction

Windows-MCP is a lightweight, open-source Model Context Protocol (MCP) server that enables AI agents to interact directly with the Windows operating system. Built on .NET 10 and C#, it exposes 64 MCP tools covering UI automation, file operations, process management, system monitoring, persistence/startup reporting, and more — over the standard MCP stdio transport by default, or Streamable HTTP/HTTPS (`--transport http`) for clients on other machines.

## Purpose

The primary goal of Windows-MCP is to provide AI agents with the ability to:

- **Understand Desktop Context**: Capture UI element trees from running applications via the Windows Accessibility API
- **Interact with UI Elements**: Click, type, scroll, drag, and manipulate interface elements programmatically
- **Control Windows**: Focus, resize, minimize, and manage application windows
- **Execute System Commands**: Run PowerShell commands for advanced system operations
- **Capture Screens**: Take screenshots and perform OCR on screen regions
- **Manage System Resources**: Control processes, registry, services, scheduled tasks, and event logs

## Key Features

| Feature | Description |
|---------|-------------|
| **Native Windows Integration** | Direct access to Windows UI Automation API via `FlaUI.UIA3` |
| **Dependency Injection** | All 36 services are singleton-scoped, registered in `Hosting/WindowsMcpHost.AddWindowsMcp` via `Microsoft.Extensions.Hosting` |
| **Source-Generated Tool Discovery** | `[McpServerTool]` attributes are discovered at compile time by the MCP SDK source generator |
| **Interface-Driven Architecture** | Every service backed by an `IXxxService` interface in a separate Abstractions assembly |
| **DPI-Aware** | Per-Monitor DPI Awareness V2 enabled at startup for correct multi-monitor coordinate handling |
| **UTF-8 Stdio** | Output encoding forced to UTF-8 before host starts — prevents buffering bugs on Windows |
| **Dual Transport** | stdio (default) or Streamable HTTP/HTTPS on a TCP port, gated by a bearer API key — opt-in via `--transport http` |

## Platform Requirements

- **Operating System**: Windows 10 or 11 (some features require Windows 10 1703+)
- **.NET Runtime**: .NET 10 or higher
- **Architecture**: x64 (64-bit)

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AI Agent / LLM                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                  MCP Protocol (stdio | HTTP/HTTPS)
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│              Windows-MCP Server (Program.cs / Host)             │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │         ModelContextProtocol SDK (Stdio or Streamable-HTTP) ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │        MCP Tool Layer  (19 [McpServerToolType] classes)     ││
│  │   InputTools · UIAutomationTools · FileTools · ShellTools   ││
│  │   SystemTools · WindowTools · ProcessTools · ScreenTools    ││
│  │   NetworkTools · RegistryTools · WebTools · DiskTools       ││
│  │   StorageTools · StartupTools · SecurityTools · JobTools    ││
│  │   IntegrityTools · UsnTools · WatchTools                    ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Abstraction Layer  (WindowsMcp.Abstractions)      ││
│  │        36 IXxxService interfaces + Model DTOs               ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Implementation Layer  (WindowsMcp.Services)       ││
│  │        36 XxxService singletons registered via DI           ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Windows Operating System                      │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────────┐ │
│  │  FlaUI.UIA3    │  │H.InputSimulator│  │  CsWin32 / WinAPI  │ │
│  │ (UI Automation)│  │(keyboard/mouse)│  │  (DPI, WMI, etc.)  │ │
│  └────────────────┘  └────────────────┘  └────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Available Tools

Windows-MCP exposes **64 MCP tools** across 19 tool classes:

### Input Tools (`InputTools` — 8 tools)
| Tool | Purpose |
|------|---------|
| `Click` | Click at screen coordinates (left/right/middle, single/double/triple) |
| `Drag` | Drag from one point to another |
| `Hover` | Hover cursor at coordinates with optional duration |
| `Type` | Type a string into the focused input |
| `Key` | Press a single key by name (Enter, Tab, F1-F12, arrows, etc.) |
| `Shortcut` | Press a keyboard shortcut (e.g., `ctrl+c`, `alt+tab`) |
| `Scroll` | Scroll the mouse wheel (up/down/left/right) |
| `Clipboard` | Get or set clipboard text |

### UI Automation Tools (`UIAutomationTools` — 8 tools)
| Tool | Purpose |
|------|---------|
| `GetState` | Capture the UI element tree of the foreground window (three levels deep) |
| `FindElement` | Find elements whose name/value contains text (kind: any / interactive / text / scrollable) |
| `GetElement` | Get properties of a specific UI element by id |
| `InteractElement` | Toggle, select, or invoke a UI element by id |
| `GetText` | Extract text content from a UI element |
| `GetTable` | Extract tabular data from a grid/table element |
| `AssertElement` | Assert element state (exists / enabled / checked / value / visible / focused) with PASS/FAIL result |
| `WaitFor` | Poll until an element whose name/value contains text appears |

### Window Tools (`WindowTools` — 5 tools)
| Tool | Purpose |
|------|---------|
| `Window` | Minimize, maximize, restore, or close a window by exact title |
| `SwitchToWindow` | Bring a window to the foreground by exact title |
| `Focus` | Alias of `SwitchToWindow` |
| `Launch` | Launch an application by name or path (ShellExecute); returns the PID |
| `MultiMonitor` | Enumerate monitors with geometry and primary flag |

### File Tools (`FileTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `FileRead` | Read a file as text (`max_bytes`, `encoding`) |
| `FileWrite` | Write text to a file (`confirm:true`) |
| `FileManage` | Copy, move, delete (`confirm:true`), or list |
| `FileInfo` | Get file/directory metadata |
| `FileSearch` | Search for files by pattern |
| `FileHash` | Compute SHA256/SHA1/MD5 hex digest |
| `FileStreams` | NTFS alternate data streams + reparse target |
| `FileDialog` | Interact with open/save dialogs |
| `Archive` | Zip a directory or unzip an archive |

### System Tools (`SystemTools` — 9 tools)
| Tool | Purpose |
|------|---------|
| `SystemInfo` | WMI system info by category (os/memory/disk/gpu/battery) |
| `Audio` | Get/set volume or mute/unmute |
| `Notification` | Show a Windows toast notification |
| `SecurityAudit` | Firewall/Defender/UAC/BitLocker posture snapshot |
| `Reliability` | Crash minidumps + recent reliability failure records |
| `DriverList` | Installed PnP drivers with version/date/signer/signed-state (BYOVD surface) |
| `WmiQuery` | Execute WMI queries for system data |
| `Env` | Get, set, or list environment variables (secret-name redaction) |
| `PowerAction` | Shutdown, reboot, logoff, lock, sleep, hibernate |

### Security Tools (`SecurityTools` — 3 tools)
| Tool | Purpose |
|------|---------|
| `VerifySignature` | Catalog-aware Authenticode trust verdict for a file |
| `DefenderStatus` | Microsoft Defender posture (real-time/tamper protection, signature age, scans) |
| `CertStore` | Enumerate a cert store; flags self-signed (rogue-root) and expired certs |

### Screen Tools (`ScreenTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Screenshot` | Capture a screenshot (full screen or region) |
| `Ocr` | Extract text from a screen region via OCR |

### Process Tools (`ProcessTools` — 6 tools)
| Tool | Purpose |
|------|---------|
| `Process` | List/inspect/kill processes: plain list, recycle-aware lineage + orphan detection (`orphans`), root-grouping, name/cmdline filtering, and recycle-safe kill by PID/name or whole tree |
| `ProcessInspect` | Deep per-process detail: parent PID, command line, start time, loaded modules |
| `StartProcess` | Start a detached process; returns the PID |
| `Service` | List/status/start/stop/restart Windows services |
| `ScheduledTask` | List/get/run/create/delete scheduled tasks |
| `EventLog` | Query the Windows Event Log |

### Shell Tool (`ShellTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Powershell` | Execute a PowerShell command; returns stdout, stderr, exit code. Emits MCP progress heartbeats on long foreground calls; `background: true` starts a job (see `JobTools`) instead of waiting |

### Job Tool (`JobTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Job` | Manage background PowerShell jobs (`status`/`output`/`cancel`/`list`): jobs run concurrently outside the foreground PowerShell gate, with bounded output capture and a per-job backstop |

### Registry Tools (`RegistryTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `RegistryGet` | Read a named value, or list value names when no name is given |
| `RegistrySet` | Write a value (String / DWord / QWord / Binary / MultiString / ExpandString); `confirm:true` |

### Network Tools (`NetworkTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Network` | Adapters, listening ports (with owning process), Wi-Fi, DNS lookup, ping |
| `Firewall` | List, add, or remove firewall rules (`confirm:true` for add/remove) |

### Web Tools (`WebTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Scrape` | Fetch a URL and convert HTML to Markdown (private address ranges rejected) |
| `HttpRequest` | HTTP request (GET/POST/PUT/DELETE/PATCH) with optional headers and body (private address ranges rejected) |

### Disk Tool (`DiskTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `DiskInspect` | List drives with capacity, free space, file system |

### Storage Tool (`StorageTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StorageHealth` | Diagnose disk/drive health: physical disks (model, bus/media type, SMART health + reliability counters), per-disk online/offline, volume→disk/partition map, and recent disk-stack error/warning events. Metadata-first + hang-safe; free space only when `include_usage:true` (time-boxed). |

### Startup Tools (`StartupTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `StartupReport` | HiJackThis-style boot/persistence report. Sections: Run/RunOnce (all hives incl. per-user SIDs, with enabled state), Startup folders, scheduled tasks, auto-start services, hosts, DNS, Winsock LSP, shell extensions, Control Panel applets (registry + `System32`/`SysWOW64` `*.cpl`), accessibility ATs, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, browser proxy, trusted-zone sites. Every file-backed entry has a catalog-aware code-signing trust flag. `format=summary` (default — counts + only flagged entries, inline) \| `json` \| `text` \| `both`; `includeProcesses` opt-in |

### Integrity Tool (`IntegrityTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Integrity` | File-integrity tripwire over a curated watch-list (hosts file, Startup folders, `~/.claude/settings.json`, `~/.gitconfig`, `C:\` governance files): `baseline` (SHA-256 snapshot to `%LOCALAPPDATA%\windows-mcp\integrity`), `check` (added / removed / modified vs. baseline), `list`; extra `paths` can be added |

### USN Tool (`UsnTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `FsChanges` | NTFS USN change journal: `status` (journal id + USN range) and `since` (change records from a USN forward). Requires elevation |

### Watch Tool (`WatchTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Watch` | Live directory watching via `FileSystemWatcher`: `start` (returns a session id), `poll` (drain buffered events), `stop`, `list`; events buffer in a bounded ring between polls |

## Core NuGet Dependencies

| Package | Purpose | Replaces (Python) |
|---------|---------|------------------|
| `ModelContextProtocol` + `.AspNetCore` | MCP server SDK — stdio and Streamable HTTP transports | `fastmcp` |
| `FlaUI.UIA3` | Windows UI Automation API | `uiautomation` |
| `H.InputSimulator` | Keyboard and mouse simulation | `pyautogui` + `humancursor` |
| `SkiaSharp` | Image capture and processing | `Pillow` |
| `CsWin32` | P/Invoke code generation for Win32 APIs | `ctypes` |
| `Microsoft.Extensions.Hosting` | DI container and application host | N/A |
| `ReverseMarkdown` | HTML → Markdown conversion | `markdownify` |
| `TaskScheduler` | Windows Task Scheduler COM wrapper | N/A |
| `TextCopy` | Clipboard access | `pyperclip` |

## Use Cases

1. **Desktop Automation**: Automate repetitive Windows tasks via AI
2. **UI Testing**: AI-driven UI verification and regression testing
3. **Accessibility Analysis**: Extract UI element trees for accessibility auditing
4. **AI Agent Development**: Enable LLM agents to fully control Windows applications
5. **RPA (Robotic Process Automation)**: Business process automation through AI
6. **System Administration**: AI-assisted process, service, and registry management
