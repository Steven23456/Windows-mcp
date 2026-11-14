# E-9 — `file_edit` and content search for remote hosts

Checklist item: `docs/upstream-parity-checklist.md` § E-9.

**Status:** implemented 2026-11-13 (see CHANGELOG [Unreleased]; the "done when" bar was met
in-process over the real HTTP transport by `FileEditHttpTransportTests`, not on a remote host; no
network share, FAT or OneDrive target was tried and ACL preservation is not tested). The review's
changes are folded in below ("What the review changed"). "Not in scope" below and the gaps found
are logged as parity D-74 … D-85.

## Problem

On a local machine the client's own edit and search tools reach the disk. On a remote host over
HTTP they cannot: changing one line of a config file means `file_read` then `file_write` of the
whole file (twice across the wire, and the model retypes every other line), and finding which file
mentions a setting means `powershell`.

Two changes: a new tool `file_edit` (the count goes 70 → 71), and a content mode on `file_search`.

## `file_edit`

```
file_edit(path, old_string, new_string, replace_all = false, encoding = "auto", confirm = false)
```

Replaces `old_string` with `new_string` in one existing text file and writes it back. Neither the
old nor the new content crosses the wire — only the two strings.

Annotations (C-7): `Title = "Edit file"`, `ReadOnly = false`, `Destructive = true`,
`Idempotent = false` (a second identical call no longer finds `old_string`), `OpenWorld = false`.

### Refused before the file is opened (`ArgumentException`)

- `confirm` is not true — the rule every file write follows.
- `path` is not a plain absolute path (`RequireAbsolute`, as C-1).
- `old_string` is empty; `new_string` is null (an empty string is how to delete `old_string`);
  `old_string` equals `new_string` (ordinal): nothing would change.
- `encoding` is not one of `auto`, `utf-8`, `utf-16`, `ascii`, `latin1` (any case).

### Refused after looking at the file, with nothing written

- The path does not exist → `FileNotFoundException` naming it. A directory →
  `InvalidOperationException`. A **symbolic link or junction** (a reparse point whose
  `LinkTarget` is not null) → `InvalidOperationException` saying to edit its target (replacing the
  file would replace the link). Other reparse files — OneDrive files, deduplicated files — are
  edited normally.
- More than one **hard link** (`GetFileInformationByHandle`, `nNumberOfLinks > 1`) →
  `InvalidOperationException` naming the link count: `File.Replace` gives the path a new file
  record, so the file's other names would keep the old content and one file would silently become
  two. When the count cannot be read the edit proceeds.
- Larger than **16 MiB** (`TextEdit.MaxFileBytes`) → `InvalidOperationException` naming the size
  and the cap.
- The bytes are not valid in the encoding (see below) → `InvalidOperationException` naming the
  encoding and saying which `encoding` values exist. Nothing is decoded with replacement
  characters: an edit must never rewrite bytes it did not understand.
- `old_string` occurs **zero** times → `InvalidOperationException`: not found in `<path>`.
- It occurs **more than once** and `replace_all` is false → `InvalidOperationException` saying how
  many times, and to add surrounding context to make it unique or pass `replace_all:true`.

### Encoding and BOM — preserved

The file's bytes are read once.

- A byte-order mark decides when present, whatever `encoding` says: UTF-8 (`EF BB BF`), UTF-16 LE
  (`FF FE`), UTF-16 BE (`FE FF`), UTF-32 LE (`FF FE 00 00`, checked before UTF-16 LE). UTF-32 BE
  has no branch (D-81).
- Without a BOM: `auto` and `utf-8` → UTF-8; `utf-16` → UTF-16 LE; `ascii` → ASCII; `latin1` →
  ISO-8859-1 (every byte is a character, so any single-byte legacy file can be edited without
  loss).
- **`auto` does not guess UTF-16.** Without a BOM, `auto` refuses decoded text that contains
  U+0000 (`InvalidOperationException`: it looks like UTF-16 without a byte-order mark, or binary;
  pass `encoding:"utf-16"` if it is text, or `encoding:"utf-8"` if it really is UTF-8 text that contains
  NUL characters). UTF-16 ASCII text is valid UTF-8, and a one-character
  edit would shift every later character. An explicit `utf-8` is taken at its word.
- Decoding is **strict** (invalid sequences are an error; for `ascii`, any byte above `0x7F`).
- The result is encoded with the same encoding, and has a BOM **exactly when the original had
  one**. A `new_string` the encoding cannot represent (a non-ASCII character into an `ascii` file,
  a character above U+00FF into `latin1`, a lone surrogate anywhere) is refused
  (`InvalidOperationException`) with nothing written.

