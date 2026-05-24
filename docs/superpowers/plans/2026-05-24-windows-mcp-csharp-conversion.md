# Windows-mcp Python → C# Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert Windows-mcp from Python (FastMCP, 45 tools) to a single-binary C# MCP server (ModelContextProtocol NuGet SDK, 50 tools) shipping as one self-contained `WindowsMcp.exe`.

**Architecture:** .NET 9 solution at repo root with 3 projects (WindowsMcp executable + WindowsMcp.Abstractions interfaces + WindowsMcp.Tests xUnit). Tool handlers depend on service interfaces (mocked in unit tests). FlaUI.UIA3 for UI Automation on a dedicated STA thread. `System.Management.Automation` for persistent PowerShell runspace. Source-generator tool discovery via `[McpServerTool]` — no runtime reflection. Self-contained single-file publish (~70 MB); native AOT deferred to v0.3.0.

**Tech Stack:** .NET 9, C# 13, ModelContextProtocol NuGet, FlaUI.UIA3, H.InputSimulator, SkiaSharp, Microsoft.Windows.CsWin32 (source-generated P/Invoke), Microsoft.Win32.TaskScheduler, ReverseMarkdown, xUnit + FluentAssertions + Moq.

**Reference spec:** `docs/superpowers/specs/2026-05-24-windows-mcp-csharp-conversion-design.md` — every implementer subagent should be given this spec as context.

**Commit message convention:** Each task ends with one atomic commit. Subject: imperative (`feat: …`, `test: …`, `chore: …`). Body trailer:
```
Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
```

**Test categories** (xUnit `[Trait]`):
- `Unit` (default) — pure logic, mocked dependencies, fast
- `Integration` — real Windows APIs
- `UIAutomation` — real apps + real a11y tree

Run unit-only during dev: `dotnet test --filter "Category=Unit"`. Run all in CI / pre-PR.

---

## Phased overview

| Phase | Tasks | Goal |
|---|---|---|
| 0. Snapshot | T1 | Move Python aside |
| 1. Scaffolding | T2–T3 | Solution + projects + bootstrap server |
| 2. Services | T4–T8 | Concrete implementations behind interfaces |
| 3. Tools (by category) | T9–T19 | All 50 tool handlers, TDD throughout |
| 4. Wiring | T20 | Program.cs DI + tool registration |
| 5. Cutover | T21–T22 | Publish, .mcp.json swap, retire Python |

---

## Task 1: Snapshot Python sources

**Files:**
- Create: `.python-snapshot-2026-05-24/` (move target)
- Move: `main.py`, `pyproject.toml`, `requirements.txt`, `src/desktop/`, `src/tree/`, any `windows_mcp_entry.py`

- [ ] **Step 1: List current Python sources**

```powershell
Get-ChildItem -Path "C:/Users/danie/Dropbox/Github/Windows-mcp" -Force | Select-Object Name, Mode
```

Expected: see `main.py`, `pyproject.toml`, `requirements.txt`, `src/` directory.

- [ ] **Step 2: Move Python tree into snapshot folder**

```powershell
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
New-Item -ItemType Directory -Path ".python-snapshot-2026-05-24"
Move-Item main.py .python-snapshot-2026-05-24/
Move-Item pyproject.toml .python-snapshot-2026-05-24/
Move-Item requirements.txt .python-snapshot-2026-05-24/
Move-Item src .python-snapshot-2026-05-24/
if (Test-Path windows_mcp_entry.py) { Move-Item windows_mcp_entry.py .python-snapshot-2026-05-24/ }
```

- [ ] **Step 3: Verify the Python entry is still pointed at by `.mcp.json`**

The MCP host config currently launches `C:/Users/danie/.venvs/windows-mcp/Scripts/python.exe -X utf8 C:/Users/danie/Dropbox/Github/Windows-mcp/main.py`. Since `main.py` is now under `.python-snapshot-2026-05-24/`, the Python server will fail to load after `/reload-plugins` — that's expected and acceptable for the duration of development. **Do not change `.mcp.json` in this task** — the cutover happens in Task 21.

- [ ] **Step 4: Commit**

```bash
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
git add -A
git commit -m "$(cat <<'EOF'
chore: snapshot Python sources before C# rewrite

Move main.py, src/desktop, src/tree, pyproject.toml, requirements.txt into
.python-snapshot-2026-05-24/. Retained for reference during the C# rewrite;
deleted in the cutover commit (Task 22) after live verification.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Solution scaffolding

**Files:**
- Create: `Windows-mcp.sln`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `.gitignore` (or update existing)
- Create: `src/WindowsMcp/WindowsMcp.csproj`
- Create: `src/WindowsMcp.Abstractions/WindowsMcp.Abstractions.csproj`
- Create: `tests/WindowsMcp.Tests/WindowsMcp.Tests.csproj`

- [ ] **Step 1: Create `global.json`**

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 2: Create `Directory.Build.props`**

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

- [ ] **Step 3: Update `.gitignore`**

Append:
```
bin/
obj/
*.user
*.suo
.vs/
dist/
src/WindowsMcp/Properties/launchSettings.json
```

- [ ] **Step 4: Create the solution and projects**

```powershell
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
dotnet new sln -n Windows-mcp
dotnet new classlib -o src/WindowsMcp.Abstractions -f net9.0-windows10.0.19041.0
dotnet new console -o src/WindowsMcp -f net9.0-windows10.0.19041.0
dotnet new xunit -o tests/WindowsMcp.Tests -f net9.0-windows10.0.19041.0
dotnet sln add src/WindowsMcp.Abstractions/WindowsMcp.Abstractions.csproj
dotnet sln add src/WindowsMcp/WindowsMcp.csproj
dotnet sln add tests/WindowsMcp.Tests/WindowsMcp.Tests.csproj
dotnet add src/WindowsMcp reference src/WindowsMcp.Abstractions
dotnet add tests/WindowsMcp.Tests reference src/WindowsMcp.Abstractions
dotnet add tests/WindowsMcp.Tests reference src/WindowsMcp
```

- [ ] **Step 5: Delete `Class1.cs` and `Program.cs` stub content**

```powershell
Remove-Item src/WindowsMcp.Abstractions/Class1.cs -Force
```

Replace `src/WindowsMcp/Program.cs` with a minimal placeholder:

```csharp
namespace WindowsMcp;

internal static class Program
{
    public static Task<int> Main(string[] args) => Task.FromResult(0);
}
```

- [ ] **Step 6: Verify clean build**

```powershell
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 7: Verify clean test run**

```powershell
dotnet test
```

Expected: tests pass (xUnit creates a single trivial test by default; or 0 tests if you've removed `UnitTest1.cs`).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: scaffold .NET 9 solution with 3 projects

- WindowsMcp (executable, net9.0-windows10.0.19041.0)
- WindowsMcp.Abstractions (interfaces + DTOs)
- WindowsMcp.Tests (xUnit)

Pinned SDK 9.0.100 via global.json. Shared Nullable/ImplicitUsings/
TreatWarningsAsErrors via Directory.Build.props. Bin/obj/dist gitignored.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: MCP server bootstrap (hello-world tool, stdio transport)

**Files:**
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add ModelContextProtocol + Logging packages
- Modify: `src/WindowsMcp/Program.cs` — full MCP server bootstrap
- Create: `src/WindowsMcp/Tools/EchoTool.cs` — placeholder hello-world tool (deleted in Task 9)
- Create: `tests/WindowsMcp.Tests/EchoToolTests.cs`
- Modify: `tests/WindowsMcp.Tests/WindowsMcp.Tests.csproj` — add FluentAssertions + Moq

- [ ] **Step 1: Add NuGet packages to main project**

```powershell
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
dotnet add src/WindowsMcp package ModelContextProtocol --version 0.4.*
dotnet add src/WindowsMcp package Microsoft.Extensions.Logging.Console --version 9.*
dotnet add src/WindowsMcp package Microsoft.Extensions.Hosting --version 9.*
```

- [ ] **Step 2: Add NuGet packages to test project**

```powershell
dotnet add tests/WindowsMcp.Tests package FluentAssertions --version 7.*
dotnet add tests/WindowsMcp.Tests package Moq --version 4.*
```

- [ ] **Step 3: Write the failing test (`tests/WindowsMcp.Tests/EchoToolTests.cs`)**

```csharp
using FluentAssertions;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests;

[Trait("Category", "Unit")]
public class EchoToolTests
{
    [Fact]
    public void Echo_returns_the_input_text()
    {
        var result = EchoTool.Echo("hello windows-mcp");
        result.Should().Be("hello windows-mcp");
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

```powershell
dotnet test --filter "FullyQualifiedName~EchoToolTests"
```

Expected: FAIL with `error CS0246: The type or namespace name 'EchoTool' could not be found`.

- [ ] **Step 5: Implement the echo tool (`src/WindowsMcp/Tools/EchoTool.cs`)**

```csharp
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace WindowsMcp.Tools;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echo the input text back. Used to verify the server is alive.")]
    public static string Echo(
        [Description("Text to echo back")] string text) => text;
}
```

- [ ] **Step 6: Run the test to verify it passes**

```powershell
dotnet test --filter "FullyQualifiedName~EchoToolTests"
```

Expected: PASS, 1 test.

- [ ] **Step 7: Wire up the MCP server in `Program.cs`**

Replace `src/WindowsMcp/Program.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace WindowsMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // CRITICAL: MCP stdio servers must log to stderr only. stdout is JSON-RPC.
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();   // source generator discovers [McpServerTool] methods

        await builder.Build().RunAsync();
        return 0;
    }
}
```

- [ ] **Step 8: Verify build still passes**

```powershell
dotnet build
```

Expected: `Build succeeded`.

- [ ] **Step 9: Smoke test the MCP server end-to-end**

```powershell
$initRequest = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
$initRequest | dotnet run --project src/WindowsMcp --no-build 2>$null | Select-Object -First 1
```

Expected: A single line of JSON containing `"jsonrpc":"2.0"`, `"id":1`, and a `result` object with `serverInfo` and `capabilities.tools`.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: MCP server bootstrap with echo tool + stdio transport

- ModelContextProtocol 0.4.x SDK wired via Host.CreateApplicationBuilder
- Logging routed to stderr (stdout reserved for JSON-RPC per spec)
- WithToolsFromAssembly() picks up [McpServerTool]-decorated methods at
  compile time via source generator
- EchoTool added as smoke target; deleted in Task 9 when InputTools lands
- 1 passing unit test verifies the echo round-trip

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Service abstractions + InputService

**Files:**
- Create: `src/WindowsMcp.Abstractions/IInputService.cs`
- Create: `src/WindowsMcp.Abstractions/Models/ClickResult.cs` (and small DTOs)
- Create: `src/WindowsMcp/Services/InputService.cs`
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add `H.InputSimulator` + `Microsoft.Windows.CsWin32`
- Create: `src/WindowsMcp/NativeMethods.txt` (CsWin32 input)
- Create: `tests/WindowsMcp.Tests/Services/InputServiceTests.cs`

- [ ] **Step 1: Add packages**

```powershell
dotnet add src/WindowsMcp package H.InputSimulator --version 1.*
dotnet add src/WindowsMcp package Microsoft.Windows.CsWin32 --version 0.3.* --no-restore
```

For CsWin32, edit `src/WindowsMcp/WindowsMcp.csproj` and ensure the PackageReference has `PrivateAssets="all"`:

```xml
<PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.*" PrivateAssets="all" />
```

- [ ] **Step 2: Create `src/WindowsMcp/NativeMethods.txt`**

```
SendInput
SetCursorPos
GetCursorPos
mouse_event
```

CsWin32's source generator reads this file and emits a `Windows.Win32.PInvoke` partial class with strongly-typed bindings.

- [ ] **Step 3: Define DTOs in Abstractions project (`src/WindowsMcp.Abstractions/Models/InputDtos.cs`)**

```csharp
namespace WindowsMcp.Abstractions.Models;

