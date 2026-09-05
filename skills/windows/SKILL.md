---
name: windows
description: "Playbook for driving Windows via the windows-mcp server's 65 tools — UI automation, system inspection, files, registry, services, processes, disk, network, security, and startup analysis. Use when the user says 'automate this Windows app', 'click/type into that window', 'take a screenshot' or 'OCR the screen', 'audit my startup items', 'why is my PC booting slowly', 'clean up orphaned processes', 'what's running', 'check Defender/firewall status', 'run a security audit', 'read/set a registry value', 'inspect a service or scheduled task', 'find/hash/inspect a file', 'check disk or storage health', 'baseline/check file integrity', 'what changed on my C: drive', 'watch a folder for changes', or any Windows desktop-automation or system-inspection task. Steers toward the windows-mcp tools over ad-hoc PowerShell, gives composed multi-tool workflows, and flags destructive tools. Does NOT add tools; it is guidance over the windows-mcp server. Not cross-platform; the server runs unelevated so admin-only operations may need elevation the skill cannot grant."
---

# Windows

A judgment layer over the `windows-mcp` server's 65 atomic tools for Windows desktop automation and system inspection — UI driving, screenshots/OCR, files, registry, services, processes, disk, network, and security/startup analysis. This skill adds no tools of its own: every action below is one of the server's existing MCP tools, composed into the right order with the right safety checks. Its job is to steer tool selection (MCP vs. raw PowerShell), sequence multi-step workflows correctly, and flag which tools are destructive enough to need confirmation first.

**Skill root**: this skill ships inside the `windows-mcp` plugin (repo
`Steven23456/Windows-mcp`, `skills/windows/`). Slash trigger: `/windows`.

## When to use this skill

Trigger this skill when the user wants any of:

- **Drive a GUI application** — click, type, read UI state, wait for elements to appear
- **Capture or read the screen** — screenshot, OCR a region, extract text/tables from a UI element
- **Diagnose slow boot / startup bloat** — "why is my PC slow to start", "audit autoruns"
- **Clean up processes** — "what's eating memory", "kill orphaned processes" (whitelist-only, never kill-all)
- **Check security posture** — Defender status, firewall rules, certificate trust, a full security audit
- **Inspect or modify system state** — registry values, services, scheduled tasks, environment variables
- **File forensics** — locate, hash, inspect metadata/streams, or verify the signature of a file
- **Disk/network diagnostics** — disk usage vs. drive health, adapters/ports/DNS/ping

Do NOT use this skill for:
- Cross-platform automation (this server and its tools are Windows-only)
- Anything requiring elevation the user hasn't granted — the server itself runs unelevated (see Section 5)

## Tool selection: windows-mcp tools vs. raw PowerShell

**Default to the MCP tool.** It is faster than a PowerShell cold-start, returns structured JSON instead of text to parse, and runs unelevated in one consistent place. Reach for raw PowerShell only when none of the 65 tools express what's needed.

**Fall back to the `powershell` tool** only for one-off scripting the 65 tools don't cover. Multi-line scripts are fine — the tool passes the script as one unit. For anything that may run longer than a few minutes (installers, `DISM`, bulk hashes) pass `background: true` and poll with `job`.

The MCP server **runs unelevated**. Admin-only operations — `registry_set` under `HKLM`, `service` start/stop, some `scheduled_task` actions — can return access-denied. Recognize that signature and surface it to the user instead of retrying blindly; the skill cannot grant elevation it doesn't have.

| The task | Reach for |
|---|---|
| Inspect OS/memory/disk/GPU/battery | `system_info`, `wmi_query` |
| List/inspect/kill a process | `process`, `process_inspect` |
| Read/search/hash a file | `file_read`, `file_search`, `file_hash`, `file_info` |
| Read a registry value | `registry_get` |
| Drive a GUI app | `snapshot` → `interact_element`/`click`/`type` → `assert_element` |
| One-off scripting no tool covers | `powershell` (`background: true` for long runs, then `job`) |

If a `windows-mcp` tool isn't loaded, fetch its schema via `ToolSearch select:mcp__plugin_windows-mcp_Windows-mcp__<tool>`.

## The 65 tools, grouped by domain

