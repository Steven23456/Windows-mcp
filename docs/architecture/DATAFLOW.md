# Windows-MCP Data Flow

## Overview

This document describes the data flow patterns within Windows-MCP, illustrating how information moves through the system from MCP tool invocation to Windows OS interaction and back.

The key architectural shift from the Python version: there are no module-level globals. The MCP SDK's `WithToolsFromAssembly()` source generator handles dispatch, the DI container wires all dependencies, and every service call is async.

---

## Primary Data Flow: MCP Request Dispatch

How a tool call travels from the AI agent to the Windows API and back:

```
┌──────────┐   ┌───────────────────┐   ┌──────────────┐   ┌────────────────┐
│ AI Agent │   │ MCP SDK transport │   │  Tool Class  │   │    Service     │
│          │   │  stdio or HTTP    │   │ [McpServTool] │   │ Implementation │
└────┬─────┘   └────────┬──────────┘   └──────┬───────┘   └───────┬────────┘
     │                  │                      │                   │
     │  JSON-RPC        │                      │                   │
     │  {method,params} │                      │                   │
     ├─────────────────►│                      │                   │
     │                  │  Deserialize params  │                   │
     │                  │  Route to method     │                   │
     │                  ├─────────────────────►│                   │
     │                  │                      │  await ServiceAsync│
     │                  │                      ├──────────────────►│
     │                  │                      │                   │  Windows API
     │                  │                      │                   ├──────────►
     │                  │                      │                   │◄──────────
     │                  │                      │◄──────────────────┤
     │                  │                      │  JsonSerializer   │
     │                  │◄─────────────────────┤  .Serialize(result)
     │                  │  JSON-RPC response   │                   │
     │◄─────────────────┤                      │                   │
```

All tool methods follow the same shape:
```csharp
[McpServerTool, Description("...")]
public async Task<string> ToolName(/* parameters */)
{
    var result = await _service.DoSomethingAsync(/* mapped params */);
    return JsonSerializer.Serialize(result);  // or plain string
}
```

---

## GetState Data Flow (UI Automation)

`GetState` is the primary context-gathering tool — returns the full UI element tree of the foreground application.

### Sequence

```
┌──────────┐   ┌─────────────┐   ┌──────────────────────┐   ┌───────────────┐
│ AI Agent │   │UIAutoTools  │   │ UIAutomationService  │   │ FlaUI.UIA3    │
│          │   │             │   │                      │   │ (Windows UIA3)│
└────┬─────┘   └─────┬───────┘   └──────────┬───────────┘   └───────┬───────┘
     │               │                      │                       │
     │ GetState()    │                      │                       │
     ├──────────────►│                      │                       │
     │               │ GetStateAsync()      │                       │
     │               ├─────────────────────►│                       │
     │               │                      │ AutomationElement     │
     │               │                      │ .RootElement          │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │                      │ GetForegroundWindow() │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │                      │ TreeWalker.Walk()     │
     │               │                      ├──────────────────────►│
     │               │                      │  [recursive DFS]      │
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │    UiState           │                       │
     │               │◄─────────────────────┤                       │
     │               │ JsonSerializer       │                       │
     │ JSON string   │ .Serialize(state)    │                       │
     │◄──────────────┤                      │                       │
```

### Data Transformations

```
1. MCP Request
   └─► no parameters (returns foreground window state)

2. UIAutomationService.GetStateAsync()
   ├─► Get desktop root via AutomationElement.RootElement
   ├─► Identify foreground window via P/Invoke GetForegroundWindow()
   └─► Walk UIA3 tree recursively

3. Per-element classification (FlaUI ControlType checks):
   ├─► Interactive: Button, Edit, CheckBox, RadioButton, ComboBox,
   │               ListItem, MenuItem, Hyperlink, TabItem, TreeItem, ...
   ├─► Text: Text, Document controls (read-only content)
   └─► Scrollable: elements supporting IScrollPattern

4. UiState aggregate:
   UiState {
     Interactive: [{ Id, Name, ControlType, BoundingBox, Value, ... }]
     Text:        [{ Id, Name, Content }]
     Scrollable:  [{ Id, Name, BoundingBox, H: bool, V: bool }]
   }

5. MCP Response: JSON string of UiState
```

---

## Click Data Flow

