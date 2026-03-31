# Changelog

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