**UI automation / input (26)** — drive and read the desktop and its GUI applications: `snapshot`, `click`, `drag`, `hover`, `key`, `shortcut`, `type`, `scroll`, `focus`, `get_state`, `get_element`, `get_text`, `get_table`, `find_element`, `assert_element`, `interact_element`, `wait_for`, `switch_to_window`, `window`, `multi_monitor`, `screenshot`, `ocr`, `clipboard`, `file_dialog`, `notification`, `launch`

**Processes / shell (5)** — enumerate, inspect, start, or kill processes; run arbitrary scripts: `process`, `process_inspect`, `start_process`, `powershell` (with `background: true` for long-running jobs), `job`

**System (7)** — machine-level inspection and power control: `system_info`, `wmi_query`, `env`, `reliability`, `event_log`, `driver_list`, `power_action`

**Files (7)** — read, write, search, and forensically inspect files: `file_read`, `file_write`, `file_manage`, `file_search`, `file_info`, `file_hash`, `file_streams`

**Disk / storage (2)** — usage vs. drive health: `disk_inspect`, `storage_health`

**Services / tasks (2)** — Windows services and scheduled tasks: `service`, `scheduled_task`

**Registry (2)** — read and write registry values: `registry_get`, `registry_set`

**Network / web (4)** — connectivity and HTTP: `network`, `firewall`, `http_request`, `scrape`

**Security (5)** — trust and posture checks: `security_audit`, `defender_status`, `cert_store`, `verify_signature`, `startup_report`

**Monitoring / integrity (3)** — file-integrity tripwire, NTFS USN change journal, and live directory watching: `integrity`, `fs_changes`, `watch`

**Misc (2)** — utility operations: `archive`, `audio`

(26+5+7+7+2+2+2+4+5+3+2 = 65.) If a `windows-mcp` tool isn't loaded, fetch its schema via `ToolSearch select:mcp__plugin_windows-mcp_Windows-mcp__<tool>`.

## Workflow playbooks

### 1. Startup / boot-slowness triage

```
startup_report
  → for each suspicious entry:
      verify_signature + cert_store    (is it signed / trusted?)
      reliability + event_log          (recent crashes/hangs, boot/service errors)
```

`startup_report` gives a HiJackThis-style read-only inventory of autoruns, startup folders, services, and shell extensions, each already carrying a catalog-aware trust flag — start there rather than re-deriving it. For anything unsigned, untrusted, or unfamiliar, cross-check `verify_signature`/`cert_store` directly, then corroborate with `reliability` (crash minidumps and failure records) and `event_log` (boot/service errors). Decision: unsigned or untrusted autoruns that line up with recent reliability drops are the prime suspects.

### 2. Process cleanup (whitelist — never kill-all)

```
process (action: orphans)                     — recycle-aware orphans + signals, one call
  → (or) process (action: list, groupByRoot: true)   — see which root spawned a pile
  → process (action: kill, pid, confirm: true[, tree: true][, startTime])
```

Prefer `action: orphans` (or `list` with `includeLineage: true`) over the old `list → process_inspect` dance — it returns parent lineage, command line, `ageMinutes`, `runtimeKind`, `orphaned`, and `isSystemAdjacent` for every process in a single call, so you can rank candidates without inspecting each one. `groupByRoot: true` collapses processes under their root ancestor — the fast way to see, e.g., five stale sessions each holding a server fleet. **Read the signals, don't trust the label:** `orphaned` is COMMON and by-design on Windows (`explorer.exe` and anything launched from a since-closed shell are orphaned) — it is NOT a leak signal; `isSystemAdjacent: true` flags the boot/session processes to leave alone. **Hard rail:** never terminate `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `explorer`, or any user-facing application. To reap a stale session and its whole fleet in one guarded call, use `kill` with `tree: true` (kills the pid + its descendants, each re-validated before killing) and pass `startTime` to guard against PID reuse. Always confirm the kill list with the user; `confirm: true` is required.

### 3. Security audit sweep

```
security_audit + defender_status + firewall (action: list) + startup_report
```

Run all four and compose the results into one health summary with a per-area verdict (firewall, Defender/AV, UAC/BitLocker where available, and autorun/startup hygiene). Note that `security_audit`'s admin-gated fields (BitLocker, some firewall profiles) return null when run unelevated — report those as "unknown," not "failed."

### 4. UI-automation loop

