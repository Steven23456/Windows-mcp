# `windows` Skill — Design

## Goal

Ship a guidance/playbook **skill** inside the `windows-mcp` plugin — named
`windows` (slash `/windows`, loads as `windows-mcp:windows`) — that gives Claude
the judgment to wield the server's 60 atomic MCP tools well: which tool to reach
for, how to compose them into common multi-tool workflows, and which operations
are destructive and must be confirmed. It mirrors the *judgment half* of the
existing `dropbox` skill inside `dropbox-mcp`, minus bulk scripts (windows-mcp
tools are atomic and Claude orchestrates them directly).

## Motivation

`windows-mcp` currently ships only the MCP server. Like `dropbox-mcp`, which
ships both a server and a `dropbox` skill, the plugin should carry a companion
skill so that:

- A user typing `/windows` (or a matching natural-language request) loads a
  playbook that steers Claude toward the MCP tools instead of ad-hoc PowerShell.
- Hard-won operating rules already recorded in memory
  (`feedback_prefer_windows_mcp`, `feedback_process_cleanup_pass`, the
  `startup_report` work, the CLAUDE.md gotchas) become an in-repo, versioned
  artifact that loads automatically, rather than living only in private memory.

The skill is **judgment, not new capability** — it adds no tools and no scripts.

## Non-Goals

- No helper scripts. The 60 MCP tools are atomic and called directly by Claude;
  there is no SDK-bypass bulk operation (unlike dropbox sync) that needs a script.
- No new MCP tools, no server code changes.
- Not an exhaustive per-tool reference manual — the tool descriptions already
  live in the server. The skill maps tools to domains and to *workflows*.

## Placement & Load Model

```
windows-mcp/
  skills/
    windows/
      SKILL.md     # the playbook (frontmatter + body)
      README.md    # short human-facing overview
```

- The plugin auto-discovers `skills/<name>/SKILL.md`; the skill's frontmatter
  `name: windows` sets the load id `windows-mcp:windows` and slash `/windows`.
- No `marketplace.json` or `settings.json` edits — the plugin is already
  enabled. Delivery is: commit to the repo → `/plugin marketplace update
  local-marketplace` → `/reload-plugins` (fresh re-clone lands the skill in the
  cache).

## SKILL.md Structure

The `SKILL.md` contains, in order:

### 1. Frontmatter
- `name: windows`
- `description:` a trigger-rich one-paragraph description in the same style as
  the `dropbox` skill — enumerating natural-language triggers ("automate a
  Windows app", "audit my startup items", "why is my PC booting slowly", "clean
  up orphaned processes", "check Defender/firewall status", "read the registry",
  "find and hash a file", "take a screenshot / OCR the screen", etc.) and stating
  what the skill does NOT cover (it is guidance over the `windows-mcp` server; it
  is not a cross-platform tool and does not replace elevation for admin-only ops).

### 2. Tool-selection: windows-mcp tools vs. raw PowerShell
Encodes `feedback_prefer_windows_mcp`. A short heuristic + matrix:
- **Default to the MCP tool** for system inspection, UI automation, file /
  registry / service / process / network operations — it is faster than a
  PowerShell cold-start, returns structured JSON, and runs in one place.
- **Fall back to the `powershell` tool** only for genuine one-off scripting the
  60 tools don't express. Note the gotcha: the `powershell` tool's stdin can
  arrive empty — pass a script via a temp `.ps1` rather than piping.
- The MCP runs **unelevated**; admin-only operations (some `registry_set`,
  `service`, `scheduled_task`) can fail with access-denied — recognize that
  signature instead of retrying blindly.

### 3. The 60 tools, grouped by domain
A compact map (domain → tool list, one line of purpose per domain), so Claude
knows what exists without reading server source:
- **UI automation / input**: `click`, `drag`, `hover`, `key`, `type`, `scroll`,
  `focus`, `get_state`, `get_element`, `get_text`, `get_table`, `find_element`,
  `assert_element`, `interact_element`, `wait_for`, `switch_to_window`,
  `window`, `multi_monitor`, `screenshot`, `ocr`, `clipboard`, `file_dialog`,
  `notification`, `launch`
- **Processes / shell**: `process`, `process_inspect`, `start_process`,
  `powershell`
- **System**: `system_info`, `wmi_query`, `env`, `reliability`, `event_log`,
  `driver_list`, `power_action`
