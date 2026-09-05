## [Unreleased]

### Added

- **`window(action:"list"|"active")` — the whole-desktop window inventory** (parity A-1). `list`
  returns every user-visible top-level window in z-order (`ZOrder` 0 = frontmost): `Title`
  (sanitised, A-13), `Hwnd`, `Pid`/`ProcessName`, `State` (`Normal|Minimized|Maximized`, by
  name), `Bounds` in virtual-desktop pixels, `IsActive`, `IsBrowser` (chrome/msedge/firefox/brave/
  opera/vivaldi), `MonitorIndex` into `multi_monitor`'s list (-1 when the window's centre is on no
  monitor, e.g. minimized), and `DesktopId` (reserved for A-12, null). `active` returns the
  foreground window as the list sees it, or `{"found":false}`. `include_minimized` (default
  true) and `include_hidden` (default false: untitled windows) narrow or widen the list. The
  filter is a pure `WindowFilter` over `WindowProbe` records: invisible, tool windows (unless
  `WS_EX_APPWINDOW`), DWM-cloaked (UWP ghosts, other virtual desktops), zero-area, shell chrome
  (taskbars, Program Manager, WorkerW, IME) and untitled windows are dropped. New
  `IWindowService.ListAsync`/`GetActiveAsync`, `WindowInfo`/`WindowProbe`/`WindowState`. The
  `window` tool now validates the action first and only the acting actions need a `title`; an
  unknown action is an error naming the six actions before any window is touched (it used to
  reach the service, which reported `Success:false` whenever no window carried that title).
  `UIAutomationService`'s last stray `DllImport` (`GetForegroundWindow`) is retired for CsWin32
  (roadmap C9). Design note: `docs/design/A-1-window-inventory.md`.

### Changed

- **`screenshot` returns the image as MCP image content** (parity A-7). The tool result is now a
  text block with one JSON metadata object (`{width, height, format, coordinateSpace:
  "virtual-desktop", region, path?}` — A-8/A-9/A-11 below add `displays`, `originalWidth`/
  `originalHeight`, `cursor` and the scale fields) followed by an `ImageContentBlock`, so Claude
  Code and Claude Desktop render the capture inline and the model can look at it in the same call.
  **Migration:** `output` defaults to `inline` (was `file`; ask for `file` explicitly to get a
  path, `base64` is an alias of `inline` for one release), `format` defaults to `auto` (jpeg for
  inline, png for file; was always png), and the old `data_base64` JSON key is gone. Arguments
  are validated before any capture; unknown `output`/`format` name the accepted values. The
  reported format and mime type describe what was encoded, never the request. Design note:
  `docs/design/A-7-screenshot-image-content.md`.

- **`screenshot` downscales to 1920×1080 by default and reports the coordinate scale** (parity
  A-9). A 4K capture was a ~10 MB PNG; it is now fitted inside `max_width` × `max_height`
  (default 1920×1080, 0 = no limit) with a Mitchell cubic resample, then shrunk further by the
  call's `scale` (0–1] and the server's new `--screenshot-scale` / `WINDOWSMCP_SCREENSHOT_SCALE`
  (0.1–1.0, honoured by both transports). Metadata always carries
  `originalWidth`/`originalHeight`; when anything was scaled it adds `coordinateScale` and a
  `note` telling the model to multiply image pixel coordinates by that factor before `click`/
  `drag`/`scroll`, and omits both otherwise. New `quality` argument (1–100, default 90) for JPEG.
  `ocr` is unaffected — it always captures at full resolution. `IScreenshotService.CaptureAsync`
  now takes a `CaptureOptions` record and `ScreenshotResult` gained the original size and scale.
  Design note: `docs/design/A-9-screenshot-downscale.md`.

