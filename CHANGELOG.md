# Changelog

## [Unreleased]

### Changed
- **`tools/create-dependency-graph`**: Added C# language support (auto-detects via `.sln` at
  project root). New functions: `detectProjectLanguage`, `getAllCsFiles`, `parseCsFile`,
  `categorizeCsFiles`, `buildCsDependencyMatrix`, `inferCsProjectNames` (auto-discovers
  namespace roots from `.csproj` files — no longer hardcoded). Fixed `detectUnused` to
  return empty results for C# projects (namespace-based imports cannot be resolved to file
  paths). Fixed `Microsoft.Extensions.*` and `Microsoft.Win32.TaskScheduler` misclassified
  as system deps (added `CS_MICROSOFT_NUGET_PREFIXES` blocklist). Added `--lang=auto|typescript|csharp`
  CLI flag with validation. Renamed `Statistics.totalTypeScriptFiles` → `totalSourceFiles`.
- **`docs/architecture/OVERVIEW.md`**: Rewritten for C# architecture — 50 MCP tools, 12 tool
  classes, .NET 9, FlaUI/H.InputSimulator/SkiaSharp dependency table; removed all Python references.
- **`docs/architecture/ARCHITECTURE.md`**: Rewritten with 4-layer C# diagram (MCP Protocol /
  Tool / Service Abstraction / Service Implementation); DI wiring, source-generated tool
  discovery, `Program.cs` startup sequence.
- **`docs/architecture/COMPONENTS.md`**: Rewritten — all 12 tool classes with tool counts and
  injected services; all 20 service interfaces with key methods; model DTO files; NuGet
  package reference table.
- **`docs/architecture/DATAFLOW.md`**: Rewritten with C# sequence diagrams — `GetState` via
  FlaUI UIA3 tree walk, `Click` via `H.InputSimulator.SendInput`, `Powershell` via
  `System.Diagnostics.Process`, `Screenshot`/`Ocr` via SkiaSharp + `Windows.Media.Ocr`,
  `WaitFor` polling loop, DI resolution at startup, `AssertElement` state checks.

## [0.2.0] - 2026-05-26

### Changed
- **Complete rewrite from Python to C# on the official ModelContextProtocol SDK (1.0.0).**
  Same server identity (`Windows-mcp` in `.mcp.json`); single self-contained
  `dist/WindowsMcp.exe` replaces the venv-launched `python main.py`.
- Tool names normalized to snake_case (e.g. `Click-Tool` → `click`,
  `Find-Element-Tool` → `find_element`).
