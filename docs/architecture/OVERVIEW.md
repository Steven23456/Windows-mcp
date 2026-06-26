# Windows-MCP Overview

## Introduction

Windows-MCP is a lightweight, open-source Model Context Protocol (MCP) server that enables AI agents to interact directly with the Windows operating system. Built on .NET 9 and C#, it exposes 52 MCP tools covering UI automation, file operations, process management, system monitoring, persistence/startup reporting, and more — all via the standard MCP stdio transport.

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
| **Dependency Injection** | All 24 services are singleton-scoped, wired via `Microsoft.Extensions.Hosting` |
| **Source-Generated Tool Discovery** | `[McpServerTool]` attributes are discovered at compile time by the MCP SDK source generator |
| **Interface-Driven Architecture** | Every service backed by an `IXxxService` interface in a separate Abstractions assembly |
| **DPI-Aware** | Per-Monitor DPI Awareness V2 enabled at startup for correct multi-monitor coordinate handling |
| **UTF-8 Stdio** | Output encoding forced to UTF-8 before host starts — prevents buffering bugs on Windows |

## Platform Requirements

- **Operating System**: Windows 10 or 11 (some features require Windows 10 1703+)
- **.NET Runtime**: .NET 9 or higher
- **Architecture**: x64 (64-bit)

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        AI Agent / LLM                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                         MCP Protocol (stdio)
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│              Windows-MCP Server (Program.cs / Host)             │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │         ModelContextProtocol SDK (WithStdioServerTransport) ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │        MCP Tool Layer  (12 [McpServerToolType] classes)     ││
│  │   InputTools · UIAutomationTools · FileTools · ShellTools   ││
│  │   SystemTools · WindowTools · ProcessTools · ScreenTools    ││
│  │   NetworkTools · RegistryTools · WebTools · DiskTools       ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Abstraction Layer  (WindowsMcp.Abstractions)      ││
│  │        28 IXxxService interfaces + Model DTOs               ││
│  └─────────────────────────────────────────────────────────────┘│
│  ┌─────────────────────────────────────────────────────────────┐│
│  │   Service Implementation Layer  (WindowsMcp.Services)       ││
│  │        28 XxxService singletons registered via DI           ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Windows Operating System                      │
│  ┌────────────────┐  ┌───────────────┐  ┌─────────────────────┐ │
│  │  FlaUI.UIA3    │  │H.InputSimulator│  │  CsWin32 / WinAPI   │ │
│  │ (UI Automation)│  │(keyboard/mouse)│  │   (DPI, WMI, etc.)  │ │
│  └────────────────┘  └───────────────┘  └─────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Available Tools

Windows-MCP exposes **52 MCP tools** across 14 tool classes:

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
| `GetState` | Capture full UI element tree of the foreground window |
| `FindElement` | Find a UI element by name, control type, or automation ID |
| `GetElement` | Get properties of a specific UI element |
| `InteractElement` | Invoke, toggle, select, or expand a UI element |
| `GetText` | Extract text content from a UI element |
| `GetTable` | Extract tabular data from a grid/table element |
| `AssertElement` | Assert element state with PASS/FAIL result |
| `WaitFor` | Wait until a condition on a UI element is met |

### Window Tools (`WindowTools` — 5 tools)
| Tool | Purpose |
|------|---------|
| `SwitchToWindow` | Focus a window by title pattern |
| `Window` | Get window info (position, size, state) |
| `MultiMonitor` | Get all monitor layouts and resolutions |
| `Launch` | Launch an application by name |
| `StartProcess` | Start a detached process that survives independently |

### File Tools (`FileTools` — 7 tools)
| Tool | Purpose |
|------|---------|
| `FileRead` | Read file contents |
| `FileWrite` | Write or append file contents |
| `FileManage` | Copy, move, delete, or create files/directories |
| `FileInfo` | Get file/directory metadata |
| `FileSearch` | Search for files by pattern |
| `FileDialog` | Interact with open/save dialogs |
| `Archive` | Create, extract, or inspect zip/tar archives |

### System Tools (`SystemTools` — 7 tools)
| Tool | Purpose |
|------|---------|
| `SystemInfo` | Get CPU, RAM, OS version, hostname |
| `Service` | List, start, stop, or restart Windows services |
| `ScheduledTask` | Manage Windows Task Scheduler tasks |
| `EventLog` | Query Windows Event Log entries |
| `WmiQuery` | Execute WMI queries for system data |
| `Env` | Get or set environment variables |
| `PowerAction` | Sleep, hibernate, lock, or sign out |

### Screen Tools (`ScreenTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Screenshot` | Capture a screenshot (full screen or region) |
| `Ocr` | Extract text from a screen region via OCR |

### Process Tools (`ProcessTools` — 5 tools)
| Tool | Purpose |
|------|---------|
| `Process` | List, start, or kill processes |
| `GetProcess` | Get details for a specific process |
| `NetworkConnections` | List active network connections per process |
| `SecurityAudit` | Audit running process security posture |
| `FirewallRules` | List or manage Windows Firewall rules |

### Shell Tool (`ShellTools` — 1 tool)
| Tool | Purpose |
|------|---------|
| `Powershell` | Execute a PowerShell command; returns stdout, stderr, exit code |

### Registry Tools (`RegistryTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `RegistryGet` | Read a registry key or value |
| `RegistrySet` | Write a registry value |

### Network Tools (`NetworkTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Network` | Get network adapter info and IP configuration |
| `HttpRequest` | Make HTTP requests (GET/POST/etc.) |

### Web Tool (`WebTools` — 2 tools)
| Tool | Purpose |
|------|---------|
| `Scrape` | Fetch a webpage and convert to Markdown |
| `Shortcut` | Create or read a Windows shell shortcut (.lnk) |

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

## Core NuGet Dependencies

| Package | Purpose | Replaces (Python) |
|---------|---------|------------------|
| `ModelContextProtocol` | MCP server SDK, stdio transport | `fastmcp` |
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
