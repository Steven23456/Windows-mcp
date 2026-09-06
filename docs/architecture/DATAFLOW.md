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

`GetState` returns the UI element tree of the foreground application, three levels deep. (`Snapshot`, below, is the whole-desktop context-gathering call.)

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
     │               │                      │ GetForegroundWindow() │
     │               │                      │ → FromHandle          │
     │               │                      ├──────────────────────►│
     │               │                      │◄──────────────────────┤
     │               │                      │  (else FocusedElement │
     │               │                      │   → GetDesktop)       │
     │               │                      │                       │
     │               │                      │ FindAllChildren()     │
     │               │                      ├──────────────────────►│
     │               │                      │  [recursive DFS,      │
     │               │                      │   depth 3, budgeted]  │
     │               │                      │◄──────────────────────┤
     │               │                      │                       │
     │               │    ElementTree       │                       │
     │               │◄─────────────────────┤                       │
     │               │ JsonSerializer       │                       │
     │ JSON string   │ .Serialize(tree)     │                       │
     │◄──────────────┤                      │                       │
```

### Data Transformations

```
1. MCP Request
   └─► no parameters (returns foreground window state)

2. UIAutomationService.GetStateAsync()   [on the STA worker thread]
   ├─► Foreground root: GetForegroundWindow() → FromHandle, falling back to
   │   the focused element, then the desktop
   ├─► BuildTree(root, depth: 3, budget) — recursive, depth-limited
   └─► ElementBudget.TryTake() per node (UiTreeOptions.MaxElements,
       from --max-tree-elements, default 500); a refused child ends its
       parent's child list and stops the walk

3. Per element → ElementInfo:
   { ElementId "el_N" (cached for get_element/interact_element), Name,
     ControlType, IsEnabled, IsOffscreen, Bounds, Value, IsChecked,
     IsSelected, Scroll (null here — snapshot populates it) }

4. ElementTree aggregate:
   ElementTree {
     Root: ElementInfo, Children: ElementTree[],
     Truncated / ElementLimit  ← set on the ROOT only when the budget
                                  stopped the walk; omitted from the JSON
                                  otherwise
   }

5. MCP Response: JSON string of ElementTree
```

---

## Snapshot Data Flow (whole desktop)

`snapshot` is the entry point for an interaction loop: one call returns the window list, the
cursor, every interactive element with its centre coordinates, and the scrollable regions.

### Data Transformations

```
1. MCP Request
   └─► snapshot(scope, window?, include_tree, max_elements, format, use_dom)
       UIAutomationTools validates in order: scope → the window/scope rule →
       max_elements ≥ 0 → format; use_dom is passed straight through