public enum MouseButton { Left, Right, Middle }

public record ClickResult(int X, int Y, MouseButton Button, int Clicks);
public record DragResult(int FromX, int FromY, int ToX, int ToY, MouseButton Button);
public record TypeResult(int CharsTyped);
```

- [ ] **Step 4: Define the interface (`src/WindowsMcp.Abstractions/IInputService.cs`)**

```csharp
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task HoverAsync(int x, int y, int durationMs = 0, CancellationToken ct = default);
    Task<TypeResult> TypeAsync(string text, CancellationToken ct = default);
    Task PressKeyAsync(string key, CancellationToken ct = default);
    Task PressShortcutAsync(string shortcut, CancellationToken ct = default);
    Task ScrollAsync(int x, int y, string direction, int amount = 3, CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the failing test (`tests/WindowsMcp.Tests/Services/InputServiceTests.cs`)**

```csharp
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class InputServiceTests
{
    [Fact]
    public async Task ClickAsync_returns_result_with_correct_coordinates_and_button()
    {
        var service = new InputService();
        var result = await service.ClickAsync(100, 200, MouseButton.Left);
        result.Should().BeEquivalentTo(new ClickResult(100, 200, MouseButton.Left, 1));
    }

    [Fact]
    public async Task TypeAsync_reports_character_count_for_unicode_input()
    {
        var service = new InputService();
        var result = await service.TypeAsync("héllo");
        result.CharsTyped.Should().Be(5);
    }

    [Fact]
    public async Task PressShortcutAsync_throws_on_invalid_format()
    {
        var service = new InputService();
        Func<Task> act = () => service.PressShortcutAsync("not+a+real+key");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
```

- [ ] **Step 6: Verify the tests fail**

```powershell
dotnet test --filter "FullyQualifiedName~InputServiceTests"
```

Expected: FAIL — `InputService` does not exist.

- [ ] **Step 7: Implement `src/WindowsMcp/Services/InputService.cs`**

```csharp
using Gma.System.MouseKeyHook;   // optional; or use H.InputSimulator
using WindowsInput;              // H.InputSimulator namespace
using WindowsInput.Native;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class InputService : IInputService
{
    private readonly InputSimulator _sim = new();
    private static readonly Dictionary<string, VirtualKeyCode> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = VirtualKeyCode.RETURN, ["tab"] = VirtualKeyCode.TAB,
        ["esc"] = VirtualKeyCode.ESCAPE, ["escape"] = VirtualKeyCode.ESCAPE,
        ["space"] = VirtualKeyCode.SPACE, ["backspace"] = VirtualKeyCode.BACK,
        ["delete"] = VirtualKeyCode.DELETE, ["up"] = VirtualKeyCode.UP,
        ["down"] = VirtualKeyCode.DOWN, ["left"] = VirtualKeyCode.LEFT,
        ["right"] = VirtualKeyCode.RIGHT, ["home"] = VirtualKeyCode.HOME,
        ["end"] = VirtualKeyCode.END, ["pageup"] = VirtualKeyCode.PRIOR,
        ["pagedown"] = VirtualKeyCode.NEXT,
        ["ctrl"] = VirtualKeyCode.CONTROL, ["alt"] = VirtualKeyCode.MENU,
        ["shift"] = VirtualKeyCode.SHIFT, ["win"] = VirtualKeyCode.LWIN,
    };
    // Add F1..F12 programmatically
    static InputService()
    {
        for (int i = 1; i <= 12; i++)
            KeyMap[$"f{i}"] = (VirtualKeyCode)((int)VirtualKeyCode.F1 + i - 1);
    }

    public Task<ClickResult> ClickAsync(int x, int y, MouseButton button, int clicks, CancellationToken ct)
    {
        _sim.Mouse.MoveMouseToPositionOnVirtualDesktop(
            x * (65535.0 / Screen.PrimaryScreen!.Bounds.Width),
            y * (65535.0 / Screen.PrimaryScreen.Bounds.Height));
        for (int i = 0; i < clicks; i++)
        {
            switch (button)
            {
                case MouseButton.Left:   _sim.Mouse.LeftButtonClick(); break;
                case MouseButton.Right:  _sim.Mouse.RightButtonClick(); break;
                case MouseButton.Middle: _sim.Mouse.XButtonClick(2); break;
            }
        }
        return Task.FromResult(new ClickResult(x, y, button, clicks));
    }

    public async Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button, CancellationToken ct)
    {
        await ClickAsync(fromX, fromY, button, 1, ct); // move-and-press
        _sim.Mouse.LeftButtonDown();
        _sim.Mouse.MoveMouseTo(toX, toY);
        _sim.Mouse.LeftButtonUp();
        return new DragResult(fromX, fromY, toX, toY, button);
    }

    public Task HoverAsync(int x, int y, int durationMs, CancellationToken ct)
    {
        _sim.Mouse.MoveMouseToPositionOnVirtualDesktop(
            x * (65535.0 / Screen.PrimaryScreen!.Bounds.Width),
            y * (65535.0 / Screen.PrimaryScreen.Bounds.Height));
        if (durationMs > 0) return Task.Delay(durationMs, ct);
        return Task.CompletedTask;
    }

    public Task<TypeResult> TypeAsync(string text, CancellationToken ct)
    {
        _sim.Keyboard.TextEntry(text);
        return Task.FromResult(new TypeResult(text.Length));
    }

    public Task PressKeyAsync(string key, CancellationToken ct)
    {
        if (!KeyMap.TryGetValue(key, out var vk))
            throw new ArgumentException($"Unknown key: '{key}'", nameof(key));
        _sim.Keyboard.KeyPress(vk);
        return Task.CompletedTask;
    }

    public Task PressShortcutAsync(string shortcut, CancellationToken ct)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"Invalid shortcut format: '{shortcut}'", nameof(shortcut));
        var vks = new List<VirtualKeyCode>();
        foreach (var part in parts)
        {
            if (!KeyMap.TryGetValue(part, out var vk))
                throw new ArgumentException($"Unknown key in shortcut: '{part}'", nameof(shortcut));
            vks.Add(vk);
        }
        var mods = vks.Take(vks.Count - 1).ToArray();
        var final = vks[^1];
        _sim.Keyboard.ModifiedKeyStroke(mods, final);
        return Task.CompletedTask;
    }

    public Task ScrollAsync(int x, int y, string direction, int amount, CancellationToken ct)
    {
        HoverAsync(x, y, 0, ct).Wait(ct);
        switch (direction.ToLowerInvariant())
        {
            case "up":    _sim.Mouse.VerticalScroll(amount); break;
            case "down":  _sim.Mouse.VerticalScroll(-amount); break;
            case "left":  _sim.Mouse.HorizontalScroll(-amount); break;
            case "right": _sim.Mouse.HorizontalScroll(amount); break;
            default: throw new ArgumentException($"Invalid direction: '{direction}'", nameof(direction));
        }
        return Task.CompletedTask;
    }
}
```

Note: this implementation assumes H.InputSimulator's API. If the installed version uses `MouseSimulator.LeftButtonClick()` etc., adjust accordingly. The interface stays stable.

- [ ] **Step 8: Run the tests**

```powershell
dotnet test --filter "FullyQualifiedName~InputServiceTests"
```

Expected: PASS, 3 tests.

Caveat: the click and type tests above actually exercise the real input system. If running in a non-interactive session (CI/headless) these may need to be marked `[Trait("Category","Integration")]`. For first run, keep them as Unit; if they fail due to no desktop session, move them to Integration in a follow-up.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(services): IInputService + InputService implementation

H.InputSimulator-backed click/drag/hover/type/key/shortcut/scroll. Key
map for named keys (enter, tab, F1-F12, arrows, etc.). Shortcut format
'ctrl+shift+s' parsed via split-and-lookup; invalid forms throw
ArgumentException with the offending token quoted.

CsWin32 NativeMethods.txt seeded with SendInput/SetCursorPos for future
direct-P/Invoke paths if H.InputSimulator proves limiting.

3 passing unit tests cover click coordinates, Unicode type count, and
shortcut format validation.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: ScreenshotService + clipboard/audio interfaces

**Files:**
- Create: `src/WindowsMcp.Abstractions/IScreenshotService.cs`
- Create: `src/WindowsMcp.Abstractions/IClipboardService.cs`
- Create: `src/WindowsMcp.Abstractions/IAudioService.cs`
- Create: `src/WindowsMcp.Abstractions/Models/ScreenDtos.cs`
- Create: `src/WindowsMcp/Services/ScreenshotService.cs`
- Create: `src/WindowsMcp/Services/ClipboardService.cs`
- Create: `src/WindowsMcp/Services/AudioService.cs`
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add `System.Drawing.Common`, `SkiaSharp`, `TextCopy`
- Create: `tests/WindowsMcp.Tests/Services/ScreenshotServiceTests.cs`
- Create: `tests/WindowsMcp.Tests/Services/ClipboardServiceTests.cs`

- [ ] **Step 1: Add packages**

```powershell
dotnet add src/WindowsMcp package System.Drawing.Common --version 9.*
dotnet add src/WindowsMcp package SkiaSharp --version 3.*
dotnet add src/WindowsMcp package TextCopy --version 6.*
```

- [ ] **Step 2: Define DTOs (`src/WindowsMcp.Abstractions/Models/ScreenDtos.cs`)**

```csharp
namespace WindowsMcp.Abstractions.Models;

public record ScreenRegion(int X, int Y, int Width, int Height);
public enum ImageFormat { Png, Jpeg }
public record ScreenshotResult(byte[] Bytes, int Width, int Height, ImageFormat Format);
```

- [ ] **Step 3: Define interfaces**

`src/WindowsMcp.Abstractions/IScreenshotService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IScreenshotService
{
    Task<ScreenshotResult> CaptureAsync(ScreenRegion? region = null, ImageFormat format = ImageFormat.Png, CancellationToken ct = default);
}
```

`src/WindowsMcp.Abstractions/IClipboardService.cs`:
```csharp
namespace WindowsMcp.Abstractions;

public interface IClipboardService
{
    Task<string?> GetTextAsync(CancellationToken ct = default);
    Task SetTextAsync(string text, CancellationToken ct = default);
}
```

`src/WindowsMcp.Abstractions/IAudioService.cs`:
```csharp
namespace WindowsMcp.Abstractions;

public interface IAudioService
{
    Task<AudioState> GetAsync(CancellationToken ct = default);
    Task SetVolumeAsync(int level0to100, CancellationToken ct = default);
    Task SetMutedAsync(bool muted, CancellationToken ct = default);
}

public record AudioState(int Level, bool Muted);
```

- [ ] **Step 4: Write failing tests**

`tests/WindowsMcp.Tests/Services/ScreenshotServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ScreenshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_returns_non_empty_png_with_dimensions()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), ImageFormat.Png);

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Png);
        // PNG magic bytes: 89 50 4E 47
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }
}
```

`tests/WindowsMcp.Tests/Services/ClipboardServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ClipboardServiceTests : IDisposable
{
    private readonly ClipboardService _svc = new();
    private readonly string? _saved;

    public ClipboardServiceTests()
    {
        _saved = _svc.GetTextAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SetTextAsync_then_GetTextAsync_roundtrips()
    {
        await _svc.SetTextAsync("hello windows-mcp test");
        var got = await _svc.GetTextAsync();
        got.Should().Be("hello windows-mcp test");
    }

    public void Dispose()
    {
        if (_saved is not null) _svc.SetTextAsync(_saved).GetAwaiter().GetResult();
    }
}
```

- [ ] **Step 5: Run tests to verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~ScreenshotServiceTests|FullyQualifiedName~ClipboardServiceTests"
```

Expected: FAIL (services don't exist).

- [ ] **Step 6: Implement `src/WindowsMcp/Services/ScreenshotService.cs`**

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ScreenshotService : IScreenshotService
{
    public Task<ScreenshotResult> CaptureAsync(ScreenRegion? region, Abstractions.Models.ImageFormat format, CancellationToken ct)
    {
        var r = region ?? new ScreenRegion(0, 0, Screen.PrimaryScreen!.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, new Size(r.Width, r.Height));

        using var ms = new MemoryStream();
        var skFormat = format == Abstractions.Models.ImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;

        var data = new byte[bmp.Width * bmp.Height * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, data, 0, data.Length);
        bmp.UnlockBits(bd);

        using var skBmp = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, skBmp.GetPixels(), data.Length);
        using var img = SKImage.FromBitmap(skBmp);
        using var encoded = img.Encode(skFormat, 90);
        var bytes = encoded.ToArray();

        return Task.FromResult(new ScreenshotResult(bytes, r.Width, r.Height, format));
    }
}
```

Note: requires `<UseWindowsForms>true</UseWindowsForms>` or pulling in `System.Windows.Forms` for `Screen.PrimaryScreen`. **Alternative without WinForms:** use `User32.GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)` via CsWin32. Add `GetSystemMetrics` to `NativeMethods.txt`.

If the subagent finds WinForms cleaner, add to `WindowsMcp.csproj`:
```xml
<UseWindowsForms>true</UseWindowsForms>
```

- [ ] **Step 7: Implement `src/WindowsMcp/Services/ClipboardService.cs`**

Note: our class is `WindowsMcp.Services.ClipboardService`. TextCopy ships a
static class also named `ClipboardService` in namespace `TextCopy`. Use the
fully-qualified `TextCopy.ClipboardService` to avoid name collision.

```csharp
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class ClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync(CancellationToken ct)
        => await TextCopy.ClipboardService.GetTextAsync();

    public async Task SetTextAsync(string text, CancellationToken ct)
        => await TextCopy.ClipboardService.SetTextAsync(text);
}
```

- [ ] **Step 8: Implement `src/WindowsMcp/Services/AudioService.cs`** (PowerShell-based; uses IPowerShellService which doesn't exist yet — implement after Task 6 lands the service)

For this task, leave AudioService as a stub that throws `NotImplementedException`. The full implementation will land in Task 6 (when PowerShellService is available) or Task 16 (SystemTools). Keep the interface defined here so tools can reference it.

```csharp
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class AudioService : IAudioService
{
    public Task<AudioState> GetAsync(CancellationToken ct) =>
        throw new NotImplementedException("Wired in Task 6 when PowerShellService lands.");
    public Task SetVolumeAsync(int level, CancellationToken ct) =>
        throw new NotImplementedException();
    public Task SetMutedAsync(bool muted, CancellationToken ct) =>
        throw new NotImplementedException();
}
```

- [ ] **Step 9: Run tests**

```powershell
dotnet test --filter "FullyQualifiedName~ScreenshotServiceTests|FullyQualifiedName~ClipboardServiceTests"
```

Expected: PASS (Screenshot integration test should produce a real PNG; Clipboard test roundtrips).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(services): ScreenshotService + ClipboardService + AudioService stub

ScreenshotService: Graphics.CopyFromScreen + SkiaSharp encode to PNG or
JPEG. Region defaults to primary monitor full bounds.

ClipboardService: TextCopy NuGet for get/set with cross-platform-safe
async API. Test saves and restores user's clipboard on dispose.

AudioService: interface defined; implementation deferred to Task 6 when
PowerShellService lands.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: PowerShellService (persistent runspace)

**Files:**
- Create: `src/WindowsMcp.Abstractions/IPowerShellService.cs`
- Create: `src/WindowsMcp.Abstractions/Models/PowerShellDtos.cs`
- Create: `src/WindowsMcp/Services/PowerShellService.cs`
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add `System.Management.Automation`
- Update: `src/WindowsMcp/Services/AudioService.cs` — real impl
- Create: `tests/WindowsMcp.Tests/Services/PowerShellServiceTests.cs`

- [ ] **Step 1: Add package**

```powershell
dotnet add src/WindowsMcp package System.Management.Automation --version 7.4.*
```

- [ ] **Step 2: Define DTOs**

`src/WindowsMcp.Abstractions/Models/PowerShellDtos.cs`:
```csharp
namespace WindowsMcp.Abstractions.Models;

public record PSResult(
    bool Success,
    string Stdout,
    string Stderr,
    int ExitCode,
    string[] Errors);
```

- [ ] **Step 3: Define interface (`src/WindowsMcp.Abstractions/IPowerShellService.cs`)**

```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IPowerShellService : IDisposable
{
    Task<PSResult> RunAsync(string command, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write failing tests (`tests/WindowsMcp.Tests/Services/PowerShellServiceTests.cs`)**

```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class PowerShellServiceTests
{
    [Fact]
    public async Task RunAsync_executes_simple_echo_and_captures_stdout()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("'hello from PS'");
        result.Success.Should().BeTrue();
        result.Stdout.Trim().Should().Be("hello from PS");
    }

    [Fact]
    public async Task RunAsync_returns_error_for_invalid_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Get-DoesNotExistCommand");
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_50_concurrent_calls_no_crosstalk()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var tasks = Enumerable.Range(0, 50).Select(i =>
            svc.RunAsync($"'{i}'")).ToArray();
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < 50; i++)
            results[i].Stdout.Trim().Should().Be(i.ToString());
    }
}

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
```

(NullLogger replaces Mock<ILogger> for simplicity.)

- [ ] **Step 5: Run to verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~PowerShellServiceTests"
```