```
┌──────────┐   ┌────────────┐   ┌──────────────────┐   ┌────────────────────┐
│ AI Agent │   │InputTools  │   │  InputService    │   │ Win32 (user32) +   │
│          │   │            │   │                  │   │ H.InputSimulator   │
└────┬─────┘   └─────┬──────┘   └────────┬─────────┘   └─────────┬──────────┘
     │               │                   │                        │
     │ Click(x,y,    │                   │                        │
     │  button,      │                   │                        │
     │  clicks)      │                   │                        │
     ├──────────────►│                   │                        │
     │               │ ParseButton(btn)  │                        │
     │               ├──────────────────►│                        │
     │               │  ClickAsync(x,y,  │                        │
     │               │  MouseButton,     │                        │
     │               │  clicks)          │                        │
     │               ├──────────────────►│                        │
     │               │                   │ SetCursorPos(x, y)     │
     │               │                   ├───────────────────────►│
     │               │                   │ GetCursorPos read-back │ (throws if Windows
     │               │                   │◄───────────────────────┤  clamped the point)
     │               │                   │ ButtonDown/Up × clicks │
     │               │                   ├───────────────────────►│
     │               │                   │                        │ SendInput(MOUSECLICK)
     │               │                   │◄───────────────────────┤
     │               │ ClickResult       │                        │
     │               │◄──────────────────┤                        │
     │ JSON string   │                   │                        │
     │◄──────────────┤                   │                        │
```

### Data

```
Input:  x=800, y=400, button="right", clicks=1

Processing:
  ParseButton("right") → MouseButton.Right
  InputService.ClickAsync(800, 400, MouseButton.Right, 1):
    ├─► SetCursorPos(800, 400) + GetCursorPos   // physical virtual-desktop pixels; throws if clamped
    └─► IMouseSimulator.RightButtonClick()      // SendInput(MOUSE_RIGHT_DOWN + MOUSE_RIGHT_UP) at the cursor

Output: ClickResult { X=800, Y=400, Button="Right", Clicks=1 }
        → JSON: {"X":800,"Y":400,"Button":"Right","Clicks":1}
```

---

## Powershell Data Flow

```
┌──────────┐   ┌──────────┐   ┌──────────────────────┐   ┌───────────────────┐
│ AI Agent │   │ShellTools│   │  PowerShellService   │   │ System.Diagnostics│
│          │   │          │   │                      │   │    .Process       │
└────┬─────┘   └────┬─────┘   └──────────┬───────────┘   └─────────┬─────────┘
     │              │                    │                          │
     │ Powershell   │                    │                          │
     │ (command)    │                    │                          │
     ├─────────────►│                    │                          │
     │              │ RunAsync(command)  │                          │
     │              ├───────────────────►│                          │
     │              │                    │ acquire serialization    │
     │              │                    │ gate, then start 15-min  │
     │              │                    │ execution backstop CTS   │
     │              │                    │                          │
     │              │                    │ PowerShellInvocation:    │
     │              │                    │  -EncodedCommand (or     │
     │              │                    │  temp .ps1 -File if big) │
     │              │                    │                          │
     │              │                    │ Process.Start()          │
     │              │                    │  powershell.exe          │
     │              │                    │  -NoProfile              │
     │              │                    │  -NonInteractive         │
     │              │                    ├─────────────────────────►│
     │              │                    │ close stdin; read        │
     │              │  progress          │ stdout+stderr; on cancel │
     │  progress    │  heartbeat / 10s   │ or backstop: kill whole  │
     │  notification│  while waiting     │ process tree             │
     │◄─────────────┤                    │◄─────────────────────────┤
     │              │ PSResult           │                          │
     │              │◄───────────────────┤                          │
     │ JSON string  │                    │                          │
     │◄─────────────┤                    │                          │
```

`background:true` skips this path entirely: ShellTools calls `JobService.StartAsync`, which
builds the child via the same `PowerShellInvocation` helper but runs it **outside** the
serialization gate, pumps stdout/stderr into bounded buffers, and returns a `JobInfo`
immediately. The agent then polls via the `job` tool (`status`/`output`/`cancel`/`list`);
a per-job 60-min backstop tears down runaway jobs as `timedOut`.

### Data

```
Input:  command = "Get-Process | Select-Object Name,CPU | ConvertTo-Json"

Processing:
  1. Acquire the serialization gate (one PowerShell at a time), then arm the 15-min backstop
  2. PowerShellInvocation.BuildArgumentsAsync — UTF-8 preamble + -EncodedCommand
     (temp .ps1 -File fallback for oversized scripts)
  3. Process.Start(powershell.exe); close stdin (protects the MCP stdio channel)
  4. Await exit (ShellTools reports a progress heartbeat every 10s meanwhile);
     read stdout + stderr

Output: PSResult { Success=true, Stdout="[{...}]", Stderr="", ExitCode=0, Errors=[] }
        → JSON: {"Success":true,"Stdout":"[{...}]","Stderr":"","ExitCode":0,"Errors":[]}
```

