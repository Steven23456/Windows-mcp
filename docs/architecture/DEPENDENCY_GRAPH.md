# unknown - Dependency Graph

**Version**: 0.0.0 | **Last Updated**: 2026-07-08

This document provides a comprehensive dependency graph of all files, components, imports, functions, and variables in the codebase.

---

## Table of Contents

1. [Overview](#overview)
2. [Entry Dependencies](#entry-dependencies)
3. [Services Dependencies](#services-dependencies)
4. [Tools Dependencies](#tools-dependencies)
5. [Abstractions Dependencies](#abstractions-dependencies)
6. [Models Dependencies](#models-dependencies)
7. [Dependency Matrix](#dependency-matrix)
8. [Circular Dependency Analysis](#circular-dependency-analysis)
9. [Visual Dependency Graph](#visual-dependency-graph)
10. [Summary Statistics](#summary-statistics)

---

## Overview

The codebase is organized into the following modules:

- **entry**: 4 files
- **services**: 33 files
- **tools**: 15 files
- **abstractions**: 32 files
- **models**: 17 files

---

## Entry Dependencies

### `src/WindowsMcp/Program.cs` - Program.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `Microsoft` | `Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Hosting, Microsoft.Extensions.Logging` |
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Services` | `WindowsMcp.Services` | Import |

**Exports:**
- Classes: `Program`

---

### `src/WindowsMcp/Startup/CommandTarget.cs` - <summary>

**Exports:**
- Classes: `CommandTarget`

---

### `src/WindowsMcp/Startup/StartupApproval.cs` - <summary>

**Exports:**
- Classes: `StartupApproval`

---

### `src/WindowsMcp/Startup/StartupReportRenderer.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `StartupReportRenderer`

---

## Services Dependencies

### `src/WindowsMcp/Services/AudioService.cs` - TODO(v0.3.0): swap to NAudio/AudioDeviceCmdlets for accurate get/set

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `AudioService`

---

### `src/WindowsMcp/Services/AuthenticodeInspector.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `AuthenticodeInspector`

---

### `src/WindowsMcp/Services/CertStoreService.cs` - CertStoreService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `CertStoreService`

---

### `src/WindowsMcp/Services/ClipboardService.cs` - ClipboardService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `ClipboardService`

---

### `src/WindowsMcp/Services/DiskService.cs` - DiskService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `DiskService`

---

### `src/WindowsMcp/Services/DriverService.cs` - DriverService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `DriverService`

---

### `src/WindowsMcp/Services/EnvService.cs` - EnvService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `EnvService`

---

### `src/WindowsMcp/Services/EventLogService.cs` - EventLogService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `EventLogService`

---

### `src/WindowsMcp/Services/FileStreamService.cs` - FileStreamService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `FileStreamService`

---

### `src/WindowsMcp/Services/FileSystemService.cs` - FileSystemService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `FileSystemService`

---

### `src/WindowsMcp/Services/FirewallService.cs` - FirewallService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `FirewallService`

---

### `src/WindowsMcp/Services/InputService.cs` - Disambiguate: H.InputSimulator also exposes a WindowsInput.MouseButton enum.

**External Dependencies:**
| Package | Import |
|---------|--------|
| `WindowsInput` | `WindowsInput` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `InputService`

---

### `src/WindowsMcp/Services/LspEnumerator.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `LspEnumerator`

---

### `src/WindowsMcp/Services/NetworkService.cs` - NetworkService.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `Microsoft` | `Microsoft.Extensions.Logging` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `NetworkService`

---

### `src/WindowsMcp/Services/NotificationService.cs` - NotificationService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `NotificationService`

---

### `src/WindowsMcp/Services/OcrService.cs` - OcrService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `OcrService`

---

### `src/WindowsMcp/Services/PowerService.cs` - PowerService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `PowerService`

---

### `src/WindowsMcp/Services/PowerShellService.cs` - PowerShellService.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `Microsoft` | `Microsoft.Extensions.Logging` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `PowerShellService`

---

### `src/WindowsMcp/Services/ProcessLineage.cs` - <summary>Typed projection of a Win32_Process row (parse boundary handles CIM_DATETIME).</summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ProcessLineage`

---

### `src/WindowsMcp/Services/ProcessService.cs` - ProcessService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ProcessService`

---

### `src/WindowsMcp/Services/RegistryService.cs` - RegistryService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `Microsoft` | `Microsoft` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `RegistryService`

---

### `src/WindowsMcp/Services/ReliabilityService.cs` - ReliabilityService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ReliabilityService`

---

### `src/WindowsMcp/Services/ScreenshotService.cs` - ScreenshotService.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `SkiaSharp` | `SkiaSharp` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ScreenshotService`

---

### `src/WindowsMcp/Services/SecurityService.cs` - SecurityService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `SecurityService`

---

### `src/WindowsMcp/Services/ServiceControlService.cs` - ServiceControlService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ServiceControlService`

---

### `src/WindowsMcp/Services/ShortcutResolver.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `ShortcutResolver`

---

### `src/WindowsMcp/Services/StartupReportService.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |
| `WindowsMcp.Startup` | `WindowsMcp.Startup` | Import |

**Exports:**
- Classes: `StartupReportService`

---

### `src/WindowsMcp/Services/StorageService.cs` - <summary>

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `StorageService`

---

### `src/WindowsMcp/Services/TaskSchedulerService.cs` - Suppress ambiguous-reference: TaskScheduler library exports 'Task' which clashes with

**External Dependencies:**
| Package | Import |
|---------|--------|
| `Microsoft` | `Microsoft.Win32.TaskScheduler` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `TaskSchedulerService`

---

### `src/WindowsMcp/Services/UIAutomationService.cs` - TODO (v0.3.0): Element cache (_elementCache) is unbounded by design in v0.2.0.

**External Dependencies:**
| Package | Import |
|---------|--------|
| `FlaUI` | `FlaUI.Core.AutomationElements, FlaUI.Core.Definitions, FlaUI.UIA3` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `UIAutomationService`

---

### `src/WindowsMcp/Services/WebService.cs` - WebService.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `Microsoft` | `Microsoft.Extensions.Logging` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `WebService`

---

### `src/WindowsMcp/Services/WindowService.cs` - WindowService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |
| `Windows` | `Windows` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `WindowService`

---

### `src/WindowsMcp/Services/WmiService.cs` - WmiService.cs module

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `WmiService`

---

## Tools Dependencies

### `src/WindowsMcp/Tools/DiskTools.cs` - DiskTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `DiskTools`
- Functions: `DiskInspect`

---

### `src/WindowsMcp/Tools/FileTools.cs` - FileTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `FileTools`
- Functions: `FileSearch`, `FileManage`, `FileDialog`, `FileRead`, `FileWrite`, `FileHash`, `FileStreams`, `FileInfo`, `Archive`

---

### `src/WindowsMcp/Tools/InputTools.cs` - InputTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `InputTools`
- Functions: `Click`, `Drag`, `Hover`, `Type`, `Key`, `Shortcut`, `Scroll`, `Clipboard`

---

### `src/WindowsMcp/Tools/NetworkTools.cs` - NetworkTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `NetworkTools`
- Functions: `Network`, `Firewall`

---

### `src/WindowsMcp/Tools/ProcessTools.cs` - ProcessTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `ProcessTools`
- Functions: `Process`, `ProcessInspect`, `StartProcess`, `Service`, `ScheduledTask`, `EventLog`

---

### `src/WindowsMcp/Tools/RegistryTools.cs` - RegistryTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `RegistryTools`
- Functions: `RegistryGet`, `RegistrySet`

---

### `src/WindowsMcp/Tools/ScreenTools.cs` - ScreenTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `ScreenTools`
- Functions: `Screenshot`, `Ocr`

---

### `src/WindowsMcp/Tools/SecurityTools.cs` - SecurityTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `SecurityTools`
- Functions: `VerifySignature`, `DefenderStatus`, `CertStore`

---

### `src/WindowsMcp/Tools/ShellTools.cs` - ShellTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `ShellTools`
- Functions: `Powershell`

---

### `src/WindowsMcp/Tools/StartupTools.cs` - StartupTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Startup` | `WindowsMcp.Startup` | Import |

**Exports:**
- Classes: `StartupTools`
- Functions: `StartupReport`

---

### `src/WindowsMcp/Tools/StorageTools.cs` - StorageTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `StorageTools`
- Functions: `StorageHealth`

---

### `src/WindowsMcp/Tools/SystemTools.cs` - SystemTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `SystemTools`
- Functions: `SystemInfo`, `Audio`, `Notification`, `SecurityAudit`, `Reliability`, `DriverList`, `WmiQuery`, `Env`, `PowerAction`

---

### `src/WindowsMcp/Tools/UIAutomationTools.cs` - UIAutomationTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Classes: `UIAutomationTools`
- Functions: `GetState`, `FindElement`, `GetElement`, `GetText`, `AssertElement`, `InteractElement`, `GetTable`, `WaitFor`

---

### `src/WindowsMcp/Tools/WebTools.cs` - WebTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `WebTools`
- Functions: `Scrape`, `HttpRequest`

---

### `src/WindowsMcp/Tools/WindowTools.cs` - WindowTools.cs module

**External Dependencies:**
| Package | Import |
|---------|--------|
| `ModelContextProtocol` | `ModelContextProtocol.Server` |

**Node.js Built-in Dependencies:**
| Module | Import |
|--------|--------|
| `System` | `System` |

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions` | `WindowsMcp.Abstractions` | Import |

**Exports:**
- Classes: `WindowTools`
- Functions: `Window`, `SwitchToWindow`, `Launch`, `Focus`, `MultiMonitor`

---

## Abstractions Dependencies

### `src/WindowsMcp.Abstractions/IAudioService.cs` - Get the current playback volume level (0-100) and muted state.

**Exports:**
- Classes: `AudioState`
- Interfaces: `IAudioService`

---

### `src/WindowsMcp.Abstractions/IAuthenticodeInspector.cs` - <summary>

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IAuthenticodeInspector`

---

### `src/WindowsMcp.Abstractions/ICertStoreService.cs` - ICertStoreService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `ICertStoreService`

---

### `src/WindowsMcp.Abstractions/IClipboardService.cs` - IClipboardService.cs module

**Exports:**
- Interfaces: `IClipboardService`

---

### `src/WindowsMcp.Abstractions/IDiskService.cs` - IDiskService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IDiskService`

---

### `src/WindowsMcp.Abstractions/IDriverService.cs` - IDriverService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IDriverService`

---

### `src/WindowsMcp.Abstractions/IEnvService.cs` - IEnvService.cs module

**Exports:**
- Interfaces: `IEnvService`

---

### `src/WindowsMcp.Abstractions/IEventLogService.cs` - IEventLogService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IEventLogService`

---

### `src/WindowsMcp.Abstractions/IFileStreamService.cs` - IFileStreamService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IFileStreamService`

---

### `src/WindowsMcp.Abstractions/IFileSystemService.cs` - IFileSystemService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IFileSystemService`

---

### `src/WindowsMcp.Abstractions/IFirewallService.cs` - IFirewallService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IFirewallService`

---

### `src/WindowsMcp.Abstractions/IInputService.cs` - IInputService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IInputService`

---

### `src/WindowsMcp.Abstractions/ILspEnumerator.cs` - <summary>

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `ILspEnumerator`

---

### `src/WindowsMcp.Abstractions/INetworkService.cs` - INetworkService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `INetworkService`

---

### `src/WindowsMcp.Abstractions/INotificationService.cs` - INotificationService.cs module

**Exports:**
- Interfaces: `INotificationService`

---

### `src/WindowsMcp.Abstractions/IOcrService.cs` - IOcrService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IOcrService`

---

### `src/WindowsMcp.Abstractions/IPowerService.cs` - IPowerService.cs module

**Exports:**
- Interfaces: `IPowerService`

---

### `src/WindowsMcp.Abstractions/IPowerShellService.cs` - IPowerShellService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IPowerShellService`

---

### `src/WindowsMcp.Abstractions/IProcessService.cs` - IProcessService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IProcessService`

---

### `src/WindowsMcp.Abstractions/IRegistryService.cs` - IRegistryService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IRegistryService`

---

### `src/WindowsMcp.Abstractions/IReliabilityService.cs` - IReliabilityService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IReliabilityService`

---

### `src/WindowsMcp.Abstractions/IScreenshotService.cs` - IScreenshotService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IScreenshotService`

---

### `src/WindowsMcp.Abstractions/ISecurityService.cs` - ISecurityService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `ISecurityService`

---

### `src/WindowsMcp.Abstractions/IServiceControlService.cs` - IServiceControlService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IServiceControlService`

---

### `src/WindowsMcp.Abstractions/IShortcutResolver.cs` - <summary>

**Exports:**
- Interfaces: `IShortcutResolver`

---

### `src/WindowsMcp.Abstractions/IStartupReportService.cs` - <summary>

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IStartupReportService`

---

### `src/WindowsMcp.Abstractions/IStorageService.cs` - Diagnose disk/volume health from the storage stack (metadata-first, hang-safe).

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IStorageService`

---

### `src/WindowsMcp.Abstractions/ITaskSchedulerService.cs` - ITaskSchedulerService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `ITaskSchedulerService`

---

### `src/WindowsMcp.Abstractions/IUIAutomationService.cs` - IUIAutomationService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IUIAutomationService`

---

### `src/WindowsMcp.Abstractions/IWebService.cs` - IWebService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IWebService`

---

### `src/WindowsMcp.Abstractions/IWindowService.cs` - IWindowService.cs module

**Internal Dependencies:**
| File | Imports | Type |
|------|---------|------|
| `WindowsMcp.Abstractions.Models` | `WindowsMcp.Abstractions.Models` | Import |

**Exports:**
- Interfaces: `IWindowService`

---

### `src/WindowsMcp.Abstractions/IWmiService.cs` - IWmiService.cs module

**Exports:**
- Interfaces: `IWmiService`

---

## Models Dependencies

### `src/WindowsMcp.Abstractions/Models/DiskDtos.cs` - DiskDtos.cs module

**Exports:**
- Classes: `DiskUsageEntry`, `FileTypeEntry`, `StaleFileEntry`, `ReclaimableSpace`

---

### `src/WindowsMcp.Abstractions/Models/DriverDtos.cs` - <summary>

**Exports:**
- Classes: `DriverInfo`

---

### `src/WindowsMcp.Abstractions/Models/FileSystemDtos.cs` - <summary>An NTFS alternate data stream (e.g. <c>:Zone.Identifier:$DATA</c>).</summary>

**Exports:**
- Classes: `AlternateStreamInfo`, `FileStreamsDto`, `FileInfoDto`, `FileSearchHit`, `RegistryValueDto`, `ServiceDto`, `EventLogEntryDto`, `ScheduledTaskDto`, `ScheduledTaskDetailDto`

---

### `src/WindowsMcp.Abstractions/Models/FirewallDtos.cs` - <summary>A Windows Firewall rule (enabled-state, direction, and action rendered as strings).</summary>

**Exports:**
- Classes: `FirewallRuleDto`

---

### `src/WindowsMcp.Abstractions/Models/InputDtos.cs` - InputDtos.cs module

**Exports:**
- Classes: `ClickResult`, `DragResult`, `TypeResult`
- Enums: `MouseButton`

---

### `src/WindowsMcp.Abstractions/Models/NetworkDtos.cs` - NetworkDtos.cs module

**Exports:**
- Classes: `NetworkAdapterDto`, `PortInfoDto`, `PingResult`, `WifiInfoDto`

---

### `src/WindowsMcp.Abstractions/Models/PowerShellDtos.cs` - PowerShellDtos.cs module

**Exports:**
- Classes: `PSResult`

---

### `src/WindowsMcp.Abstractions/Models/ProcessDtos.cs` - <summary>A DLL/module loaded into a process.</summary>

**Exports:**
- Classes: `ProcessDto`, `ModuleInfo`, `ProcessDetailDto`, `ProcessLineageDto`, `ProcessGroupDto`

---

### `src/WindowsMcp.Abstractions/Models/ReliabilityDtos.cs` - <summary>A crash minidump file in C:\Windows\Minidump.</summary>

**Exports:**
- Classes: `MinidumpInfo`, `ReliabilityRecord`, `ReliabilityReport`

---

### `src/WindowsMcp.Abstractions/Models/ScreenDtos.cs` - ScreenDtos.cs module

**Exports:**
- Classes: `ScreenRegion`, `ScreenshotResult`
- Enums: `ImageFormat`

---

### `src/WindowsMcp.Abstractions/Models/SecurityDtos.cs` - <summary>

**Exports:**
- Classes: `AuthenticodeInfo`, `LspProviderDto`, `SecurityAuditDto`, `CertInfoDto`, `DefenderStatusDto`

---

### `src/WindowsMcp.Abstractions/Models/StartupReportDtos.cs` - <summary>Aggregated boot/persistence report (the HiJackThis-style snapshot).</summary>

**Exports:**
- Classes: `StartupReportDto`, `StartupHeader`, `ProcessEntry`, `RunEntry`, `StartupFolderEntry`, `StartupTaskEntry`, `StartupServiceEntry`, `HostsEntry`, `DnsEntry`, `LspProviderEntry`, `ShellExtensionEntry`, `ControlPanelAppletEntry`, `AccessibilityToolEntry`, `IfeoEntry`, `WinlogonHookEntry`, `AppInitDllEntry`, `ActiveSetupEntry`, `BrowserProxyEntry`, `TrustedZoneEntry`

---

### `src/WindowsMcp.Abstractions/Models/StorageDtos.cs` - <summary>Top-level storage-health report. Empty arrays + a Notes entry signal a degraded probe.</summary>

**Exports:**
- Classes: `StorageHealthReport`, `PhysicalDiskInfo`, `ReliabilityInfo`, `DiskInfo`, `VolumeInfo`, `DiskEventInfo`

---

### `src/WindowsMcp.Abstractions/Models/SystemDtos.cs` - SystemDtos.cs module

**Exports:**
- Classes: `WmiResultDto`

---

### `src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs` - UIAutomationDtos.cs module

**Exports:**
- Classes: `ElementInfo`, `Bounds`, `ElementTree`, `FindElementResult`, `TableData`
- Enums: `FindKind`

---

### `src/WindowsMcp.Abstractions/Models/WebDtos.cs` - WebDtos.cs module

**Exports:**
- Classes: `HttpResponseDto`

---

### `src/WindowsMcp.Abstractions/Models/WindowDtos.cs` - WindowDtos.cs module

**Exports:**
- Classes: `WindowAction`, `MonitorInfo`

---

## Dependency Matrix

### File Import/Export Matrix

| File | Imports From | Exports To |
|------|--------------|------------|
| `Program.cs` | 2 files | 0 files |
| `AudioService.cs` | 2 files | 1 files |
| `AuthenticodeInspector.cs` | 2 files | 1 files |
| `CertStoreService.cs` | 2 files | 1 files |
| `ClipboardService.cs` | 1 files | 1 files |
| `DiskService.cs` | 2 files | 1 files |
| `DriverService.cs` | 2 files | 1 files |
| `EnvService.cs` | 1 files | 1 files |
| `EventLogService.cs` | 2 files | 1 files |
| `FileStreamService.cs` | 2 files | 1 files |
| `FileSystemService.cs` | 2 files | 1 files |
| `FirewallService.cs` | 2 files | 1 files |
| `InputService.cs` | 2 files | 1 files |
| `LspEnumerator.cs` | 2 files | 1 files |
| `NetworkService.cs` | 2 files | 1 files |
| `NotificationService.cs` | 1 files | 1 files |
| `OcrService.cs` | 2 files | 1 files |
| `PowerService.cs` | 1 files | 1 files |
| `PowerShellService.cs` | 2 files | 1 files |
| `ProcessLineage.cs` | 1 files | 1 files |
| `ProcessService.cs` | 2 files | 1 files |
| `RegistryService.cs` | 2 files | 1 files |
| `ReliabilityService.cs` | 2 files | 1 files |
| `ScreenshotService.cs` | 2 files | 1 files |
| `SecurityService.cs` | 2 files | 1 files |
| `ServiceControlService.cs` | 2 files | 1 files |
| `ShortcutResolver.cs` | 1 files | 1 files |
| `StartupReportService.cs` | 3 files | 1 files |
| `StorageService.cs` | 2 files | 1 files |
| `TaskSchedulerService.cs` | 2 files | 1 files |

---

## Circular Dependency Analysis

**No circular dependencies detected.**
---

## Visual Dependency Graph

```mermaid
graph TD
    subgraph Entry
        N0[Program.cs]
        N1[CommandTarget.cs]
        N2[StartupApproval.cs]
        N3[StartupReportRenderer.cs]
    end

    subgraph Services
        N4[AudioService.cs]
        N5[AuthenticodeInspector.cs]
        N6[CertStoreService.cs]
        N7[ClipboardService.cs]
        N8[DiskService.cs]
        N9[...28 more]
    end

    subgraph Tools
        N10[DiskTools.cs]
        N11[FileTools.cs]
        N12[InputTools.cs]
        N13[NetworkTools.cs]
        N14[ProcessTools.cs]
        N15[...10 more]
    end

    subgraph Abstractions
        N16[IAudioService.cs]
        N17[IAuthenticodeInspector.cs]
        N18[ICertStoreService.cs]
        N19[IClipboardService.cs]
        N20[IDiskService.cs]
        N21[...27 more]
    end

    subgraph Models
        N22[DiskDtos.cs]
        N23[DriverDtos.cs]
        N24[FileSystemDtos.cs]
        N25[FirewallDtos.cs]
        N26[InputDtos.cs]
        N27[...12 more]
    end

```

---

## Summary Statistics

| Category | Count |
|----------|-------|
| Total Source Files | 101 |
| Total Modules | 5 |
| Total Lines of Code | 6448 |
| Total Exports | 220 |
| Total Re-exports | 0 |
| Total Classes | 125 |
| Total Interfaces | 32 |
| Total Functions | 60 |
| Total Type Guards | 0 |
| Total Enums | 3 |
| Type-only Imports | 0 |
| Runtime Circular Deps | 0 |
| Type-only Circular Deps | 0 |

---

*Last Updated*: 2026-07-08
*Version*: 0.0.0