2. UIAutomationService.SnapshotAsync(SnapshotRequest)
   ├─► Header, each collaborator read once:
   │     IInputService.GetCursorPositionAsync()      → Cursor
   │     IWindowService.EnumerateMonitorsAsync()     → CursorMath.MonitorIndexOf
   │     IWindowService.ListAsync(...)               → Windows, WindowFilter.ActiveOf
   ├─► Roots by scope: desktop = every non-minimised window (topmost first),
   │   foreground = the active entry (else UIA's foreground window),
   │   window = title match, exact then substring
   ├─► use_dom: a target window with IsBrowser is re-rooted on its page —
   │   FindPageDocument (Document + AutomationId "RootWebArea", retried
   │   because Chromium fills its tree lazily); no page → the whole window
   │   is walked and the Pages entry carries DomCorrection.NoPageNote
   └─► [STA thread] one ElementBudget for the whole call;
       UiTraverser.Walk(root, title, budget) per window under one CacheRequest;
       a window whose walk throws is logged and skipped

3. Per walked node → el_N id (ids the previous snapshot issued are evicted
   first) → UiTree.Project:
   ├─► UiClassifier.Classify → Interactive?  → SnapshotElement
   │     (Window, ControlType, Name, CenterX/CenterY from CenterOf(Bounds),
   │      Action from ActionFor, Focused, IsPassword, Value (null when
   │      password), Toggle, Expand, Shortcut, Range min/value/max)
   ├─► UiClassifier.IsScrollable → SnapshotScrollable (+ ScrollInfo)
   └─► DOM page: DomCorrection.SuppressesInteractive drops the page document
       from the interactive list (it keeps its id and its scrollable row),
       DomCorrection.PageFor projects the walk onto SnapshotPage
       (DocumentId, Title, Url, Scroll, Text in document order)

4. SnapshotResult { Windows, ActiveWindow, Cursor, CursorMonitorIndex,
                    Interactive[], Scrollable[], Tree?, Truncated,
                    ElementLimit, ElementCount, CaptureMs,
                    Stages? (--profile-snapshot: header, walk),
                    Pages?  (use_dom only; null otherwise, so a plain
                             snapshot serialises exactly as before) }

5. MCP Response:
   format="text" (default) → SnapshotRenderer.Render(result)  — compact rows,
                             then the Pages section, the truncation note and
                             the "Timing:" line when each applies
   format="json"           → JsonSerializer.Serialize(result)
```

---

## Click Data Flow (and the shared target resolver)

`click`, `type`, `scroll` and `drag` all start at `InputTools.ResolveTargetAsync` (roadmap C1),
so the point is decided — and an unreachable element refused — before any input is injected.

```
┌──────────┐   ┌────────────┐   ┌──────────────────┐   ┌────────────────────┐
│ AI Agent │   │InputTools  │   │  InputService    │   │ Win32 (user32) +   │
│          │   │            │   │  UIAutomationSvc │   │ H.InputSimulator   │
└────┬─────┘   └─────┬──────┘   └────────┬─────────┘   └─────────┬──────────┘
     │               │                   │                        │
     │ Click(x?,y?,  │                   │                        │
     │  element_id?, │                   │                        │
     │  button,      │                   │                        │
     │  clicks)      │                   │                        │
     ├──────────────►│                   │                        │
     │               │ ParseButton(btn)  │                        │
     │               │ ResolveTarget:    │                        │
     │               │  GetElementAsync  │  (element_id only —    │
     │               ├──────────────────►│   a coordinate call    │
     │               │  ElementInfo      │   never touches UIA)   │
     │               │◄──────────────────┤                        │
     │               │ ElementTarget     │                        │
     │               │  .CentreOf(info)  │  (off-screen / no      │
     │               │  → (x, y)         │   bounds → refused)    │
     │               ├──────────────────►│                        │
     │               │  ClickAsync(x,y,  │                        │
     │               │  MouseButton,     │  (clicks:0 → HoverAsync│
     │               │  clicks)          │   and nothing pressed) │
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
Input:  element_id="el_12", button="right", clicks=1

Processing:
  ParseButton("right") → MouseButton.Right
  ResolveTargetAsync(null, null, "el_12", allowCursor: false):
    ├─► IUIAutomationService.GetElementAsync("el_12")  // the snapshot/find_element id cache
    └─► ElementTarget.CentreOf(info) → (800, 400)      // Bounds.X + Width/2, Bounds.Y + Height/2
  InputService.ClickAsync(800, 400, MouseButton.Right, 1):
    ├─► SetCursorPos(800, 400) + GetCursorPos   // physical virtual-desktop pixels; throws if clamped
    └─► IMouseSimulator.RightButtonClick()      // SendInput(MOUSE_RIGHT_DOWN + MOUSE_RIGHT_UP) at the cursor

Output: {"action":"click","x":800,"y":400,"button":"right","clicks":1,
         "elementId":"el_12","name":"Save"}
```

With `x`/`y` instead of the id the resolver returns the point unchanged and `elementId`/`name`
are `null`; `scroll` and `drag`'s origin allow no target at all, which reads
`IInputService.GetCursorPositionAsync()` and reports `cursor`.

---

## Type Data Flow (keys or paste)

`type` resolves its target the same way, clicks it when one was given, and then hands the text to
the pure `TypePlanner` (roadmap C8); `InputService` executes the plan against its `IKeyboardSink`.

```
Input:  text="report.txt", element_id="el_7", clear=true, press_enter=true, pace_ms=5

Processing:
  ResolveTargetAsync(…, "el_7") → (420, 260)   → ClickAsync(420, 260, Left, 1)
  TypePlanner.Plan("report.txt", TypeOptions(Clear:true, Idle, PressEnter:true, 5)):
    ├─► shortcut "ctrl+a"      (Clear)
    ├─► key      "backspace"   (Clear)
    ├─► text     "report.txt"  (< 200 chars → keys; a "\n" here would be key "enter")
    └─► key      "enter"       (PressEnter)
  InputService.TypeAsync: each step on the sink, Task.Delay(PaceMs) between steps

  A ≥ 200-character step with no control characters other than \n/\t becomes ONE paste step:
    GetTextAsync() → previous  ·  SetTextAsync(text)  ·  shortcut "ctrl+v"
    · 150 ms settle · SetTextAsync(previous)          → clipboardRestored: true

Output: {"typed":10,"method":"keys","clipboardRestored":null,
         "x":420,"y":260,"elementId":"el_7","name":"File name"}
```

---

## Bring-to-Front Data Flow (window matching + the foreground ladder)

`switch_to_window`, `focus` and the seven acting `window` actions (`minimize`, `maximize`,
`restore`, `close`, and B-9's `move`, `resize`, `set_bounds`) share one target resolver and,
for the two focus tools, one foreground ladder. Windows refuses `SetForegroundWindow` to a
background process, so the outcome is re-read rather than assumed (roadmap C11).

### Data Transformations

```
1. MCP Request
   └─► switch_to_window(title?, hwnd?) | focus(title?, hwnd?)
       WindowTools refuses "neither given" with an ArgumentException before
       the inventory is read; window(action, title?, hwnd?) validates the
       action first, then that one of title/hwnd is present — except
       move/resize/set_bounds, which check their own x/y/width/height rule
       and fall back to the foreground window when neither is given

2. WindowService.BringToFrontAsync(title, hwnd)  |  ExecuteAsync(action, title,
   hwnd)  |  SetBoundsAsync(title, hwnd, x, y, width, height, restoreFirst)
   └─► ListAsync(includeMinimized: true, includeHidden: false)   — A-1's inventory
       (SetBoundsAsync with neither title nor hwnd skips it and takes
        GetActiveAsync instead: Strategy="foreground", Score=100)

3. WindowMatcher.Match(inventory, title, hwnd)   — pure
   ├─► hwnd given      → that window, Strategy="hwnd", Score=100 (never fuzzes)
   ├─► exact title     → Strategy="exact",     Score=100   (OrdinalIgnoreCase)
   ├─► substring       → Strategy="substring", Score=100
   ├─► fuzzy           → max(FuzzyMatch.PartialRatio, TokenSetRatio) ≥ 70
   └─► nothing matched → KeyNotFoundException listing up to 15 open titles
       Ties inside one strategy go to the lowest ZOrder (the frontmost);
       minimised windows are candidates

4a. window(minimize|maximize|restore|close): ShowWindow / PostMessage(WM_CLOSE)
    on the matched handle
    └─► WindowAction { Action, Title (the matched window's), Success=true,
                       MatchStrategy, Score, Hwnd }

4b. window(move|resize|set_bounds): WindowGeometry.Apply(match, x?, y?, width?,
    height?, restore_first, IWindowGeometryNative)   — pure decisions,
    Win32WindowGeometryNative in production
    ├─► IsIconic / IsZoomed → refuse naming the state, unless restore_first,
    │   which sends ShowWindow(SW_RESTORE) first          → Restored=true
    ├─► GetWindowRect                                     → Before
    ├─► SetWindowPos(hwnd, null, x ?? Before.X, y ?? Before.Y,
    │       width ?? Before.Width, height ?? Before.Height,
    │       SWP_NOZORDER|SWP_NOACTIVATE
    │       [|SWP_NOMOVE when no x/y] [|SWP_NOSIZE when no width/height])
    ├─► GetWindowRect again                               → After (the outcome)
    └─► WindowBoundsResult { Window, Before, After, MatchStrategy, Score,
                             Restored }

4c. ForegroundLadder.Bring(match, IForegroundNative)  — Win32ForegroundNative in
    production, a recording fake in the tests
    ├─► IsIconic → ShowWindow(SW_RESTORE)                    → Restored=true
    ├─► rung 1: SetForegroundWindow                        → "SetForegroundWindow"
    ├─► rung 2: AttachThreadInput + BringWindowToTop +
    │           SetForegroundWindow + detach              → "AttachThreadInput"
    │           (skipped whole when the attach is refused — elevated target)
    ├─► rung 3: keybd_event(VK_MENU) down/up +
    │           SetForegroundWindow                       → "AltNudge"
    └─► GetForegroundWindow is re-read after every rung and is the only source
        of Success/Strategy; SetForegroundWindow's return value is never consulted

5. MCP Response: ForegroundResult { Window, MatchStrategy, Score, Restored,
                                    Strategy (null when no rung worked), Success }
```

---

## Launch Data Flow (path → catalog → activate → wait)

`launch` resolves a name the way a person says it, starts the app, and then reports the window it
actually produced rather than the request it sent (roadmap C7 and C11). Nothing here spawns
PowerShell.

### Data Transformations

```
1. MCP Request
   └─► launch(app_name, wait_for_window = true, timeout_ms = 10000)
       WindowTools rejects a blank app_name and a timeout_ms outside 1..60000
       before the service is touched

2. WindowService.LaunchAsync(appName, waitForWindow, timeoutMs)
   └─► with waitForWindow: ListAsync(true, false) FIRST — the set of Hwnds that
       were already open, so a title match afterwards can only be a NEW window

3a. IsPathOrExecutable(appName)      — File/Directory.Exists, or a name ending
    in ".exe" found on PATH; a bare word like "calc" is NOT a path
    └─► IAppActivator.StartShortcutOrPath(appName)  → pid
        Kind="path", Strategy="path", Score=100, MatchedName=appName

3b. otherwise IAppCatalogService.ResolveAsync(appName)
    ├─► ListAsync(): the two Start Menu Programs folders' *.lnk files +
    │   PackageManager.FindPackagesForUser("") → GetAppListEntriesAsync()
    │   (display name + AUMID), merged by AppCatalog.Merge, cached 5 min
    ├─► AppCatalog.Match: exact → prefix (shortest name) → fuzzy
    │   max(PartialRatio, TokenSetRatio) ≥ 70 (highest, ties shortest name)
    ├─► a miss refreshes the cache ONCE, then re-matches
    └─► still nothing → KeyNotFoundException naming the five nearest with scores
    └─► IAppActivator.ActivatePackaged(AUMID)      → pid   (packaged)
        IAppActivator.StartShortcutOrPath(.lnk)    → pid   (shortcut)

4. wait_for_window:false → LaunchResult with Hwnd/Title null, WindowDetected=false
   wait_for_window:true  → LaunchWait.ForWindowAsync(ListAsync, pid, matchedName,
                             before, timeout_ms, pollMs = 250)
   └─► LaunchWait.Pick per poll: a window with that Pid (lowest ZOrder first),
       else a window NOT in `before` whose title matches matchedName
       exact → substring → fuzzy ≥ 70  (packaged apps and browsers hand off to
       a process the activation never named)
   └─► timeout → null, which is WindowDetected:false — an outcome, not an error

5. MCP Response: LaunchResult { MatchedName, Kind (shortcut|packaged|path), Score,
                                Pid, Hwnd?, Title?, WindowDetected,
                                Strategy (path|exact|prefix|fuzzy) }
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
│ AI Agent │   │ScreenTools│   │ScreenshotService │   │ SkiaSharp / GDI+/WGC│
│          │   │           │   │   OcrService     │   │ Windows.Media.Ocr   │
└────┬─────┘   └─────┬─────┘   └────────┬─────────┘   └──────────┬──────────┘
     │               │                  │                         │
     │ Screenshot()  │                  │                         │
     ├──────────────►│ resolve rect:    │                         │
     │               │ monitors, region │                         │
     │               │ or display; then │                         │
     │               │ read the cursor  │                         │
     │               │ annotate: one    │                         │
     │               │ SnapshotAsync    │                         │
     │               │ (desktop), keep  │                         │
     │               │ what overlaps r  │                         │
     │               │ CaptureAsync(r,  │                         │
     │               │  CaptureOptions) │                         │
     │               ├─────────────────►│                         │
     │               │                  │ AcquireFrame: gdi | wgc │
     │               │                  ├────────────────────────►│
     │               │                  │◄────────────────────────┤
     │               │                  │ cursor: icon or ring    │
     │               │                  │ ScaleMath.Fit → resize  │
     │               │                  │ Annotator.Draw on a copy│
     │               │                  │ SKBitmap.Encode(jpg/png)│
     │               │                  ├────────────────────────►│
     │               │ ScreenshotResult │◄────────────────────────┤
     │               │ flash: glow on r │                         │
     │ metadata text │◄─────────────────┤                         │
     │ + element list│                  │                         │
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

**Annotate path (A-6).** The snapshot walk runs **after** the rect is resolved and the cursor read
and **before** the capture, so label N in the picture is row N of the text block from the same
call. Only elements and scrollables whose bounds overlap the captured rect are kept (half-open —
one touching the far edge is out), in snapshot order; the kept elements become
`AnnotationBox(ElementId, Bounds)` in `CaptureOptions.Annotations`. `ScreenshotService` draws them
after the downscale on a copy of the bitmap and returns `AnnotationsDrawn` (boxes that landed, not
boxes requested). The tool then emits metadata (`annotated`, `annotations`, and `grid` when a grid
was asked for), `SnapshotRenderer.Render` of the filtered snapshot, and the image — three blocks
inline, two with `output="file"`. A grid alone (`grid_columns`/`grid_rows` without `annotate`)
needs no walk. Because the walk is a snapshot walk, it evicts the previous `snapshot`'s `el_N` ids.

**Backend selection (A-10).** The call's `backend` argument is validated with the other arguments,
before anything is captured. `ScreenshotService.ResolveBackend` turns `auto` into the process
default (`--screenshot-backend` / `WINDOWSMCP_SCREENSHOT_BACKEND`, itself `auto` unless set), then
`AcquireFrame` asks the compositor (`WgcCaptureBackend`: one `GraphicsCaptureItem` per monitor the
rect touches → D3D11 staging texture → blit into the rect) or copies the screen with GDI. `auto`
falls back to GDI silently when WGC is unsupported or refuses; an explicit `wgc` throws instead.
Whatever produced the frame is what `ScreenshotResult.Backend` and the metadata `backend` field
report — never `auto`. The cursor overlay, downscale, annotations and grid run identically on
either frame.

**Flash and profiling (A-14).** `IFlashOverlay.Hide()` runs immediately before every capture —
unconditionally, so a glow left over from the previous call can never appear in the picture — and
`Show(r, 3.5 s)` runs immediately after when `--flash` is on. The tool then reports `flash: true`
from `IFlashOverlay.IsVisible`, i.e. what happened, not what was asked (a host with no window
station shows nothing). With `--profile-snapshot` the tool's own stages (`resolve`, `cursor`,
`snapshot` when annotating, `capture`) are merged with the service's finer-grained ones
(`capture`, `cursor`, `resize`, `encode` — the service wins a name clash) into the metadata
`stages` object, and the same numbers go to stderr at Information.

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
  ...  (39 services + the ScreenshotOptions record from --screenshot-scale,
       --flash, --profile-snapshot and --screenshot-backend, and the
       UiTreeOptions record from --max-tree-elements and --profile-snapshot)
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
  │            ← IUIAutomationService (element_id) │
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
UiTraverser.ReadNode — is the element something the model can see?
        │
        ▼
  ┌─────────────────────────┐
  │ IsOffscreen == false     │──NO──► Skip (and its subtree)
  │ (an Edit with real       │
  │  bounds is kept — D-7)   │
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐   NO ─► Skip the node, still walk
  │ Bounds clipped to the   │        its children (zero-area
  │ window rect, area > 0   │        containers hold real ones)
  └──────────┬──────────────┘
             │YES
             ▼
  ┌─────────────────────────┐
  │ ElementBudget.TryTake() │──NO──► Truncated = true, walk stops
  └──────────┬──────────────┘
             │YES
             ▼
   [UiNode] ──► UiClassifier.Classify
        │
        ├─► ControlType in InteractiveControlTypes ──► Interactive
        ├─► LegacyIAccessible role in InteractiveLegacyRoles ──► Interactive
        │   ("text" only when the node carries a value)
        ├─► ControlType in InformativeControlTypes ──► Informative
        └─► otherwise ──► Structural
   (a node with a movable ScrollPattern is additionally listed as Scrollable)


Interactive control types (UiClassifier.InteractiveControlTypes, 17):
  Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox,
  Hyperlink, SplitButton, TabItem, TreeItem, DataItem, HeaderItem,
  Spinner, Slider, ScrollBar, Document

Informative control types (never clicked, worth reporting):
  Text, Image, StatusBar, ProgressBar, ToolTip, Header
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
the path. With `annotate:true` a second `TextContentBlock` — the rendered element list — sits
between the two, so an inline annotated capture carries three blocks and a file one carries two.

### JSON Response Examples

```jsonc
// Click response
{"X":800,"Y":400,"Button":"Left","Clicks":1}

// PowerShell response
{"Stdout":"ProcessName  CPU\n---\npwsh   1.23\n","Stderr":"","ExitCode":0}

// GetState response (abbreviated) — an ElementTree; Truncated/ElementLimit
// appear on the root only when the element budget stopped the walk
{
  "Root": { "ElementId": "el_0", "Name": "Untitled - Notepad",
            "ControlType": "Window", "IsEnabled": true, "IsOffscreen": false,
            "Bounds": {"X":100,"Y":200,"Width":800,"Height":600},
            "Value": null, "IsChecked": null, "IsSelected": null, "Scroll": null },
  "Children": [
    { "Root": { "ElementId": "el_1", "Name": "OK", "ControlType": "Button", "...": null },
      "Children": [] }
  ]
}

// Snapshot response, format:"text" (default) — one element per row
Cursor: (1204, 733) on display 0
Active window: "Untitled - Notepad" (pid 8124, Normal)
Windows (z-order, topmost first):
  0. "Untitled - Notepad" [Normal] 1120x740 @ (280,150) pid=8124
Interactive (2 of 37, ids valid until the next snapshot):
window "Untitled - Notepad"
  el_3 (612,388) button "Save"  [action: click]  [shortcut: Ctrl+S]
  el_7 (840,470) document "Text editor"  [action: fill]  [focused]
Scrollable (1):
  el_7 (840,470) document "Text editor"  [v: 0%]  [h: 0%]  [reached top]
```

---

## Timing and Delays

| Location | Behavior | Notes |
|----------|----------|-------|
| `WaitFor` | Polls every `interval_ms` (default 500ms) up to `timeout_ms` (default 10s); sleep clamped to the remaining budget, minimum 10ms | Only tool with a loop; a failed poll is retried, and every-poll-failed throws rather than reporting `null` |
| `InputService` clicks | No delays: clicks are back-to-back `SendInput`; the cursor is placed with `SetCursorPos` and read back before any button event | A clamped (off-monitor) point throws rather than clicking elsewhere |
| `InputService.TypeAsync` | `TypeOptions.PaceMs` (default 5 ms) between the steps of a typing plan, never after the last one; a paste waits 150 ms after `ctrl+v` before restoring the previous clipboard text | The settle only runs on the real simulator sink, so the unit tests do not pay it. The target reads the clipboard on its own schedule, which is what the settle covers |
| `InputService.DragAsync(…, durationMs, steps)` | `durationMs / steps` between the interpolated moves (default 300 ms / 20 = 15 ms), including after the last one; `durationMs:0` moves without pausing | A drop target needs real motion, not a teleport; the button is released in a `finally`, so a cancelled drag never leaves it down |
| `wait` (`InputTools`) | The only tool whose *job* is a delay: one `Task.Delay(seconds)` on the request's cancellation token, `seconds` in (0, 60] | Validated in the tool before the delay; outside the range is an `ArgumentException` naming it and pointing at `wait_for`. No process is spawned — it replaces `powershell("Start-Sleep")`, which paid a cold start and took the serialization gate |
| `launch`'s window wait | `LaunchWait.ForWindowAsync` reads the inventory immediately, then every 250 ms until `timeout_ms` (default 10 000, max 60 000); the last sleep is clamped to the remaining budget | A timeout is `WindowDetected:false` with the pid, never an exception — a packaged app or a browser may hand its window to another process |
| `AppCatalogService` | The catalog is read from the Start Menu and the package manager at most once per 5 minutes; a resolve miss forces one extra refresh | Enumerating a few hundred packages costs ~1 s cold, which is why it is cached rather than read per `launch` |
| `PowerShellService` | Async wait on process exit | 15-min execution backstop (armed after the serialization gate); caller cancellation kills the process tree |
| `ShellTools` heartbeat | Progress notification every 10s during a foreground `powershell` call | Lets spec-compliant clients reset their request timeout |
| `JobService` | Background jobs poll-based; per-job 60-min backstop | Runs outside the PowerShell serialization gate |
| `FlashOverlay` | The post-capture glow is up for 3.5 s, then taken down by its own timer — and unconditionally at the start of the next capture | `Show`/`Hide` marshal to the overlay thread and wait for it, bounded by a 2 s call timeout; both are silent no-ops with no interactive window station |
| `UIAutomationService.FindPageDocument` | `use_dom` only: up to 3 attempts for the `RootWebArea` document, 150 ms apart, no pause after the last | Chromium builds its accessibility tree lazily on the first query, so attempt 1 can miss a page that is there |
| MCP SDK transport (stdio or HTTP) | No artificial pauses — reads JSON-RPC frames continuously | Contrast: Python `pg.PAUSE = 1.0` |