```
snapshot                                    (windows, cursor, every el_N with a centre + action)
  → interact_element / click / type / key   (act, using the el_N ids or their centres)
  → assert_element / wait_for               (confirm the state changed)
  → snapshot                                (re-read: ids are stale after acting)
  → repeat
```

**Start the loop with `snapshot`.** One call returns what used to take `window(action:"list")` + `get_state` + a `find_element` per control: the z-ordered window list, the active window, the cursor, and every interactive element as a row `el_N (x,y) type "name" [action: …]` — the centre coordinates are virtual-desktop pixels `click`/`drag`/`scroll` take unchanged, and the action tag (`click`/`fill`/`toggle`/`select`/`slide`/`scroll`) says which verb the control expects. Scrollable regions follow with their `[v: N%] [h: N%]` and a `[reached top]`/`[reached bottom]` tag. The default output is compact text; ask for `format:"json"` only when something must be parsed (`include_tree:true` adds the element tree there).

Pass those `el_N` ids straight to `interact_element`, `get_element` and `get_text`, or click the printed centre. **The ids are only valid until the next `snapshot`** — a new one evicts the previous one's ids (an id from `find_element` in between survives), so re-snapshot after acting rather than reusing a row from two turns ago. Read, act, confirm, re-read; don't chain blind actions.