---

## Process Lineage / Orphan Data Flow

`Process` (actions `list|orphans|kill`) exposes recycle-aware parent lineage, orphan detection,
and root-grouping on top of the plain process list.

```
Flow — process action orphans / list includeLineage / list groupByRoot:

  AI Agent          →  ProcessTools.Process(action, name?, includeLineage?, groupByRoot?)
  ProcessTools      →  ProcessService.ListLineageAsync(...) | GroupByRootAsync(...)
  ProcessService    →  IWmiService.QueryAsync("Win32_Process", null, null)   [single bulk enumeration]
  ProcessService    :  Win32ProcRow.From(row) — parse raw CIM_DATETIME CreationDate → UTC at the seam
  ProcessService    →  ProcessLineage.Classify(rows, nowUtc)                 [pure, recycle-aware]
  ProcessService    :  apply orphansOnly / name-or-cmdline filter AFTER classification
  ProcessService    →  ProcessTools  →  AI Agent : JSON (ProcessLineageDto[] | ProcessGroupDto[])
```

### Data

```
Input:  orphans (name=null)  →  ListLineageAsync(orphansOnly:true, nameFilter:null)

Processing:
  1. QueryAsync("Win32_Process", null, null, ct) — single bulk WMI enumeration
  2. Parse each row's CreationDate (CIM_DATETIME string) at the seam only, to UTC DateTime
  3. Pure classifier over Win32ProcRow[] + nowUtc: for each process, resolve RootPid by walking
     parent links; orphaned = parent id absent, OR not provably recycled (a null CreationUtc on
     either side cannot prove recycling, so the parent is treated as alive); attach ageMinutes,
     runtimeKind, isSystemAdjacent
  4. Filter (orphansOnly / name substring on name-or-command-line) applied AFTER classification,
     so a filtered-out root PID still resolves correctly for surviving children

Output: ProcessLineageDto[] → JSON array (recycle-aware lineage + signals per process)
        or ProcessGroupDto[] → JSON array (processes collapsed under nearest-live root)

Kill-tree: KillTreeAsync(pid, expectedStartUtc?) verifies the root PID's start time once against
expectedStartUtc when given, then walks descendants leaves-first. Before killing each PID it
re-reads the live start time and compares it to that PID's snapshot CreationUtc, skipping any
mismatch — so a PID reused between the snapshot and the kill is not an innocent bystander (guards
PID reuse mid-walk). A snapshot row with no CIM date cannot be validated and is killed as-is.
```

---

## Screenshot + OCR Data Flow

```
┌──────────┐   ┌───────────┐   ┌──────────────────┐   ┌─────────────────────┐
│ AI Agent │   │ScreenTools│   │ScreenshotService │   │ SkiaSharp / GDI+    │
│          │   │           │   │   OcrService     │   │ Windows.Media.Ocr   │
└────┬─────┘   └─────┬─────┘   └────────┬─────────┘   └──────────┬──────────┘
     │               │                  │                         │
     │ Screenshot()  │                  │                         │
     ├──────────────►│ resolve rect:    │                         │
     │               │ monitors, region │                         │
     │               │ or display; then │                         │
     │               │ read the cursor  │                         │
     │               │ CaptureAsync(r,  │                         │
     │               │  CaptureOptions) │                         │
     │               ├─────────────────►│                         │
     │               │                  │ CopyFromScreen(r)       │
     │               │                  ├────────────────────────►│
     │               │                  │◄────────────────────────┤
     │               │                  │ cursor: icon or ring    │
     │               │                  │ ScaleMath.Fit → resize  │
     │               │                  │ SKBitmap.Encode(jpg/png)│
     │               │                  ├────────────────────────►│
     │               │ ScreenshotResult │◄────────────────────────┤
     │ metadata text │◄─────────────────┤                         │
     │ + image block │                  │                         │
     │◄──────────────┤                  │                         │
     │               │                  │                         │
     │ Ocr(region)   │                  │                         │
     ├──────────────►│ same resolver,   │                         │
     │               │ ExtractTextAsync │                         │
     │               ├─────────────────►│                         │
     │               │                  │ OcrEngine.RecognizeAsync│
     │               │                  ├────────────────────────►│
     │               │                  │  (Windows.Media.Ocr)    │
     │               │ recognised text  │◄────────────────────────┤
     │◄──────────────┤◄─────────────────┤                         │
```

