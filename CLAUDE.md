# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Windows-MCP is an MCP server that enables AI agents to interact with Windows OS through UI automation and a11y (accessibility) tree traversal.

**Version**: 0.5.2 | **Platform**: Windows 7-11 | **Python**: 3.13+ | **Entry Point**: `main.py`

## Architecture

### Two-Layer Design

**Desktop Layer (`src/desktop/__init__.py`)** - Windows OS interface
- `Desktop` class manages app state, screenshots, PowerShell execution
- Uses `uiautomation` library for Windows UI Automation API
- `Tree` is lazily imported inside `get_state()` to avoid circular dependency
- `get_state()` → `Tree.get_state()` → `get_appwise_nodes()` → `get_nodes()` → `tree_traversal()`

**Tree Layer (`src/tree/__init__.py`)** - UI element extraction
- Parallel traversal with `ThreadPoolExecutor`
- **Important**: `get_appwise_nodes()` only traverses the foreground app + Taskbar + Desktop (Program Manager), not all visible apps
- Three element categories:
  - **Interactive**: Buttons, links, text fields (see `INTERACTIVE_CONTROL_TYPE_NAMES` in `src/tree/config.py`)
  - **Informative**: Static text, labels (see `INFORMATIVE_CONTROL_TYPE_NAMES`)
  - **Scrollable**: Elements with scroll patterns
- DOM correction logic handles a11y tree quirks (list items with child links, unnamed groups)

**Tool Definitions (`main.py`)** - 25 MCP tools + 2 MCP resources via FastMCP
- Mouse: `humancursor` library (human-like movement)
- Keyboard: `pyautogui` library
- `State-Tool` is primary context-gathering tool

## Build & Development Commands

```bash
# Run server (development)
python main.py

# Run with uv
uv --directory C:\mcp-servers\Windows-MCP run main.py

# Test with MCP inspector
npx @modelcontextprotocol/inspector python main.py

# Build package
python -m build

# Build DXT extension for Claude Desktop
npx @anthropic-ai/dxt pack

# Install for development
pip install -e .
```

## MCP Tools

37 tools and 2 resources defined in `main.py` via `@mcp.tool()` and `@mcp.resource()`. Key tool: `State-Tool` is the primary context-gathering tool — returns desktop state + UI elements, optionally with annotated screenshot (`use_vision=True`). Resources: `windows-mcp://current-directory` (server cwd) and `windows-mcp://security-config` (active security rules).

### Testing & Inspection Tools (v0.5.0)
- `Get-Element-Property-Tool`: Read element properties (value, checked state, enabled, bounding box, automation patterns)
- `Get-Text-Tool`: Extract text content from a UI element (faster than OCR)
- `Assert-Element-Tool`: Verify element state with PASS/FAIL results (exists, enabled, checked, value, visible, focused)
- `Checkbox-Toggle-Tool`: Toggle checkboxes via TogglePattern
- `Select-Option-Tool`: Select dropdown/combobox items via ExpandCollapsePattern + SelectionItemPattern
- `Focus-Tool`: Set keyboard focus without clicking
- `Hover-Tool`: Hover cursor with duration for tooltips/hover states
- `Compare-Screenshot-Tool`: Visual regression testing with pixel diff percentage
- `Get-Table-Tool`: Extract tabular data via GridPattern (returns markdown)
- `Record-Replay-Tool`: Save/replay UI action sequences as JSON
- `Start-Process-Tool`: Launch detached processes that survive independently (GUI apps, scripts, long-running tasks)

## Key Technical Details

### COM Interface Patterns
- Use C# `_VtblGap1_N()` to skip N vtable slots in COM interfaces — never use stub methods with guessed signatures (causes silent stack corruption)
- `IMMDeviceEnumerator` needs `_VtblGap1_1()` before `GetDefaultAudioEndpoint` (skips `EnumAudioEndpoints`)
- `IAudioEndpointVolume` needs `_VtblGap1_4()` before `SetMasterVolumeLevelScalar` (skips Register/Unregister/GetChannelCount/SetMasterVolumeLevel)