- Version reset from 0.8.x (Python) to 0.2.0 (C#) to signal the platform break.
  The Python source tree is preserved in
  `legacy/python-pre-csharp-conversion-archive-2026-05-26.zip` for reference;
  the active codebase is C# under `src/WindowsMcp/` + `src/WindowsMcp.Abstractions/`.

### Added (9 new tools beyond the Python set)
- `file_read`, `file_write`, `file_info` — file content primitives
- `http_request` — REST/HTTP client (beyond HTML scraping)
- `wmi_query` — structured WMI queries
- `env` — environment variable get/set/list (secret-named values redacted
  by default; `include_secrets:true` opts out)
- `power_action` — shutdown/reboot/logoff/lock/sleep/hibernate
  (`confirm: true` required)
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

### Removed (4 tools dropped from Python set)
- `Wait-Tool` — pure sleep; LLM can space its own calls
- `Compare-Screenshot-Tool` — niche QA tool
- `Record-Replay-Tool` — LLM is the orchestrator
- `Command-History-Tool` — session-scoped PowerShell history

### Fixed (during cutover audit)
- **Stdout JSON-RPC responses lost under cp1252 default encoding.**
  `Console.OutputEncoding` now explicitly set to UTF-8 before
  `Host.CreateApplicationBuilder` so the SDK's `StreamWriter` has
  `AutoFlush=true` and JSON responses reach the pipe (commit `ed76faa`).
- **PowerShell SDK incompatible with `PublishSingleFile=true`** —
  `InitialSessionState.CreateDefault2` deep-called `Path.Combine(null)`
  because `Assembly.Location` returns "" in single-file mode. Replaced
  the `System.Management.Automation` runspace with a `Process.Start
  ("powershell.exe", ...)` shell-out. Same `IPowerShellService` API;
  ~30 MB smaller binary (commit `0e43215`).
- **`env(list)` leaked secret-named environment variables** verbatim
  into LLM transcripts. Now redacts vars matching
  `KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT` to
  `***REDACTED***` by default (commit `0897dbc`).
- **`firewall(list)` returned 187 KB / 5910 lines** by default,
  overflowing MCP token limits. Added `name_like` filter and `max`
  cap; defaults to enabled rules only (commit `0897dbc`).
- **`security_audit` returned empty string** when probes ran
  unelevated. Each probe now in its own try/catch; always emits a
  JSON object with `null` fields where probes failed (commit `0897dbc`).
- **DPI awareness** set to per-monitor-V2 at process start so
  screenshots capture physical pixels on HiDPI displays
  (commit `ed7ba42`).
- **UI Automation calls** properly marshaled to a dedicated STA thread
  via `BlockingCollection<Action>` work queue (Task 7).
- `humancursor` 3-second startup cost removed (uses straight
  `SendInput` via `H.InputSimulator`).

### Backlog (v0.3.0 candidates)
- Native AOT compilation (blocked on FlaUI reflection)
- CI / GitHub Actions
- Real `audio(get/set/mute)` via NAudio or AudioDeviceCmdlets
  (current SendKeys backend is ±2% imprecise and can't read mute state)
- `multi_monitor` device-name lookup via `EnumDisplayDevices`
  (current `MONITORINFOEXW` is internal in CsWin32 0.3.x; synthesizes
  `Monitor{N}` names)
- Real WiFi info via `Windows.Networking.Connectivity` (currently a
  placeholder)
- LRU eviction for `UIAutomationService` element-id cache
  (currently grows unbounded per session)

## [0.8.5] - 2026-05-01

### Changed
- **Replaced GPL-2.0-or-later `Levenshtein` dependency with MIT-licensed `rapidfuzz` (3.0+).** Drop-in replacement for fuzzy app-name matching in `src/desktop/__init__.py` (`launch_app`, `switch_app`, `manage_window`). The transitive `python-Levenshtein` package was the only GPL/copyleft contamination in the dependency graph; removing it eliminates distribution-friction risk for any future commercial redistribution. The score scale (0-100) is preserved between `fuzzywuzzy.process.extractOne` and `rapidfuzz.process.extractOne`, but the return shape changed from 2-tuple `(match, score)` to 3-tuple `(match, score, index)`; call sites updated to use index access (`matched[0]`) instead of unpacking. `fuzzywuzzy>=0.18.0` and `python-levenshtein>=0.27.1` removed from `pyproject.toml`; `rapidfuzz>=3.0` added.

### Tests
- Added `tests/test_fuzzy_matching.py` (9 passing) — characterization tests pinning the observable behavior of fuzzy matching across the migration. Covers `process.extractOne` API contract (return shape, list/dict_keys inputs, empty-input handling) plus method-level coverage of `Desktop.launch_app`, `Desktop.switch_app`, and `Desktop.manage_window` to verify the tuple-unpacking pattern survives the swap. Total passing tests: 207 → 216, 0 xfail.

### Documentation
- Add CycloneDX SBOM (sbom.json).

### Security
- **`_sanitize_name` regex hardened against glob and newline injection** (`main.py:2738`): the v0.8.4 sanitizer accepted `*`, `?`, `\n`, `\r`, and `\t`, leaving four documented gaps in the Process-Tool injection-defense surface. Glob wildcards reach PowerShell as literal stars (no expansion in `-Name`, but bypasses the intent of the allow-list) and newlines inside a double-quoted PS string can inject statements on a fresh line. Extended the rejection regex to also match `*`, `?`, `\n`, `\r`, `\t`, `\v`, `\f`, and NUL. Total enforced injection-payload coverage rises from 15 to 19; xfail count drops to 0.

### Tests
- Added `tests/test_process_tool_injection.py` (72 passing) — regression coverage for the v0.8.4 PowerShell-injection fix in Process-Tool. Battery of 15 injection payloads (quote-breakers, command chaining, subshell expansion, pipe-to-attacker, brace/paren smuggling, path-separator smuggling) plus 7 legitimate-name positive cases for both `action="list"` and `action="kill"`. Asserts via mock that `desktop.execute_command` is **not** invoked for any payload, and that legitimate names still produce the expected `Get-Process -Name "<n>"` / `Stop-Process -Name "<n>"` PowerShell strings. Includes direct unit tests on `_sanitize_name` plus 4 hardening-pass payloads (glob `*`, `*.exe`, `\n`, `\r\n`) that the v0.8.5 regex now rejects.

## [0.8.4] - 2026-04-30

### Security
- **High-severity PowerShell injection in Process-Tool** (`main.py:894`, `:915`): the `name` argument was interpolated into double-quoted PS strings (`Get-Process -Name "{name}"`, `Stop-Process -Name "{name}"`) without escaping. A name like `x"; Stop-Process -Name explorer; "` could close the quote and chain arbitrary PowerShell. `validate_text` only enforced length and `validate_command(..., trusted=True)` skipped the operator check. Fixed by passing `name` through the existing `_sanitize_name` helper (rejects `'` `"` `` ` `` `$` `;` `|` `&` `{}` `()` `\` `/`) before interpolation in both call sites.

## [0.8.3] - 2026-04-10

### Fixed
- **Critical**: `default_factory=[]` in `TreeState` dataclass passed a list instance instead of a callable — raises `TypeError` on Python 3.12+ and shares mutable state between instances on older versions
- `get_appwise_nodes` crashes with `KeyError` when Taskbar or Program Manager is not visible (e.g., auto-hide taskbar, Remote Desktop)
- Thread-unsafe `PIL.ImageDraw` in `annotated_screenshot()` — concurrent `draw.rectangle()`/`draw.text()` via `ThreadPoolExecutor` could corrupt annotated images; replaced with sequential loop
- Click-Tool: extra `pg.mouseDown()` before `pg.click()` produced double mouse-down (drag-like behavior); `pg.click()` already handles down+up internally
- `scrape_tool`: unhandled `requests.get()` exceptions (`ConnectionError`, `Timeout`, `HTTPError`, and other `RequestException` subclasses) produced raw tracebacks instead of user-friendly messages
- Redundant `if use_vision` ternary inside already-true `if use_vision:` block in `Desktop.get_state()`
- `get_monitors` return type annotation said `list[dict]` but method returns `dict`
- `get_appwise_nodes` return type annotation missing `ScrollElementNode` from 3-tuple

### Changed
- Added missing `ensure_com()` calls to `clipboard_tool`, `move_tool`, `wait_tool`, `disk_cleanup_tool`
- Renamed misleading variable `is_minimized` → `is_not_minimized` in `Desktop.is_app_visible()`
- Fixed typo `Cordinates` → `Coordinates` in State-Tool output and docs
- Updated stale docs: tool count (14/25 → 45), `FAILSAFE` (False → True), Click-Tool sequence diagram, annotation drawing (parallel → sequential), security section (added input sanitization), command length limit (2000 → 10000)
- Bumped version to 0.8.3 in `pyproject.toml` and `manifest.json`

## [0.8.2] - 2026-04-05

### Fixed
- `_check_allowed_path` failed on drive roots — `C:\` + `os.sep` produced double backslash `C:\\` that never matched
- `_sanitize_path` blocked parentheses, rejecting legitimate paths like `C:\Program Files (x86)`

## [0.8.1] - 2026-04-05

### Changed
- Added `C:\` to `ALLOWED_PATHS` — tools can now access the entire C drive

## [0.8.0] - 2026-04-01

### Added
- **Storage-Tool**: 8-action storage analysis and cleanup tool with preview/execute safety pattern
  - `breakdown`: File type breakdown by extension with sizes and percentages
  - `stale`: Find files untouched for N days, sorted by size
  - `compress` / `compress-run`: Preview then zip stale files into per-folder archives, originals to Recycle Bin
  - `dedup` / `dedup-run`: Preview then remove duplicate files (SHA-256, keep first by path), extras to Recycle Bin
  - `archive` / `archive-run`: Preview then move old files into `_archive/<year>-Q<quarter>/` folders
- Parameters: `path`, `days` (1-3650), `minSizeMB`, `extensions` filter
- 22 new tests in `test_storage_tool.py`, 135 total passing

### Security
- All destructive operations (`compress-run`, `dedup-run`) send files to Recycle Bin via Shell.Application COM — fully recoverable
- `archive-run` uses Move-Item (non-destructive, files are relocated not deleted)
- Preview/execute split: agents must explicitly choose `-run` actions after reviewing previews
- Days parameter clamped to 1-3650, extension validation, ALLOWED_PATHS enforcement

### Changed
- Semicolons inside `{}` now allowed in Powershell-Tool (brace-depth tracking) — enables calculated properties like `@{N='x';E={...}}`

## [0.7.0] - 2026-03-31

### Added
- **Security-Audit-Tool**: Comprehensive Windows security scan — Defender status, Firewall per-profile, UAC level, BitLocker, pending updates, PowerShell execution policy, open shares, Remote Desktop status. Quick boolean summary at top.
- **Network-Tool**: Network diagnostics with 5 actions — `status` (adapters/IPs/gateway/DNS/connectivity), `connections` (active TCP + listening ports with process names), `ping` (with SSRF protection), `dns` (hostname resolution with SSRF protection), `wifi` (SSID/signal/available networks).
- `_validate_hostname()` helper for hostname/IP input validation with SSRF protection
- 26 new tests in `test_security_network_tools.py`

### Security
- Network-Tool `target` parameter validated against `BLOCKED_IP_RANGES` to prevent SSRF
- Hostname input restricted to safe characters (alphanumeric, dots, hyphens, colons)

## [0.6.2] - 2026-03-31

### Fixed
- **Disk-Analysis, File-Search, Process-Tool, File-Manage, Duplicate-Finder**: all returned `Security Error: blocked operator` because `validate_command()` rejected legitimate `;` and `` ` `` in server-generated PS scripts. Added `trusted=True` parameter to skip operator checks for internal scripts while preserving injection protection for user-supplied commands (Powershell-Tool).
- **Disk-Cleanup-Tool**: now also passes through `validate_command(trusted=True)` instead of bypassing validation entirely.
- **Restore missing `mcp.run()` entry point** — accidentally removed in v0.6.0, preventing the MCP server from starting its stdio transport loop.
- Removed `validate_command` mock workarounds from tests (no longer needed with `trusted` parameter).

## [0.6.1] - 2026-03-31

### Fixed
- Fix 17 failing tests in `test_existing_tools.py` — `ensure_com()` (COM thread init) was not mocked, causing `comtypes.CoInitialize()` to crash in test context
- Added `autouse` pytest fixture to mock `ensure_com` across all existing-tool tests
- Synced version across `pyproject.toml`, `manifest.json`, and `CLAUDE.md` (were out of sync at 0.5.3/0.4.1)

## [0.6.0] - 2026-03-30

### Added
- **Disk-Analysis-Tool**: Analyze disk usage for any folder/drive — top subfolders by size, free/used space, file counts. Configurable depth (1-3) and minimum size threshold.
- **Disk-Cleanup-Tool**: Find reclaimable disk space — scans temp files, npm/pip/bun caches, node_modules, Chrome data, Recycle Bin, Windows Update cache. Reports only, does not delete.
- **File-Search-Tool**: Search files by name pattern, extension, size range, and date range. Returns sorted results with sizes and dates.
- **File-Manage-Tool**: File operations — copy, move, rename, info, list. Native PowerShell with `-LiteralPath` for safety.
- **Duplicate-Finder-Tool**: Find duplicate files by size + SHA-256 hash. Reports wasted space per duplicate set.
- Input sanitization helpers: `_sanitize_path`, `_sanitize_name`, `_validate_date`, `_validate_extension`, `_check_allowed_path`
- `validate_command()` security gate applied to all new tools (was previously only on Powershell-Tool)
- `ALLOWED_PATHS` enforcement on all path parameters
- 84 automated tests (46 for new tools, 38 for existing tools including security validation)

### Security
- All user-supplied parameters (path, pattern, dates, extensions, filenames) sanitized before PowerShell interpolation
- `validate_command()` called on assembled PS scripts before execution
- ALLOWED_PATHS checked for all file operation paths
- SHA-256 used for duplicate detection (not MD5)
- Generic error messages returned to callers (no raw PS output disclosure)
- node_modules scan bounded to depth 3 and 20 results max

### Changed
- Synced with origin/main (v0.5.3): includes 10 testing/inspection tools, Start-Process-Tool, Switch-Tool improvements