- **Files**: `file_read`, `file_write`, `file_manage`, `file_search`,
  `file_info`, `file_hash`, `file_streams`
- **Disk / storage**: `disk_inspect`, `storage_health`
- **Services / tasks**: `service`, `scheduled_task`
- **Registry**: `registry_get`, `registry_set`
- **Network / web**: `network`, `firewall`, `http_request`, `scrape`
- **Security**: `security_audit`, `defender_status`, `cert_store`,
  `verify_signature`, `startup_report`
- **Misc**: `shortcut`, `archive`, `audio`

The domain list must total the tools the server actually exposes; the plan step
verifies the count against the live server (`ToolSearch`) rather than trusting
this document.

### 4. Workflow playbooks
Five composed, multi-tool recipes — the core value. Each names the tool
sequence and the decision points:
1. **Startup / boot-slowness triage** — `startup_report` → inspect suspicious
   entries with `verify_signature` + `cert_store` → corroborate with
   `reliability` + `event_log`.
2. **Process cleanup (whitelist, never kill-all)** — `process` /
   `process_inspect` → identify orphans against a whitelist; **never** terminate
   `csrss`/`wininit`/`winlogon`/`explorer` or user apps (encodes
   `feedback_process_cleanup_pass` as a hard rail).
3. **Security audit sweep** — `security_audit` + `defender_status` + `firewall`
   + `startup_report` composed into one health summary.
4. **UI-automation loop** — the correct `get_state` → act (`click`/`type`) →
   `assert_element` / `wait_for` cycle, with the interactive-desktop /
   foreground-app requirement called out.
5. **File forensics** — `file_search` → `file_info` / `file_hash` /
   `file_streams` (alternate data streams) / `verify_signature`.

### 5. Safety rails & gotchas
- **Confirm before destructive tools**: `registry_set`, `service`,
  `scheduled_task`, `power_action`, `file_write`, `file_manage`, `firewall`.
  Modifying system/security settings is gated — prefer the read-only counterpart
  first (`registry_get` before `registry_set`, `process_inspect` before killing).
- **`storage_health` can wedge on external / USB drives** — scope to internal
  disks or warn before running against removable media.
- **Runs unelevated** — see the access-denied note above.
- **UIAutomation tools need the target app foregrounded** on an interactive
  desktop; they fail headless/background.
- **`powershell` stdin-empty → temp `.ps1`.**

## README.md

A short human-facing overview: what the skill is (a playbook over the
`windows-mcp` server), how it loads (`windows-mcp:windows`, `/windows`), and a
one-line pointer to the five workflows. No duplication of the SKILL body.

## Release

- Bump `windows-mcp` **minor**: `0.4.1 → 0.5.0` (new user-facing capability —
  the skill) in `.claude-plugin/plugin.json` and any other version-bearing file
  in the repo (`package.json`, server constructor version string) that the plan
  step confirms exists and must stay in lock-step.
- Update repo `README.md` (note the plugin now ships a skill) and `CHANGELOG.md`
  (`## [0.5.0]` entry, Keep-a-Changelog).
- Atomic commit; push to `main`.
- Deliver: `/plugin marketplace update local-marketplace` + `/reload-plugins`.

## Success Criteria

1. `skills/windows/SKILL.md` and `README.md` exist in the repo; frontmatter
   `name: windows`.
2. SKILL.md contains all five sections above with the five workflow playbooks
   and the safety rails (no placeholders).
3. The tool-domain map's tool set matches the live server's tool list (verified,
   not assumed).
4. Version bumped to 0.5.0 across all version-bearing files; README + CHANGELOG
   updated; committed atomically and pushed.
5. After marketplace update + reload, the skill loads as `windows-mcp:windows`
   and `/windows` triggers it (final verification step).

## Testing

This is a documentation/skill artifact — "tests" are verification, not unit
tests:
- **Frontmatter validity**: the skill parses and appears in the skills list
  after reload (the `skill-validator` skill or a reload check).
- **Tool-set accuracy**: cross-check the domain map against `ToolSearch`
  `mcp__plugin_windows-mcp_Windows-mcp__*` — every listed tool exists, none is
  omitted.
- **No broken claims**: every gotcha/rail traces to a real behavior (memory
  entries or CLAUDE.md); verified with `honest-claude`.
- **Load verification**: after release, confirm `windows-mcp:windows` is present
  in the reloaded skills list.
