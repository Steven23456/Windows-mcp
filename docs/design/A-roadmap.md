# Section A roadmap — desktop state and screenshots (A-1 … A-14)

**Scope:** every item in [section A](../upstream-parity-checklist.md#a--desktop-state-and-screenshots)
of the parity checklist. This is the implementation plan; each item still gets its own
`docs/design/<ID>-<slug>.md` note when it is picked up (checklist rule 1), and this file is the
place those notes link back to for the cross-item decisions. ·
**Status:** planned 2026-09-04 against `main` @ `cb3b488` (64 tools, v0.7.3, all nine D items
closed). Phase 1 has since shipped — A-7, A-9, A-8, A-11 and A-13 — phase 2's A-1 with it, and
phase 3's A-2 (with A-4 and A-3 inside it), which is the one new tool: **65 tools**; where the
code deviates from the plan below, the item carries a **Shipped as** line and its design note has
the reasoning. Phase 4's A-6 has since shipped too; phase 5 has not started. ·
**Baseline facts** used below were read from the code on that commit; the `file:line` anchors
will drift, the member names will not.

## 1. What section A is, in one paragraph

Upstream's agent loop starts with one `Snapshot` call that returns the picture, the window list,
the cursor, and a labelled list of every interactive element with centre coordinates. Ours needs
three to five calls to approximate that (`screenshot` → a file path the model cannot see,
`get_state` → a three-level JSON tree of the foreground window only, `find_element` per control)
and still has no window list, no cursor, no element centres, no scroll positions, no budget. The
fourteen items split into two tracks that only meet at A-6: the **screenshot track** (A-7, A-9,
A-8, A-11, A-10, A-14) makes the picture usable, and the **snapshot track** (A-1, A-13, A-2 with
A-4 and A-3 inside it, A-5, A-12) makes the element list usable. Nothing in section B/C/S is
needed for any of it; A-1 and A-2 are what B-6, B-8 and B-10 are waiting on.

## 2. Cross-item decisions (settle once, every design note inherits them)

| # | Decision | Why |
|---|---|---|
| C1 | **Coordinate space is the virtual desktop, everywhere.** Screenshots, regions, element centres, cursor, window bounds all use the same signed pixel coordinates `click` already normalises to (`InputService.cs:41`, D-3). Every response that carries coordinates says so once: `"coordinateSpace":"virtual-desktop"`. | `ScreenshotService` captures from `CopyFromScreen(r.X, r.Y, …)` which is already virtual-desktop, but defaults to `SM_CXSCREEN×SM_CYSCREEN` = primary at (0,0) and the tool description says "full primary display". A-8 fixes the default; the *space* is not changing, only being stated. |
| C2 | **Screenshot default output becomes inline image content** (A-7); `output:"file"` stays as an opt-in. | The whole point of A-7. The file path default was a token-saving measure from before clients rendered images; the downscale in A-9 is the replacement for it. |
| C3 | **Default capture stays the primary display** (A-8), `display:"all"` opt-in, `region` wins over `display`. | Upstream defaults to all displays. Ours should not: a 3-monitor union at A-9's 1920 cap is unreadable, and the token cost triples. Reversible later by flipping one default; document it in the tool description and SKILL.md playbook. |
| C4 | **One new tool, `snapshot`** (A-2) — 64 → 65. A-1 extends `window` (`action: list \| active`), A-3/A-4/A-11 ride inside `snapshot`/`screenshot`, A-12 phase 1 is a `window list` field plus `action: desktops` on `window` (no new tool), A-6 is `screenshot(annotate:true)`. `get_state` is kept, unchanged in shape, and its description points at `snapshot`. | Every extra tool is a description the model reads on every call. Actions on existing tools are cheaper than tools. `get_state` has callers in the skill playbooks; breaking its JSON shape buys nothing. |
| C5 | **Snapshot element IDs are per-snapshot labels** — `1..N` in the text output, and `el_N` stays the id `click`/`interact_element` accept. Each `snapshot` clears the entries it issued last time (`_elementCache` is unbounded today, `UIAutomationService.cs:1`); `find_element` ids are unaffected. The response says "ids are valid until the next snapshot". | Upstream labels are indices. Keeping `el_N` as the accepted id means no tool signature changes; scoping eviction to snapshot-issued ids means `find_element` → `interact_element` workflows in flight are not broken by an unrelated snapshot. |
| C6 | **Text is the default snapshot format**, JSON opt-in (`format:"json"`). | 5–10× fewer tokens for the same information (checklist A-2). The JSON form is for tests and programmatic callers. |
| C7 | **Env vars are `WINDOWSMCP_*`** and are read through `ServerOptions` (`ServerOptions.cs:44`, the only reader of process config), surfaced to services as constructor-injected options records, never `Environment.GetEnvironmentVariable` inside a service. | Testability (the option is a record in the test) and one place to document. Adds `ScreenshotOptions(MaxWidth, MaxHeight, Scale, Backend, DisableFlash)` and `SnapshotOptions(MaxElements, Profile)`. |
| C8 | **Every UIA read in new code goes through a `CacheRequest`** and a guarded `TryGet` (D-5's rule), and every traversal is count-budgeted (A-4). A-4 is therefore not a separate deliverable — it is the traversal A-2 is built on, shipped in the same PR, and back-ported to `get_state`/`find_element` in that PR. | Building A-2 without the budget and then retrofitting it is doing the walk twice. The checklist lists A-4 as depending on A-2; in practice it is the other way round. |
| C9 | **New native surface is declared in `NativeMethods.txt` (CsWin32), not `DllImport`**, except COM interfaces (A-12, A-5 phase 2) which follow the vtable-gap rule in `CLAUDE.md` and get the `ShortcutResolver.cs` treatment. | Repo convention. `UIAutomationService.cs:120` has one stray `DllImport` for `GetForegroundWindow`; A-1 replaces it while it is there. |
| C10 | **Tests first, per the `test-agent` workflow in `CLAUDE.md`.** Each item's design note carries the requirement matrix `test-agent` produced in RED; the implementation PR is not opened until the GREEN pass is in the note. Pure logic (classifier, renderer, budget, region maths, sanitiser, downscale maths) gets `Unit` tests; capture and UIA get one `Integration` or `UIAutomation` test each that proves the wiring, not the logic. | The section is heavy on code that only runs on a live desktop. Extracting the pure core is what makes it testable at all, and the test-agent's "design request" output is the mechanism for insisting on it. |
| C11 | **One PR per item, one version bump per phase**, CHANGELOG bullet per item under `[Unreleased]`, `docs-agent` before each PR. Version: phase 1 → 0.8.0 (screenshot behaviour change, C2), phase 2 → 0.8.x, phase 3 → 0.9.0 (new tool). | Matches how D-1…D-9 landed (per-item commits, one close-out PR). Behaviour changes get a minor bump. |

## 3. Order and phases

```
Phase 1  screenshot track     A-7 → A-9 → A-8 → A-11        (+ A-13 anywhere)      ~4 days
Phase 2  window inventory     A-1 → A-13                                            ~2 days
Phase 3  snapshot core        A-2 (with A-4 and A-3 inside)                          ~6 days
Phase 4  annotate             A-6   (needs phase 1 + phase 3)                        ~2 days
Phase 5  long tail (P3)       A-14 → A-12 (phase 1) → A-10 → A-5 (phase 1)          ~9 days
```

Phase 1 goes first because every agent loop starts with a screenshot and A-7 alone changes what
the model can do today. Phases 1 and 2 are independent and can run in parallel branches. Phase 3
is the large one and depends on A-1 (window list in the header) and A-13 (names must serialise).
A-6 is the only item that needs both tracks. Phase 5 items are P3 and each stands alone; the
order inside it is by value per day.

### Dependency graph (checklist "Depends on" column, corrected per C8)

```
A-7 ──► A-9 ──┐
A-8 ──────────┼──► A-6
A-11 ─────────┤
A-1 ──► A-2 ──┘        A-2 contains A-4 and A-3
A-13 ─► A-2            A-1 ──► A-12
A-2 ──► A-5            A-10, A-14 stand alone
```

## 4. Per-item plan

Each item: what changes, the decisions that go beyond the checklist sketch, the RED test matrix
seed (what `test-agent` should be handed), and the done-when bar. "Touches" are as in the
checklist unless corrected.

### Phase 1 — screenshot track

#### A-7 — Screenshot as MCP image content  `P1 · S · ~½ day`

- **Change.** `ScreenTools.Screenshot` returns `CallToolResult` instead of `Task<string>`:
  `Content = [TextContentBlock(metadata JSON), ImageContentBlock(Data=base64, MimeType)]`. SDK
  2.2.0 supports this directly (`WindowsMcpHost.cs:115` already builds a `CallToolResult` in
  the error filter; `ImageContentBlock` is in `ModelContextProtocol.Core`). `output` gains
  `"inline"` (new default) alongside `"file"`; `"base64"` is kept as an alias of `inline` for one
  release then removed.
- **Metadata block** (the contract A-8/A-9/A-11 fill in): `{width, height, format,
  originalWidth, originalHeight, scale, region, displays, selectedDisplays, cursor,
  coordinateSpace, backend, path?}` — fields not yet implemented are simply absent, not `null`.
- **Decision.** The ~1 MB tool-result ceiling some clients enforce is A-9's job; A-7 ships with
  JPEG quality 85 as the inline default format (PNG stays for `file`) so a 1080p capture fits
  before A-9 lands. Document that `format` defaults differ by output.
- **RED matrix seed.** Tool returns two content blocks in order text-then-image; image block
  mime matches `format`; `output:"file"` still returns a path in the text block and no image
  block; `output:"base64"` behaves as `inline`; unknown `output` throws naming the choices;
  `HttpTransportTests` round-trips an image block through the HTTP transport (real host,
  ephemeral port); `ScreenToolsTests` uses a `Mock<IScreenshotService>` returning a 2×2 PNG.
- **Shipped as** ([note](A-7-screenshot-image-content.md)): as planned, except the inline JPEG
  default is quality **90** (A-9 made `quality` an argument, so 85 had no reason to be the
  default), and the metadata block filled out over A-8/A-9/A-11 rather than in one step.
- **Done when.** Claude Code shows the screenshot inline from one `screenshot` call.

#### A-9 — Auto-downscale, scale env, coordinate-scale report  `P1 · S · ~1 day`

- **Change.** `IScreenshotService.CaptureAsync` gains a `CaptureOptions(MaxWidth=1920,
  MaxHeight=1080, Scale=1.0, Format, Quality)` record (replacing the loose `format` parameter);
  `ScreenshotService` resizes with `SKBitmap.Resize(SKSizeI, SKSamplingOptions)` after capture
  and before encode; result carries `OriginalWidth/Height` and the effective `Scale`.
  `WINDOWSMCP_SCREENSHOT_SCALE` (0.1–1.0) multiplies on top, via `ScreenshotOptions` (C7).
- **Pure core.** `ScaleMath.Fit(origW, origH, maxW, maxH, userScale) → (w, h, scale)` as
  `internal static` — the whole A-9 logic, unit-tested without a capture.
- **Text block** gets the upstream sentence verbatim in spirit: `"coordinateScale": 2.0,
  "note": "multiply image pixel coordinates by 2.0 before passing them to click/drag/scroll"`.
  Omitted when scale is 1.0.
- **RED matrix seed.** `Fit` for a 3840×2160 → 1920×1080 scale 2.0; 1000×500 unchanged;
  portrait 1080×1920 → 607×1080; user scale 0.5 on top; env value out of range rejected at
  `ServerOptions.Parse` with the range named; capture of a synthetic bitmap resizes and reports
  the original size; JPEG `quality` honoured (size ordering test on the same bitmap).
- **Shipped as** ([note](A-9-screenshot-downscale.md)): `CaptureOptions(Format, MaxWidth,
  MaxHeight, Scale, Quality, …)`; the result carries `CoordinateScale` (= OriginalWidth / Width)
  instead of an "effective Scale"; the resize is `SKBitmap.ScalePixels` with a Mitchell cubic
  filter (not `SKBitmap.Resize`); and C7's options record ships as `ScreenshotOptions(Scale)`
  only — the other knobs arrive with the items that need them.
- **Done when.** A 4K capture comes back ≤ 1920 wide with `coordinateScale: 2` in the text.

#### A-8 — Multi-display capture and virtual-desktop coordinates  `P1 · M · ~1½ days`

- **Change.** `screenshot`/`ocr` gain `display` (`int[]` indices from `multi_monitor`, or
  `"all"`); `region` keeps the `x,y,w,h` syntax (ours is already documented to the model; not
  worth the churn to `l,t,r,b`) but is now **validated against the virtual screen**
  (`SM_XVIRTUALSCREEN/YVIRTUALSCREEN/CXVIRTUALSCREEN/CYVIRTUALSCREEN`, already in
  `NativeMethods.txt` via `GetSystemMetrics`) and throws `ArgumentException` naming the bounds
  when outside. `region` wins over `display`; default is the primary (C3).
- **Pure core.** `RegionMath.Union(MonitorInfo[] selected)`, `RegionMath.Validate(region,
  virtualScreen)`, `RegionMath.ParseIndices("all" | "0,2")` — `internal static`, no Win32.
  `ParseRegion` moves out of `ScreenTools` into this class so `ocr` and `screenshot` share one
  tested parser.
- **Capture.** `CopyFromScreen` already takes virtual-desktop coordinates, so a union rect that
  starts at a negative `X` works with no change to the copy; the `Bitmap` is sized to the union.
- **Metadata.** `displays` (all, from `EnumerateMonitorsAsync`), `selectedDisplays`, `region`,
  `coordinateSpace:"virtual-desktop"` (C1).
- **RED matrix seed.** Union of two side-by-side monitors; union with a monitor left of primary
  (negative origin); region straddling two monitors passes validation; region one pixel outside
  throws with the virtual-screen rect in the message; `display:[7]` on a 2-monitor system throws
  naming the valid indices; `region` given with `display` → region wins and the text says so;
  `ocr` shares the parser (same error text). `Integration`: `CaptureAsync(display:"all")` returns
  a bitmap whose size equals the virtual screen.
- **Shipped as** ([note](A-8-multi-display-capture.md)): `display` is a **string** argument on
  both tools (`"all"` or `"0,2"`), parsed by `RegionMath.ParseDisplays` (not `ParseIndices`), and
  the virtual screen validated against is the union of `EnumerateMonitorsAsync` rather than the
  `SM_*VIRTUALSCREEN` metrics — one inventory behind `display`, `displays` and the bounds in the
  error message.
- **Done when.** `screenshot(display:[1])` captures the second monitor; a straddling region is
  correct; an out-of-bounds region errors.

#### A-11 — Cursor position in responses and drawn on captures  `P2 · S · ~1 day`

- **Change.** `IInputService.GetCursorAsync() → CursorInfo(X, Y, MonitorIndex)` (the service
  already calls `PInvoke.GetCursorPos`, `InputService.cs:36`; expose it). `screenshot` text block
  gains `cursor`; `snapshot` (A-2) will reuse the same DTO. `include_cursor` (default true) on
  `screenshot`: composite the real cursor with `GetCursorInfo` + `DrawIconEx` (add both to
  `NativeMethods.txt`) onto the GDI bitmap before Skia wraps it; when `GetCursorInfo` reports no
  visible cursor, or the draw fails, draw a 12 px ring with SkiaSharp instead and say
  `cursor.drawn:"ring"`.
- **Pure core.** `CursorMath.MonitorIndexOf(point, MonitorInfo[])`; `CursorOverlay.DrawRing(
  SKCanvas, x, y, scale)` takes the A-9 scale so the ring lands on the downscaled pixel.
- **RED matrix seed.** Monitor index for a point on the second monitor / on no monitor (−1);
  ring drawn at `(x−left)×scale`; `include_cursor:false` leaves the bitmap byte-identical to a
  capture without; metadata always carries `cursor` even when not drawn. `Integration`:
  `GetCursorAsync` returns a point inside the virtual screen.
- **Shipped as** ([note](A-11-cursor.md)): `IInputService.GetCursorPositionAsync() →
  CursorPosition(X, Y)` — no `CursorInfo`, and the monitor index stays in the tool layer
  (`CursorMath.MonitorIndexOf` over the same inventory A-8 already reads). `CursorOverlay.DrawRing(
  SKBitmap, x, y)` takes **no** scale: the ring is painted on the full-resolution bitmap before
  A-9's downscale, so it shrinks with the picture. The metadata field is a top-level
  `cursorDrawn` (`"icon"`/`"ring"`, absent when nothing was drawn), not `cursor.drawn`.
- **Done when.** Metadata reports the cursor and the image shows it.

#### A-13 — Unicode hygiene  `P2 · S · ~½ day` (anywhere; before A-1)

- **Change.** `UiText.Sanitize(string?) → string` in `Services/UiText.cs`: strip U+E000–U+F8FF,
  replace lone surrogates with U+FFFD, drop C0/C1 controls except tab/newline, trim. Applied in
  `UIAutomationService.TryGetName`, `TryGetValue`, `TryGetControlType` (defensive), and in A-1's
  window titles. First step of the note: **measure** what `System.Text.Json` does with a lone
  surrogate on .NET 10 (the checklist leaves it open) and record the answer.
- **RED matrix seed.** Each rule as its own `[Theory]` row; a VS Code-style codicon string
  (`" Explorer"` → `"Explorer"`); an emoji pair survives; a lone high surrogate becomes
  U+FFFD; the sanitised string round-trips through `JsonSerializer` without throwing; null → "".
- **Shipped as** ([note](A-13-unicode-hygiene.md)): applied in `TryGetName`, `TryGetValue`,
  `GetTextAsync`, `get_table` (via the unit-testable `BuildTable`) and the `assert_element
  state=value` observation; `TryGetControlType` is an enum name and was left alone. Window titles
  follow with A-1. The measurement: `System.Text.Json` writes U+FFFD silently, it does not throw.
- **Done when.** An emoji window title and a codicon element name both serialise cleanly.

### Phase 2 — window inventory

#### A-1 — Whole-desktop window inventory  `P1 · M · ~1½ days`

- **Change.** `WindowInfo(Title, Hwnd, Pid, ProcessName, State, Bounds, ZOrder, IsActive,
  IsBrowser, MonitorIndex, DesktopId?)` in `Models/WindowDtos.cs` (`DesktopId` reserved for
  A-12, null until then). `IWindowService.ListAsync(includeMinimized=true,
  includeHidden=false)` and `GetActiveAsync()`. `window` gains `action: list | active` — the
  only actions that do not need `title` (today `ExecuteAsync` throws on a missing title before it
  looks at the action, `WindowService.cs:20`; reorder that).
- **Filter** (the part worth getting exactly right): `EnumWindows` order is z-order; keep
  `IsWindowVisible`; drop `WS_EX_TOOLWINDOW`; drop DWM-cloaked (`DwmGetWindowAttribute
  DWMWA_CLOAKED` — filters UWP ghosts and other-virtual-desktop windows); drop zero-area; drop
  empty titles unless `includeHidden`; drop the shell chrome upstream drops (class
  `Shell_TrayWnd`, `Progman`, `WorkerW`, IME windows). `IsIconic`/`IsZoomed` → State.
  `IsBrowser` = process name in {chrome, msedge, firefox, brave, opera, vivaldi} (a `static
  readonly` set A-5 reuses). Add `EnumWindows`, `IsWindowVisible`, `GetWindowLong`,
  `GetWindowRect`, `GetWindowThreadProcessId`, `IsIconic`, `IsZoomed`, `MonitorFromWindow`,
  `DwmGetWindowAttribute`, `GetClassName`, `GetForegroundWindow` to `NativeMethods.txt`.
- **Pure core.** `WindowFilter.Keep(WindowProbe probe, options) → bool` where `WindowProbe` is a
  record of the raw Win32 facts (visible, exStyle, cloaked, rect, title, className) — the whole
  filter is unit-testable on fake probes; the enumerator only fills probes.
- **RED matrix seed.** Filter rows per rule (tool window, cloaked, zero-area, empty title,
  tray, Progman); z-order preserved; `IsActive` set on exactly one; `IsBrowser` by process
  name; monitor index from bounds; `action:"list"` needs no title, `action:"minimize"` still does;
  `active` returns one window. `Integration`: `ListAsync` returns without throwing and, when run
  interactively, contains the test host's console window title.
- **Done when.** `window(action:"list")` returns every user-visible top-level window in
  z-order; `action:"active"` returns the foreground one.
- **Shipped as** ([note](A-1-window-inventory.md)): as planned, plus `WindowFilter.ActiveOf` so
  `active` is the list's flagged entry (real `ZOrder`) and the choice is testable without a
  desktop; the tool validates action-then-title instead of reordering `ExecuteAsync`; CsWin32
  has no `GetWindowLongPtr`, `GetWindowLong` is the x64 entry; C9's stray `DllImport` is gone
  (the pre-C9 ones in `AuthenticodeInspector`, `LspEnumerator`, `UsnService` and
  `StartupReportService` stay).
  `MonitorFromWindow` was not needed — `MonitorIndex` is `CursorMath.MonitorIndexOf` of the
  window's centre over `EnumerateMonitorsAsync`, so it indexes `multi_monitor` by construction;
  `GetWindowTextLength` joined the `NativeMethods.txt` list instead.

### Phase 3 — snapshot core

#### A-2 — Desktop-wide labelled interactive-element snapshot  `P1 · L · ~5 days` (with A-4, A-3)

The centre of the section. Split into a `Services/UiTree/` folder so each part is testable
without a desktop:

| File | Responsibility | Test type |
|---|---|---|
| `UiNode.cs` | Record of everything read from one element: control type, name, bounds, centre, enabled, offscreen, focused, password, value, range min/max, toggle, expand/collapse, access key, accelerator, legacy role, scroll (A-3), window title, element id | — |
| `UiClassifier.cs` | `Classify(UiNode) → Interactive \| Informative \| Structural \| Scrollable` and `ActionFor(node) → "click" \| "fill" \| "toggle" \| "select" \| "slide" \| "scroll"`. Takes over `UIAutomationService.InteractiveControlTypes` (D-6's set, the checklist's carried-over acceptance test) and adds the LegacyIAccessible role fallback the D-6 note deferred here. `find_element(kind:interactive)` calls it. | Unit, on fake nodes |
| `UiTraverser.cs` | Walks one window under a `CacheRequest` (Name, ControlType, BoundingRectangle, IsEnabled, IsOffscreen, HasKeyboardFocus, IsPassword, AccessKey, AcceleratorKey + Value/Toggle/RangeValue/ExpandCollapse/Scroll/LegacyIAccessible patterns; `TreeScope.Subtree`, `AutomationElementMode.Full`); clips to the window rect (upstream `iou_bounding_box`); every read guarded (D-5); decrements an `ElementBudget` and stops with `Truncated=true` (A-4); per-window try/catch so one dead window does not fail the snapshot | `UIAutomation` (Notepad fixture) + a Unit test of the budget with a fake element source |
| `ElementBudget.cs` | Counter with `Default=500`, `WINDOWSMCP_MAX_TREE_ELEMENTS`, `max_elements` param; exposes `Truncated`, `Limit`, `Note()` — the upstream truncation note text | Unit |
| `SnapshotRenderer.cs` | Text form: window header lines, `├── (x,y) type "name"  [action: fill]  [focused]  [value:"…"]  [v:37%]` rows, the truncation note; JSON form is the DTOs serialised | Unit, golden-string |

- **Tool.** `snapshot(scope:"desktop"|"foreground"|"window", window?, include_tree:false,
  max_elements:500, format:"text"|"json", use_dom:false)` → text block (C6). Header: active
  window, cursor (A-11 DTO), window list (A-1), then per-window interactive list, then
  scrollable list (A-3), then the note if truncated, then `ids valid until next snapshot` (C5).
  `use_dom` is accepted and rejected with "A-5 not implemented" until A-5 lands, so the parameter
  shape is stable.
- **A-3 inside.** `ScrollInfo(VerticalPercent, HorizontalPercent, VerticallyScrollable,
  HorizontallyScrollable)` from `Patterns.Scroll` on `UiNode`; rendered as `[v:37%] [h:0%]`;
  `find_element(kind:scrollable)` gains the same fields on `ElementInfo` (nullable, additive).
- **A-4 back-port.** `get_state` and `find_element(scope:desktop)` switch to `UiTraverser` in
  the same PR, so the budget and the cache request apply to them too. `get_state`'s JSON shape is
  unchanged except for two additive fields `truncated`/`elementLimit`.
- **RED matrix seed.** Classifier: every type in D-6's set is interactive, `Text` is informative,
  `Pane` structural, a `Custom` with legacy role `pushbutton` is interactive, action map per type,
  `Edit` with `IsPassword` renders `[password]` and no value. Budget: stops at N, `Truncated`,
  note text, env override, param override wins over env, 0/negative rejected. Renderer:
  golden strings for one window with three elements, truncated variant, focused/value/toggle/
  range/shortcut metadata. Traverser (UIAutomation): Notepad's editor listed with `action:
  fill` and a centre inside its bounds; snapshot with `max_elements:5` returns 5 and the note;
  a second snapshot invalidates the first's ids but not a `find_element` id issued between.
  Timing: a snapshot of the Notepad fixture under the cap completes under a generous bound.
- **Done when.** One call returns every visible window's interactive elements with centres,
  actions and metadata in the text form, bounded by the cap, and the centres work unchanged with
  `click`/`type`/`scroll`.
- **Shipped as** ([note](A-2-desktop-snapshot.md)): as planned in two test-first cycles (pure core,
  then traversal); the `el_N` ids are the labels (no separate numbers); Document → fill; the
  cache request must cache pattern *properties* too (the GREEN pass caught every pattern read
  silently null); `find_element` keeps its own walk and does not yet fill `ElementInfo.Scroll`.
  The A-4 back-port is narrower than planned: `get_state` keeps its own `BuildTree` recursion and
  only gained the `ElementBudget` (same foreground root, same three-level shape, additive
  `Truncated`/`ElementLimit` on the root); neither it nor `find_element` was switched to
  `UiTraverser`.

### Phase 4 — annotate

#### A-6 — Annotated screenshot  `P2 · M · ~2 days`

- **Change.** `screenshot(annotate:false, grid_columns:0, grid_rows:0)`. When `annotate` is
  true the tool runs the A-2 traverser first (same scope rules, `foreground` default), then
  captures, then draws with SkiaSharp on the captured bitmap: 2 px box per element in a fixed
  12-colour palette by index, a label chip with the element's snapshot label at the box's
  top-left clamped inside the image, the cursor (A-11), and the grid with coordinate captions.
  The text block includes the same element list the snapshot would, so label N in the image is
  row N in the text from the **same call**.
- **Pure core.** `Annotator.Draw(SKCanvas, IReadOnlyList<UiNode>, scale, grid)`; all geometry
  in a testable `LabelPlacement.Clamp(box, chipSize, imageSize)`.
- **RED matrix seed.** Chip clamped at the four edges; colour index cycles; grid caption
  coordinates are virtual-desktop values, not image pixels, when scale ≠ 1; `annotate:true`
  with `output:"file"` writes the annotated bytes; element ordering identical between text
  and drawing (assert on a fake node list rendered to a bitmap, sample pixel colours).
- **Done when.** Label N sits on element N from the same call.
- **Shipped as** ([note](A-6-annotated-screenshot.md)): annotations travel in `CaptureOptions` and are
  drawn after the downscale on a copy; the walk is always `scope=desktop` (a capture can show
  several windows); the cursor is A-11's, not redrawn; `LabelPlacement.Clamp` shipped as
  `Annotator.ChipRect`; grid divisions capped at 64.

### Phase 5 — long tail

#### A-14 — Flash overlay and snapshot profiling  `P3 · M · ~1½ days`

- Profiling first (half a day, no risk): `Stopwatch` per stage in `snapshot`/`screenshot`
  (windows, tree, capture, resize, encode, render) → `ILogger` Debug to stderr when
  `WINDOWSMCP_PROFILE_SNAPSHOT` is set, and `captureMs`/`stages` in the JSON metadata always.
- Flash second: a `WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOPMOST|WS_EX_NOACTIVATE` window on a
  dedicated STA thread, `UpdateLayeredWindow` with a Skia-drawn glow, torn down on a 3.5 s timer
  **and** at the start of the next capture. **On by default under both transports** — the
  overlay is the only signal a person at the target machine gets that a remote agent just
  captured their screen, so it matters *more* under HTTP, not less. One switch,
  `WINDOWSMCP_DISABLE_FLASH` plus a `--no-flash` flag (HTTP deployments are configured by
  flags), and a silent no-op when there is no interactive window station (Task Scheduler,
  session 0) — that is a robustness case, not a policy one, and is independent of transport.
- **RED seed.** Stage timings present and non-negative; flash window class not present in the
  A-1 window list (it is a tool window — the filter test covers it); a capture taken during the
  flash does not contain the glow (`Integration`, pixel sample at the border).
- **Shipped as** ([note](A-14-flash-and-profiling.md)): `--flash on|off` / `WINDOWSMCP_FLASH` and
  `--profile-snapshot on|off` / `WINDOWSMCP_PROFILE_SNAPSHOT` (the parser has no valueless flags,
  so no `--no-flash`); timings only when profiling is on, not always; logged at Information, not
  Debug (the stderr logger's minimum); `flash` metadata reports the outcome.

#### A-12 — Virtual desktops, phase 1 only  `P3 · L (phase 1: S) · ~1 day`

- Documented `IVirtualDesktopManager` (`CLSID_VirtualDesktopManager`, three methods:
  `IsWindowOnCurrentVirtualDesktop`, `GetWindowDesktopId`, `MoveWindowToDesktop`) declared per
  the vtable-gap rule; names from `HKCU\…\VirtualDesktops\Desktops\{guid}\Name` via the existing
  `IRegistryService`. Fills `WindowInfo.DesktopId` and `IsOnCurrentDesktop`, adds `window(
  action:"desktops")` → `{current, all:[{id, name}]}`. Phase 2 (the undocumented internal
  interface) is explicitly **not** planned — log it as a new checklist item if wanted later.
- **RED seed.** COM declaration smoke (`Integration`: create the manager and query the test
  host's own window); registry name parsing on fake `IRegistryService` values (missing name →
  `"Desktop N"`); `DesktopId` appears in `window list`.
- **Shipped as** ([note](A-12-virtual-desktops.md)): phase 1 only, with fallbacks the plan did not
  foresee — this Windows 11 build has no `VirtualDesktopIDs`/`CurrentVirtualDesktop`, so the list
  comes from the `Desktops` subkeys and the current desktop from the foreground window's; the
  envelope is the full `VirtualDesktopInfo`; no `IsOnCurrentDesktop` on `WindowInfo`.

#### A-10 — Alternative capture backend  `P3 · M–L · ~3 days`

- `IScreenCaptureBackend { string Name; bool TryCapture(rect, out SKBitmap) }` with `Gdi`
  (today's code moved) and `WindowsGraphicsCapture` (`Windows.Graphics.Capture` is available
  under the `net10.0-windows10.0.19041.0` TFM without a new package; needs a `GraphicsCaptureItem`
  for the monitor via `IGraphicsCaptureItemInterop` — one COM interface, vtable-gap rule).
  `WINDOWSMCP_SCREENSHOT_BACKEND=auto|gdi|wgc`, `auto` = wgc then gdi; the metadata `backend`
  field (reserved in A-7) reports which produced the frame.
- Risk: WGC draws a yellow capture border on some builds and needs
  `GraphicsCaptureSession.IsBorderRequired=false` (Win11 22H2+); WGC of a single monitor gives
  monitor-local pixels, so the union/region maths of A-8 has to crop per monitor and compose.
  Prototype the monitor path in a scratch console app before committing to the design note.
- **RED seed.** Backend selection by env and by fallback (fake backends); `auto` falls back
  when the first throws; metadata names the backend; `Integration`: both backends produce
  same-sized bitmaps of the primary.

#### A-5 — Browser DOM mode, Chromium only  `P2 · L · ~3 days`

- `use_dom:true` on `snapshot`: for each A-1 window with `IsBrowser`, find
  `RootWebArea` (`cf.ByAutomationId("RootWebArea")`), traverse only that subtree with
  `UiTraverser` and a Chromium role map (LegacyIAccessible roles → classifier), collect text
  nodes into a `DomText` block, report the document's scroll percent. Browser chrome (address
  bar, tab strip) is excluded by construction. Port upstream's `_dom_correction` dedupe rules
  one at a time, each with a test. Firefox/IA2 is a documented follow-up (new checklist item),
  not part of this.
- **RED seed.** Role map rows; chrome excluded when `RootWebArea` present; falls back to the
  normal walk with a note when it is absent; `DomText` order is document order. `UIAutomation`
  test needs an Edge window on a local page served by `LocalHttpServerFixture` — a new fixture
  that launches `msedge --app=<url>` and closes it.

## 5. Effort and sequencing summary

| Phase | Items | Days | Version | Unlocks |
|---|---|---|---|---|
| 1 | A-7, A-9, A-8, A-11, A-13 | 4½ | 0.8.0 | usable screenshots; A-6 half |
| 2 | A-1 | 1½ | 0.8.1 | B-6, B-8, B-10, A-12 |
| 3 | A-2 (+A-4, A-3) | 5 | 0.9.0 | A-6, A-5, B-3/B-4 element targets |
| 4 | A-6 | 2 | 0.9.1 | — |
| 5 | A-14, A-12 ph1, A-10, A-5 ph1 | 8½ | 0.9.x / 0.10.0 | C-5 (DOM part) |
| | **Total** | **~21½ days** | | |

Estimates assume one implementer, the test-first loop (RED ≈ 25 % of each item), and the D-5
experience that UIAutomation-category tests cost as much as the code. Phase 1 and 2 in parallel
branches saves ~1½ days of wall clock.

## 6. Risks and how the plan absorbs them

- **`CallToolResult` return type and the tool-discovery source generator.** A-7 changes a tool
  method's return type; `WithToolsFromAssembly` must still discover it and the HTTP transport
  must still serialise it. Prove it in the first hour of A-7 with `HttpTransportTests` before
  touching anything else.
- **UIA cache requests and FlaUI 5.** `CacheRequest` exists in `FlaUI.Core`; the pattern-in-cache
  behaviour (`Patterns.Value` on a cached element) needs `AutomationElementMode.Full` and is the
  first thing A-2's spike verifies on the Notepad fixture. If it does not deliver the COM-call
  reduction, A-4 still ships the budget — the cache is an optimisation, the budget is the
  guarantee.
- **STA.** Everything UIA already runs through `OnStaAsync`; the traverser must stay on that
  thread and never await inside the walk. The flash overlay (A-14) needs its own STA thread with
  a message pump, which is why it is last.
- **Token cost creep.** Every metadata field added in phase 1 appears on every screenshot. Keep
  the text block one line of JSON, omit absent fields, and re-measure the per-call token cost at
  the end of phase 1 against today's `file` mode.
- **Behaviour changes** (C2, and A-8's validation turning silent clipping into an error) go in
  CHANGELOG under Changed with the migration in one sentence, and SKILL.md's playbooks are
  updated in the same PR (docs-agent will catch what is missed).

## 7. Decisions that need a human before phase 1 starts

1. **C3** — default capture primary-only (recommended) or all displays like upstream.
2. **C2** — inline image as the new default, or keep `file` as default and make inline opt-in.
   Recommended: inline; the file mode's reason (token cost) is what A-9 removes.
3. **C4** — `snapshot` as a new tool (recommended) or fold it into `get_state` with a
   `format` parameter. Recommended: new tool; `get_state` keeps its contract.

(A-14's flash default was a fourth question in the first draft — "off under HTTP" — and was
dropped: transport is a poor proxy for "is anyone watching", and the overlay is worth most when
the controller is remote. It is on everywhere, with one switch.)

Everything else in section 2 is a recommendation the individual design notes can overturn with
a stated reason.