---

## WaitFor Data Flow (Polling Loop)

`WaitFor` is the only tool with internal retry logic — all other tools are single-pass:

```
UIAutomationTools.WaitFor(text, timeout_ms, interval_ms, kind, scope, window, include_offscreen)
        │
        ▼
UIAutomationService.WaitForAsync(...)  →  PollAsync(poll, timeout_ms, interval_ms, ct)
        │
        ▼
  ┌──────────────────────────────────────────────────────────────┐
  │ deadline = UtcNow + timeout_ms   lastFailure = null           │
  │ anyCleanPoll = false                                          │
  │                                                               │
  │  ┌──────────────────────────────────────────┐                 │
  │  │ FindElementAsync(text, kind, scope, …)   │◄────────────┐   │
  │  │  (window re-resolved on EVERY poll)      │             │   │
  │  └──────────────┬───────────────────────────┘             │   │
  │                 │                                          │   │
  │        threw? ──┤ YES → lastFailure = ex ─────┐            │   │
  │           NO    │                             │            │   │
  │                 ▼                             │            │   │
  │        anyCleanPoll = true                    │            │   │
  │           found? ─┬── YES → return element    │            │   │
  │                   │                           │            │   │
  │                   NO ◄────────────────────────┘            │   │
  │                   ▼                                        │   │
  │        remaining = deadline − UtcNow                       │   │
  │           ≤ 0 ? ─┬── NO → await Task.Delay(               │   │
  │                  │        min(interval_ms, remaining)) ────┘   │
  │                 YES                                            │
  │                  ▼                                             │
  │        anyCleanPoll ? return null                              │
  │                     : throw TimeoutException(lastFailure)      │
  └──────────────────────────────────────────────────────────────┘
```

A poll that throws is **retried**, never fatal — absorbing transient UIA failure is the whole
point of a wait (checklist D-5). The loop polls at least once, so `timeout_ms: 0` means "check
now". It distinguishes two outcomes the old loop conflated: polls ran and found nothing (`null`),
versus every poll failed (`TimeoutException` carrying the last error).

---

## DI Resolution Flow at Startup

```
Host.CreateApplicationBuilder(args)
        │
        ▼
builder.Services.AddSingleton<IInputService, InputService>()
  ...  (36 services + the ScreenshotOptions record from --screenshot-scale)
        │
        ▼
builder.AddWindowsMcp(options)    ← Hosting/WindowsMcpHost: AddMcpServer(...) + filter + WithToolsFromAssembly()
    .WithStdioServerTransport()   ← or .WithHttpTransport(stateless) + MapMcp("/mcp") with --transport http
        │
        ▼
builder.Build()
        │
        ▼
  IServiceProvider built
  ┌─────────────────────────────────────────────────┐
  │  On first tool call, DI resolves:               │
  │                                                 │
  │  InputTools ← IInputService (InputService)      │
  │            ← IClipboardService (ClipboardSvc)  │
  │                                                 │
  │  UIAutomationTools ← IUIAutomationService       │
  │                      (UIAutomationService)      │
  │  ... etc.                                       │
  └─────────────────────────────────────────────────┘
        │
        ▼
builder.Build().RunAsync()
  → reads stdin forever (JSON-RPC)
  → dispatches to tool methods
  → exits when stdin closes (EOF)
```

---

## Element State Determination (FlaUI)

```
Is element interactive?
        │
        ▼
  ┌─────────────────────────┐
  │ ControlType in           │──NO──► Skip
  │ INTERACTIVE_CONTROL_TYPES│
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ IsEnabled == true        │──NO──► Skip
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ IsOffscreen == false     │──NO──► Skip
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ BoundingRectangle.Area  │──NO──► Skip
  │       > 0               │
  └──────────┬──────────────┘
             │YES
             ▼
      [Include in Interactive]


Interactive control types (FlaUI ControlType names):
  Button, Edit, CheckBox, RadioButton, ComboBox, List, ListItem,
  MenuItem, Hyperlink, SplitButton, TabItem, TreeItem, DataItem,
  Slider, Spinner, ScrollBar, Document
```

---

## AssertElement Data Flow

