# C-1 — File tools: line windows, append, overwrite, recursive, listing flags

**Checklist item:** [C-1](../upstream-parity-checklist.md#c-1--file-tools-offsetlimit-append-overwrite-recursive-pattern--p2--m) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 2, first item — decisions R1 (absolute paths only),
R2 (safer `file_manage` defaults) and R3 (`FileEntry` listings), settled in section 7 ·
**Status:** implemented 2026-09-06, revised 2026-09-07 through review rounds 4 … 4f below
(build clean, headless suite green — see CHANGELOG [Unreleased]) ·
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
- **`overwrite:true` replaces, never merges** (review finding on PR #25): an existing
  destination — a directory tree with stale files, or a file where a directory is going, or a
  directory where a file is going — is cleared before the copy, as `move` already did.
- **Self-containment is refused first** (the second review round): the same path, a destination
  inside the source, or a destination that contains the source, checked segment-wise on full
  paths before existence or `overwrite` are looked at. A copy into its own subtree used to
  recurse into the directory it had just created until the path length ran out, and with
  `overwrite:true` the clear-first step deleted part of the source before that.
- A blank `sort_by` on `process` is "not given" everywhere (the same review): the lineage,
  group and orphan shapes refuse only a real value.
- Service flags are required, not defaulted (see Decision); the roadmap's phrasing is
  superseded.
- `file_search` gains only the absolute-path check; its `pattern`/`recursive` semantics are
  unchanged.
- `file_hash`, `file_info`, `file_streams` and `archive` still accept a relative path: R1 named
  the four tools that write or enumerate, and the check was applied to exactly those. Widening it
  to the read-only four is a small follow-up.

## Round 4 — the review-agent's findings (2026-09-07)

The first run of `review-agent` on the finished phase-2 diff found what three external review
rounds had not. Decisions, each a RED row before code (implemented 2026-09-07, 55 tests plus
ten from GREEN, all green):

- **R4-1 Verify before mutating.** `copy`/`move` check the source exists before anything
  (`FileNotFoundException` naming it). An existing destination is never deleted up front: with
  `overwrite:true` it is **moved aside** to a sibling (`<dst>.replaced.<guid>`), the copy or move
  runs, and only on success is the aside removed; on any failure — an exception or cancellation —
  the partial destination is removed and the aside is put back. Covers a missing or locked
  source, a copy that fails on the third file, and a cancel after the clear, for files and
  directories, same-volume and cross-volume moves alike.
- **R4-2 Canonical paths for the containment check.** Both paths are canonicalised before the
  comparison: `\\?\` and `\\?\UNC\` prefixes stripped, `GetFullPath`, and every existing
  ancestor that is a reparse point resolved through `Directory.ResolveLinkTarget(…, true)`, so
  `C:\j\x` with `C:\j → C:\a` compares as `C:\a\x`. A resolution that fails (a junction cycle)
  refuses the call. The tool layer refuses `\\?\` and `\\.\` forms on every path parameter
  outright: a model has no reason to send them.
- **R4-3 Roots.** A volume root or share root (`Path.GetPathRoot(full) == full`) is refused as a
  copy/move source or destination and as a delete target, before anything else.
- **R4-4 Junctions and symlinks.** `CopyDirectory` does not descend into a directory reparse
  point and does not recreate it (the cross-volume move shares the path). `delete` of a
  directory reparse point removes the link only, never enumerates through it, needs no
  `recursive`, and the reply says `removed link`.
- **R4-5 The listing is bounded and cancellable.** `ListAsync` observes the token per entry and
  stops at `maxEntries`; it returns `FileListing(Entries, Truncated, MaxEntries)` (PascalCase
  DTO, replacing the bare array shipped earlier in this same unreleased phase). The tool's
  `max_entries` defaults to 1000, range 1–100000. A recursive listing of `C:\Windows` was
  160 000 entries and 42 MB in one response.
- **R4-6 No orphaned temp file.** Any failure after the temp file is written — an
  `UnauthorizedAccessException` on a read-only target, a cancel during the retry delay — deletes
  it before propagating. `archive(zip)` writes to a temp beside the target and moves it into
  place instead of deleting the target first.
- **R4-7 More exception types reach the client.** `ToolErrors.IsCallerFacing` gains
  `KeyNotFoundException`, `IOException` (and so `FileNotFound`, `DirectoryNotFound`,
  `PathTooLong`, "in use"), `UnauthorizedAccessException` and `TimeoutException`. These are the
  deliberate answers of `registry_get`, the window matcher ("open windows: …"), element ids, the
  app catalog, `scheduled_task`, `watch`, `wait_for`'s old overload and every file tool — all of
  them masked as "An error occurred invoking …" until now. `NullReference`, `IndexOutOfRange`,
  `OutOfMemory`, `COMException` and `Win32Exception` stay masked.
- **R4-6b (found while writing the zip test)** `archive(zip)` was worse than "deletes the
  target first": `ZipFile.CreateFromDirectory` creates the archive before it reads the source,
  so a failing zip left a valid *empty* archive where the caller's previous one was. It now
  builds beside the target and moves into place only when complete.
- **R4-8 Honest replies.** The `file_manage` description says `overwrite:true` **replaces** the
  destination, deleting what was there. `delete` of a path that does not exist replies
  `nothing at '<path>' to delete`, not `deleted`.

### Tests (round 4)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4-1 | Missing source: refused, destination intact; locked source on a same-volume directory move: destination intact, source intact; a copy that fails mid-tree (a read-denied file) leaves the destination as it was; a copy cancelled mid-tree restores the destination; success removes the aside and leaves no `*.replaced.*` | `FileSystemServiceFlagsTests` | Integration |
| R4-2 | `\\?\` form of an inside/contains/same path refused like the plain form, both directions, both `overwrite` values; a destination inside the source through a junction refused; tool refuses `\\?\` and `\\.\` before the service | `FileSystemServiceFlagsTests`, `FileToolsTests` | Integration / Unit |
| R4-3 | A `subst` root as source, as destination (with and without `overwrite`), and as delete target: refused, nothing created or deleted | `FileSystemServiceFlagsTests` | Integration |
| R4-4 | Self-referencing junction and a junction to an outside directory inside the source: not descended, not recreated, copy otherwise complete; `delete` of a junction removes the link, target untouched, no `recursive` needed | `FileSystemServiceFlagsTests` | Integration |
| R4-5 | Cancelled mid-walk → `OperationCanceledException`; `maxEntries` honoured with `Truncated:true`; tool default and range; the DTO JSON | `FileSystemServiceFlagsTests`, `FileToolsTests`, `HttpTransportTests` | Integration / Unit |
| R4-6 | Read-only target, cancel during the retry delay: no `*.tmp.*` left; `ZipAsync` failure leaves the old archive | `FileSystemServiceFlagsTests` | Integration |
| R4-7 | The four types caller-facing, the masked five still masked; over HTTP `file_write(create_parents:false)`, `registry_get` on a missing key, `switch_to_window` on a missing title and `file_read` on a missing file each return the message verbatim | `ToolErrorsTests`, `HttpTransportTests` | Unit / Integration |
| R4-8 | The description text; the two replies | `FileToolsTests` | Unit |

### Round 4b — the review of round 4 (2026-09-07)

`review-agent` on the round-4 diff found six more, two of them data loss in the aside logic
itself. Decisions, each a RED row before code:

- **R4b-1 Commit never triggers Restore.** `aside.Commit()` runs after the `try`/`catch`; a
  failure to remove the aside is reported as "replaced, but the previous destination could not
  be removed and remains at `<aside>`" (`InvalidOperationException`) with nothing else touched.
  The common cause — a read-only file or a junction inside the old tree — is handled by
  **R4b-3**, so Commit rarely fails at all.
- **R4b-2 A cross-volume move never leaves a file in zero places.** The copy lands and the aside
  is committed *before* the source is removed; a failure while removing the source leaves the
  destination complete and reports what remains at the source (`InvalidOperationException`
  naming it). Links inside the tree are not carried across volumes; the description says so.
- **R4b-3 One tree remover.** `DeleteTree(path)` replaces every `Directory.Delete(…, true)`:
  it removes a directory reparse point as a link without descending, clears the read-only
  attribute on files (the caller confirmed the delete; a read-only file is not a second gate),
  and deletes files then directories. Used by `delete recursive:true`, the aside's commit and
  restore, and the cross-volume source removal — so `delete recursive:true` of a tree that
  contains a junction succeeds, the junction's target is untouched, and the reply says `deleted`.
- **R4b-4 Every spelling of the device prefix.** Both the tool's refusal and the service's
  `StripExtendedPrefix` normalise `/` to `\` first, so `//?/`, `//./`, `\\?/` and `/\?\` are the
  same as `\\?\` and `\\.\`. The four read-only file tools (`file_hash`, `file_info`,
  `file_streams`, `archive`) now go through `RequireAbsolute` too — the earlier follow-up closed.
- **R4b-5 Roots are checked on the canonical path** as well as the given one, so a junction to
  a volume root is refused as a root whichever volume the other end is on.
- **R4b-6 The listing does not follow links.** The walk is a `FileSystemEnumerable` whose
  recursion predicate skips reparse points; a junction or symlink is listed as an entry
  (`FileEntry.IsLink`, a new trailing field) but never descended into, so a self-referencing
  junction cannot run the walk into the path limit. Every caller-facing error message is capped
  at 2 000 characters by the host's filter (`ToolErrors.MessageFor`), so no answer to a client is
  a 32 KB path.
- **R4b-7 Small honesties.** `delete` of a path whose parent is missing replies `nothing at …`
  (the service treats a missing path as a no-op); a file `copy`/`move` creates a missing
  destination parent as a directory copy does, and the description says so; `orphans` refuses
  `includeLineage`/`groupByRoot` like the other plain-list options; the `max_entries` refusal
  says that `0` is not "all" here.

### Tests (round 4b)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4b-1 | Directory move with `overwrite:true` onto a tree holding a read-only file, and onto one holding a junction: succeeds, source at the destination, no aside left; a file move onto a read-only file: succeeds; a Commit that cannot remove the aside (a handle held inside it) throws naming the aside and leaves the moved tree in place | `FileSystemServiceRound4Tests` | Integration |
| R4b-2 | Cross-volume move with a source file held `FileShare.Read`: throws naming the source, every source file exists in exactly one place, destination complete; with a junction in the source: succeeds, link dropped, target untouched | `FileSystemServiceRound4Tests` | Integration |
| R4b-3 | `delete recursive:true` of a tree with a junction and a read-only file inside: gone, target untouched, reply `deleted`; without `recursive` still refused | `FileSystemServiceRound4Tests`, `FileToolsDeleteReplyTests` | Integration |
| R4b-4 | `//?/`, `//./`, `\\?/`, `/\?\` refused at the tool on every path parameter of every file tool including the four read-only ones; the containment guard refuses the `//?/` spelling of same / inside / contains, both `overwrite` values | `FileToolsTests`, `FileSystemServiceRound4Tests` | Unit / Integration |
| R4b-5 | A junction to a `subst` root as source, destination on another volume: refused as a root | `FileSystemServiceRound4Tests` | Integration |
| R4b-6 | `list recursive:true max_entries:100000` over a self-referencing junction: returns, `IsLink:true` on the junction entry, no descent; `MessageFor` caps at 2 000 chars and the HTTP filter uses it | `FileSystemServiceRound4Tests`, `ToolErrorsTests`, `HttpTransportTests` | Integration / Unit |
| R4b-7 | The four replies and refusals | `FileToolsTests`, `FileToolsDeleteReplyTests`, `ProcessToolsTests` | Unit / Integration |

### Round 4c — the second review (2026-09-07)

Eleven smaller findings on the round-4b code, each a RED row before code (implemented the same
day; the link-cycle refusal moved from the final-target resolution to an explicit visited set
once the hop-by-hop walk replaced it):

- **R4c-1 A root is a root with a trailing separator.** `IsRoot` compares the trimmed forms, so
  `\\server\share\` and `\\?\UNC\server\share\` are refused as delete targets like `C:\`.
- **R4c-2 A restore that could not put the destination back says so.** `Aside.Restore` returns
  whether the destination is back as it was; when it is not, the caller's failure is wrapped in
  an `InvalidOperationException` that keeps the original as its inner exception and names the
  partial destination and the `<dst>.replaced.<guid>` holding the previous content. The tool
  description says the put-back is best effort and where the previous content is if not.
- **R4c-3 A cross-volume move whose aside cannot be removed says the move did not complete**:
  the copy is at the destination, the source is still at the source, the previous destination
  is at the aside.
- **R4c-4 Link chains to a root.** `Canonical` walks a link's immediate targets hop by hop (at
  most 32) and refuses when any hop is a volume root — but only for the path itself, not for an
  ancestor: `link\sub` where `link → S:\` is an ordinary subdirectory and copies.
- **R4c-5 `delete` clears the read-only bit on a directly named file or empty directory**, as
  the tree remover and the aside already do: the caller confirmed the delete.
- **R4c-6 A `list` pattern with a path separator is refused** naming the parameter (a pattern is
  a name glob; `recursive:true` descends), not answered with an empty listing.
- **R4c-7 The four read-only file tools say "(absolute)"** in their path descriptions.
- **R4c-8 `delete recursive:true` observes the token per entry** (`DeleteTree` takes it; the
  aside and the cross-volume source removal pass none — they must finish).
- **R4c-9 The message cap never splits a surrogate pair**, and the marker says what it is: a
  2 000-character limit.
- **R4c-10 A file symlink deleted by name replies `removed link`** and its target survives.
- **R4c-11 `StripExtendedPrefix` strips only when a drive or UNC path follows**;
  `\\?\Volume{…}\` is left as it is and refused as a root by the same check.

### Tests (round 4c)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4c-1 | `IsRoot` (via `DeleteAsync` and `RefuseRootsAndSelfContainment`) refuses `\\host\share\`, `\\host\share`, `\\host\share\\`, `\\?\UNC\host\share\` and `C:\`, `C:/` | `FileSystemServiceRound4Tests` | Integration |
| R4c-2 | Copy failing while the partial destination is pinned: `InvalidOperationException` naming the destination and the `.replaced.` aside, inner exception the original `IOException`; a restore that succeeds keeps throwing the original type | `FileSystemServiceRound4Tests` | Integration |
| R4c-3 | Cross-volume move with the aside pinned: message names the source as still there and the destination copy | `FileSystemServiceRound4Tests` | Integration |
| R4c-4 | `l1 → l2 → subst root` as source and as destination refused; `link\sub` under a root link copies both ways; `delete` and `copy` agree | `FileSystemServiceRound4Tests` | Integration |
| R4c-5 | Read-only file and read-only empty directory named directly delete with and without `recursive` | `FileSystemServiceRound4Tests` | Integration |
| R4c-6 | `pattern:"sub\*.txt"` and `"sub/*.txt"` refused at the tool naming `pattern`; the service refuses too | `FileToolsTests`, `FileSystemServiceRound4Tests` | Unit / Integration |
| R4c-7 | The four descriptions contain "absolute" | `FileToolsTests` | Unit |
| R4c-8 | Cancel mid-walk of a recursive delete → `OperationCanceledException`, some entries remain | `FileSystemServiceRound4Tests` | Integration |
| R4c-9 | A message with an emoji at the cut: no lone surrogate; the marker text | `ToolErrorsTests` | Unit |
| R4c-10 | File symlink: reply `removed link`, target survives | `FileToolsDeleteReplyTests` | Integration |
| R4c-11 | `\\?\Volume{0000…}\` as a copy source refused as a root; `\\?\C:\x` still stripped | `FileSystemServiceRound4Tests` | Integration |

### Round 4d — the third review (2026-09-07)

Five findings on the round-4c code, one of them the PR #25 runaway reachable once more through a
drive-letter alias. Each a RED row before code (implemented the same day: `PathCanonical` +
`IFinalPathNative`/`Win32FinalPathNative`, `CreateFile` and `GetFinalPathNameByHandle` in
`NativeMethods.txt`; the leaf-link-to-root walk stays as `RefuseLeafLinkToRoot`):

- **R4d-1 The canonical path is the volume's own.** A `subst` drive or a mapped network drive
  is a second spelling of the same directory that no string comparison sees. `Canonical` now
  asks Windows for the final path of the deepest existing ancestor
  (`GetFinalPathNameByHandle` on a directory handle opened with backup semantics, declared in
  `NativeMethods.txt`, `VOLUME_NAME_DOS`) and appends the segments that do not exist yet; that
  final path resolves aliases, junctions and symlinks alike. The hop-by-hop walk stays only for
  the leaf-link-to-a-root refusal. Behind an internal `IFinalPathNative` seam so the pure
  comparison is unit-testable.
- **R4d-2 A failed copy or move removes the parent directories it created**, deepest first and
  only while empty; a parent that existed before is left alone.
- **R4d-3 A drive reference is not a path.** After the prefix strip, `\\?\C:`, `\\?\C:relative`
  and anything not fully qualified are refused as "not a plain absolute path" at the service,
  after the root check (so `\\?\Volume{…}\` is still "a volume root").
- **R4d-4 `list` of a file says so**: `'<path>' is a file, not a directory`.
- **R4d-5 `NotRestored` punctuates**: a cause without terminal punctuation still reads as two
  sentences.
- **R4d-6 A canary for the environment-dependent tests.** The link and root tests return
  silently where a symlink or a `subst` drive cannot be made; one test fails when a `subst`
  drive cannot be created at all, so the whole family cannot vanish without a sound.

### Tests (round 4d)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4d-1 | `<vol>\project` → `<L>:\project\backup` refused as inside (copy and move), nothing created; `<L>:\project` → `<vol>\project` with `overwrite:true` refused as contains, no aside; the same directory through the two letters refused as the same path, with and without `overwrite`, never renamed; the canonicaliser equates the two spellings (unit, fake seam) | `FileSystemServiceRound4Tests`, `PathCanonicalTests` | Integration / Unit |
| R4d-2 | Locked source, destination three parents deep and missing: after the failure none of the created directories exist; with the first parent pre-existing, it survives | `FileSystemServiceRound4Tests` | Integration |
| R4d-3 | `\\?\C:` and `\\?\C:relative` as copy/move source, destination and delete target: refused naming the path as not a plain absolute one, nothing touched | `FileSystemServiceRound4Tests` | Integration |
| R4d-4 | `ListAsync` on a file: the exception message names the path and says it is not a directory | `FileSystemServiceRound4Tests` | Integration |
| R4d-5 | `TwoSentences` with a cause message lacking a final period (no provocable cause reaches `NotRestored` without one, so the join is tested on its own) | `FileSystemServiceTests` | Unit |
| R4d-6 | The canary | `FileSystemServiceRound4Tests` | Integration |

Kept on purpose: the root check on the canonical paths in `RefuseRootsAndSelfContainment` is
unreachable today (`RefuseLeafLinkToRoot` runs first, and the final path of an alias to a root is
its target directory), and stays as defence in depth rather than tested coverage.

### Round 4e — the fourth review (2026-09-07)

Implemented the same day (build clean, headless suite green).

- **R4e-1 Containment is checked on both spellings.** The as-written full paths and the
  canonical paths are each compared; either relationship refuses. A destination that is itself a
  junction or symlink inside the source resolves to its target outside the source, but its
  literal place is inside — and the aside step would have moved the link and let the copy
  recurse into what it created.
- **R4e-2 Created parents are tracked as they are made**, so a parent chain that fails part-way
  (an illegal name, an over-long component) leaves nothing behind; and they are removed on
  every failure path, including the one where the destination could not be put back.
- **R4e-3 The final-path seam is long-path safe**: the plain path is opened in its
  extended-length form, so an ancestor beyond 260 characters is still resolved.
- **R4e-4 `SameRoot` compares canonical roots**, so a junction to another volume takes the
  cross-volume path and a `subst` alias of one volume does not.
- **R4e-5 Cosmetic:** `TwoSentences` adds no period after `:`, `;` or `…`; `list` of a file
  spelled with a trailing separator gets the "is a file" sentence.

### Tests (round 4e)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4e-1 | A junction and a directory symlink to an outside directory, as the destination inside the source, copy and move, `overwrite` true and false: refused as inside (never "already exists; pass overwrite:true"), nothing created, the link and its target untouched; through a `subst` letter too | `FileSystemServiceRound4Tests` | Integration |
| R4e-2 | `dst = <tmp>\newB\x*y\leaf` and an over-255-character middle component: throws, `newB` gone (copy and move); in the R4c-2 pin case the created parents survive only because they still hold the partial destination nobody could remove, and the caller still hears their own failure | `FileSystemServiceRound4Tests` | Integration |
| R4e-3 | `FinalPathOf` on a plain path longer than 260 characters (a junction at that depth) returns the target | `Win32FinalPathNativeTests` | Integration |
| R4e-4 | Move of a directory through a junction to a second volume completes (skipped with a reason on a one-volume box) | `FileSystemServiceRound4Tests` | Integration |
| R4e-5 | `TwoSentences` with `:`; `ListAsync` of `<file>\` | `FileSystemServiceTests`, `FileSystemServiceRound4Tests` | Unit / Integration |

### Round 4f — the fifth review (2026-09-07)

Three findings on the round-4e code, implemented the same day:

- **R4f-1 `SameRoot` needs both agreements.** `Directory.Move` refuses roots that differ as
  written, so round 4e's canonical comparison alone skipped the copy-then-delete fallback for a
  `subst` alias of one volume and the move failed with the framework's "identical roots"
  message. The fallback now runs when the roots differ as written **or** canonically.
- **R4f-2 An honest message when nothing pre-existed.** With no aside, `NotRestored` says a
  partial result could not be removed and was left at the destination, not that it "could not be
  put back".
- **R4f-3 Every trailing separator.** `list` of `<file>\` or `<file>/\` gets the "is a file"
  sentence; one separator was trimmed before.

Recorded as cosmetic follow-ups, not fixed: a destination that is a link to the source is refused
as "the same path" (a safe refusal with an imprecise sentence); `TwoSentences` after a closing
quote that already ends a sentence adds a second period.

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R4f-1 | Move out of, into, and between `subst` spellings of one volume completes; the junction-to-a-second-volume move still completes | `FileSystemServiceRound4Tests` | Integration |
| R4f-2 | The no-aside failure message says "left at" the destination and never "put back" | `FileSystemServiceRound4Tests` | Integration |
| R4f-3 | `\`, `//`, `\/`, `/\` suffixes on a file path | `FileSystemServiceRound4Tests` | Integration |