### Line endings — preserved

The model usually sends `\n`; a Windows config file usually has `\r\n`. Matching is ordinal on the
decoded text, in two attempts (`TextEdit.Apply`, pure):

1. `old_string` exactly as given.
2. Only if that found nothing: `old_string` with its line endings converted to the file's — bare
   `\n` → `\r\n` when the file contains `\r\n`; `\r\n` → `\n` when the file contains no `\r`.

**A match never splits a `\r\n` pair:** an occurrence that starts with `\n` right after a `\r`, or
ends with `\r` right before a `\n`, is not a match. So `old_string:"\nfoo"` in a CRLF file is not
found exactly, is found as `"\r\nfoo"` on the second attempt, and leaves no stray `\r`
(`"a\r\nfoo\r\nb"` → `"a\r\nbar\r\nb"`; deleting it with `new_string:""` → `"a\r\nb"`;
`replace_all` of `"\n"` by `"\n\n"` doubles the line breaks cleanly).

`new_string` is then written in the file's convention: when the file has `\r\n` and no bare `\n`,
bare `\n` in `new_string` becomes `\r\n`; when the file has line endings but no `\r`, `\r\n` becomes
`\n`. In a file with **mixed** endings, or with **no line endings at all**, `new_string` is written
as given, except that when attempt 2 was what matched, `new_string` gets the same conversion
`old_string` got. Bytes outside the replaced spans are never touched, so the rest of the file
keeps whatever endings it had.

Occurrences are counted left to right, non-overlapping. `firstLine` counts `\r\n`, `\n` and a lone
`\r` each as one line break.

### The write

- The edited bytes go to a temp file in the target's folder (`~wmcp-<16 hex>.tmp`), created **with the
  target's access rules** (`FileSystemAclExtensions.Create` with the access section of the
  target's descriptor), so the edited content of a file with a restrictive explicit ACL is never
  briefly readable through the folder's inherited ACL. When the target's ACL cannot be read or
  applied the temp is created normally and the edit still works (silently — D-80).