Narrow the view when the desktop is busy: `scope:"foreground"`, or `scope:"window"` with `window:<title>` (exact then substring). If the footer says the walk was truncated, narrow the scope or raise `max_elements` (the default budget is the server's `--max-tree-elements`, 500). **The target app must be on an interactive desktop, and a minimised window is listed but not walked.** Prefer element ids over hardcoded coordinates, which break when the window moves or resizes. `get_state` remains for the raw three-level JSON tree of the foreground window; `snapshot` is the cheaper read for driving the UI.

`window(action:"list")` is still the right call when all you need is the window inventory — checking what is open before launching a second copy of an app that is already running, without paying for an element walk. It returns every user-visible top-level window in z-order (`ZOrder` 0 = frontmost) with `Title`, `Pid`/`ProcessName`, `State` (`Normal|Minimized|Maximized`), `Bounds` in virtual-desktop pixels, `IsActive`, `IsBrowser`, and `MonitorIndex` into `multi_monitor`'s list (`-1` = on no monitor, e.g. minimized). `window(action:"active")` returns just the foreground window, or `{"found":false}`. **Target by `Title`** — that string is what `switch_to_window`/`focus` and `window(action:"minimize"|"maximize"|"restore"|"close")` match (exact), and what `find_element(scope:"window", window:…)` matches exact-then-substring; the reported `Hwnd` is informational, no tool accepts it. Minimized windows are listed by default (`include_minimized:false` drops them); untitled ones are not (`include_hidden:true` adds them). Check `IsActive`/`State` before a `get_state` or a coordinate `click` — a backgrounded or minimized target is the usual cause of an empty tree.

Prefer `interact_element` over a coordinate `click` for a named control: it acts through the UIA pattern (Invoke, SelectionItem, Toggle, Value) and falls back to a physical click at the element's centre, reports which one fired in `Method`, and errors — instead of silently doing nothing — when a pattern is unsupported. Keyboard chords go through `shortcut` (`ctrl+c`, `ctrl+shift+s`, `win+r`, `alt+f4`, a bare `win`); a single key through `key` (`a`, `enter`, `f5`). Coordinates for `click`/`drag`/`hover`/`scroll` are physical pixels on the virtual desktop with the origin at the primary monitor's top-left, so a monitor left of or above it has negative coordinates — take them from `multi_monitor` or an element's `Bounds`.

`screenshot` hands back the picture **inline** — look at it, never ask for a file path. It captures the **primary display** by default; pass `display:"1"` (or `"all"`, `"0,2"` — indices from `multi_monitor`) for another monitor, or `region:"x,y,w,h"` in those same virtual-desktop pixels. Read the metadata text block that arrives with the image before acting on it: it carries the `region` actually captured, every monitor in `displays`, and the pointer in `cursor {x, y, monitorIndex}` — which is also painted on the image (`cursorDrawn` says whether that mark is the real cursor icon or the fallback ring; `include_cursor:false` turns it off). Captures are downscaled to fit 1920×1080, so on a 4K monitor image pixels are **not** desktop pixels: when the metadata carries a `note`, follow it literally (multiply by `coordinateScale`, add the region origin) before passing any coordinate to `click`/`drag`/`scroll`. `ocr` takes the same `region`/`display` and always reads at full resolution.

`find_element` searches the **foreground window** by default, resolved at call time. For anything multi-step, pass `scope:"window"` with `window:<title>` (matched exact-then-substring, so `"Notepad"` finds `"Untitled - Notepad"`) — then a notification or another app stealing focus cannot change what is searched, and a title that matches nothing lists the open windows so you can retry. `scope:"desktop"` searches every top-level window. Results are on-screen elements only and capped at 20 **after** filtering; pass `include_offscreen:true` to see collapsed panes, virtualised rows and minimised windows when diagnosing why something is missing. Use `kind:"interactive"` to find something to click **or type into** (buttons, menu items, list rows, tabs, combo boxes, sliders, and edit/document areas); `kind:"text"` is for reading. `wait_for` takes the same filters and retries a poll that fails, so it is safe on a busy desktop.

`assert_element` checks exists / enabled / checked / visible / focused, and `value` with `expected` (an exact match against the element's ValuePattern value, else its Name). A `FAIL:` names what was observed — the focus owner, the actual value, the toggle state, or `element no longer available` when the window closed since the id was issued — so choose the next action from that instead of re-reading the tree.

### 5. File forensics

```
file_search (locate)
  → file_info (size/timestamps/attributes)
  → file_hash (SHA-256)
  → file_streams (alternate data streams)
  → verify_signature (Authenticode/catalog trust)
```

Locate the file(s) first, then layer on metadata, a hash suitable for IOC/VirusTotal lookups, a check for hidden alternate-data-stream payloads, and a signature/trust verdict. Useful both for vetting a suspicious binary found elsewhere (a process path, an autorun entry) and for general integrity checks.

## Safety rails & gotchas

- **Confirm before destructive tools:** `registry_set`, `service`, `scheduled_task`, `power_action`, `file_write`, `file_manage`, `firewall`. Each of these is gated behind a `confirm: true` parameter on the write/destructive path — treat that gate as a place to pause and get user sign-off, not just a required field to fill in. Run the read-only counterpart first: `registry_get` before `registry_set`; `process_inspect` before killing; `service` (status/list) before start/stop.
- **`storage_health` can wedge on external / USB drives.** Its default mode is fast and never wakes sleeping drives, but `include_usage: true` wakes sleeping/USB drives to collect SMART data — scope to internal disks by default, or warn the user before running it with `include_usage: true` against removable media.
- **Runs unelevated.** Admin-only operations (`registry_set` under `HKLM`, `service` start/stop, some `scheduled_task` actions) fail with access-denied. Surface that signature to the user; don't loop retrying.
- **UIAutomation tools need the target app foregrounded** on an interactive desktop — they fail headless or against a backgrounded window.
- **Multi-line PowerShell is passed whole.** The `powershell` tool sends the script as a single `-EncodedCommand` unit (temp-`.ps1` fallback only for oversized scripts), so heredocs and multi-line strings work as-is — no temp-file workaround is needed.
- **Long jobs are fine; disk-saturation storms are not.** A single long `powershell`/heavy tool call (>~120s — e.g. `DISM`, a big hash, a bulk delete) is safe: the Claude Code harness "moves it to the background" (benign) and the result is delivered on completion, and the server allows a 15-min PowerShell backstop (long foreground calls also emit MCP progress heartbeats so spec-compliant clients keep the request alive). What *does* break it is running several heavy ops (`DISM` + a `service` stop + bulk deletes) **while the disk is already saturated** by a concurrent large hash/copy — the MCP call can fail transiently with `"An error occurred invoking 'powershell'"`. That is **I/O starvation, not a timeout or a 120s limit** (verified 2026-07-17: lone 150s and two concurrent ~135s calls all succeeded; the only failures came during a 42 GB SHA-256 storm, with no server crash). Mitigation: for the heaviest/longest ops (installers, `DISM`, big hashes), prefer `powershell` with `background: true` — it returns a job id immediately and runs outside the serialization gate; poll with the `job` tool (`status`/`output`/`cancel`/`list`). Claude Code's own `run_in_background` remains a fallback, and don't stack heavy windows-mcp ops during a saturation storm. Raising `MCP_TOOL_TIMEOUT` is **not** the fix — its default is ~28 h, not 120s.