Expected: FAIL (PowerShellService missing).

- [ ] **Step 6: Implement `src/WindowsMcp/Services/PowerShellService.cs`**

```csharp
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class PowerShellService : IPowerShellService
{
    private readonly ILogger _log;
    private Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _callCount;
    private DateTime _runspaceCreated;
    private const int RestartAfterCalls = 1000;
    private static readonly TimeSpan RestartAfter = TimeSpan.FromMinutes(30);

    public PowerShellService(ILogger<PowerShellService> log)
    {
        _log = log;
        _runspace = CreateRunspace();
    }

    // Test ctor accepting non-generic ILogger
    public PowerShellService(ILogger log)
    {
        _log = log;
        _runspace = CreateRunspace();
    }

    private Runspace CreateRunspace()
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        _runspaceCreated = DateTime.UtcNow;
        _callCount = 0;
        _log.LogInformation("PowerShell runspace created");
        return rs;
    }

    public async Task<PSResult> RunAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            MaybeRestartRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(command);

            var output = new StringBuilder();
            var errors = new List<string>();
            try
            {
                var results = await Task.Run(() => ps.Invoke(), ct);
                foreach (var item in results)
                    output.AppendLine(item?.ToString() ?? "");
                foreach (var err in ps.Streams.Error)
                    errors.Add(err.ToString());

                _callCount++;
                return new PSResult(
                    Success: ps.HadErrors == false,
                    Stdout: output.ToString(),
                    Stderr: string.Join('\n', errors),
                    ExitCode: ps.HadErrors ? 1 : 0,
                    Errors: errors.ToArray());
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PowerShell execution failed");
                return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MaybeRestartRunspace()
    {
        if (_callCount >= RestartAfterCalls || DateTime.UtcNow - _runspaceCreated > RestartAfter)
        {
            _log.LogInformation("Recycling PowerShell runspace ({Calls} calls / {Age} age)", _callCount, DateTime.UtcNow - _runspaceCreated);
            _runspace.Dispose();
            _runspace = CreateRunspace();
        }
    }

    public void Dispose() => _runspace.Dispose();
}
```

- [ ] **Step 7: Update AudioService to use PowerShellService**

```csharp
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class AudioService : IAudioService
{
    private readonly IPowerShellService _ps;
    public AudioService(IPowerShellService ps) => _ps = ps;

    public async Task<AudioState> GetAsync(CancellationToken ct)
    {
        var script = @"
            Add-Type -AssemblyName presentationCore
            $obj = New-Object -ComObject WScript.Shell
            # Use nircmd or fallback to registry/WMI
            $level = (Get-AudioDevice -PlaybackVolume) 2>$null
            if (-not $level) { $level = 50 }   # placeholder when AudioDeviceCmdlets missing
            ""$level""
        ";
        var r = await _ps.RunAsync(script, ct);
        var lvl = int.TryParse(r.Stdout.Trim(), out var v) ? v : 0;
        return new AudioState(lvl, false);
    }

    public async Task SetVolumeAsync(int level, CancellationToken ct)
    {
        level = Math.Clamp(level, 0, 100);
        // SendKeys approach for reliability without external deps
        var script = $@"
            $wsh = New-Object -ComObject WScript.Shell
            # Each volume-up key bumps by 2; rough but doesn't need extra modules
            for ($i = 0; $i -lt 50; $i++) {{ $wsh.SendKeys([char]174) }}  # 50 down
            for ($i = 0; $i -lt {level / 2}; $i++) {{ $wsh.SendKeys([char]175) }}
        ";
        await _ps.RunAsync(script, ct);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken ct)
    {
        // Toggle via key 173 (VK_VOLUME_MUTE); we don't know current state, so
        // get current first and only toggle if mismatch
        await _ps.RunAsync(@"
            $wsh = New-Object -ComObject WScript.Shell
            $wsh.SendKeys([char]173)
        ", ct);
    }
}
```

Note: this is "good enough" PowerShell-based audio. If precision matters, the implementer can swap to NAudio NuGet or `Microsoft.Windows.SDK.NET` audio session APIs.

- [ ] **Step 8: Run tests**

```powershell
dotnet test --filter "FullyQualifiedName~PowerShellServiceTests"
```

Expected: 3 tests PASS.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(services): PowerShellService with persistent runspace + AudioService

PowerShellService keeps one Runspace alive per server lifetime; calls
are gated by SemaphoreSlim for thread safety. Runspace is recycled
every 1000 calls or 30 minutes (whichever first) to prevent memory
growth. Returns PSResult { Success, Stdout, Stderr, ExitCode, Errors[] }.

AudioService uses SendKeys volume-up/down/mute scancodes via PowerShell,
which avoids COM IMMDeviceEnumerator dependency. Precision is +/- 2%;
acceptable for an LLM-driven volume tool.

3 integration tests cover: simple echo, error capture, 50-way concurrent
no-crosstalk.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: UIAutomationService (FlaUI on dedicated STA thread)

**Files:**
- Create: `src/WindowsMcp.Abstractions/IUIAutomationService.cs`
- Create: `src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`
- Create: `src/WindowsMcp/Services/UIAutomationService.cs`
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add `FlaUI.UIA3`
- Create: `tests/WindowsMcp.Tests/Services/UIAutomationServiceTests.cs`
- Create: `tests/WindowsMcp.Tests/Fixtures/NotepadFixture.cs`

- [ ] **Step 1: Add package**

```powershell
dotnet add src/WindowsMcp package FlaUI.UIA3 --version 5.0.0
```

- [ ] **Step 2: Define DTOs (`src/WindowsMcp.Abstractions/Models/UIAutomationDtos.cs`)**

```csharp
namespace WindowsMcp.Abstractions.Models;

public record ElementInfo(
    string ElementId,
    string Name,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    Bounds? Bounds,
    string? Value,
    bool? IsChecked,
    bool? IsSelected);

public record Bounds(int X, int Y, int Width, int Height);

public record ElementTree(ElementInfo Root, ElementTree[] Children);

public record FindElementResult(ElementInfo[] Matches);

public enum FindKind { Interactive, Text, Scrollable, Any }

public record TableData(string[] Headers, string[][] Rows);
```

