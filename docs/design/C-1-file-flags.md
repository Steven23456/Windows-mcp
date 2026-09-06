# C-1 — File tools: line windows, append, overwrite, recursive, listing flags

**Checklist item:** [C-1](../upstream-parity-checklist.md#c-1--file-tools-offsetlimit-append-overwrite-recursive-pattern--p2--m) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 2, first item — decisions R1 (absolute paths only),
R2 (safer `file_manage` defaults) and R3 (`FileEntry` listings), settled in section 7 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~3 h including the RED/GREEN passes.

## Problem

`file_read` returns a whole file or nothing (a 5 MB log is either over `max_bytes` or 5 MB of
context). `file_write` cannot append and fails on a missing parent directory. `file_manage`
copies and moves **over** an existing destination and deletes a whole directory tree behind
nothing but `confirm` — a weaker rail than upstream's — and `list` returns bare paths with no
type or size, so every entry costs a `file_info` round-trip. A relative path silently resolves
against the server's working directory, which the caller cannot see.

## Decision

- **Absolute paths only (R1).** `file_read`, `file_write`, `file_manage` (both `src` and `dst`)
  and `file_search`'s `root` refuse a path that is not fully qualified
  (`Path.IsPathFullyQualified`) with one `ArgumentException` naming the parameter and the rule;
  UNC paths pass. Checked in the tool layer before the service is called.
- **`file_read(path, max_bytes, encoding, offset_lines = 0, limit_lines = 0)`.** With both at
  `0` the result is today's plain text. With either given the result is JSON
  `{path, totalLines, offset, returned, truncated, content}`: `offset_lines` is 1-based like
  upstream (`0` and `1` both mean the first line), `limit_lines = 0` means to the end, and
  `truncated` says lines remain past the window. The window is cut by a pure
  `LineWindow.Slice(text, offset, limit)` after decoding: lines split on `\n` with a trailing
  `\r` stripped, so a CRLF file counts the same as an LF one; a final newline does not add an
  empty line; `content` joins the window with `\n`. An offset past the end returns zero lines,
  `truncated:false`. Negative values are refused. `max_bytes` still bounds the *file*.
- **`file_write(path, content, encoding, confirm, append = false, create_parents = true)`.**
  `append` opens for append — no temp-file rename, since an append must not rewrite the file —
  and the reply says `appended`; `create_parents:false` refuses a missing directory naming the
  flag. `confirm` is still required. The C-7 row flips: `file_write` is no longer `Idempotent`.
- **`file_manage(action, src, dst?, confirm, overwrite = false, recursive = false, pattern?,
  include_hidden = false)`** (R2, R3):
  - `copy`/`move` refuse an existing destination unless `overwrite:true`
    (`InvalidOperationException` naming the flag). `copy` of a directory copies the tree; `move`
    across volumes falls back to copy-then-delete (`Directory.Move` refuses it).
  - `delete` refuses a non-empty directory unless `recursive:true` (naming the flag); an empty
    directory and a file need only `confirm`.
  - `list` returns `FileEntry[]` — `{Path, Name, IsDirectory, Size, Modified, Hidden}` (a DTO,
    PascalCase like the other DTO-returning tools) — through `EnumerationOptions`: `pattern`
    is a name glob (`*`, `?`, case-insensitive, applied to files and directories);
    `recursive` descends; hidden **and system** entries are skipped unless `include_hidden`,
    and recursion does not descend into skipped directories; inaccessible entries are skipped
    rather than failing the listing. `Size` is `0` for a directory.
- **The service takes the flags as required parameters** — `CopyAsync(src, dst, overwrite)`,
  `MoveAsync(src, dst, overwrite)`, `DeleteAsync(path, recursive)`, `WriteTextAsync(path,
  content, encoding, append, createParents)`, `ListAsync(path, pattern, recursive,
  includeHidden)`, `ReadLinesAsync(path, maxBytes, encoding, offset, limit)` beside the
  unchanged `ReadTextAsync`. `FileTools` is the only caller; the roadmap's "defaults that
  reproduce today's behaviour at the service" is not needed and a required flag cannot be
  forgotten.
- Contract changes → CHANGELOG *Changed*: the two `file_manage` refusals (pass `overwrite:true`
  / `recursive:true`), the `list` shape, relative paths refused, `file_read`'s JSON when
  windowed.

## Changes

- `Abstractions/Models/FileSystemDtos.cs` — `FileEntry`, `TextWindow(TotalLines, Offset,
  Returned, Truncated, Content)`; `Abstractions/IFileSystemService.cs` — the signatures above.
- `Services/LineWindow.cs` (new, pure); `Services/FileSystemService.cs` — the flags, directory
  copy, cross-volume move, the listing.
- `Tools/FileTools.cs` — the parameters, the absolute-path check, the results, descriptions;
  `file_write`'s `Idempotent = false`.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `LineWindow.Slice`: LF and CRLF count alike, a final newline adds no line, offset 0 and 1 are the first line, limit 0 is to the end, a window in the middle with `truncated:true`, the last window `false`, offset past the end is empty, an empty file is zero lines, negatives refused | `LineWindowTests` | Unit |
| R2 | The service on a temp directory: `ReadLinesAsync` on a CRLF file; append twice keeps both; `createParents` both ways; copy/move refuse an existing target without `overwrite` and replace with; directory copy copies the tree; cross-volume move (skipped with a reason when only one volume exists); delete refuses a non-empty directory without `recursive`, removes an empty one and a file without; list: glob, recursion, hidden and system skipped then included, no descent into a hidden directory, the `FileEntry` fields, a directory's `Size` 0 | `FileSystemServiceFlagsTests` | Integration |
| R3 | The tool: a relative path refused before the service on every parameter (Moq `Times.Never`); every new flag forwarded; `file_read` plain when un-windowed and JSON when windowed, negatives refused; `file_write` still needs `confirm`, says `appended`; `file_manage` defaults are the safe ones; `list` returns the `FileEntry` DTO JSON | `FileToolsTests` | Unit |
| R4 | The three schemas over HTTP (parameter names and defaults); a relative path refused over the wire; `file_write` no longer `Idempotent` in the C-7 lists; SKILL.md's file line names `overwrite`/`recursive` | `HttpTransportTests`, `ToolInventoryTests` | Integration / Unit |

## Deviations and follow-ups

- The listing reports `FileSystemInfo.FullName`, so a path the caller wrote with forward
  slashes comes back with backslashes — Windows' own normalisation, not ours.
- `TextWindow.Offset` echoes `max(offset_lines, 1)` even past the end of the file, so the caller
  sees the window it asked for beside `totalLines`.
- Service flags are required, not defaulted (see Decision); the roadmap's phrasing is
  superseded.
- `file_search` gains only the absolute-path check; its `pattern`/`recursive` semantics are
  unchanged.
- `file_hash`, `file_info`, `file_streams` and `archive` still accept a relative path: R1 named
  the four tools that write or enumerate, and the check was applied to exactly those. Widening it
  to the read-only four is a small follow-up.