- Immediately before the replace the target's length and last-write time are compared with what
  was read; if they changed, the edit is abandoned with an `IOException` ("changed while it was
  being edited"), nothing written.
- `File.Replace(temp, path, backup)` swaps it in, with a **backup name** beside the target
  (`~wmcp-<the same hex>.bak`). Unlike the move `file_write` uses, a replace keeps the target's identity:
  its ACL, attributes, creation time and alternate data streams, where the file system supports
  it. A sharing violation is retried twice (50 ms, 100 ms), and only while `path` still exists.
- **A failed swap never loses the file, and never recreates one** (`RecoverFromFailedEdit`).
  When the swap had not started (a changed file, a cancel, a failed temp write) the temp is
  deleted, whatever became of the target. When it had: `ReplaceFile` can fail after it has moved
  the original to the backup name (error 1177), so if `path` no longer exists and the backup
  does, the backup is moved back; if that move fails, the `IOException` names the backup and
  says to rename it. With a backup name every documented failure leaves the target or the
  backup, so **no file and no backup** means someone else deleted the target while it was being
  edited: their deletion wins — the edit is not applied, the file is not recreated, and an
  `IOException` says so and names the temp file, which keeps the edited content. The temp and
  the backup are deleted only once a file is at `path`; on success the backup is deleted (best
  effort, unreported if it fails — D-83).
- A read-only target is refused by Windows (`UnauthorizedAccessException`); it is not made
  writable.
- Cancellation is honoured before the read, before the temp write and before the replace.

### Result

```json
{"path":"…","replacements":1,"firstLine":42,"encoding":"utf-8","bom":false,
 "lineEndings":"crlf","bytesBefore":1834,"bytesAfter":1851}
```

`firstLine` is the 1-based line of the first replacement. `lineEndings` is `crlf`, `lf`, `mixed`
or `none` (as found in the file before the edit). `encoding` is `utf-8`, `utf-16`, `utf-16-be`,
`utf-32`, `ascii` or `latin1`.

The description says: refused for a hard-linked file; symlinks and junctions (not every reparse
file); permissions and the rest are kept "where the file system supports it"; when the swap fails
the original is left in place or put back, and a file removed meanwhile is not recreated.

### Shape in the code

- `IFileSystemService.EditTextAsync(path, oldString, newString, replaceAll, encoding, ct)` →
  `FileEditResult(Path, Replacements, FirstLine, Encoding, Bom, LineEndings, BytesBefore, BytesAfter)`.
- `Services/TextEdit.cs` (pure): `Detect(bytes, encoding)` → the encoding, BOM length, name and
  whether it was guessed; `Decode` / `Encode` (strict); `Apply(text, oldString, newString,
  replaceAll)` → new text, count, first line, line-ending kind. The refusals about content are
  thrown from here so they are unit-tested without a disk.
- `FileSystemService.EditTextAsync` with `CreateTempLike`, `HardLinkCount` and
  `RecoverFromFailedEdit`.

## `file_search` content mode

`file_search` gains three parameters, appended after `find_duplicates`:

| parameter | meaning |
|---|---|
| `content` (`string?`) | a .NET regular expression matched against each line; null/blank = today's behaviour, unchanged |
| `max_matches` (`int`, default 100) | stop after this many matching lines, 1–1000 |
| `context_lines` (`int`, default 0) | lines of context before and after each match, 0–5 |

With `content`, the existing `pattern` (name glob), `min_size` and `modified_since` still choose
which files are read.

Refused before anything is enumerated (`ArgumentException`): an invalid regex (naming `content`
and quoting the parser's message); `max_matches` or `context_lines` out of range (checked always,
as `settle_ms` is); `content` together with `find_duplicates:true`; and, in a content search, a
`pattern` containing `\` or `/` — `pattern` is a file-name glob there, so the folder belongs in
`root` (the name search still accepts one, D-74).

### Reading

- Files are enumerated recursively with inaccessible directories skipped. **Every folder that is
  not a link is entered** — OneDrive folders are reparse points too — while a junction or a
  directory symlink (a reparse directory with a link target, `IsDirectoryLink`) is not entered, so
  a junction loop cannot hang a search. Hidden and system files are
  read. The existing no-`content` path is unchanged (D-74).
- A file is **skipped and counted** in `filesSkipped` when it is a **file symlink**, a **cloud file
  not on disk** (`Offline`, `RecallOnOpen` or `RecallOnDataAccess` — a search never downloads
  files), cannot be opened, is larger than 16 MiB, or is binary: no BOM and a NUL byte in its first
  8 KiB (so BOM-less UTF-16 and UTF-32 BE are not searchable — D-81).
- Text is decoded by BOM, else UTF-8 — leniently: this only reads. Lines are read one at a time, so
  a large log does not load whole.
- The regex is matched per line (the line without its terminator), `CultureInvariant`, with a
  **2-second match timeout** per line; a timeout is an `InvalidOperationException` naming the
  pattern and the file ("too slow on …") — a pathological pattern must not pin a core.
  Case-insensitive search is `(?i)` in the pattern.
- Cancellation is observed on every entry the walk visits and every 1,000 lines.
- The search stops at `max_matches` matching lines, **or once the matched and context lines
  returned total 60,000 characters** (`ContentSearch.MaxResultChars`, shared across files), and
  reports `truncated:true`. (1,000 matches × 11 lines × 400 characters would be 4.4 MB.)
- Scan cost does not grow with the matches found: only the matches still collecting after-context
  are visited per line (none when `context_lines` is 0).

### Result (content mode)

```json
{"matches":[{"path":"…","line":12,"text":"…","before":["…"],"after":["…"]}],
 "filesSearched":37,"filesSkipped":2,"truncated":false,"maxMatches":100}
```

`line` is 1-based. `text`, and each context line, is clipped at 400 characters with `...`.
`before` / `after` are present only when `context_lines > 0`. Matches are in enumeration order,
then line order. Without `content` the result is the array of `{Path, Size, Modified}` it is today.

The description says: hidden and system files are read; file symlinks and cloud-only files are
skipped and counted, never downloaded; junctions and symlinked folders are not entered (other
folders, OneDrive's included, are); `pattern` is a file-name glob; the 60,000-character output cap.

### Shape in the code

- `IFileSystemService.SearchContentAsync(ContentSearchRequest, ct)` → `ContentSearchResult`.
- `Services/ContentSearch.cs` (pure, over a `TextReader`): the per-file line scan with context, the
  match cap and the character cap (`MaxResultChars`); `LooksBinary(bytes)`.

## What the review changed, and why

The first version swapped the file in with no backup name and deleted the temp on any failure, so
a `ReplaceFile` that failed after removing the original (error 1176) left no file at all — hence
the backup and the restore-before-delete cleanup. It matched `"\nfoo"` inside a `\r\n` pair and
left a stray `\r` — hence the pair rule. Its temp file had the folder's inherited ACL, briefly
exposing a protected file's edited content — hence `CreateTempLike`, and "where the file system
supports it" in the description. It refused every reparse file (so OneDrive files could not be
edited) while the content search silently dropped them and would have recalled cloud files — hence
"link" means `LinkTarget`, and links and cloud-only files are skipped and counted. `auto` decoded
BOM-less UTF-16 as UTF-8 and would have corrupted it — hence the NUL refusal. It forced `\n` on a
file with no line endings, and counted lines wrongly after a lone `\r`. The content result had no
size bound, a hard-linked file would silently have become two files, a null `new_string` was a
deletion, and the scan revisited every earlier match on every line (14 s against 0.14 s on a
16 MB file with 999 early matches). Each is fixed as described above.

A second pass changed four more things. The recovery had moved the edited temp into place when
neither the target nor a backup was left, which recreated a file someone had just deleted and
reported success — now their deletion wins and the temp is named instead (and a swap that never
started always deletes its temp). The content search had refused to enter any reparse
directory, which skipped OneDrive folders — now only links are not entered. A content search's
`pattern` may no longer name a folder. And the NUL refusal names `encoding:"utf-8"` as well.
Logged, not fixed: the owner (and possibly the integrity label) of an edited file can change
(D-82); a backup whose delete fails is left unreported (D-83); the two search modes walk and
match differently (D-74).

## Decisions from the PR #47 review

**1. Every retry gets the first attempt's check.** When the swap hits a sharing violation it is
retried (twice, after 50 ms and 100 ms) only while the target exists, and after each wait the
target is checked again exactly as before the first attempt — exists, length, last-write time.
Whoever held the file may have written it meanwhile: if it **changed**, the call fails with
"File '<path>' changed while it was being edited; nothing was written. Read it again and retry
the edit." and the other writer's content stays. If it is **gone**, there is no second replace
attempt and the file is not recreated (the recovery above reports it, naming the temp).

**2. The result is capped too, not only the input.** An edit whose result would exceed 16 MiB is
refused with `InvalidOperationException` before anything is written: `TextEdit.Apply` refuses a
result longer than 16,777,216 characters (the length is computed in 64 bits — a `replace_all`
that multiplied the text used to overflow an `int` and surface as an
`ArgumentOutOfRangeException`), and `FileSystemService.EditTextAsync` refuses an encoded result
longer than 16,777,216 bytes, BOM included (a UTF-16 file doubles the characters).

**3. `file_edit` reads through one handle.** The 16 MiB check, the bytes and the last-write
baseline for the "changed while it was being edited" check all come from the same open file,
opened with `FileShare.Read` so no writer can have it open meanwhile. A file that grows between a
separate size check and the read can no longer be read in full; a change made just before the
open is simply what gets edited.

**4. A content search observes cancellation on every entry the walk visits**, not only on files
whose name matches `pattern` (the check is in the enumeration's include predicate), so a walk
that matches nothing still stops when the caller cancels.

**5. A content search bounds what it reads.** The size is checked again on the opened file — one
that grew past 16 MiB since the walk saw it is skipped and counted in `filesSkipped` — and the
file is read through a 16 MiB bound (`CappedReadStream`, internal, in `Services/ContentSearch.cs`,
which ends the stream at the cap and records whether the source had more). A file that keeps
growing while it is read is skipped and counted too, and its matches are discarded.

`FileSystemService.Stage` gained `EditOpen` and `SearchEntry`, test hooks only.

**6. The hard-link count is checked before every replace attempt**, not only before the read:
before the first attempt and again after each sharing-violation wait. A second name added while
the file was being edited is the same hard-link refusal (`InvalidOperationException` naming the
count), with nothing written.

**7. The recovery deletes the backup only when the file at the path is still the original** —
the same length and last-write time as when it was read. If the swap moved the original aside
and someone else then put a different file at the path, the backup is kept and the
`IOException` names it ("The original is in '<backup>'; nothing was deleted.").

**8. A content search re-reads an entry's attributes from the path right before opening it.**
One that has become a link, a cloud-only file or a directory since the walk saw it, or is gone,
is skipped and counted in `filesSkipped`. This narrows the window between the walk and the
open; it does not close it (D-84: a no-follow open by handle would).

**9. The temp and backup names no longer depend on the target's name.** They are
`<target folder>\~wmcp-<16 hex>.tmp` and `.bak` (the same hex), so a file whose name is near the
255-character component limit can be edited — `<name>.tmp.<guid>` would have been too long.
`file_write` and `archive` still use `<path>.tmp.<guid>` (D-85).

## Not in scope

- Multi-line regex matches; replacing by regex in `file_edit` (D-77).
- Creating a file with `file_edit` (that is `file_write`).
- A time or file-count budget for a content search over a huge tree (cancellation only; D-75).
- Following symlinks in a content search; fixing the no-`content` path's abort on an inaccessible
  directory (D-74).