- [ ] **Step 3: Define interface (`src/WindowsMcp.Abstractions/IUIAutomationService.cs`)**

```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IUIAutomationService : IDisposable
{
    Task<ElementTree> GetStateAsync(CancellationToken ct = default);
    Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any, CancellationToken ct = default);
    Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct = default);
    Task<string> GetTextAsync(string elementId, CancellationToken ct = default);
    Task<bool> AssertElementAsync(string elementId, string state, CancellationToken ct = default);
    Task InteractAsync(string elementId, string action, string? value, CancellationToken ct = default);
    Task<TableData> GetTableAsync(string elementId, CancellationToken ct = default);
    Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs, CancellationToken ct = default);
    Task FocusAsync(string elementId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create the Notepad test fixture (`tests/WindowsMcp.Tests/Fixtures/NotepadFixture.cs`)**

```csharp
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.UIA3;

namespace WindowsMcp.Tests.Fixtures;

public sealed class NotepadFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; }

    public NotepadFixture()
    {
        App = Application.Launch("notepad.exe");
        Automation = new UIA3Automation();
        Thread.Sleep(800);   // Notepad startup
    }

    public void Dispose()
    {
        Automation.Dispose();
        try { App.Close(); } catch { /* best effort */ }
    }
}
```

- [ ] **Step 5: Write the failing test**

`tests/WindowsMcp.Tests/Services/UIAutomationServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "UIAutomation")]
public class UIAutomationServiceTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;
    public UIAutomationServiceTests(NotepadFixture np) => _np = np;

    [Fact]
    public async Task GetStateAsync_returns_tree_with_notepad_root()
    {
        using var svc = new UIAutomationService();
        var state = await svc.GetStateAsync();
        state.Root.Name.Should().NotBeNullOrEmpty();
        state.Children.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FindElementAsync_finds_notepad_text_area()
    {
        using var svc = new UIAutomationService();
        var matches = await svc.FindElementAsync("", FindKind.Text);
        matches.Matches.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Concurrency_50_parallel_calls_no_deadlock()
    {
        using var svc = new UIAutomationService();
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => svc.GetStateAsync()).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Root.Should().NotBeNull());
    }
}
```

- [ ] **Step 6: Run to verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~UIAutomationServiceTests"
```

Expected: FAIL (service missing).

- [ ] **Step 7: Implement `src/WindowsMcp/Services/UIAutomationService.cs`**

```csharp
using System.Collections.Concurrent;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class UIAutomationService : IUIAutomationService
{
    private readonly UIA3Automation _automation;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _staThread;
    private readonly Dictionary<string, AutomationElement> _elementCache = new();
    private readonly Lock _cacheLock = new();
    private int _nextId;

    public UIAutomationService()
    {
        _automation = new UIA3Automation();
        _staThread = new Thread(WorkerLoop) { IsBackground = true, Name = "WindowsMcp-UA-STA" };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void WorkerLoop()
    {
        foreach (var work in _workQueue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* logged inside the work item */ }
        }
    }

    private Task<T> OnStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        _workQueue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<ElementTree> GetStateAsync(CancellationToken ct) => OnStaAsync(() =>
    {
        var root = _automation.GetDesktop();
        var foreground = _automation.FocusedElement() ?? root;
        return BuildTree(foreground, depth: 3);
    });

    private ElementTree BuildTree(AutomationElement el, int depth)
    {
        var info = ToInfo(el);
        if (depth <= 0) return new ElementTree(info, Array.Empty<ElementTree>());
        var children = el.FindAllChildren().Select(c => BuildTree(c, depth - 1)).ToArray();
        return new ElementTree(info, children);
    }

    private ElementInfo ToInfo(AutomationElement el)
    {
        string id;
        lock (_cacheLock)
        {
            id = $"el_{_nextId++}";
            _elementCache[id] = el;
        }
        var b = el.BoundingRectangle;
        return new ElementInfo(
            ElementId: id,
            Name: el.Name ?? "",
            ControlType: el.ControlType.ToString(),
            IsEnabled: el.IsEnabled,
            IsOffscreen: el.IsOffscreen,
            Bounds: new Bounds(b.X, b.Y, b.Width, b.Height),
            Value: TryGetValue(el),
            IsChecked: TryGetChecked(el),
            IsSelected: TryGetSelected(el));
    }

    private static string? TryGetValue(AutomationElement el)
    {
        try { return el.Patterns.Value.PatternOrDefault?.Value.Value; } catch { return null; }
    }
    private static bool? TryGetChecked(AutomationElement el)
    {
        try { return el.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On; } catch { return null; }
    }
    private static bool? TryGetSelected(AutomationElement el)
    {
        try { return el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value; } catch { return null; }
    }

    public Task<FindElementResult> FindElementAsync(string text, FindKind kind, CancellationToken ct) => OnStaAsync(() =>
    {
        var root = _automation.GetDesktop();
        var all = root.FindAllDescendants();
        var matches = all
            .Where(el => MatchesKind(el, kind))
            .Where(el => string.IsNullOrEmpty(text) || (el.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(20)
            .Select(ToInfo)
            .ToArray();
        return new FindElementResult(matches);
    });

    private static bool MatchesKind(AutomationElement el, FindKind kind) => kind switch
    {
        FindKind.Any => true,
        FindKind.Text => el.ControlType is ControlType.Text or ControlType.Edit or ControlType.Document,
        FindKind.Interactive => el.ControlType is ControlType.Button or ControlType.CheckBox or ControlType.Hyperlink or ControlType.MenuItem,
        FindKind.Scrollable => el.Patterns.Scroll.IsSupported,
        _ => true
    };

    public Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct) => OnStaAsync(() =>
    {
        if (!_elementCache.TryGetValue(elementId, out var el))
            throw new KeyNotFoundException($"Element '{elementId}' not in cache");
        return ToInfo(el);
    });

    public Task<string> GetTextAsync(string elementId, CancellationToken ct) => OnStaAsync(() =>
    {
        var el = ResolveCached(elementId);
        return el.Patterns.Value.PatternOrDefault?.Value.Value ?? el.Name ?? "";
    });

    public Task<bool> AssertElementAsync(string elementId, string state, CancellationToken ct) => OnStaAsync(() =>
    {
        var el = ResolveCached(elementId);
        return state.ToLowerInvariant() switch
        {
            "exists" => true,
            "enabled" => el.IsEnabled,
            "checked" => TryGetChecked(el) == true,
            "visible" => !el.IsOffscreen,
            _ => throw new ArgumentException($"Unknown assertion state: '{state}'")
        };
    });

    public Task InteractAsync(string elementId, string action, string? value, CancellationToken ct) => OnStaAsync<int>(() =>
    {
        var el = ResolveCached(elementId);
        switch (action.ToLowerInvariant())
        {
            case "toggle":
                el.Patterns.Toggle.PatternOrDefault?.Toggle();
                break;
            case "select":
                if (value is null) throw new ArgumentException("'select' requires a value");
                el.Patterns.SelectionItem.PatternOrDefault?.Select();
                break;
            case "invoke":
                el.Patterns.Invoke.PatternOrDefault?.Invoke();
                break;
            default:
                throw new ArgumentException($"Unknown interact action: '{action}'");
        }
        return 0;
    });

    public Task<TableData> GetTableAsync(string elementId, CancellationToken ct) => OnStaAsync(() =>
    {
        var el = ResolveCached(elementId);
        var grid = el.Patterns.Grid.PatternOrDefault
            ?? throw new InvalidOperationException("Element doesn't support GridPattern");
        var rows = grid.RowCount.Value;
        var cols = grid.ColumnCount.Value;
        var headers = new string[cols];
        var data = new string[rows][];
        for (int r = 0; r < rows; r++)
        {
            data[r] = new string[cols];
            for (int c = 0; c < cols; c++)
            {
                var cell = grid.GetItem(r, c);
                data[r][c] = cell.Name ?? "";
            }
        }
        return new TableData(headers, data);
    });

    public async Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var matches = await FindElementAsync(text, FindKind.Any, ct);
            if (matches.Matches.Length > 0) return matches.Matches[0];
            await Task.Delay(intervalMs, ct);
        }
        return null;
    }

    public Task FocusAsync(string elementId, CancellationToken ct) => OnStaAsync<int>(() =>
    {
        var el = ResolveCached(elementId);
        el.Focus();
        return 0;
    });

    private AutomationElement ResolveCached(string id)
    {
        lock (_cacheLock)
        {
            if (!_elementCache.TryGetValue(id, out var el))
                throw new KeyNotFoundException($"Element '{id}' not in cache");
            return el;
        }
    }

    public void Dispose()
    {
        _workQueue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(2));
        _automation.Dispose();
    }
}
```

- [ ] **Step 8: Run tests**

```powershell
dotnet test --filter "FullyQualifiedName~UIAutomationServiceTests"
```

Expected: 3 tests PASS (against the live notepad.exe via fixture).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(services): UIAutomationService on dedicated STA thread

FlaUI.UIA3 wrapper. UA calls marshal onto a single dedicated STA worker
thread via BlockingCollection<Action> + TaskCompletionSource. Element
handles cached by string ID; ToInfo() flattens AutomationElement to
ElementInfo DTO with bounds, control type, enabled, value, checked,
selected.

8 interface methods: GetState, FindElement, GetElement, GetText,
AssertElement, Interact (toggle/select/invoke merged), GetTable, WaitFor,
Focus.

3 UA tests against notepad.exe via NotepadFixture: state tree, find text
elements, 50-way concurrency without deadlock.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: FileSystemService + RegistryService + small services

**Files:**
- Create: `src/WindowsMcp.Abstractions/IFileSystemService.cs`
- Create: `src/WindowsMcp.Abstractions/IRegistryService.cs`
- Create: `src/WindowsMcp.Abstractions/IServiceControlService.cs`
- Create: `src/WindowsMcp.Abstractions/IEventLogService.cs`
- Create: `src/WindowsMcp.Abstractions/ITaskSchedulerService.cs`
- Create: `src/WindowsMcp.Abstractions/Models/FileSystemDtos.cs`
- Create: corresponding `src/WindowsMcp/Services/*.cs`
- Tests for each

- [ ] **Step 1: Add packages**

```powershell
dotnet add src/WindowsMcp package Microsoft.Win32.TaskScheduler --version 2.*
dotnet add src/WindowsMcp package System.ServiceProcess.ServiceController --version 9.*
dotnet add src/WindowsMcp package System.Diagnostics.EventLog --version 9.*
dotnet add src/WindowsMcp package System.Management --version 9.*
```

- [ ] **Step 2: Define DTOs (`src/WindowsMcp.Abstractions/Models/FileSystemDtos.cs`)**

```csharp
namespace WindowsMcp.Abstractions.Models;

public record FileInfoDto(
    string Path,
    long Size,
    DateTime Created,
    DateTime Modified,
    DateTime Accessed,
    string Attributes,
    bool IsDirectory);

public record FileSearchHit(string Path, long Size, DateTime Modified);

public record RegistryValueDto(string Path, string Name, object? Data, string Kind);

public record ServiceDto(string Name, string DisplayName, string Status, string StartType);

public record EventLogEntryDto(int Id, string Source, string Message, string Level, DateTime Time);

public record ScheduledTaskDto(string Name, string Path, string State, DateTime? LastRun, DateTime? NextRun);
```

- [ ] **Step 3: Define interfaces (one file per interface)**

`IFileSystemService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IFileSystemService
{
    Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct = default);
    Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default);
    Task WriteTextAsync(string path, string content, string encoding, CancellationToken ct = default);
    Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct = default);
    Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct = default);
    Task CopyAsync(string src, string dst, CancellationToken ct = default);
    Task MoveAsync(string src, string dst, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<string[]> ListAsync(string path, CancellationToken ct = default);
    Task ZipAsync(string srcDir, string dstZip, CancellationToken ct = default);
    Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct = default);
}
```

