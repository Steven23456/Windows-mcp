# Section C roadmap — files, registry, processes, notifications, scrape, shell (C-1 … C-7)

**Scope:** every item in [section C](../upstream-parity-checklist.md#c--files-registry-processes-notifications-scrape-shell)
of the parity checklist. This is the implementation plan; each item still gets its own
`docs/design/<ID>-<slug>.md` note when it is picked up (checklist rule 1), and this file is the
place those notes link back to for the cross-item decisions. ·
**Status:** planned 2026-09-06 against `main` @ `4bd2122` (68 tools, v0.7.3 with sections D, A
and B closed; the B release cut is still the owner's `/version-bump`). Phases 1 and 2 shipped
2026-09-06 (**69 tools**). Where the code deviates from the plan below, the item carries a
**Shipped as** line and its design note the reasoning. ·
**Baseline facts** used below were read from the code on that commit; the `file:line` anchors
will drift, the member names will not.

## 1. What section C is, in one paragraph

Sections A and B made the desktop readable and drivable. Section C is the *non-desktop* half
of the server: the seven tools an agent reaches for between UI steps — read a file, fix a
registry value, find the process eating the CPU, run a script, pull a web page — each of which
today does the job but without upstream's knobs, and in three places with a *less* safe default
than upstream (`file_manage` copies over an existing file, moves over one, and deletes a full
directory tree with nothing but `confirm`). The seven items split into three tracks:
**annotations and small surfaces** (C-7 tool hints on every tool, C-2 registry listing and
delete, C-4 the toast's AUMID); **files and processes** (C-1 the file flags, C-3 CPU/sort/limit
and a graceful kill); and **shell and web** (C-6 a per-call PowerShell timeout and a PATH that
works, C-5 `scrape` from the live browser tab with a client-side summary). Only C-5's DOM part
depends on earlier work (A-5, done). Nothing in section S is needed.

## 2. Cross-item decisions (settle once, every design note inherits them)

Numbered **R1 … R10** so they do not collide with the item ids.

| # | Decision | Why |
|---|---|---|
| R1 | **Absolute paths only, everywhere in C-1.** `file_read`, `file_write`, `file_manage` (and `file_search`'s root) refuse a relative path with one `ArgumentException` naming the rule (`Path.IsPathFullyQualified`); a UNC path is fine. Upstream's Desktop-relative resolution is **not** ported; the checklist already settled this. Today a relative path silently resolves against the server's working directory, which is whatever the MCP host set. | A relative path from a model is a guess about a cwd it cannot see; refusing costs one retry and removes a class of wrong-file writes. |
| R2 | **`file_manage` gets upstream's safer defaults**: `copy`/`move` refuse an existing destination unless `overwrite:true`; `delete` refuses a non-empty directory unless `recursive:true`. Both refusals name the flag. Today the service overwrites and recurses unconditionally (`FileSystemService.cs:152,159,166`). This is the section's first contract change → CHANGELOG *Changed* with the one-line migration. | `confirm:true` was meant to acknowledge "this deletes"; it never meant "and the whole tree under it". Matching upstream here makes the two servers' safety rails equivalent rather than ours being weaker on the one tool that removes data. |
| R3 | **One `FileEntry` for listings**: `file_manage(list)` returns `[{path, name, isDirectory, size, modified, hidden}]` (a `record FileEntry`), not today's `string[]` of paths. `pattern` (glob, `*`/`?`, matched on the name), `recursive` (default false), `include_hidden` (default false: hidden and system entries are skipped, which also keeps `$RECYCLE.BIN` and `System Volume Information` out of a root listing). Contract change → CHANGELOG *Changed*. | A path alone forces a `file_info` round-trip per entry to learn whether it is a directory; upstream prints type and size in the listing for exactly that reason. |
| R4 | **CPU % is a two-sample measurement, pure and injectable.** `ProcessService.ListAsync` takes two `TotalProcessorTime` readings ~250 ms apart for every process and a pure `CpuSample.Percent(before, after, elapsed, Environment.ProcessorCount)` normalises to 0–100 across all cores (a process at 100 % on one of eight cores reads 12.5, as Task Manager shows). `ProcessDto` gains a trailing `CpuPercent (double)`; `sort_by: memory|cpu|name|pid` (default `memory`, descending for the two numbers, ascending for the names) and `limit` (default **0 = all**, so no existing caller loses rows; the description tells the model to pass 20). The name filter stays a **substring**, not upstream's fuzzy > 60. | 250 ms per `process(list)` is cheap for a column an agent asks for constantly ("what is using the CPU"). A fuzzy filter on a list is harmless but on a `kill` it is not, and the two share `name`; one matching rule keeps the kill exact. |
| R5 | **Graceful kill is our addition, not a port.** Upstream's `terminate()` is `TerminateProcess` on Windows — psutil has no graceful path there — so `process(kill, graceful:true, grace_ms:3000)` is defined here: `CloseMainWindow()` when the process has one, otherwise `WM_CLOSE` posted to every top-level window whose owner pid matches (the A-1 inventory carries the pid); wait `grace_ms`; then `Kill()` if it is still alive. The result becomes JSON — `{pid, name, graceful, exitedGracefully, forced, waitedMs}` — and a console process with no window says `exitedGracefully:false, forced:true` honestly. `graceful` with `tree:true` is refused (descendants are killed leaves-first and forcibly; a graceful tree is a different feature). Default stays `false`: today's hard kill is unchanged for existing callers. | A hard kill of an editor loses unsaved work; the graceful path gives the app its own "save changes?" — which the agent can then answer with the section-B verbs. |
| R6 | **Registry: one read shape, one new tool.** `registry_get(hive, path)` without `value_name` returns `{path, values:[{name, kind, data}], subKeys:[…]}` from the two enumerators the service already has (`RegistryService.cs:22,49`), replacing the comma-joined name string (contract change, *Changed*). New tool **`registry_delete(hive, path, value_name?, recursive=false, confirm)`** (68 → 69): a value delete needs `confirm`; a key delete needs `confirm` and, when the key has sub-keys, `recursive:true`; an empty path (a hive root) and a **denylist of roots** (`Software`, `Software\Microsoft`, `Software\Microsoft\Windows`, `Software\Classes`, `System`, `SYSTEM\CurrentControlSet`, `SAM`, `SECURITY`, case-insensitive, trailing-separator-tolerant) are refused outright. Elevation failures under HKLM are reported as the OS gives them. README's Safety-rails list gains the tool. | The enumerators exist and nothing exposes them; delete is the one registry verb an agent cannot do today without `powershell`. The denylist is the cheapest possible guard against `recursive:true` on a root a model typed by mistake. |
| R7 | **Toasts go in-process, with an optional `app_id`.** `NotificationService` calls `Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(aumid)` directly through the `net10.0-windows10.0.19041.0` projection (the route B's C7 took for the app catalog) instead of the PowerShell script that pays a cold start and takes the serialization gate. `notification(title, message, app_id = "Windows-MCP")`. For the **default** id only, the service ensures `HKCU\Software\Classes\AppUserModelId\Windows-MCP` carries `DisplayName` (the documented registration for an unpackaged exe; done once per process, best effort, `RegistryService.SetAsync`), and the result says `{shown, appId, registered}`. A caller-supplied `app_id` is used as given, never registered — the result reports `registered:false` when the key is absent so the agent knows why a toast may be dropped. Fallback if the in-process route fails on a build in the spike: keep the PowerShell script behind the same interface and record it as a Shipped-as deviation. | The AUMID is what Windows uses as toast identity; the hard-coded string is why unregistered builds drop it. Registering *our own* id under HKCU is the minimum that makes the default work, and writing a caller's id would be a registry change behind a `confirm`-less tool. |
| R8 | **`scrape` returns JSON, reads the live tab through A-5, and summarises only on request.** `scrape(url?, query?, source = "http"|"dom", summarize = false, max_chars = 100000)` → `{source, url, title?, chars, truncated, summarized, model?, content}`. `source:"dom"` takes a `snapshot` with `UseDom` scoped to the foreground browser window (or `window` title) and joins `Pages[0].Text` with upstream's edge hints ("Reached top" / "Scroll down to see more") derived from the page's `ScrollInfo`; `url` is then optional and the page's own URL is reported. `summarize:true` sends the content to the **client's** model through `McpServer.SampleAsync` (`CreateMessageRequestParams` with upstream's boilerplate-stripping system prompt focused on `query`, `MaxTokens` sized to the budget) — only when `McpServer.ClientCapabilities?.Sampling` is present; otherwise the raw content comes back with `summarized:false` and a `note`. `max_chars` truncates before sampling and the result says so. The tool takes `McpServer` as an injected parameter (the SDK binds it), so the unit test passes a fake. Contract change (string → JSON) → *Changed*. | Sampling costs the *client* a model call it may bill; defaulting it on would surprise every client that supports it, while Claude Code, the client this repo is used from, does not support sampling at all — the fallback path is the one that runs here, so opt-in is the honest default. The JSON shape is what carries `truncated`/`summarized`, which a plain string cannot. |
| R9 | **PowerShell: a per-call timeout returns a result; the environment is repaired once, process-wide.** `powershell(command, background, timeout_seconds = 0)` — `0` means the 15-minute backstop only; `1…900` starts a linked timer **after** the gate is acquired (the backstop rule in `CLAUDE.md`: bound execution, never queue-wait). On expiry the child tree is killed, the partial stdout/stderr already read are kept, and `RunAsync` **returns** `PSResult` with a trailing `TimedOut:true`, `Success:false`, `ExitCode:-1`, `Errors:["timed out after Ns"]` — the backstop is folded into the same result so the two timeouts behave alike; only the *caller's* cancellation still throws. `timeout_seconds` with `background:true` is refused (a job has `job(cancel)`). Environment: `Hosting/EnvironmentRepair` (already runs first in `Main`) gains one more rule — when the inherited `Path` is empty or lacks `%SystemRoot%\System32`, the registry machine + user `Path` (already `REG_EXPAND_SZ`-expanded by `Environment.GetEnvironmentVariables(target)`; verified in the note) is **appended** after the host's entries and de-duplicated by a pure `PathMerge.Merge(host, machine, user)` (ordinal-ignore-case, trailing separator normalised, empty entries dropped). Nothing the host set is removed or reordered; the startup log names `Path` among the repaired names. `PowerShellInvocation.CreateStartInfo` stays environment-free: foreground calls, jobs, `start_process` and `launch` all inherit the repaired block. | The checklist's "env rebuild" is a startup concern, not a per-spawn one — `EnvironmentRepair` is already the place and already has the injectable pure core and tests. A timeout that *throws* loses the partial output that is usually the diagnosis. |
| R10 | **Every `[McpServerTool]` names all four hints and a `Title`, and a test reads the source-level named arguments.** `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld` are written explicitly on every tool even where the value equals the SDK default, because the only way to assert "explicit" is `CustomAttributeData.NamedArguments` (the property values are indistinguishable from defaults by reflection). Rules: a multi-action tool is `ReadOnly` only when **every** action is (`file_manage`, `process`, `window`, `service`, `env`, `clipboard`, `job`, `firewall`, `scheduled_task`, `audio` are not); `Destructive` = the README Safety-rails set plus `registry_delete`, kill, `power_action`, `file_manage`, `scheduled_task`, `env`, `firewall`; `OpenWorld` = the tools that reach past this machine or run arbitrary code: `scrape`, `http_request`, `network`, `powershell`, `job`, `start_process`, `launch`; everything else `false`. `Idempotent` = the set-state tools (`registry_set`, `env(set)`, `audio(set)`, `window` state actions, `focus`, `switch_to_window`, `file_write` without `append`, `clipboard(set)`, `wait`, all read-only tools). Titles are short Title-Case nouns ("Take screenshot"). The classification table lives in `C-7`'s design note and is pinned by a literal-list test. | Clients auto-approve on `readOnlyHint` and confirm on `destructiveHint`; a wrong hint is worse than none, so the set is a reviewed table, not per-file judgement, and a new tool cannot compile past the test without being classified. |

## 3. Order and phases

```
Phase 1  hints and small surfaces   C-7 → C-2 → C-4                     ~½ day
Phase 2  files and processes        C-1 → C-3                           ~1 day
Phase 3  shell and web              C-6 → C-5                           ~1 day
```

C-7 first, deliberately: once its "every tool is annotated" test exists, C-2's `registry_delete`
and every later signature change is annotated at birth instead of in a sweep at the end. C-2 and
C-4 are the two small items that share phase 1's PR. Phase 2 holds the two contract changes on
`file_manage` and `process` (R2, R3, R5) so they ship under one minor bump. Phase 3 is the two
items with a spike each (the sampling API, the WinRT-free PATH merge) and the two `Integration`
suites that spawn real `powershell.exe` and real HTTP hosts. Phases 2 and 3 are independent and
can run in parallel branches; both need phase 1's annotation test to be green first so their
new parameters do not regress it.

### Dependency graph (checklist "Depends on" column, corrected per R8/R10)

```
C-7 ──► C-2, C-1, C-3, C-5, C-6   the annotation test gates every later tool/signature change
A-5 ──► C-5 (dom source)          done; Pages/UseDom on SnapshotRequest/SnapshotResult
A-1 ──► C-3 (graceful kill)       done; the window inventory's pid → WM_CLOSE targets
C-4, C-6 stand alone              C-6 extends EnvironmentRepair, which already has its pure seam
```

## 4. Per-item plan

Each item: what changes, the decisions that go beyond the checklist sketch, the RED test matrix
seed (what `test-agent` should be handed), and the done-when bar.

### Phase 1 — hints and small surfaces

#### C-7 — Tool annotations on every tool  `P2 · S · ~2 h`

- Every `[McpServerTool]` in `Tools/*.cs` gains `Title`, `ReadOnly`, `Destructive`,
  `Idempotent`, `OpenWorld` per R10's rules; `wait` already carries two of the four and gets the
  rest. The design note holds the full 68-row table (name → the four hints → the reason when it
  is not obvious, e.g. `screenshot` is read-only although the flash overlay draws on screen;
  `hover` is not read-only because it moves the pointer; `storage_health` is read-only although
  it queries SMART). No behaviour changes; no service touched.
- **RED seed.** `ToolInventoryTests`: every tool method's `McpServerToolAttribute` has all five
  named arguments in `CustomAttributeData` (the test fails today for 67 of 68); the read-only
  set equals a literal list; the destructive set is a superset of every tool named in README's
  Safety-rails section (parsed from the file, like the count checks); every open-world tool is
  in a literal list; no tool is both `ReadOnly` and `Destructive`. `HttpTransportTests`:
  `ListToolsAsync` returns `Annotations` with `Title` and all four hints non-null for all 68.
- **Done when.** A client's tool list shows `readOnlyHint:true` on `screenshot` and
  `destructiveHint:true` on `file_manage`, and adding a tool without hints fails the build's
  test run.
- **Shipped as** ([note](C-7-tool-annotations.md)): as planned, except `job` is closed-world
  (R10 listed it open-world; the note has the reason). All four hint sets are pinned as literal
  lists, not only the read-only and open-world ones.

#### C-2 — Registry listing and `registry_delete`  `P2 · S · ~2 h`

- `registry_get(hive, path)` without `value_name` → R6's `{path, values, subKeys}` object
  (`RegistryKeyDto(string Path, RegistryValueDto[] Values, string[] SubKeys)`); with a
  `value_name` unchanged. An absent key stays `KeyNotFoundException` (today's message).
- `IRegistryService.DeleteValueAsync(hive, path, name)` and `DeleteKeyAsync(hive, path,
  recursive)` (`RegistryKey.DeleteValue` / `DeleteSubKey` vs `DeleteSubKeyTree`); a pure
  `RegistryGuard.Check(hive, path, recursive)` holds the denylist and the empty-path refusal.
  New tool `registry_delete` in `RegistryTools`, annotated `Destructive = true`, `Idempotent =
  true` (deleting what is gone is a no-op that says `existed:false`). Result
  `{hive, path, valueName?, deleted, existed, subKeysRemoved?}`.
- **RED seed.** `RegistryToolsTests`: the read shape with values and sub-keys, the old
  `value_name` path unchanged; delete refuses without `confirm`, refuses a key with sub-keys
  without `recursive`, refuses the denylist and an empty path before touching the service (Moq
  verifies no call), forwards value vs key deletes; `RegistryGuardTests` table (each root, each
  case, `Software\` with a trailing separator, `Software\MyApp` allowed). `RegistryServiceTests`
  (`Integration`, under `HKCU\Software\WindowsMcpTests\<guid>`): create → list shows values and
  sub-keys → delete value → delete tree → gone; a missing key deletes as `existed:false`.
  `ToolInventoryTests` count 68 → 69; `HttpTransportTests` schema for the new tool.
- **Done when.** `registry_get("HKCU", "Software\\Microsoft")` returns sub-keys, and
  `registry_delete("HKCU", "Software", recursive:true, confirm:true)` is refused by name.
- **Shipped as** ([note](C-2-registry-delete.md)): as planned, with the guard as
  `RegistryGuard.Refusal(path)` (no hive, no `recursive`: it applies regardless); the denylist
  grew to sixteen roots (the `CurrentVersion`, `Windows NT`, `Policies`, `WOW6432Node` and the three HKCU
  profile roots), each a key whose recursive loss breaks the profile or the OS.

#### C-4 — Notification `app_id`  `P3 · S · ~2 h (incl. a 15-min spike)`

- Spike first: a console call to `ToastNotificationManager.CreateToastNotifier("Windows-MCP")`
  + `Show` from the published single-file exe, with and without the HKCU `AppUserModelId` key,
  on this build. The outcome fixes whether R7's in-process route ships or the PowerShell script
  stays behind the interface.
  **Spike result (2026-09-06, build 28000):** the in-process route works. An unregistered id
  fails at the first property read or `Show` with `COMException 0x80070490` (element not found);
  a packaged AUMID works as-is; the HKCU `AppUserModelId\<id>` key with `DisplayName` makes the
  default id work, with a lag of a second or so on the very first call after registration, and
  the platform remembers the id even after the key is removed. R7 ships as written; the service
  retries once after `0x80070490` and reports `shown:false` with a note if it persists.
- `INotificationService.ShowAsync(title, message, appId)` → `NotificationResult(Shown, AppId,
  Registered)`; the XML escaping stays; `notification(title, message, app_id = "Windows-MCP")`.
  Registration only for the default id, once per process, through `IRegistryService` so the
  unit test sees the exact key and value. `WindowsMcpHost` wiring changes only if the service's
  constructor does.
- **RED seed.** `NotificationServiceTests` (`Unit`, toast sink mocked behind an internal
  `IToastSink`): the default id registers `DisplayName` once across two calls; a custom id never
  writes the registry and reports `registered` from a read; title/message are XML-escaped in the
  payload; a blank `app_id` is refused. `SystemToolsTests`: the parameter default and the JSON
  result. One `Integration` test shows a real toast with the default id and asserts
  `shown:true` (it is visible on the desktop; documented as such).
- **Done when.** `notification("hi", "there")` shows without spawning `powershell.exe`, and
  `notification(…, app_id:"Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")` shows under that app's
  name.
- **Shipped as** ([note](C-4-notification-app-id.md)): as planned, in-process; the spike's
  findings are in section 7 and the note.

### Phase 2 — files and processes

#### C-1 — File tools: offset/limit, append, overwrite, recursive, pattern  `P2 · M · ~3 h`

- `file_read(path, max_bytes, encoding, offset_lines = 0, limit_lines = 0)`: 1-based `offset`
  like upstream (`0`/omitted = from the top), `limit_lines = 0` = to the end; the window is cut
  after decoding on `\n` with `\r` stripped, so a CRLF file counts the same lines as an LF one;
  the result becomes `{path, encoding, totalLines, offset, returned, truncated, content}` when
  either windowing parameter is given and stays the plain text otherwise (no break for today's
  callers). `max_bytes` still bounds the *file*, not the window.
- `file_write(path, content, encoding, confirm, append = false, create_parents = true)`:
  `append` opens for append (no temp-file rename — an append must not rewrite the file);
  `create_parents:false` refuses a missing directory by name. Both need `confirm` as today.
- `file_manage`: R1 absolute paths, R2 `overwrite`/`recursive`, R3 `list` with `pattern`,
  `recursive`, `include_hidden` and the `FileEntry` shape. `copy` of a directory copies the
  tree (today's `File.Copy` throws on a directory); `move` across volumes falls back to
  copy + delete (`Directory.Move` refuses it). The service methods gain the flags as trailing
  parameters with defaults that reproduce **today's** behaviour at the service level, so the
  tool layer owns the safer defaults and nothing else that calls the service changes.
- **RED seed.** `FileSystemServiceTests` (`Integration` on a temp directory): the line window
  on LF and CRLF files, offset past the end returns zero lines and `truncated:false`, append
  twice yields both contents, `create_parents` both ways, copy/move refuse an existing target
  without `overwrite` and replace it with, delete refuses a non-empty directory without
  `recursive` and an empty one without, directory copy and cross-volume move (skipped when only
  one volume is present, and says so), list with a glob, recursion, hidden entries excluded and
  included, the `FileEntry` fields. `FileToolsTests` (`Unit`): every new flag forwarded, a
  relative path refused before the service is called, the two result shapes of `file_read`,
  `write` still needs `confirm`. `HttpTransportTests` schema for the three tools. SKILL.md's
  file lines updated in the same PR.
- **Done when.** `file_read(path, offset_lines:100, limit_lines:20)` returns lines 100–119 with
  `totalLines`, and `file_manage("delete", dir)` on a non-empty directory is refused naming
  `recursive`.
- **Shipped as** ([note](C-1-file-flags.md)): as planned, with the service flags required
  rather than defaulted (`FileTools` is the only caller), the windowed `file_read` result
  carrying no `encoding` key, and R3's entry fields serialised PascalCase
  (`{Path, Name, IsDirectory, Size, Modified, Hidden}`) like the other DTO-returning tools.

#### C-3 — Process list CPU %, sort, limit; graceful kill  `P2 · M · ~3 h`

- `ProcessDto +CpuPercent`; `ListAsync(nameFilter, sortBy, limit)` with the two-sample
  measurement (R4) behind an injectable `TimeProvider`/sampler seam so the unit test does not
  sleep; a process that exits between samples reads `0`. `process(list, sort_by = "memory",
  limit = 0)`; `orphans`/`includeLineage`/`groupByRoot` keep their shapes (CPU is the plain
  list's column; adding it to the lineage rows is a follow-up).
- `IProcessService.KillAsync(pid, KillOptions(Graceful, GraceMs))` beside today's overload;
  `process(kill, graceful = false, grace_ms = 3000)` per R5; the kill branch's text results
  become JSON (`{killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}`; the
  tree kill keeps its count) — a *Changed* line.
- **RED seed.** `CpuSampleTests` (`Unit`): the normalisation table (one core saturated on 8 =
  12.5; zero elapsed = 0; a negative delta = 0), sorting for the four keys and both directions,
  `limit` after sort and filter. `ProcessServiceTests` (`Integration`): `ListAsync` on the live
  box has `CpuPercent` in 0–100 for every row and the sum ≤ 100 × cores; a spun-up
  `powershell -c "while($true){}"` child ranks first under `sort_by:cpu` and is killed
  afterwards. Graceful kill (`UIAutomation`, Notepad fixture): `graceful:true` on an unmodified
  Notepad exits it gracefully with `forced:false`; a console child with no window reports
  `forced:true` after `grace_ms` (`Integration`). `ProcessToolsTests`: the defaults, the
  JSON kill result, `graceful` + `tree` refused, `sort_by` validated.
- **Done when.** `process(list, sort_by:"cpu", limit:5)` returns the five busiest processes
  with a CPU column, and `process(kill, pid, graceful:true)` on Notepad closes it without
  `TerminateProcess`.
- **Shipped as** ([note](C-3-process-cpu-graceful-kill.md)): as planned, except that the old
  `ListAsync(nameFilter)` overload does not sample (the name kill goes through it), the CPU
  readings are timestamped per process (one shared window over-counted by half), and the
  graceful path posts `WM_CLOSE` to every visible window through the seam instead of
  `CloseMainWindow()`.

### Phase 3 — shell and web

#### C-6 — `powershell`: per-call timeout; environment repair for `Path`  `P2 · S–M · ~3 h`

- `PSResult +TimedOut (bool, default false)`; `IPowerShellService.RunAsync(command, TimeSpan?
  timeout, ct)` beside the old overload (which is `timeout:null`); R9's linked timer after the
  gate, partial output harvested, the backstop folded into the same result path. `powershell(…,
  timeout_seconds = 0)` validated `0…900`, refused with `background:true`. The tool description
  and SKILL.md's "long jobs" paragraph gain the parameter.
- `Hosting/PathMerge.cs` (pure) and the new rule in `EnvironmentRepair.Apply` (the injected
  pure core gains nothing but the `Path` check; the `changed` list names it).
- **RED seed.** `PowerShellServiceTests` (`Integration`, real `powershell.exe`): a 2-second
  timeout on `Start-Sleep 30` returns within ~3 s with `TimedOut:true`, `ExitCode:-1`, the
  stdout written before the sleep present, and no `powershell.exe` child left (pid checked);
  `timeout:null` still runs to completion; a caller's cancellation still throws. `Unit`: the
  timer does not start until the gate is held (two queued calls, the second's timeout measured
  from its start). `PathMergeTests`: host entries first and untouched, machine then user
  appended, duplicates by case and trailing separator dropped, empty entries dropped, `System32`
  present ⇒ no change. `EnvironmentRepairTests`: a `Path` lacking `System32` is repaired and
  named; one with it is left alone; a `powershell` call after repair resolves `git`/`where.exe`
  (`Integration`, on a host-stripped block simulated through the pure seam). `ShellToolsTests`:
  the range, the `background` refusal, the parameter's default.
- **Done when.** `powershell("Start-Sleep 60", timeout_seconds:5)` comes back in five seconds
  with `timedOut:true`, and a server launched with `Path=C:\nothing` still finds `git` from a
  `powershell` call.

#### C-5 — `scrape`: DOM source, query focus, sampling summary  `P2 · M · ~4 h`

- `IWebService.ScrapeAsync(url, maxChars)` → `ScrapeResult(Source, Url, Title?, Chars,
  Truncated, Content)`; the DOM source lives in the tool layer over `IUIAutomationService.
  SnapshotAsync(UseDom:true, scope: foreground | window)`, with a pure `DomPage.Render(
  SnapshotPage)` producing the text plus the edge hints. A pure `ScrapeSummary.Request(content,
  query, maxTokens)` builds the `CreateMessageRequestParams` (system prompt: strip navigation,
  ads, cookie banners, repeated boilerplate; answer `query` if given, otherwise summarise
  faithfully; keep numbers and names verbatim) so the prompt is unit-tested without a server.
  `WebTools.Scrape` takes `McpServer` as an injected parameter and checks
  `ClientCapabilities?.Sampling` before calling `SampleAsync`; the tool is `OpenWorld = true`,
  `ReadOnly = true`. `Title` from the HTML `<title>` (http) or the page (dom).
- **RED seed.** `WebServiceTests` (`Integration`, local Kestrel page as today): the JSON shape,
  `max_chars` truncation with `truncated:true`, the title, private IPs still refused.
  `DomPageTests` (`Unit`): hints for top / middle / bottom / unknown scroll, empty text.
  `ScrapeSummaryTests`: the prompt contains the query when given and the faithful-summary
  instruction when not; `MaxTokens` bounded. `WebToolsTests` (`Unit`, fake `McpServer`
  capabilities): `summarize:true` without the capability returns raw with `summarized:false` and
  the note; `source:"dom"` without a browser window is refused naming the source; `url` required
  for http and optional for dom. `HttpTransportTests`: the in-process `McpClient` registers a
  `SamplingHandler` returning a canned summary, and `scrape(summarize:true)` over HTTP returns
  it with `summarized:true` and the handler's model name — the sampling path proven end to end.
  `UIAutomation` (Edge fixture): `source:"dom"` returns the fixture page's heading.
- **Done when.** `scrape(source:"dom")` on the open Edge tab returns its text with a scroll
  hint, and `scrape(url, summarize:true)` from Claude Code returns the raw markdown with
  `summarized:false` and the reason.

## 5. Effort and sequencing summary

| Phase | Items | Days | Version | Unlocks |
|---|---|---|---|---|
| 1 | C-7, C-2, C-4 | ½ | +0.1 (new tool `registry_delete`, 69) | the annotation gate for every later change |
| 2 | C-1, C-3 | 1 | +0.1 (contract changes R2/R3/R5) | — |
| 3 | C-6, C-5 | 1 | +0.1 (contract change R8; `PSResult` field) | SKILL.md drops its last PowerShell workaround lines |
| | **Total** | **~2½ days** | | |

Estimates are **wall clock for this workflow** (the B roadmap's baseline): two `test-agent`
passes per item, the `Integration` runs that spawn real `powershell.exe` (C-3, C-6: minutes each
under Defender), one `docs-agent` pass per phase, and the two spikes (C-4's toast route, C-5's
sampling call). Versions are relative to whatever the B release cut lands on (currently
`0.7.3` in `Directory.Build.props`, `0.10.0` per the B roadmap's C12); each phase carries at
least one contract change or a new tool, so each is a minor bump. Phases 2 and 3 in parallel
branches save ~½ day.

## 6. Risks and how the plan absorbs them

- **Sampling is not supported by Claude Code.** R8's fallback (raw + `summarized:false` + note)
  is the path that runs on this box; the HTTP test with a `SamplingHandler` is the only place the
  summary path is exercised. It is still worth shipping: any client with sampling gets upstream's
  behaviour, and the fallback is what upstream does too.
- **Toasts from an unpackaged single-file exe.** The WinRT call may throw or silently drop on a
  build; the C-4 spike settles it before the RED pass, and the PowerShell script stays as the
  documented fallback behind the same interface.
- **`file_manage` safer defaults break a caller relying on silent overwrite** (R2). Both
  refusals name the flag to pass; CHANGELOG *Changed* carries the migration; the skill playbook's
  file line says `overwrite:true` is needed to replace.
- **Console processes have nothing to close gracefully** (R5). The result says `forced:true`
  after the grace period rather than pretending; `grace_ms` bounds the wait.
- **CPU sampling adds ~250 ms to every `process(list)`.** Accepted; `orphans`/lineage/group
  calls do not pay it. If a caller needs the old latency, `limit` and the name filter do not
  help — a `include_cpu:false` switch is the follow-up if it ever matters.
- **Appending the registry `Path` could shadow nothing but could add a lot.** Host entries stay
  first and untouched, so resolution of anything the host provided is unchanged; the append only
  makes absent tools resolvable. A host that *intended* an empty `Path` is not a case worth
  supporting.
- **The annotation table is a judgement call in ~10 rows** (`hover`, `focus`, `watch`, `job`,
  `clipboard`, `storage_health`, `launch`, `start_process`, `wait_for`, `shortcut`). The note
  states the reason per row and the literal-list test makes any later change a visible diff, not
  a drift.
- **`registry_delete` under HKLM needs elevation** the server usually lacks; the OS error is
  passed through. The denylist is deliberately short: it guards against the catastrophic roots,
  not every unwise delete — `confirm` and the client's `destructiveHint` prompt do the rest.

## 7. Decisions taken before phase 1 (2026-09-06)

Five questions were put to the owner and decided as recommended ("go with recommendations");
they are settled, not open. Everything else in section 2 is a recommendation the individual
design notes can overturn with a stated reason.

1. **R2 — `file_manage` refuses to overwrite and to delete a non-empty directory unless told**
   (recommended), or keeps today's silent overwrite and recursive delete with the flags as
   no-ops for parity's sake.
2. **R3 — `file_manage(list)` returns entry objects** (`path, name, isDirectory, size, modified,
   hidden`; recommended), or keeps the `string[]` of paths and adds the flags only.
3. **R7 — toasts move in-process (WinRT) and the server registers its own default AUMID under
   HKCU once** (recommended), or the PowerShell script stays and `app_id` is the only change.
4. **R8 — `scrape(summarize)` defaults to `false`** (recommended: opt-in to a client-billed model
   call; the fallback is what runs from Claude Code anyway), or to `true` as upstream.
5. **R6 — `registry_delete` is a new tool (68 → 69) with the short root denylist** (recommended),
   or delete rides on `registry_set` as an `action` to hold the count at 68.