### WinRT Async in PowerShell
- Never call `GetResults()` directly on WinRT async operations — it doesn't block
- Use an `Await($WinRtTask, $ResultType)` helper that resolves via `AsTask().Wait(-1)`
- The `AsTask` method must be found by reflection filtering on `` IAsyncOperation`1 `` parameter type

### Input Validation (v0.1.4+)
- `MAX_SCREEN_COORD = 10000` - reasonable max for multi-monitor
- `MAX_TEXT_LENGTH = 10000` - limit text input
- `MAX_WAIT_DURATION = 300` - 5 minutes max wait
- `MAX_WHEEL_TIMES = 100` - scroll limit
- `MAX_CLICKS = 3` - triple-click max

### Command Security (v0.2.0+)
- `BLOCKED_COMMANDS` in `src/desktop/config.py`: 11 dangerous commands (format, shutdown, rm, del, etc.)
- `BLOCKED_ARGUMENTS`: 9 injection-risk flags (-enc, -encodedcommand, --exec, etc.)
- `BLOCKED_OPERATORS`: `;` and `` ` `` blocked; `&` and `|` allowed (essential PS operators)
- `MAX_COMMAND_LENGTH = 2000` - command length limit
- `ALLOWED_PATHS`: workingDir restricted to user home + server cwd
- Validation runs in `validate_command()` in `main.py` before any shell execution

### PyAutoGUI Configuration
```python
pg.FAILSAFE = True   # Abort by moving mouse to corner (security)
pg.PAUSE = 1.0       # 1-second delay between operations
```

### Element Visibility Criteria
- `IsControlElement == True`
- `IsOffscreen == False`
- `IsEnabled == True`
- Bounding box area > 0
- Not an unlabeled image control

### App Filtering
- `EXCLUDED_APPS` in `src/desktop/config.py`: Filtered from `get_apps()`
- `AVOIDED_APPS`: Filtered from tree traversal

### Performance
- **State-Tool latency**: 1.5-2.3 seconds (varies with app count)
- **Sleep delays**: 0.75s (app enumeration), 1.0s (tree traversal), 0.25s (post-screenshot)

## Development Notes

### Adding New Tools
1. Add function in `main.py` with `@mcp.tool()` decorator
2. Add type hints for all parameters
3. Return `str` or `list[str | Image]` for vision support
4. Update `manifest.json` tools array for DXT metadata
5. Rebuild: `python -m build`

### Modifying UI Element Detection
1. Edit `src/tree/config.py`:
   - `INTERACTIVE_CONTROL_TYPE_NAMES`: Clickable elements
   - `INFORMATIVE_CONTROL_TYPE_NAMES`: Text-only elements
2. Modify `Tree.get_nodes()` filtering logic
3. DOM correction in `dom_correction()` handles a11y quirks

### Known Issues
- `get_element_under_cursor()` returns focused control, not element at cursor coordinates

## Testing

No test framework is configured yet. There are no automated tests. Manual testing is done via:
```bash
npx @modelcontextprotocol/inspector python main.py
```

## Code Style
- **Formatter**: Ruff (default config, no ruff.toml or pyproject.toml overrides)
- **Line length**: ~100 characters (convention, not enforced by config)
- **Quotes**: Double quotes
- **Type hints**: Required on function signatures
- **Docstrings**: Google-style

## Cleanup Before Committing

Remove temporary files before committing:
```bash
rm test-*.py debug-*.py temp-*.py .error.txt
git status  # verify clean
```

## Entry Points

Run via `python main.py`, `python -m windows_mcp`, or `windows-mcp` (after pip install). Console script uses `windows_mcp_entry.py` (not `__main__.py`) due to ImportError with pip's entry point generation.

## PyPI Publication

**Package**: `windows-mcp-server` | **Command**: `windows-mcp`

```bash
python -m build
python -m twine upload --username __token__ --password "$(cat ~/Dropbox/Github/PyPi_Key.txt | tail -1 | cut -d: -f2)" dist/windows_mcp_server-<version>*
```

## Known Limitations

1. Cannot select specific text within paragraphs (a11y tree limitation)
2. `Type-Tool` types entire text at once (not suitable for incremental IDE typing)
3. Windows-only (requires Windows UI Automation API)