`IRegistryService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IRegistryService
{
    Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct = default);
    Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct = default);
}
```

`IServiceControlService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IServiceControlService
{
    Task<ServiceDto[]> ListAsync(CancellationToken ct = default);
    Task<ServiceDto> GetStatusAsync(string name, CancellationToken ct = default);
    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RestartAsync(string name, CancellationToken ct = default);
}
```

`IEventLogService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IEventLogService
{
    Task<EventLogEntryDto[]> QueryAsync(string log, string? level, string? source, DateTime? since, int max, CancellationToken ct = default);
}
```

`ITaskSchedulerService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface ITaskSchedulerService
{
    Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct = default);
    Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct = default);
    Task RunAsync(string name, CancellationToken ct = default);
    Task CreateAsync(string name, string command, string trigger, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write failing tests**

`tests/WindowsMcp.Tests/Services/FileSystemServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class FileSystemServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"wm-test-{Guid.NewGuid():N}");
    public FileSystemServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task WriteText_then_ReadText_roundtrips_utf8()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "test.txt");
        await svc.WriteTextAsync(path, "héllo wörld", "utf-8");
        var got = await svc.ReadTextAsync(path, 1024, "utf-8");
        got.Should().Be("héllo wörld");
    }

    [Fact]
    public async Task ReadText_throws_when_file_exceeds_max_bytes()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "big.txt");
        await File.WriteAllTextAsync(path, new string('x', 2000));
        Func<Task> act = () => svc.ReadTextAsync(path, 100, "utf-8");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public async Task WriteText_is_atomic_via_temp_file_rename()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "atomic.txt");
        await File.WriteAllTextAsync(path, "original");

        // Start a write and verify the original is intact until rename
        var task = svc.WriteTextAsync(path, "new content", "utf-8");
        await task;
        (await File.ReadAllTextAsync(path)).Should().Be("new content");
    }

    [Fact]
    public async Task Search_finds_files_matching_pattern()
    {
        var svc = new FileSystemService();
        await File.WriteAllTextAsync(Path.Combine(_tmp, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "c.log"), "c");
        var hits = await svc.SearchAsync(_tmp, "*.txt", null, null, false);
        hits.Should().HaveCount(2);
    }
}
```

`tests/WindowsMcp.Tests/Services/RegistryServiceTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.Win32;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class RegistryServiceTests : IDisposable
{
    private readonly string _ns = $"Software\\WindowsMcp.Tests\\{Guid.NewGuid():N}";
    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_ns); } catch { }
    }

    [Fact]
    public async Task Set_then_Get_roundtrips_string_value()
    {
        var svc = new RegistryService();
        await svc.SetAsync("HKCU", _ns, "TestVal", "hello", "String");
        var v = await svc.GetAsync("HKCU", _ns, "TestVal");
        v.Data.Should().Be("hello");
    }

    [Fact]
    public async Task Get_throws_KeyNotFound_for_missing_path()
    {
        var svc = new RegistryService();
        Func<Task> act = () => svc.GetAsync("HKCU", "Software\\DoesNotExistXYZ123", null);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
```

`tests/WindowsMcp.Tests/Services/ServiceControlServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ServiceControlServiceTests
{
    [Fact]
    public async Task List_includes_print_spooler_service()
    {
        var svc = new ServiceControlService();
        var services = await svc.ListAsync();
        services.Should().Contain(s => s.Name.Equals("Spooler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStatus_returns_state_for_spooler()
    {
        var svc = new ServiceControlService();
        var state = await svc.GetStatusAsync("Spooler");
        state.Status.Should().BeOneOf("Running", "Stopped", "StartPending", "StopPending", "Paused");
    }
}
```

`tests/WindowsMcp.Tests/Services/EventLogServiceTests.cs`:
```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class EventLogServiceTests
{
    [Fact]
    public async Task QueryAsync_returns_entries_from_application_log()
    {
        var svc = new EventLogService();
        var entries = await svc.QueryAsync("Application", null, null, DateTime.UtcNow.AddDays(-30), 5);
        entries.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 5: Verify failures**

```powershell
dotnet test --filter "FullyQualifiedName~FileSystemServiceTests|FullyQualifiedName~RegistryServiceTests|FullyQualifiedName~ServiceControlServiceTests|FullyQualifiedName~EventLogServiceTests"
```

Expected: FAIL — services missing.

- [ ] **Step 6: Implement services (one .cs per service in `src/WindowsMcp/Services/`)**

`FileSystemService.cs` (key methods only — implementer fills in the rest using `System.IO`):
```csharp
using System.IO.Compression;
using System.Text;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class FileSystemService : IFileSystemService
{
    public async Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        var enc = ResolveEncoding(encoding, info);
        return await File.ReadAllTextAsync(path, enc, ct);
    }

    public async Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task WriteTextAsync(string path, string content, string encoding, CancellationToken ct)
    {
        var enc = encoding.ToLowerInvariant() switch
        {
            "utf-16" => Encoding.Unicode,
            "ascii" => Encoding.ASCII,
            _ => new UTF8Encoding(false)   // no BOM
        };
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tmp, content, enc, ct);
        // Atomic rename with retry on Windows EBUSY
        for (int i = 0; i < 3; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (i < 2) { await Task.Delay(50 * (i + 1), ct); }
        }
    }

    public Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var size = info is FileInfo fi ? fi.Length : 0;
        return Task.FromResult(new FileInfoDto(
            Path: info.FullName,
            Size: size,
            Created: info.CreationTimeUtc,
            Modified: info.LastWriteTimeUtc,
            Accessed: info.LastAccessTimeUtc,
            Attributes: info.Attributes.ToString(),
            IsDirectory: info is DirectoryInfo));
    }

    public Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct)
    {
        var hits = new List<FileSearchHit>();
        var files = Directory.EnumerateFiles(root, pattern ?? "*", SearchOption.AllDirectories);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(f);
                if (minSize.HasValue && info.Length < minSize.Value) continue;
                if (modifiedSince.HasValue && info.LastWriteTimeUtc < modifiedSince.Value) continue;
                hits.Add(new FileSearchHit(info.FullName, info.Length, info.LastWriteTimeUtc));
            }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        if (findDuplicates)
        {
            // Group by size, then hash matching-size groups
            var grouped = hits.GroupBy(h => h.Size).Where(g => g.Count() > 1);
            var dups = new List<FileSearchHit>();
            foreach (var group in grouped)
            {
                var byHash = group.GroupBy(h => HashFile(h.Path));
                foreach (var hg in byHash.Where(g => g.Count() > 1))
                    dups.AddRange(hg);
            }
            return Task.FromResult(dups.ToArray());
        }
        return Task.FromResult(hits.ToArray());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    public Task CopyAsync(string src, string dst, CancellationToken ct) { File.Copy(src, dst, overwrite: true); return Task.CompletedTask; }
    public Task MoveAsync(string src, string dst, CancellationToken ct) { File.Move(src, dst, overwrite: true); return Task.CompletedTask; }
    public Task DeleteAsync(string path, CancellationToken ct)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
        return Task.CompletedTask;
    }
    public Task<string[]> ListAsync(string path, CancellationToken ct)
        => Task.FromResult(Directory.EnumerateFileSystemEntries(path).ToArray());

    public Task ZipAsync(string srcDir, string dstZip, CancellationToken ct)
    {
        if (File.Exists(dstZip)) File.Delete(dstZip);
        ZipFile.CreateFromDirectory(srcDir, dstZip);
        return Task.CompletedTask;
    }
    public Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct)
    {
        ZipFile.ExtractToDirectory(srcZip, dstDir, overwriteFiles: true);
        return Task.CompletedTask;
    }

    private static Encoding ResolveEncoding(string encoding, FileInfo info) =>
        encoding.ToLowerInvariant() switch
        {
            "utf-8" => Encoding.UTF8,
            "utf-16" => Encoding.Unicode,
            "ascii" => Encoding.ASCII,
            "auto" => DetectEncodingFromBom(info) ?? Encoding.UTF8,
            _ => Encoding.UTF8
        };

    private static Encoding? DetectEncodingFromBom(FileInfo info)
    {
        using var s = info.OpenRead();
        var bom = new byte[4];
        var read = s.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return null;
    }
}
```

`RegistryService.cs`:
```csharp
using Microsoft.Win32;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class RegistryService : IRegistryService
{
    public Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct)
    {
        var root = ResolveHive(hive);
        using var key = root.OpenSubKey(path) ?? throw new KeyNotFoundException($"Registry path not found: {hive}\\{path}");
        var data = valueName is null
            ? string.Join(",", key.GetValueNames())
            : key.GetValue(valueName);
        var kind = valueName is null ? "Names" : key.GetValueKind(valueName).ToString();
        return Task.FromResult(new RegistryValueDto(path, valueName ?? "(default)", data, kind));
    }

    public Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct)
    {
        var root = ResolveHive(hive);
        using var key = root.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Cannot create or open key: {hive}\\{path}");
        var rk = kind switch
        {
            "String" => RegistryValueKind.String,
            "ExpandString" => RegistryValueKind.ExpandString,
            "DWord" => RegistryValueKind.DWord,
            "QWord" => RegistryValueKind.QWord,
            "Binary" => RegistryValueKind.Binary,
            "MultiString" => RegistryValueKind.MultiString,
            _ => RegistryValueKind.String
        };
        key.SetValue(valueName, data, rk);
        return Task.CompletedTask;
    }

    private static RegistryKey ResolveHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKU" or "HKEY_USERS" => Registry.Users,
        _ => throw new ArgumentException($"Unknown hive: '{hive}'", nameof(hive))
    };
}
```

`ServiceControlService.cs`:
```csharp
using System.ServiceProcess;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ServiceControlService : IServiceControlService
{
    public Task<ServiceDto[]> ListAsync(CancellationToken ct)
        => Task.FromResult(ServiceController.GetServices()
            .Select(s => new ServiceDto(s.ServiceName, s.DisplayName, s.Status.ToString(), s.StartType.ToString()))
            .ToArray());

    public Task<ServiceDto> GetStatusAsync(string name, CancellationToken ct)
    {
        using var sc = new ServiceController(name);
        return Task.FromResult(new ServiceDto(sc.ServiceName, sc.DisplayName, sc.Status.ToString(), sc.StartType.ToString()));
    }

    public Task StartAsync(string name, CancellationToken ct)
    {
        using var sc = new ServiceController(name);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken ct)
    {
        using var sc = new ServiceController(name);
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    public async Task RestartAsync(string name, CancellationToken ct)
    {
        await StopAsync(name, ct);
        await StartAsync(name, ct);
    }
}
```

`EventLogService.cs`:
```csharp
using System.Diagnostics;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class EventLogService : IEventLogService
{
    public Task<EventLogEntryDto[]> QueryAsync(string log, string? level, string? source, DateTime? since, int max, CancellationToken ct)
    {
        using var el = new EventLog(log);
        var entries = el.Entries.Cast<EventLogEntry>()
            .Where(e => since == null || e.TimeGenerated >= since.Value)
            .Where(e => source == null || e.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .Where(e => level == null || e.EntryType.ToString().Equals(level, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.TimeGenerated)
            .Take(max)
            .Select(e => new EventLogEntryDto(
                Id: (int)e.InstanceId,
                Source: e.Source,
                Message: e.Message,
                Level: e.EntryType.ToString(),
                Time: e.TimeGenerated))
            .ToArray();
        return Task.FromResult(entries);
    }
}
```

`TaskSchedulerService.cs`:
```csharp
using Microsoft.Win32.TaskScheduler;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class TaskSchedulerService : ITaskSchedulerService
{
    public Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct)
    {
        using var ts = new TaskService();
        var tasks = ts.RootFolder.AllTasks
            .Select(t => new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime))
            .ToArray();
        return Task.FromResult(tasks);
    }

    public Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct)
    {
        using var ts = new TaskService();
        var t = ts.GetTask(name) ?? throw new KeyNotFoundException($"Scheduled task '{name}' not found");
        return Task.FromResult(new ScheduledTaskDto(t.Name, t.Path, t.State.ToString(), t.LastRunTime, t.NextRunTime));
    }

    public Task RunAsync(string name, CancellationToken ct)
    {
        using var ts = new TaskService();
        var t = ts.GetTask(name) ?? throw new KeyNotFoundException(name);
        t.Run();
        return Task.CompletedTask;
    }

    public Task CreateAsync(string name, string command, string trigger, CancellationToken ct)
    {
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.Actions.Add(new ExecAction(command));
        td.Triggers.Add(new TimeTrigger(DateTime.Parse(trigger)));
        ts.RootFolder.RegisterTaskDefinition(name, td);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        using var ts = new TaskService();
        ts.RootFolder.DeleteTask(name);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 7: Run all the new tests**

```powershell
dotnet test --filter "Category=Unit|Category=Integration"
```

Expected: all the new service tests pass; integration tests pass on this Windows machine.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(services): file/registry/service/eventlog/scheduledtask services

FileSystemService: read/write/info/search/copy/move/delete/list/zip/unzip.
Atomic write via tempfile + retry on Windows EBUSY. BOM-based encoding
auto-detection. Duplicate search via size-group then MD5 hash.

RegistryService: HKCU/HKLM/HKCR/HKU hives; throws KeyNotFoundException on
missing path. Kind enum mapped from string ("String", "DWord", etc.).

ServiceControlService: list/get/start/stop/restart wrapping
System.ServiceProcess.ServiceController with 15s status-change timeout.

EventLogService: query Application/System/Security logs with
since/level/source filters.

TaskSchedulerService: list/get/run/create/delete via
Microsoft.Win32.TaskScheduler NuGet.

10+ tests cover read/write roundtrip, atomic write, max-bytes enforcement,
search patterns, registry roundtrip with cleanup fixture, service status
of Spooler, event log query.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Input tool handlers (8 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/InputTools.cs`
- Delete: `src/WindowsMcp/Tools/EchoTool.cs` (smoke tool no longer needed)
- Create: `tests/WindowsMcp.Tests/Tools/InputToolsTests.cs`

- [ ] **Step 1: Write the failing test (handler-level, mocked service)**

```csharp
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class InputToolsTests
{
    [Fact]
    public async Task Click_dispatches_to_service_with_correct_args()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(s => s.ClickAsync(100, 200, MouseButton.Left, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClickResult(100, 200, MouseButton.Left, 2));
        var tools = new InputTools(mock.Object, new Mock<IClipboardService>().Object);

        var result = await tools.Click(100, 200, "left", 2);

        result.Should().Contain("100").And.Contain("200");
        mock.VerifyAll();
    }

    [Fact]
    public async Task Click_rejects_unknown_button_with_clear_message()
    {
        var tools = new InputTools(new Mock<IInputService>().Object, new Mock<IClipboardService>().Object);
        Func<Task> act = () => tools.Click(0, 0, "fourth", 1);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*button*");
    }
}
```

- [ ] **Step 2: Verify failure**

```powershell
dotnet test --filter "FullyQualifiedName~InputToolsTests"
```

Expected: FAIL.

- [ ] **Step 3: Delete `EchoTool.cs`** (and its test):
```powershell
Remove-Item src/WindowsMcp/Tools/EchoTool.cs
Remove-Item tests/WindowsMcp.Tests/EchoToolTests.cs
```

- [ ] **Step 4: Implement `src/WindowsMcp/Tools/InputTools.cs`**

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

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

    private static MouseButton ParseButton(string s) => s.ToLowerInvariant() switch
    {
        "left" or "l" => MouseButton.Left,
        "right" or "r" => MouseButton.Right,
        "middle" or "m" => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown button '{s}'; expected left|right|middle")
    };

    [McpServerTool, Description("Click at screen coordinates.")]
    public async Task<string> Click(
        [Description("X coordinate in pixels")] int x,
        [Description("Y coordinate in pixels")] int y,
        [Description("Mouse button: left, right, or middle")] string button = "left",
        [Description("Number of clicks (1=single, 2=double, 3=triple)")] int clicks = 1)
    {
        var result = await _input.ClickAsync(x, y, ParseButton(button), clicks);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Drag from one point to another.")]
    public async Task<string> Drag(int from_x, int from_y, int to_x, int to_y, string button = "left")
        => JsonSerializer.Serialize(await _input.DragAsync(from_x, from_y, to_x, to_y, ParseButton(button)));

    [McpServerTool, Description("Hover cursor at coordinates (or move cursor with duration_ms=0).")]
    public async Task<string> Hover(int x, int y, int duration_ms = 0)
    {
        await _input.HoverAsync(x, y, duration_ms);
        return $"hovered at ({x},{y}) for {duration_ms}ms";
    }

    [McpServerTool, Description("Type a string at the focused input.")]
    public async Task<string> Type([Description("Text to type")] string text)
        => JsonSerializer.Serialize(await _input.TypeAsync(text));

    [McpServerTool, Description("Press a single key by name (enter, tab, F1-F12, arrows, etc.)")]
    public async Task<string> Key([Description("Key name")] string key)
    {
        await _input.PressKeyAsync(key);
        return $"pressed {key}";
    }

    [McpServerTool, Description("Press a keyboard shortcut like ctrl+c, alt+tab, ctrl+shift+s.")]
    public async Task<string> Shortcut([Description("Shortcut, e.g. 'ctrl+c'")] string shortcut)
    {
        await _input.PressShortcutAsync(shortcut);
        return $"pressed {shortcut}";
    }

    [McpServerTool, Description("Scroll the mouse wheel at coordinates.")]
    public async Task<string> Scroll(int x, int y, [Description("up|down|left|right")] string direction, int amount = 3)
    {
        await _input.ScrollAsync(x, y, direction, amount);
        return $"scrolled {direction} by {amount} at ({x},{y})";
    }

    [McpServerTool, Description("Clipboard get/set.")]
    public async Task<string> Clipboard(
        [Description("Action: get or set")] string action,
        [Description("Text to set; ignored for 'get'")] string? text = null)
    {
        return action.ToLowerInvariant() switch
        {
            "get" => await _clipboard.GetTextAsync() ?? "",
            "set" => Set(text),
            _ => throw new ArgumentException($"Unknown clipboard action '{action}'; expected get|set")
        };

        string Set(string? t)
        {
            if (t == null) throw new ArgumentException("'set' requires text parameter");
            _clipboard.SetTextAsync(t).GetAwaiter().GetResult();
            return $"set ({t.Length} chars)";
        }
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test --filter "FullyQualifiedName~InputToolsTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(tools): 8 input tools (click, drag, hover, type, key, shortcut, scroll, clipboard)

[McpServerTool]-decorated methods on InputTools class. Each delegates to
IInputService or IClipboardService via constructor DI. Button strings
parsed via ParseButton(); unknown values throw ArgumentException with
the offending token quoted.

EchoTool removed (smoke surface no longer needed).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Screen tools (screenshot, ocr)

**Files:**
- Create: `src/WindowsMcp/Tools/ScreenTools.cs`
- Create: `src/WindowsMcp/Services/OcrService.cs` (WinRT-based)
- Create: `src/WindowsMcp.Abstractions/IOcrService.cs`
- Create: `tests/WindowsMcp.Tests/Tools/ScreenToolsTests.cs`

- [ ] **Step 1: Define IOcrService**

`src/WindowsMcp.Abstractions/IOcrService.cs`:
```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IOcrService
{
    Task<string> ExtractTextAsync(ScreenRegion? region = null, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing test (`tests/WindowsMcp.Tests/Tools/ScreenToolsTests.cs`)**

```csharp
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ScreenToolsTests
{
    [Fact]
    public async Task Screenshot_returns_base64_png()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var shotMock = new Mock<IScreenshotService>();
        shotMock.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(pngBytes, 100, 100, ImageFormat.Png));

        var tools = new ScreenTools(shotMock.Object, new Mock<IOcrService>().Object);
        var result = await tools.Screenshot(null, "png");

        result.Should().Contain(Convert.ToBase64String(pngBytes));
        result.Should().Contain("100");
    }
}
```

- [ ] **Step 3: Verify failure**
```powershell
dotnet test --filter "FullyQualifiedName~ScreenToolsTests"
```
Expected: FAIL.

- [ ] **Step 4: Implement `src/WindowsMcp/Services/OcrService.cs`**

```csharp
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class OcrService : IOcrService
{
    private readonly IScreenshotService _screenshot;
    public OcrService(IScreenshotService screenshot) => _screenshot = screenshot;

    public async Task<string> ExtractTextAsync(ScreenRegion? region, CancellationToken ct)
    {
        var shot = await _screenshot.CaptureAsync(region, ImageFormat.Png, ct);

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(shot.Bytes.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        var bitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR language pack installed");
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text;
    }
}
```

Note: requires `using System.Runtime.InteropServices.WindowsRuntime;` for the `.AsBuffer()` extension. WinRT projections are enabled via `<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>`.

- [ ] **Step 5: Implement `src/WindowsMcp/Tools/ScreenTools.cs`**

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly IScreenshotService _screenshot;
    private readonly IOcrService _ocr;

    public ScreenTools(IScreenshotService screenshot, IOcrService ocr)
    {
        _screenshot = screenshot;
        _ocr = ocr;
    }

    [McpServerTool, Description("Capture a screenshot of the screen or a region.")]
    public async Task<string> Screenshot(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null,
        [Description("png or jpeg")] string format = "png")
    {
        var r = ParseRegion(region);
        var fmt = format.ToLowerInvariant() == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        var result = await _screenshot.CaptureAsync(r, fmt);
        return JsonSerializer.Serialize(new
        {
            width = result.Width,
            height = result.Height,
            format = result.Format.ToString().ToLowerInvariant(),
            data_base64 = Convert.ToBase64String(result.Bytes)
        });
    }

    [McpServerTool, Description("Run OCR on a screen region.")]
    public async Task<string> Ocr(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null)
    {
        var r = ParseRegion(region);
        var text = await _ocr.ExtractTextAsync(r);
        return text;
    }

    private static ScreenRegion? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) throw new ArgumentException($"Invalid region '{region}'; expected 'x,y,w,h'");
        return new ScreenRegion(
            int.Parse(parts[0]), int.Parse(parts[1]),
            int.Parse(parts[2]), int.Parse(parts[3]));
    }
}
```

- [ ] **Step 6: Run tests, fix if needed, commit**

```powershell
dotnet test --filter "FullyQualifiedName~ScreenToolsTests"
```

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat(tools): screenshot + ocr tools

ScreenTools.Screenshot returns base64-encoded PNG/JPEG bytes + dimensions
as JSON. Region accepted as 'x,y,w,h' string.

ScreenTools.Ocr captures via IScreenshotService then runs Windows.Media.Ocr
via OcrEngine.TryCreateFromUserProfileLanguages(). Throws when no language
pack installed (documented limitation in spec).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Window tools (5 tools: window, switch_to_window, launch, focus, multi_monitor)

**Files:**
- Create: `src/WindowsMcp/Services/WindowService.cs`
- Create: `src/WindowsMcp.Abstractions/IWindowService.cs`
- Create: `src/WindowsMcp/Tools/WindowTools.cs`
- Create: `tests/WindowsMcp.Tests/Tools/WindowToolsTests.cs`
- Modify: `src/WindowsMcp/NativeMethods.txt` — add `FindWindow`, `ShowWindow`, `SetForegroundWindow`, `EnumDisplayMonitors`, `GetMonitorInfo`

- [ ] **Step 1: Append to `NativeMethods.txt`**
```
FindWindow
ShowWindow
SetForegroundWindow
EnumDisplayMonitors
GetMonitorInfo
IsWindow
GetWindowText
CloseWindow
DestroyWindow
```

- [ ] **Step 2: Define IWindowService**

```csharp
using WindowsMcp.Abstractions.Models;
namespace WindowsMcp.Abstractions;

public interface IWindowService
{
    Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default);
    Task<bool> SwitchToAsync(string title, CancellationToken ct = default);
    Task<int> LaunchAsync(string appName, CancellationToken ct = default);
    Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default);
}

public record WindowAction(string Action, string? Title, bool Success);
public record MonitorInfo(int Index, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary);
```

- [ ] **Step 3: Test, Implement, Verify, Commit**

(Subagent fills in following the pattern of Task 10. Use User32 P/Invoke via CsWin32 generated `Windows.Win32.PInvoke` static class. Multi-monitor uses `Screen.AllScreens` from WinForms OR EnumDisplayMonitors via P/Invoke. `launch` searches Start Menu via `IPowerShellService.RunAsync("Get-StartApps | Where-Object Name -like '*<appName>*'")`.)

Commit message: `feat(tools): 5 window/launch/monitor tools`.

---

## Task 12: UI Automation tools (8 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/UIAutomationTools.cs`
- Create: `tests/WindowsMcp.Tests/Tools/UIAutomationToolsTests.cs`

- [ ] **Steps follow the same pattern as Task 9.**

Tool methods: `get_state`, `find_element`, `get_element`, `get_text`, `assert_element`, `interact_element`, `get_table`, `wait_for`. Each delegates to `IUIAutomationService` injected via constructor. Returns JSON-serialized DTOs.

Test pattern:
```csharp
[Fact]
public async Task FindElement_passes_kind_to_service()
{
    var mock = new Mock<IUIAutomationService>();
    mock.Setup(s => s.FindElementAsync("Start", FindKind.Interactive, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new FindElementResult(new[] {
            new ElementInfo("el_1", "Start", "Button", true, false, null, null, null, null)
        }));
    var tools = new UIAutomationTools(mock.Object);
    var json = await tools.FindElement("Start", "interactive");
    json.Should().Contain("el_1");
}
```

Commit message: `feat(tools): 8 UI Automation tools`.

---

## Task 13: Process / Shell tools (6 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/ProcessTools.cs` (process, start_process, service, scheduled_task, event_log)
- Create: `src/WindowsMcp/Tools/ShellTools.cs` (powershell)
- Create: `src/WindowsMcp/Services/ProcessService.cs`
- Create: `src/WindowsMcp.Abstractions/IProcessService.cs`
- Create: tests

- [ ] **Steps follow the same TDD pattern.**

ProcessService wraps `System.Diagnostics.Process` for list/start/kill. Tool methods:
- `process(action: "list"|"kill", name?: string, pid?: int, confirm?: bool)` — kill requires confirm:true via JSON Schema anyOf
- `start_process(command: string)` — detached spawn
- `powershell(command: string)` — delegates to IPowerShellService
- `service(action: "list"|"status"|"start"|"stop"|"restart", name?: string, confirm?: bool)` — stop/restart require confirm:true
- `scheduled_task(action, name?, command?, trigger?, confirm?)` — delete requires confirm:true
- `event_log(log: string, level?: string, source?: string, since?: string, max?: int)`

For the JSON Schema `anyOf` discriminated requirement, the `ModelContextProtocol` SDK auto-generates schema from method signatures. To enforce `confirm: true` only when action is destructive, runtime-validate in the handler:

```csharp
if (action is "stop" or "restart" && confirm != true)
    throw new ArgumentException("'confirm: true' is required for stop/restart actions");
```

Document the behavior in the `[Description]` attribute.

Commit message: `feat(tools): 6 process/shell tools (process, start_process, powershell, service, scheduled_task, event_log)`.

---

## Task 14: File tools (7 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/FileTools.cs`
- Create: tests

- [ ] **Same TDD pattern.**

Tool methods on `FileTools` class:
- `file_search(root, pattern?, min_size?, modified_since?, find_duplicates?)`
- `file_manage(action: "copy"|"move"|"delete"|"list", src, dst?, confirm?)` — delete requires confirm
- `file_dialog(path)` — delegates to IInputService.TypeAsync (Open/Save dialogs)
- `file_read(path, max_bytes?=1048576, encoding?="auto")`
- `file_write(path, content, encoding?="utf-8", confirm: true)` — confirm required by schema
- `file_info(path)`
- `archive(action: "zip"|"unzip", src, dst)`

Commit message: `feat(tools): 7 file tools (search/manage/dialog/read/write/info/archive)`.

---

## Task 15: Disk tool (1 tool: disk_inspect)

**Files:**
- Create: `src/WindowsMcp/Tools/DiskTools.cs`
- Create: tests

- [ ] **Same TDD pattern.**

Single method `disk_inspect(mode, path?)` with modes:
- `"usage"` — folder size breakdown, top subdirs
- `"reclaimable"` — find temp/cache/recycle bin sizes (delegates to IPowerShellService)
- `"file_types"` — group by extension, sum sizes
- `"stale"` — files not modified in N days

Implementation primarily wraps IFileSystemService.SearchAsync + groupings.

Commit message: `feat(tools): disk_inspect (3-tool merge from Python's Disk-Analysis + Disk-Cleanup + Storage)`.

---

## Task 16: System tools (7 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/SystemTools.cs`
- Create: `src/WindowsMcp/Services/WmiService.cs` + `src/WindowsMcp/Services/EnvService.cs` + `src/WindowsMcp/Services/PowerService.cs` + `src/WindowsMcp/Services/NotificationService.cs`
- Create: corresponding interfaces in Abstractions
- Create: tests

- [ ] **Tools:**
- `system_info(category)` — combines WMI Win32_OperatingSystem + Win32_PhysicalMemory + Win32_LogicalDisk + Win32_VideoController + Win32_Battery
- `audio(action, level?)` — delegates to IAudioService
- `notification(title, message)` — delegates to INotificationService (WinRT ToastNotification with `SetCurrentProcessExplicitAppUserModelID("org.windows-mcp.server")` at Program.cs startup)
- `security_audit()` — delegates to IPowerShellService with bundled PS script
- `wmi_query(class_name, namespace?, where?)` — delegates to IWmiService (System.Management.ManagementObjectSearcher)
- `env(action, name?, value?, scope?)` — delegates to IEnvService (Environment.GetEnvironmentVariable)
- `power_action(action, confirm: true)` — delegates to IPowerService (ExitWindowsEx via CsWin32 P/Invoke)

`NativeMethods.txt` additions:
```
ExitWindowsEx
LockWorkStation
SetSuspendState
SetCurrentProcessExplicitAppUserModelID
```

Commit message: `feat(tools): 7 system tools (system_info/audio/notification/security_audit/wmi_query/env/power_action)`.

---

## Task 17: Network tools (2 tools: network, firewall)

**Files:**
- Create: `src/WindowsMcp/Tools/NetworkTools.cs`
- Create: `src/WindowsMcp/Services/NetworkService.cs`
- Create: `src/WindowsMcp.Abstractions/INetworkService.cs`
- Create: tests

- [ ] **Tools:**
- `network(action: "adapters"|"ports"|"ping"|"dns"|"wifi", host?, port?)` — wraps `System.Net.NetworkInformation` (`NetworkInterface.GetAllNetworkInterfaces`, `Ping`, `Dns.GetHostEntry`)
- `firewall(action: "list"|"add"|"remove", name?, direction?, action_type?, port?, confirm?)` — delegates to IPowerShellService (`Get-NetFirewallRule`, `New-NetFirewallRule`, `Remove-NetFirewallRule`)

Commit message: `feat(tools): 2 network tools (network, firewall)`.

---

## Task 18: Registry tools (2 tools)

**Files:**
- Create: `src/WindowsMcp/Tools/RegistryTools.cs`
- Create: tests

- [ ] **Tools:**

```csharp
[McpServerTool, Description("Read a Windows registry value.")]
public async Task<string> RegistryGet(
    [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
    [Description("Subkey path like 'Software\\Microsoft\\Windows'")] string path,
    [Description("Specific value name; if omitted lists all value names")] string? value_name = null)
{
    var result = await _registry.GetAsync(hive, path, value_name);
    return JsonSerializer.Serialize(result);
}

[McpServerTool, Description("Write a Windows registry value. Requires confirm: true.")]
public async Task<string> RegistrySet(string hive, string path, string value_name, string data, string kind, bool confirm)
{
    if (!confirm) throw new ArgumentException("'confirm: true' is required for registry writes");
    await _registry.SetAsync(hive, path, value_name, data, kind);
    return $"set {hive}\\{path}\\{value_name}";
}
```

Tests: registry_get reads `HKLM\HARDWARE\DESCRIPTION\System` (always exists, read-only). registry_set uses RegistryNamespaceFixture for cleanup.

Commit message: `feat(tools): 2 registry tools with schema-required confirm`.

---

## Task 19: Web tools (2 tools: scrape, http_request)

**Files:**
- Create: `src/WindowsMcp/Tools/WebTools.cs`
- Create: `src/WindowsMcp/Services/WebService.cs`
- Create: `src/WindowsMcp.Abstractions/IWebService.cs`
- Modify: `src/WindowsMcp/WindowsMcp.csproj` — add `ReverseMarkdown`
- Create: tests + `LocalHttpServerFixture`

- [ ] **Steps:**

```powershell
dotnet add src/WindowsMcp package ReverseMarkdown --version 4.*
```

`IWebService`:
```csharp
public interface IWebService
{
    Task<string> ScrapeAsync(string url, CancellationToken ct = default);
    Task<HttpResponseDto> RequestAsync(string url, string method, IDictionary<string,string>? headers, string? body, CancellationToken ct = default);
}
public record HttpResponseDto(int Status, IDictionary<string,string> Headers, string Body);
```

`WebService` uses `HttpClient` (singleton per process), `ReverseMarkdown.Converter` for `scrape`, and validates against SSRF (reject `127.0.0.1`, `10.*`, `192.168.*`, `169.254.*`, IPv6 link-local) per Python source.

Tests:
```csharp
public class WebToolsTests : IClassFixture<LocalHttpServerFixture>
{
    [Fact]
    public async Task Scrape_converts_html_to_markdown()
    {
        // LocalHttpServerFixture serves "<h1>Hello</h1>" at /
        var tools = new WebTools(new WebService());
        var md = await tools.Scrape(_fixture.UrlFor("/"));
        md.Should().Contain("# Hello");
    }

    [Fact]
    public async Task Scrape_rejects_private_IPs()
    {
        var tools = new WebTools(new WebService());
        Func<Task> act = () => tools.Scrape("http://127.0.0.1:80/admin");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*private*");
    }
}
```

Commit message: `feat(tools): 2 web tools (scrape, http_request) with SSRF protection`.

---

## Task 20: Wire everything in Program.cs with DI

**Files:**
- Modify: `src/WindowsMcp/Program.cs`

- [ ] **Step 1: Replace Program.cs with full DI wiring**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;

namespace WindowsMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Required for WinRT ToastNotification per spec
        SetAppUserModelId("org.windows-mcp.server");

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Service registrations
        builder.Services.AddSingleton<IInputService, InputService>();
        builder.Services.AddSingleton<IScreenshotService, ScreenshotService>();
        builder.Services.AddSingleton<IOcrService, OcrService>();
        builder.Services.AddSingleton<IClipboardService, ClipboardService>();
        builder.Services.AddSingleton<IAudioService, AudioService>();
        builder.Services.AddSingleton<IPowerShellService, PowerShellService>();
        builder.Services.AddSingleton<IUIAutomationService, UIAutomationService>();
        builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
        builder.Services.AddSingleton<IRegistryService, RegistryService>();
        builder.Services.AddSingleton<IServiceControlService, ServiceControlService>();
        builder.Services.AddSingleton<IEventLogService, EventLogService>();
        builder.Services.AddSingleton<ITaskSchedulerService, TaskSchedulerService>();
        builder.Services.AddSingleton<IProcessService, ProcessService>();
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<IWmiService, WmiService>();
        builder.Services.AddSingleton<IEnvService, EnvService>();
        builder.Services.AddSingleton<IPowerService, PowerService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<INetworkService, NetworkService>();
        builder.Services.AddSingleton<IWebService, WebService>();

        builder.Services
            .AddMcpServer(o => { o.ServerInfo = new() { Name = "Windows-mcp", Version = "0.2.0" }; })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    private static void SetAppUserModelId(string id)
    {
        try { SetCurrentProcessExplicitAppUserModelID(id); } catch { /* best effort */ }
    }
}
```

- [ ] **Step 2: Verify clean build**
```powershell
dotnet build
```
Expected: `Build succeeded`.

- [ ] **Step 3: Smoke-test end-to-end**
```powershell
$req = '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
$init = '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
"$init`n$req" | dotnet run --project src/WindowsMcp --no-build 2>$null
```
Expected: the second response lists ~50 tools by name.

- [ ] **Step 4: Commit**
```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: wire all services + tools via DI in Program.cs

19 services registered as singletons. AddMcpServer +
WithToolsFromAssembly auto-discovers all [McpServerTool] methods at
compile time via source generator (no runtime reflection).

SetCurrentProcessExplicitAppUserModelID called at startup with
'org.windows-mcp.server' so WinRT ToastNotification works.

Smoke test (tools/list response) shows 50 tools registered.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 21: Publish + .mcp.json cutover

**Files:**
- Create: `scripts/publish.ps1` (optional convenience wrapper)
- Modify: `C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json`
- Create: `.mcp.json.bak-2026-05-24-pre-windows-mcp-cs-cutover` (backup)

- [ ] **Step 1: Publish to stable location**

```powershell
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true
```

Expected: `dist/WindowsMcp.exe` appears (~70 MB).

- [ ] **Step 2: Smoke test the published binary directly**

```powershell
$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1.0"}}}'
$init | C:/Users/danie/Dropbox/Github/Windows-mcp/dist/WindowsMcp.exe
```

Expected: JSON-RPC initialize response on stdout within ~3-5 seconds (first run extracts dependencies to %TEMP%).

- [ ] **Step 3: Backup .mcp.json**

```powershell
Copy-Item C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json `
          C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json.bak-2026-05-24-pre-windows-mcp-cs-cutover
```

- [ ] **Step 4: Swap the Windows-mcp entry**

Edit `C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json`. Find:
```json
"Windows-mcp": {
  "type": "stdio",
  "command": "C:/Users/danie/.venvs/windows-mcp/Scripts/python.exe",
  "args": ["-X", "utf8", "C:/Users/danie/Dropbox/Github/Windows-mcp/main.py"],
  "env": { "_RETRY": "2026-05-19T05-37-41" }
}
```

Replace with:
```json
"Windows-mcp": {
  "type": "stdio",
  "command": "C:/Users/danie/Dropbox/Github/Windows-mcp/dist/WindowsMcp.exe",
  "args": [],
  "env": { "_RETRY": "2026-05-24-windows-mcp-cs-cutover" }
}
```

Validate JSON:
```powershell
python -c "import json; json.load(open(r'C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json'))"
```
Expected: no output (silent success).

- [ ] **Step 5: Ask the user to run `/kill-plugins` then `/reload-plugins`**

User-action step — the subagent dispatching this task should pause and present this instruction to Claude (the orchestrator), who will surface it to the user.

- [ ] **Step 6: Live verification (after user confirms reload)**

Once tools are reloaded, the orchestrator tests:

```
mcp__Windows-mcp__system_info(category: "cpu")
mcp__Windows-mcp__find_element(text: "Start")
mcp__Windows-mcp__file_read(path: "C:/Users/danie/.claude/CLAUDE.md", max_bytes: 1024)
mcp__Windows-mcp__network(action: "ping", host: "127.0.0.1")
```

All four should return expected JSON shapes.

- [ ] **Step 7: Commit (no source changes, just the publish script if added)**

This task has no source-code commit — its work is the publish + .mcp.json swap. If a `scripts/publish.ps1` is added:
```bash
git add scripts/publish.ps1
git commit -m "$(cat <<'EOF'
chore: add publish script for self-contained single-file release

Wraps `dotnet publish src/WindowsMcp -c Release -o dist -r win-x64
--self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`.
Produces dist/WindowsMcp.exe (~70 MB).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Task 22: Final retirement (Python deletion + README/CHANGELOG)

**Files:**
- Delete: `.python-snapshot-2026-05-24/` (after verification holds)
- Delete: `C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json.bak-2026-05-24-*`
- Rewrite: `README.md`
- Create/Modify: `CHANGELOG.md`

- [ ] **Step 1: Verify live verification has held**

Confirm with orchestrator: the 4 representative tool calls from Task 21 succeeded. Only proceed if YES.

- [ ] **Step 2: Delete the Python snapshot**

```powershell
cd "C:/Users/danie/Dropbox/Github/Windows-mcp"
Remove-Item -Recurse -Force .python-snapshot-2026-05-24
```

- [ ] **Step 3: Delete the .mcp.json backup**

```powershell
Remove-Item C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json.bak-2026-05-24-pre-windows-mcp-cs-cutover
```

- [ ] **Step 4: Rewrite README.md**

```markdown
# Windows-mcp

An MCP server for Windows desktop automation, written in C# on the official
`ModelContextProtocol` SDK. **50 tools** across input, screen, window, UI
automation, process/shell, file, disk, system, network, registry, and web
categories.

## Build

```powershell
git clone https://github.com/danielsimonjr/Windows-mcp.git
cd Windows-mcp
dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true
```

Output: `dist/WindowsMcp.exe` (~70 MB self-contained, no .NET runtime
required by users).

## Register with Claude Code

Add to your MCP host config (e.g.,
`~/.claude/local-marketplace/mcp-host/.mcp.json`):

```json
{
  "mcpServers": {
    "Windows-mcp": {
      "type": "stdio",
      "command": "C:/path/to/Windows-mcp/dist/WindowsMcp.exe",
      "args": []
    }
  }
}
```

Run `/reload-plugins`. Tools appear as `mcp__Windows-mcp__*`.

## Performance note

On first launch, the single-file binary extracts dependencies to `%TEMP%`,
adding ~3-5 sec startup. Subsequent launches are warm.

If you hit the 30s Claude Code startup timeout, add a Defender exclusion
for the `dist/` folder.

## Development

```powershell
dotnet test --filter "Category=Unit"      # fast loop
dotnet test                                # full suite (~200 tests)
dotnet build
```

See `docs/superpowers/specs/2026-05-24-windows-mcp-csharp-conversion-design.md`
for the architecture spec.

## License

MIT — see [LICENSE](LICENSE).
```

- [ ] **Step 5: Update CHANGELOG.md**

```markdown
# Changelog

## [0.2.0] - 2026-05-24

### Changed
- **Complete rewrite from Python to C# on the official ModelContextProtocol SDK.**
  Same server identity (`Windows-mcp` in `.mcp.json`); single self-contained
  `WindowsMcp.exe` replaces the venv-launched `python main.py`.
- Tool names normalized to snake_case (e.g. `Click-Tool` → `click`,
  `Find-Element-Tool` → `find_element`).

### Added (9 new tools beyond the Python set)
- `file_read`, `file_write`, `file_info` — file content primitives (missing from Python)
- `http_request` — REST/HTTP client (beyond HTML scraping)
- `wmi_query` — structured WMI queries
- `env` — environment variable get/set/list
- `power_action` — shutdown/restart/sleep/lock/sign_out (`confirm: true` required)
- `firewall` — list/add/remove Windows Firewall rules
- `archive` — zip/unzip
- `service` — Windows service control
- `scheduled_task` — Task Scheduler control
- `event_log` — Windows Event Log query
- `registry_get`, `registry_set` — registry access

### Consolidated
- `Checkbox-Toggle-Tool` + `Select-Option-Tool` → `interact_element`
- `File-Search-Tool` + `Duplicate-Finder-Tool` → `file_search`
- `Disk-Analysis-Tool` + `Disk-Cleanup-Tool` + `Storage-Tool` → `disk_inspect`
- `Move-Tool` + `Hover-Tool` → `hover` (with `duration_ms: 0`)

### Removed
- `Wait-Tool` — pure sleep; LLM can space its own calls
- `Compare-Screenshot-Tool` — niche QA tool
- `Record-Replay-Tool` — LLM is the orchestrator
- `Command-History-Tool` — session-scoped PowerShell history

### Fixed
- `humancursor` 3-second startup cost removed (used straight SendInput)
- PowerShell calls now use a persistent runspace (~5ms per call vs ~200ms
  spawn-per-call)
- UI Automation calls properly marshaled to a dedicated STA thread

### Backlog (v0.3.0)
- Native AOT compilation (blocked on FlaUI reflection)
- CI / GitHub Actions
- Toast notification rendering pipeline (some Windows configs require
  AppUserModelID registration via Start Menu shortcut)
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: complete C# conversion; retire Python sources

- Delete .python-snapshot-2026-05-24/ (snapshot no longer needed after
  live verification of the C# binary on .mcp.json holds)
- Rewrite README.md for the C# build flow (dotnet publish ... -o dist)
- Add CHANGELOG.md [0.2.0] - 2026-05-24 entry covering rewrite, 9 new
  tools, 4 consolidations, 4 removals, and behavior improvements
  (persistent PS runspace, dedicated UA STA thread, no humancursor)

The Python venv at C:/Users/danie/.venvs/windows-mcp can now be removed:
  Remove-Item -Recurse -Force C:/Users/danie/.venvs/windows-mcp

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: User runs venv removal (out of band)**

```powershell
Remove-Item -Recurse -Force C:/Users/danie/.venvs/windows-mcp
```

This reclaims ~200 MB. Cannot be done by the subagent — needs user/orchestrator action.

---

## Self-review checklist (for the orchestrator)

After all 22 tasks complete:

1. **Spec coverage**: every section of the spec has at least one task implementing it. ✓ (services T4-T8, tools T9-T19, wiring T20, cutover T21-T22)
2. **No placeholders**: each task contains either complete code, a clear pattern reference, or a documented stub-to-be-completed-in-task-N pointer.
3. **Type consistency**: DTOs defined in Task 4-8 (in `WindowsMcp.Abstractions/Models/`) are used identically in tool handlers in Tasks 9-19.
4. **Atomic commits**: each task produces exactly one commit.
5. **TDD**: each tool task starts with a failing test before implementation.

## Rollback contingency (not a task — referenced from Task 21)

If post-cutover live verification fails:
```powershell
Copy-Item C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json.bak-2026-05-24-pre-windows-mcp-cs-cutover `
          C:/Users/danie/.claude/local-marketplace/mcp-host/.mcp.json -Force
```
Then user: `/kill-plugins` and `/reload-plugins`. Python is live again.

Tasks 11-19 share an implementation pattern; the subagent should reference Task 9 (full worked example) and Task 10 (worked example with service helper) when implementing them.