```
AssertElement(element_id, state, expected?)
        │
        ▼
UIAutomationService.AssertElementAsync(element_id, state, expected)
        │
        ▼
  Validate: known state; `expected` only — and always — with "value"
        │
        ▼
  Resolve element by ID from internal cache
  ├─ unknown id + "exists" → FAIL "unknown element id"
  └─ unknown id + other    → KeyNotFoundException
        │
        ▼
  Liveness probe: ProcessId <= 0, or UIA_E_ELEMENTNOTAVAILABLE /
  RPC failure on any read (IsElementGone)
  └─ gone → FAIL "element no longer available"
        │
        ▼
  switch (state)
  ├─ "exists"  → PASS
  ├─ "enabled" → IsEnabled (UIA default true)              FAIL: "disabled"
  ├─ "checked" → TogglePattern state == On                 FAIL: "toggle state Off" / "no TogglePattern on …"
  ├─ "visible" → !IsOffscreen && bounds non-empty          FAIL: "offscreen" / "empty bounds"
  ├─ "focused" → HasKeyboardFocus, or == FocusedElement()  FAIL: "focus is on <type> '<name>'" / "nothing has focus"
  └─ "value"   → ValuePattern value (else Name) == expected, ordinal
                                                           FAIL: "value is '<actual>' (from ValuePattern|Name)"
        │
        ▼
  AssertResult(ElementId, State, Pass, Observed)
        │
        ▼
  Tool: "PASS" or "FAIL: {state} — observed {Observed}"
```

---

## Error Handling Flow

### PowerShell Execution Errors

```
PowerShellService.RunAsync(command)
        │
        ▼
  ValidateCommand(command)  →  ArgumentException if blocked
        │ (passes)
        ▼
  Process.Start(...)
        │
  ┌─────┴─────┐
  ▼           ▼
Success     Exception
  │           │
  ▼           ▼
PowerShell  Return PowerShellResult{
Result       Stdout="", Stderr=ex.Message, ExitCode=-1}
```

### UI Automation Errors

```
UIAutomationService methods
  catch (Exception ex)
  └─► Return null or empty result (callers check for null)
  
WaitFor — timeout path:
  elapsed > timeout_ms → return null
  Tool returns "null" string (agent detects no match)
```

---

## Response Format

### Tool Response Shape

All tool methods return `Task<string>` — except `screenshot`, which returns
`Task<CallToolResult>` so it can carry an image block — where the string is either:
- **JSON** — from `JsonSerializer.Serialize(result)`
- **Plain string** — for simple acknowledgements (`"pressed ctrl+c"`, `"PASS"`, `"null"`)

`screenshot`'s `CallToolResult` is a `TextContentBlock` of metadata JSON followed by an
`ImageContentBlock`; with `output="file"` the image block is omitted and the metadata carries
the path.

### JSON Response Examples

```jsonc
// Click response
{"X":800,"Y":400,"Button":"Left","Clicks":1}

// PowerShell response
{"Stdout":"ProcessName  CPU\n---\npwsh   1.23\n","Stderr":"","ExitCode":0}

// GetState response (abbreviated)
{
  "Interactive": [
    { "Id": "1", "Name": "OK", "ControlType": "Button",
      "BoundingBox": {"Left":100,"Top":200,"Right":180,"Bottom":230} }
  ],
  "Text": [{ "Id": "2", "Name": "Save changes?", "Content": "Save changes?" }],
  "Scrollable": []
}
```

---

## Timing and Delays

| Location | Behavior | Notes |
|----------|----------|-------|
| `WaitFor` | Polls every `interval_ms` (default 500ms) up to `timeout_ms` (default 10s); sleep clamped to the remaining budget, minimum 10ms | Only tool with a loop; a failed poll is retried, and every-poll-failed throws rather than reporting `null` |
| `InputService` | No delays: clicks are back-to-back `SendInput`; the cursor is placed with `SetCursorPos` and read back before any button event | A clamped (off-monitor) point throws rather than clicking elsewhere |
| `PowerShellService` | Async wait on process exit | 15-min execution backstop (armed after the serialization gate); caller cancellation kills the process tree |
| `ShellTools` heartbeat | Progress notification every 10s during a foreground `powershell` call | Lets spec-compliant clients reset their request timeout |
| `JobService` | Background jobs poll-based; per-job 60-min backstop | Runs outside the PowerShell serialization gate |
| MCP SDK transport (stdio or HTTP) | No artificial pauses — reads JSON-RPC frames continuously | Contrast: Python `pg.PAUSE = 1.0` |