- **`screenshot` and `ocr` capture any monitor, and regions are validated, not clipped** (parity
  A-8). New `display` argument: `all`, or comma-separated zero-based indices in `multi_monitor`
  order (`1`, `0,2`); several are captured as their union, a monitor left of or above the
  primary keeps its negative origin. The default stays the primary display (cheaper than
  upstream's all-displays default; one flag flips it later). `region` (`x,y,w,h`, virtual-desktop
  pixels) wins over `display` and is now checked against the virtual screen — outside it is an
  error naming the bounds, where it used to be silently clipped or fail deep in GDI; an invalid
  `display` errors even when `region` wins. Metadata now **always** carries `region` (the rect
  actually captured — image (0,0) is its origin) and `displays` (every monitor's bounds), adds
  `selectedDisplays` when `display` picked the rect, and the `note` gains the offset form
  (`virtual-desktop x = 1920 + imageX × 2, …`) for any capture that does not start at (0,0).
  One shared parser (`RegionMath`) for both tools; a region with a non-integer part or a
  non-positive size is a named `ArgumentException` rather than a `FormatException`.
  `multi_monitor` indices are now the position in the returned list, so a monitor whose info
  query fails cannot leave a gap between what `display` selects and what the metadata reports.
  Design note: `docs/design/A-8-multi-display-capture.md`.

- **`screenshot` reports the mouse pointer and paints it onto the capture** (parity A-11). Metadata
  always carries `cursor {x, y, monitorIndex}` (virtual-desktop pixels; `-1` = on no monitor),
  drawn or not. New `include_cursor` argument (default true): the real cursor image is composited
  through `DrawIconEx` at its hotspot, and when the cursor is hidden or the composite fails a
  two-tone ring (white outside, black inside) is drawn instead; `cursorDrawn` says `icon` or `ring`
  and is absent when the pointer was outside the captured rect. The overlay goes onto the
  full-resolution bitmap before the A-9 downscale, so it shrinks with the picture. The tool reads
  the position once and hands it to the capture, so the numbers and the mark cannot disagree.
  `ocr` never draws it. New `IInputService.GetCursorPositionAsync` / `CursorPosition`; `CaptureOptions`
  gained `IncludeCursor` and `Cursor`, `ScreenshotResult` gained `CursorDrawn`. Design note:
  `docs/design/A-11-cursor.md`.
- **UI text is sanitised before it reaches the model** (parity A-13). Element names and values,
  `get_text`, `get_table` headers and cells, and the `assert_element state=value` observation now
  go through one `UiText.Sanitize`: Private Use Area glyphs (VS Code's codicons, icon fonts — BMP
  and both supplementary planes) are stripped, lone UTF-16 surrogates become U+FFFD explicitly
  (measured on .NET 10: `System.Text.Json` was already rewriting them to U+FFFD *silently*, so the
  model got a value that differed from the UI with nothing saying so), C0/C1 controls other than
  tab/LF/CR are dropped, and the result is trimmed. Valid emoji, ZWJ sequences, combining marks
  and RTL text are untouched. `assert_element state=value` compares the sanitised value, so a
  value read back from `find_element`/`get_text` matches. `get_table` columns without a header
  element are now `""` instead of null. Design note: `docs/design/A-13-unicode-hygiene.md`.

### Fixed

- **`find_element` and `wait_for` survive a stale element, and can be pinned to one window**
  (parity D-5). The find path walked the whole desktop in one `FindAllDescendants` and read
  `Name` / `ControlType` / bounds **unguarded**, so a single element that died between the walk and
  the read — a fading tooltip, a closing menu, a virtualised row — failed the entire call
  (`find_element(kind:"any")` errored on a busy desktop while `kind:"text"` worked). Every read is
  now guarded and each element is evaluated inside a catch: a dead element is skipped, never fatal.
  New `scope` parameter — **`foreground` is now the default** (behaviour change: the old implicit
  scope was the whole desktop, which is what made the tool slow and fragile), `window` with a
  `window` title pins a multi-step workflow to one window regardless of what steals focus (matched
  exact-then-substring on the window's UIA name; an unmatched title lists the open windows), and
  `desktop` restores the old whole-desktop walk. The kind filter is pushed into a UIA `OrCondition`
  so the provider marshals fewer elements. `wait_for` gained `kind`/`scope`/`window`, re-resolves
  the window on every poll (so it doubles as "wait for that app to open"), **retries a poll that
  throws** instead of ending the wait on the first transient failure, polls at least once
  (`timeout_ms: 0` now means "check now"), and throws rather than reporting a misleading `null`
  when *every* poll failed. The retry loop is a pure `PollAsync` with unit tests.
- **`find_element(kind:"interactive")` sees inputs, dropdowns, list rows, tabs and sliders**
  (parity D-6). The filter was four control types (Button, CheckBox, Hyperlink, MenuItem), so a text
  box was not "interactive" — the Claude Code prompt box is an `Edit` and could not be found that
  way. Replaced with upstream's `INTERACTIVE_CONTROL_TYPE_NAMES` plus `Document` (17 types, pinned
  by a test and spelled out in the tool description). Upstream's `TextBox` is omitted — no such UIA
  control type; it is `Edit`, already in the set.
- **`find_element` and `wait_for` no longer return off-screen elements by default** (parity D-7).
  Measured on a normal desktop, 18 of 21 `kind:"text"` hits were `IsOffscreen` — and because the
  20-result cap ran *before* any filtering the caller could do, on-screen matches were crowded out
  of the results entirely. Off-screen elements and empty bounds are now dropped **before** the cap;
  `wait_for` can no longer succeed on an element that has not been shown yet. `include_offscreen:true`
  restores the old behaviour. Upstream's `Edit` exception is kept: an `Edit` with real bounds stays
  in the results, because Chromium/WebView2 over-report it as off-screen while it is still the
  right target for `type`.
- **Background jobs decode their CLIXML stderr too** (D-9, the leftover D-8 deliberately left
  open). `JobService` pumps stderr into a bounded buffer as chars arrive, so it never had a whole
  document to decode and `job output` still returned raw `<Objs>` XML for a job that wrote a warning
  or re-enabled progress. Now decoded **once** when the job finishes — in the monitor, before the
  state flips to a terminal value, so no reader can see a finished job together with raw XML — via a
  new `BoundedTextBuffer.ReplaceAll`, which keeps `job status`'s `StderrChars` agreeing with what
  `job output` returns at no per-read cost. A *running* job's stderr is decoded on read, which works
  because `ClixmlStderr` now retries on everything up to the last `</Objs>` and drops a trailing
  partial document (that also helps the foreground tool when a child is killed mid-flush). A stream
  with no complete document still passes through raw, so this can never swallow non-CLIXML output.
- **`powershell` no longer ships the CLIXML progress stream to the model** (D-8). Windows PowerShell
  5.1 wraps every non-stdout stream in CLIXML when stderr is redirected, so each call carried
  ~0.6–3 KB of XML the model read and ignored (measured: a one-liner with one `Write-Progress`
  produced 596 characters of it). Two layers: `$ProgressPreference='SilentlyContinue'` in the
  invocation preamble kills it at the source — shared with background jobs, and a welcome speed-up
  for `Invoke-WebRequest`/`Invoke-RestMethod` — and the new `ClixmlStderr` decoder turns whatever
  remains into readable text (`WARNING: careful`), dropping progress records even when a script
  re-enables them. Non-CLIXML and unparseable CLIXML pass through raw. `Errors[]`, `Success` and
  the `PSResult` shape are unchanged; `ExtractErrors` now shares the same parser so the two cannot
  drift.
- **Stripped host environment no longer breaks child processes.** Claude Desktop (1.46) launches
  stdio servers with ~18 variables and `PATHEXT=.CPL`, so the `powershell` tool could not resolve
  `git`, `winget`, `dotnet`, `wsl` (no `.EXE` in the search list) and `docker mcp` panicked on a
  missing `ProgramData`. `Hosting/EnvironmentRepair` now runs first thing in `Main`: it fills
  missing variables from the registry (machine, then user; the two `Path` values joined) and
  well-known folder defaults, and corrects `PATHEXT` when it cannot resolve an `.exe`. Values the
  host did set are never overwritten (only `PATHEXT` is corrected). Repaired names are logged to
  stderr once at startup. Unit-tested in `EnvironmentRepairTests`.
- **`shortcut` / `key` accept letters, digits, punctuation, and bare keys** (parity D-1). The
  19-name key map is replaced by `Services/ShortcutParser`: named keys and aliases (`win`/`windows`/
  `super`, `return`, `del`, `printscreen`, `capslock`, numpad and media keys, `f1`–`f24`), single
  characters (`a`–`z` and `0`–`9` directly; anything else through `VkKeyScan`, which also supplies
  the layout's Shift state), `plus` for the `+` key, and single-part chords (`win` opens Start). So
  `ctrl+c`, `ctrl+shift+s`, `win+r`, `ctrl+1` and `key("a")` all work, and an unknown token is named
  in the error. The parser is pure and unit-tested (`ShortcutParserTests`).
- **`interact_element` implements every action it advertises** (parity D-2). `click` (Invoke →
  SelectionItem → Toggle → physical click at the element's centre), `invoke`, `toggle`, `select`
  (with a value: expand the container and pick the child item by name), `focus`, and `type` (a
  writable ValuePattern replaces the value, otherwise keyboard entry at the caret). Every branch
  either acts or throws `NotSupportedException` naming the pattern and control — the old code
  silently no-op'd when a pattern was missing and still answered `"interacted"` — and the tool now
  returns `{ElementId, Action, Method, Detail}` saying which pattern or fallback fired.
  `IInputService` is injected into `UIAutomationService`; input is sent off the UIA STA thread.
  Verified live against Notepad (`UIAutomationServiceTests`).
- **Cursor placement on secondary monitors** (parity D-3). `InputService` scaled coordinates by the
  primary monitor's size but sent them as virtual-desktop absolutes, so every `click`/`drag`/`hover`/
  `scroll` on a second monitor landed elsewhere and negative coordinates were unreachable. It now
  places the cursor with `SetCursorPos` (physical virtual-desktop pixels, origin = the primary's
  top-left — one coordinate space with UIA bounds and `multi_monitor` under Per-Monitor-V2) and
  reads it back: a point Windows clamped (off any monitor) throws instead of clicking somewhere
  else. The four mouse tools' descriptions now state the coordinate space. An integration test
  hovers to every monitor's centre and asserts the exact position.
- **`assert_element` implements every state it advertises** (parity D-4). `focused` (keyboard
  focus, or identity with UIA's focused element) and `value` (new `expected` parameter; exact match
  against the ValuePattern value, else the Name — the same read as `get_text`) now work instead of
  throwing `Unknown assertion state`; `exists` is a real liveness probe; `visible` also requires
  non-empty bounds. A FAIL names what was observed (`focus is on Button 'Save'`, `value is 'x'
  (from ValuePattern)`, `toggle state Off`, `element no longer available` once the window closed —
  a dead Win32 window's element answers reads with defaults rather than throwing, so the probe is
  the ProcessId). Optional properties a provider omits (modern Notepad's document has no
  `IsOffscreen`) no longer throw. `IUIAutomationService.AssertElementAsync` returns `AssertResult`.
  Verified live against Notepad and a killed Character Map window (`UIAutomationServiceTests`).
- **Doc drift after D-1/D-2/D-3 and the environment repair.** `docs/architecture/OVERVIEW.md` still
  described `interact_element` as "toggle, select, or invoke" and `key` as name-only, and
  `COMPONENTS.md`'s tool tables said the same; `COMPONENTS.md`'s `Program.cs` startup sequence
  predated `EnvironmentRepair`, its DTO table was missing `InteractResult`, and its NuGet table
  said "latest" for pinned versions while omitting `System.ServiceProcess.ServiceController`;
  `ARCHITECTURE.md` quoted the pre-D-3 `click` description and left `EnvironmentRepair` out of the
  `Hosting/` file list; `DATAFLOW.md`'s `AssertElement` flow still claimed `value` and `focused`
  work (they threw at the time — parity item D-4, fixed above). The item counts in `todo.md` and
  the parity checklist now say 51 / eight defects after D-4…D-8 were logged. Tool counts (64
  tools, 19 classes, 36 services) were already correct everywhere and are unchanged.

### Added

- **`--screenshot-scale <0.1-1.0>` / `WINDOWSMCP_SCREENSHOT_SCALE`** (parity A-9): a process-wide
  multiplier on every screenshot's own `scale`, for both transports — the cheap way to shrink
  what the model sees on a large desktop. Registered as a `ScreenshotOptions` singleton in
  `AddWindowsMcp`, which now takes the parsed `ServerOptions`. `BuildHttpApp` gained an optional
  `configureServices` seam (applied after `AddWindowsMcp`) so the transport tests can swap the
  capture service for a fake and prove the screenshot surface headless.
- **`test-agent` subagent (dev infrastructure).** `.claude/agents/test-agent.md` — an Opus-model
  Claude Code subagent that owns `tests/WindowsMcp.Tests` and enforces test-first work. Given the
  requirements for a change (the ask, a `docs/design` note, a parity-checklist item's "Tests." /
  "Done when." lines, a tool `[Description]`), it writes a requirement → test matrix and the
  failing xUnit tests **before** any production code exists, adding only the minimal
  `Abstractions` stubs (interface members, DTO records, `NotImplementedException` bodies) that keep
  the tree compiling. Run again after the implementation it measures coverlet coverage of the
  changed files, closes every open matrix row, proves the tests bite with a reverted one-line
  break, and flags mocked-only PowerShell/WMI paths that need an `Integration` sibling (the
  `disk_inspect` lesson in `todo.md`). It never writes feature logic and never commits.
  `CLAUDE.md` gains a "Test-first workflow" section and "Adding a tool" now starts with it.
- **`docs-agent` subagent (dev infrastructure).** `.claude/agents/docs-agent.md` — an Opus-model
  Claude Code subagent to run after any change. It derives the facts from the code (tool count and
  names, tool classes, services, interfaces, DTOs, CLI flags, version lockstep, publish flags) and
  brings README, `docs/architecture/*`, this changelog's `[Unreleased]`, the windows skill, the
  parity checklist, design notes, and tool `[Description]` strings back in line; `CLAUDE.md` drift
  is reported, not edited. It never changes behaviour and never commits.
- **Background PowerShell jobs** (63 → 64 tools). `powershell` gains `background: true`: instead
  of waiting, it starts the command as a job and returns `{Id, Pid, State}` immediately — the
  right pattern for silent installers, DISM, and anything longer than the foreground backstop.
  A new `job` tool manages them (`status` | `output` | `cancel` | `list`). Jobs run concurrently
  **outside** the foreground PowerShell serialization gate (`IJobService`/`JobService`): max 8
  running (new starts rejected when full), a 60-min per-job backstop that tree-kills runaways as
  `timedOut`, stdout/stderr captured into bounded ~1 MB/stream buffers (`BoundedTextBuffer`,
  oldest chars trimmed with counters surfaced), and the ~32 most recent finished jobs retained
  before eviction. Unknown job ids answer `found:false`/`cancelled:false` rather than erroring.
- **MCP progress heartbeats on foreground `powershell` calls.** While a command runs (or waits
  behind the serialization gate), the tool reports an `IProgress<ProgressNotificationValue>`
  heartbeat every 10s — spec-compliant clients that sent a `progressToken` reset their request
  timeout on progress, so long commands no longer die to client-side timeouts. No schema change
  (the SDK binds the parameter outside the tool's JSON schema) and a no-op sink when the client
  sends no token.
- `Services/PowerShellInvocation.cs` — the powershell.exe invocation builder (exe path, common
  flags, UTF-8 encoding preamble, `-EncodedCommand` with temp-`.ps1` fallback, stdin-redirect
  start-info) extracted from `PowerShellService` and shared with `JobService`, so background jobs
  spawn children exactly like foreground calls.
- **Streamable HTTP / HTTPS transport alongside stdio** (`--transport http`). The same exe now
  listens on a TCP port — `--port` (default 8765), `--bind` (default all interfaces) — so a
  client on another machine, e.g. Claude Code driving an RDP session host, can use it. MCP
  endpoint at `/mcp`, stateless Streamable HTTP, HTTP/1.1. `--cert-thumbprint <sha1>` resolves a
  certificate from `LocalMachine\My` then `CurrentUser\My` (private-key access is probed up
  front; the error names the key-ACL fix) and makes the port **HTTPS only**. `--api-key` /
  `WINDOWSMCP_API_KEY` gates every path with a constant-time bearer check; the server **refuses
  to start off-loopback without a key** (every tool would otherwise be open to the network) and
  warns when serving plain HTTP off-loopback. Every option has a `WINDOWSMCP_*` env fallback;
  `--help` prints them. No arguments still means plain stdio, so the plugin's `.mcp.json` is
  unchanged. README: "Run over HTTP/HTTPS (remote)".
- `src/WindowsMcp/Hosting/` — `ServerOptions` (pure, exhaustively unit-tested parser),
  `WindowsMcpHost` (the service/MCP wiring both transports share — registrations, identity,
  the `ToolErrors` filter, tool discovery — plus the HTTP host factory) and `CertificateLocator`.
  `HttpTransportTests` starts the real HTTP host in-process on an ephemeral port and drives it
  with the SDK client: handshake + tool list, 401 on every path without / with a wrong key, HTTPS
  with an ephemeral certificate (plaintext refused on that port), and a `confirm:true` refusal
  surfacing verbatim — the first test to exercise the DI wiring end to end.
- **Claude-in-Actions guard foundation (CI / dev infrastructure).** An agent-immutable
  `claude-guard` workflow (`workflow_run`-triggered so it always runs from `main`, a PR cannot edit
  its own gate) that checks any future automation PR against a docs-only allowlist, an `src/**`
  capability guard, one-concern caps, and a `..`-traversal reject — posting a `claude-guard`
  check-run (fail-closed on error). Backed by a unit-tested policy script
  (`.github/scripts/claude-guard.sh`, 13 tests, run in CI via `guard-tests.yml`), plus `CODEOWNERS`,
  an activation runbook, and the Phase-2 design spec/plan under `docs/superpowers/`. Pilot Claude
  auth is `ANTHROPIC_API_KEY` (a service credential with a Console spend cap). Part of the
  human-gated "Claude-in-Actions" doc-drift bot pilot; the bot itself (maintenance workflow +
  digest) is pending credential provisioning. Design survived two adversarial review rounds
  (Claude-opus + cross-model Gemini/OpenAI), which caught and fixed a workflow script-injection and
  a rename bypass before merge.

### Changed

- **Single-file publish → `bundle/`; `dist/` retired (2026-09-04).** `scripts/build-release.ps1`
  now runs from any directory and publishes ONE file, `bundle/WindowsMcp.exe`. It adds
  `IncludeNativeLibrariesForSelfExtract=true` — the previously documented command left
  `libSkiaSharp.dll` and `aspnetcorev2_inprocess.dll` loose beside the exe, so the exe alone was
  not portable — and `DebugType=none`, then deletes the `libSkiaSharp.pdb` the SkiaSharp package
  emits regardless of publish flags. `bundle/` is gitignored (no binaries in the repo, by
  decision), and every `dist/` reference in README, `CLAUDE.md`, the version-bump skill,
  `todo.md`, and the parity checklist now points at `bundle/`.

- **Docs consolidated (2026-09-04).** Removed the executed `docs/superpowers/` specs and plans,
  the Python-era `docs/plans/` testing design, the generated dependency-graph / unused-analysis
  artifacts, and the Python-era `security-reviewer` agent. The feature backlog against the
  upstream Python server now lives in `docs/upstream-parity-checklist.md`. README, `CLAUDE.md`
  and `docs/architecture/*` re-aligned to the current surface (64 tools incl. `process_inspect`,
  `integrity`, `fs_changes`, `watch`; 36 service interfaces; `Hosting/` layout; SDK 2.2.0), the
  `CLAUDE.md` redeploy recipe rewritten around an explicit MCP registration instead of the
  upstream maintainer's marketplace clone, `version-bump` skill rewritten for the C# release
  flow, and the `windows` skill's obsolete temp-`.ps1` gotcha replaced (multi-line PowerShell has
  been passed whole via `-EncodedCommand` since 0.7.1).
- **Foreground PowerShell execution backstop 10 → 15 minutes** (`PowerShellService`). The
  backstop still arms only after the serialization gate is acquired, so it bounds execution,
  not queue-wait.
- `ModelContextProtocol` 1.0.x → 1.4.x, plus `ModelContextProtocol.AspNetCore` 1.4.x and a
  `Microsoft.AspNetCore.App` framework reference. The project SDK stays `Microsoft.NET.Sdk`
  (no `web.config` / `wwwroot` artefacts); the publish command is unchanged. The self-contained
  single-file exe grows from ~56 MB to ~66 MB (ASP.NET Core shared framework, compressed).
- Logging is stderr-only in both modes; HTTP mode additionally mutes the SDK's per-request
  server chatter to `Warning` (stateless mode builds a fresh `McpServer` per request).
- The `## [Unreleased]` section was buried between 0.6.1 and 0.6.0; moved to the top.
- **Target framework .NET 9 → .NET 10** (`net10.0-windows10.0.19041.0` in all three projects
  and in `Directory.Build.props`). `global.json` now pins a .NET 10 SDK (`10.0.100`,
  `rollForward: latestFeature`) — building requires the .NET 10 SDK; end users are unaffected
  (self-contained exe). README / CLAUDE.md / `docs/architecture` updated to say .NET 10.
- `Windows-mcp.sln` → `Windows-mcp.slnx` (the XML solution format). The old `.sln` is removed
  rather than kept alongside: two solution files in the root make a bare `dotnet build` fail with
  `MSB1011`. CI (`ci.yml`) and `ServerInfoTests.RepoRoot()` (which located the repo by the `.sln`)
  now look for the `.slnx`.

### Fixed

- **`powershell` no longer reports `Success:false` on healthy commands because of benign stderr
  noise.** Windows PowerShell 5.1 with redirected stderr wraps its error/warning/progress/verbose
  streams in CLIXML, so progress records ("Preparing modules for first use." on first-touch module
  import, or any `Write-Progress`) and warnings landed in `PSResult.Errors` and flipped `Success`.
  `PowerShellService` now parses CLIXML stderr and counts only genuine `<S S="Error">` records
  (decoded to plain text) against Success; `PSResult.Stderr` keeps the raw stream, and non-CLIXML
  or unparseable stderr falls back to the previous line-split behavior. Background jobs were never
  affected (their state derives from the exit code alone).
- Stale tool counts in docs: README / CLAUDE.md / `skills/windows/README.md` still said 60 tools
  (the surface has been 63 since 0.7.0; now 64), and `ARCHITECTURE.md` said 15 tool classes.
  `DATAFLOW.md`'s Powershell diagram described a long-removed implementation
  (`ValidateCommand` blocklist, `pwsh`, script piped via stdin) — rewritten to match the real
  gate → backstop → `-EncodedCommand`/temp-file flow. `COMPONENTS.md`'s `PowerShellService`
  section had the same rot and its service table was missing the integrity/USN/watch
  registrations.

## [0.7.2] - 2026-08-16

### Fixed

- **0.7.1 shipped a stale binary.** `Directory.Build.props` and `.claude-plugin/plugin.json`
  both declared `0.7.1`, but the committed `bundle/WindowsMcp.exe` was built **2026-07-26** and
  reported itself as `0.7.0`. The version plumbing added in 0.6.1 is correct — `ServerVersion`
  derives from `<Version>`, and `ServerInfoTests` pins it to the manifest — but **none of that
  runs against the committed artifact**, so the release bumped the declarations and left the
  exe behind.
  - Found by handshaking every deployed MCP server and comparing what each reported against
    what was installed. `serverInfo.version` is the field used to prove a deploy landed, so
    while the binary under-reported, a stale deploy and a healthy one were indistinguishable.
  - Rebuilt and released as **0.7.2** rather than replacing the binary in place: the plugin
    cache is keyed on version, so a same-version swap is a no-op and would never have deployed.
  - Verified by **executing** the artifact that ships: real MCP handshake reports
    `Windows-mcp 0.7.2` and enumerates all 63 tools.

### Known (environmental, pre-existing)

- **Three tests fail without an interactive desktop session** — two `InputServiceTests` (UIPI
  blocks simulated input), `ScreenshotServiceTests.CaptureAsync…` (invalid screen handle), and
  intermittently `UIAutomationServiceTests.FindElementAsync…` (UIAutomation COM). **Proven
  pre-existing**: the same failures occur on the unmodified tree at `9f283a3`. They are *not*
  skipped — a desktop-automation server failing loudly where there is no desktop is the correct
  and informative result, the same call `ui-mcp` makes for its window tests. 240 of 243 pass.

## [0.7.1] - 2026-07-26

### Fixed
- **`PowerShellService` mangled every multi-line script — silently, on exit 0.** The service ran
  `powershell.exe -Command -` and wrote the script to **stdin**. PowerShell evaluates piped stdin
  **line by line as independent statements**, so any multi-line construct (hashtable literal,
  `try/catch`, `foreach`, `function`, wrapped assignment) was broken apart — producing **empty
  stdout with exit code 0**. Now passed as a single unit via **`-EncodedCommand`** (base64
  UTF-16LE).
  - **Reported symptom:** `disk_inspect mode:reclaimable` returned
    `"reclaimable-space query returned no output (exit 0)"`. Its script ends in a multi-line
    `[PSCustomObject]@{...} | ConvertTo-Json`. The service's empty-output guard was working
    correctly and faithfully reporting a real failure — the defect was one layer below it.
  - **Blast radius was every PowerShell-backed tool**, not just `disk_inspect`. Any caller whose
    script contained a multi-line block was affected.
  - **Root-caused by controlled comparison**, not inspection: the identical script produced 0 bytes
    via `-Command -`/stdin and 136 bytes of valid JSON via `-File`.
- **Non-ASCII output was corrupted** (`café` -> `caf?`). Two independent causes, both fixed:
  stdin was written using the console default encoding (gone — the script no longer travels via
  stdin), and Windows PowerShell 5.1 **writes** stdout in the OEM codepage while the service
  **reads** it as UTF-8. A one-line `[Console]::OutputEncoding` preamble now aligns writer with
  reader. Verified at the byte level: `caf 82 20 fb` (OEM) -> `caf c3 a9 20 e2 9c 93` (UTF-8).
- **Large scripts no longer regress.** stdin had no length limit but a command line is capped at
  ~32767 chars, so an oversized script falls back to a temp `.ps1` run with `-File` (written
  UTF-8 **with BOM**, since PS 5.1 assumes ANSI for a BOM-less file).

### Changed
- `RedirectStandardInput` is kept (and closed immediately) even though stdin is no longer written.
  This process is an MCP **stdio** server, so its own stdin is the JSON-RPC channel; an
  un-redirected child would inherit that handle and could consume protocol bytes.

### Added
- 7 regression tests. 5 pin the invocation itself (multi-line hashtable / `try-catch` / `foreach`,
  oversized-script fallback, non-ASCII round-trip); 2 are **integration** tests driving
  `GetReclaimableAsync` through a **real** `PowerShellService`.
  **Why the bug shipped:** the existing `DiskServiceTests` mock `IPowerShellService` and feed it a
  hand-written JSON string, so they only ever exercised the parsing half and stayed green while the
  real invocation returned nothing. *Mocking the collaborator that is broken hides the bug.*
  Suite: 237 -> 239 (excluding UIAutomation).
- Verified in the **shipped single-file exe** over MCP stdio before release, not just in `dotnet
  test`: `disk_inspect mode:reclaimable` returned real data (3.58 GB reclaimable).

## [0.7.0] - 2026-07-18

### Added
- **Monitoring / integrity domain — 3 new tools (60 -> 63), for the maintain-and-protect mandate:**
  - **`integrity`** (baseline/check/list): a file-integrity **tripwire**. SHA-256 snapshots a curated
    watch-list (hosts file, user+machine Startup folders, `~/.claude/settings.json`, `~/.gitconfig`,
    the `C:\` governance files) to `%LOCALAPPDATA%\windows-mcp\integrity` (outside the plugin cache,
    survives upgrades); `check` diffs current vs baseline into added/removed/modified.
  - **`fs_changes`** (status/since): NTFS **USN change-journal** reader — whole-volume file-change
    tracking via native `DeviceIoControl` (`FSCTL_QUERY/READ_USN_JOURNAL`), raw byte-buffer parsing
    (no fragile struct marshalling). `status` gives the journal id + FirstUsn/NextUsn range; `since`
    reads change records forward from a USN. Requires elevation. Native path live-verified against C:.
  - **`watch`** (start/poll/stop/list): live **FileSystemWatcher** sessions; created/changed/deleted/
    renamed events buffer server-side in a bounded ring (oldest dropped when full) between polls.
- 21 new unit tests (integrity temp-dir diff, USN buffer parser + reason flags, bounded ring buffer,
  watch lifecycle). Full suite: 232 passing. Docs/skill updated to 63 tools / 18 tool classes.

## [0.6.2] - 2026-07-17

### Changed
- **`windows` skill: added a "disk-saturation storm" gotcha** to the *Safety rails & gotchas*
  section. Documents that long `powershell`/heavy tool calls (>~120s) are safe on their own — the
  Claude Code harness detaches them at 120s (benign) and delivers the result on completion, and the
  server already allows a 10-min PowerShell backstop — but stacking heavy ops (`DISM` + `service`
  stop + bulk deletes) **during an already-saturated disk** (e.g. a concurrent large hash/copy) can
  fail the MCP call transiently with `"An error occurred invoking 'powershell'"`. Clarifies this is
  I/O starvation, **not** a 120s limit or a `MCP_TOOL_TIMEOUT` issue (that env var defaults to ~28 h),
  and that the mitigation is to run the heaviest ops via Claude Code's own `run_in_background`.
  Verified 2026-07-17 by controlled probes (lone 150s and two concurrent ~135s calls all succeeded;
  no server crash). Docs-only; no code or tool-surface change.

## [0.6.1] - 2026-07-12

### Fixed
- **`process list` silently ignored the `name` filter on two of its three paths** — found by live
  e2e testing against the 0.6.0 server. `ProcessTools.Process` forwarded `name` only on the
  `includeLineage` path; the plain-`list` and `groupByRoot` paths called `ListAsync(ct)` /
  `GroupByRootAsync(ct)`, which had **no filter parameter at all** on `IProcessService`. A filter
  matching nothing therefore returned the **entire process table** (~360 rows) instead of an empty
  result — silent, and the opposite of the safe failure direction: a caller narrowing to
  `name: "chrome"` to pick a PID to kill was handed the whole machine. Root-caused at the
  interface (the tool had nowhere to pass the filter), not patched at the call site:
  - `IProcessService.ListAsync` and `GroupByRootAsync` now take `string? nameFilter = null`.
  - Plain `list` matches a case-insensitive substring of the **name only** (a `ProcessDto` carries
    no command line); `orphans` / `includeLineage` / `groupByRoot` match name **or** command line.
    The tool description previously over-promised command-line matching on every path; corrected.
  - `groupByRoot` + filter returns the **whole trees that contain a match** — full membership and
    a true `DescendantCount`. It deliberately does not trim the tree: a trimmed count still reads
    as "descendants" and would mislead.
  - The name-based `kill` path stays on **exact** matching (it passes no filter), so
    `kill --name node` cannot also kill `node-inspector`.
  - The bug shipped because `Process_list_groupByRoot_calls_GroupByRootAsync` asserted only that
    the method *was called*, never that the argument arrived. Tests now assert on the forwarded
    argument. +9 tests (205 pass, 0 fail).

- **The server misreported its own version over MCP** — the handshake returned
  `serverInfo.version = "0.4.1"`, a hardcoded literal in `Program.cs` that had been stale for
  **three releases** (0.5.0, 0.6.0 and 0.6.1 all shipped announcing 0.4.1). Surfaced while
  e2e-testing the rebuilt bundle. Not cosmetic: this plugin is served from a per-version cache
  clone of the committed `bundle/`, so a stale bundle is otherwise invisible and `serverInfo` is
  the natural thing to check — a server that lies about its version is what let v0.6.0 sit
  undeployed for four days while 0.5.0 kept answering. Root cause was three disagreeing sources of
  truth (the literal, an unset `<Version>` leaving the assembly at 1.0.0, and `plugin.json`). Now
  `<Version>` in `Directory.Build.props` is the single build-side source, `Program.ServerVersion`
  reads it off the assembly (no literal to rot), and `ServerInfoTests` pins it to
  `.claude-plugin/plugin.json` so a bump that misses one of them fails the test gate.

- **Every caller-facing error message in the server was being thrown away** — found by e2e-testing
  the orphan/kill features. The MCP SDK masks any exception that isn't an `McpException`, returning
  a bare `"An error occurred invoking '<tool>'."`. Sensible for unexpected faults; actively harmful
  for our **deliberate refusals**, whose messages are the whole point. The worst case is the
  PID-reuse start-time guard: it aborts a kill with
  `"pid N start time … != expected …; aborting (possible PID reuse)"` — and that was flattened to
  the generic string, making a guard abort **indistinguishable from a crash**. A caller could
  reasonably "retry" the kill without the guard, causing precisely the kill the guard exists to
  prevent. This affected all 54 intentional throws across 11 tool classes.
  Fixed at the boundary with a single `AddCallToolFilter` middleware (`Program.cs` + `ToolErrors`)
  that surfaces caller-facing refusals (`ArgumentException` / `InvalidOperationException`) verbatim
  with `isError: true`, while unexpected faults keep the SDK's masking (no internals leak). Services
  stay MCP-agnostic and no call sites changed. Verified live over stdio — the guard, the missing
  `confirm`, bad `startTime`, bad param combos, unknown actions, and dead PIDs all now report why.

### Changed
- `ProcessService.ListAsync` filters by name **before** projecting to DTOs — `MainModule` access
  opens a native handle and throws on protected processes, so skipping non-matches is cheaper and
  quieter. Extracted the duplicated name-or-command-line predicate into `ProcessLineage.Matches`.

## [0.6.0] - 2026-07-08

### Added
- Process tool: recycle-aware lineage (`list includeLineage:true`), orphan enumeration
  (`orphans`) with `ageMinutes`/`runtimeKind`/`isSystemAdjacent` signals, root-grouping
  (`list groupByRoot:true`), name/command-line filtering, and a recycle-safe fleet kill
  (`kill tree:true`, `startTime` PID-reuse guard). Orphan detection is recycle-aware (a parent
  whose PID was reused and started after its child counts as gone), and the "orphaned is common
  and by-design on Windows" caveat is documented — the tool describes, it does not judge.

### Changed
- **`Screenshot` tool defaults to `output="file"` instead of inline base64** — saves image to
  `%TEMP%\WindowsMcp\screenshot_<timestamp>.<ext>` and returns the file path. A full-screen
  1080p PNG was embedding ~240k tokens of base64 directly in the conversation history; the file
  path response is ~4 tokens. Pass `output="base64"` to restore the previous inline behavior.

### Fixed
- **`PowerShellService` backstop was consumed by queue-wait.** The per-call backstop
  `CancellationTokenSource` was created before acquiring the serialization gate, so a caller
  queued behind many others could burn its entire runaway-script budget just waiting and be
  cancelled before its own command ran. The backstop now starts *after* the gate is acquired, so
  it bounds execution time only (its documented intent). The serialized-calls stress test is
  right-sized (the property is independent of the call count; a large count only measured
  antivirus cold-start scan time).
- **Stale `ScreenToolsTests` base64 assertion** after the `output="file"` default — the test now
  opts into `output:"base64"` to exercise the mode it asserts.

## [0.5.0] - 2026-07-04

### Added
- **Companion `windows` skill** (`skills/windows/`, loads as `windows-mcp:windows`, slash
  `/windows`) — a guidance/playbook over the server's 60 tools: tool selection (prefer the MCP
  over raw PowerShell), a 60-tool domain map, five workflow playbooks (startup/boot triage,
  process cleanup, security sweep, UI-automation loop, file forensics), and safety rails for
  destructive tools. No new tools; the server binary is unchanged (still reports 0.4.1).

## [0.4.1] - 2026-06-26

### Fixed
- **`defender_status` faulted instead of returning data** — found by live end-to-end testing right
  after the v0.4.0 release. Windows PowerShell 5.1 `ConvertTo-Json` emits `/Date(ms)/` for
  `DateTime`, which `System.Text.Json` cannot parse into `DateTime?`. The script now forces ISO
  8601 (`.ToString('o')`), and deserialization now degrades to a `Note` instead of faulting.

## [0.4.0] - 2026-06-26

Codebase-audit sweep: fixes every defect a 3-agent audit found, restores the thin-tool pattern
across the last hold-outs, closes service test-coverage gaps, and adds 8 inspection tools
(tool count 52 → 60).

### Added
- `file_streams` tool (NTFS ADS + reparse target), new `IFileStreamService`.
- `driver_list` tool (PnP signed drivers), new `IDriverService`.
- `reliability` tool (minidumps + reliability records), new `IReliabilityService`.
- `cert_store` tool (Windows cert-store enumeration), new `ICertStoreService`.
- `defender_status` tool (`Get-MpComputerStatus` posture snapshot).
- `process_inspect` tool (parent/command line/start time/module inventory), with `ProcessService`
  now depending on `IWmiService`.
- `verify_signature` tool exposing `AuthenticodeInspector` trust checks.
- `file_hash` tool exposing SHA256/SHA1/MD5 hashing.

### Changed
- `network ports` now includes owning process PID/name (via `Get-NetTCPConnection`).
- Added broad unit coverage for previously under-tested services (`WebService`, `NetworkService`,
  `ProcessService`, `WmiService`).
- Extracted `firewall` logic into `IFirewallService`/`FirewallService` (typed DTO parsing +
  explicit failure handling).
- Extracted `security_audit` logic into `ISecurityService`/`SecurityService` (typed parse +
  note on probe-wide failure).
- Extracted `disk_inspect` logic into `IDiskService`/`DiskService` (typed DTOs, PS 5.1-safe
  reclaimable script, empty-output guard).
- Plumbed `CancellationToken` from tools into service-layer operations for PowerShell-backed,
  process/service/scheduled-task/event-log, and file flows.

### Fixed
- `storage_health` temp-script invocation now escapes apostrophes in staged `.ps1` paths.
- `PowerShellService` semaphore starvation risk fixed with a linked 10-minute backstop CTS and
  earlier cancellation-kill callback registration.
- `file_search find_duplicates` now skips unreadable files instead of aborting the run.
- `power_action` now enables `SeShutdownPrivilege` and checks native return values for all actions.
- `get_table` now reads headers from `TablePattern.ColumnHeaders` when available.
- Fixed native handle/COM leaks in process/WMI paths:
  - `ProcessService.ListAsync` now disposes `Process` wrappers.
  - `KillAsync`/`StartDetachedAsync` and `WindowService.LaunchAsync` dispose process handles.
  - `WmiService.QueryAsync` now disposes collection and each `ManagementObject` row.

## [0.3.1] - 2026-06-26

### Fixed
- **`storage_health` returned empty / timed out against the live MCP server** due to two e2e-only
  defects:
  1. Large generated script produced no stdout over `powershell -Command -` (stdin). Fixed by
     staging to temp `.ps1` and invoking as file.
  2. `Get-PhysicalDisk` + SMART could wake sleeping USB/SD devices and stall. Physical disk + SMART
     probing is now opt-in (`include_usage`), with default path using fast metadata-only probes.
- Default budget increased from 30s to 45s; both default and `include_usage:true` paths verified
  live.

## [0.3.0] - 2026-06-25

### Added
- **`storage_health` MCP tool** — disk/drive health diagnostics (physical disks, SMART reliability,
  volume↔disk mapping, recent disk-stack Error/Warning events), with metadata-first defaults,
  opt-in usage probing, and cancellation-safe execution. Backed by `IStorageService`/`StorageService`.
  - Docs counts refreshed (51→52 tools, 13→14 tool classes), and stale OVERVIEW service counts fixed.
  - Added `InternalsVisibleTo("WindowsMcp.Tests")` for white-box helper tests.
- **`startup_report` Control Panel parity + `summary` format**.
- **`startup_report` coverage expansion** (DNS, HKU Run/RunOnce, applets, AT hooks, IFEO,
  Winlogon hooks, AppInit_DLLs, Active Setup, proxy, trusted zones).
- **`startup_report` MCP tool** and supporting abstractions/services:
  `IRegistryService` enumerate helpers, `ITaskSchedulerService.ListDetailedAsync`,
  `IAuthenticodeInspector`, `ILspEnumerator`, `IShortcutResolver`, `IStartupReportService`,
  report DTOs/helpers (`StartupApproval`, `CommandTarget`, `StartupReportRenderer`), and DI wiring.

### Changed
- Docs updates for `startup_report` behavior and architecture counts.
- `tools/create-dependency-graph` gained C# support, auto language detect, C# parsing/categorization,
  C# dependency matrix, namespace-root inference from `.csproj`, `--lang=auto|typescript|csharp`,
  and `Statistics.totalTypeScriptFiles` rename to `totalSourceFiles`.
- Rewrote architecture docs (`OVERVIEW.md`, `ARCHITECTURE.md`, `COMPONENTS.md`, `DATAFLOW.md`) for
  current C#/.NET 9 architecture.

### Fixed
- `startup_report` signer resolution for bare-name targets via `CommandTarget.ResolveFullPath`.
- `startup_report` accessibility section now filters non-executable numeric `StartExe` entries.
- `AuthenticodeInspector` catalog verification now passes `hCatAdmin` (fixing false negatives on
  SHA-256 catalog members).
- `CommandTarget.Exists` now PATH-resolves bare executable names.
- `UIAutomationService.GetStateAsync` now roots at foreground top-level window (with fallbacks),
  and Notepad fixture foregrounding improved determinism.

### Security
- `tools/` dev deps audit fixed high-severity transitive advisories (`tar`, `picomatch` via
  `tinyglobby`) in lockfiles only; tool build/run behavior unchanged.
