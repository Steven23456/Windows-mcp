# `windows` Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a guidance/playbook skill named `windows` inside the `windows-mcp` plugin (`skills/windows/`, loads as `windows-mcp:windows`, slash `/windows`) that teaches Claude to wield the server's 60 atomic MCP tools well.

**Architecture:** Two Markdown files — `skills/windows/SKILL.md` (frontmatter + playbook body) and `skills/windows/README.md` (short human overview). No scripts, no server code, no new tools. Then a plugin release: bump `plugin.json` to 0.5.0, update repo README + CHANGELOG, atomic commit, push, deliver via marketplace update + reload.

**Tech Stack:** Markdown + YAML frontmatter. Reference style: the `dropbox` skill at `C:\Users\danie\Github\dropbox-mcp\skills\dropbox\SKILL.md`.

## Global Constraints

- Skill `name:` frontmatter is exactly `windows` (→ load id `windows-mcp:windows`, slash `/windows`). Copy verbatim.
- The skill adds **no tools and no scripts** — it is judgment/guidance only.
- The tool-domain map must list **exactly the 60 tools the live server exposes** — verified against `ToolSearch`, never assumed.
- **Do NOT bump `src/WindowsMcp/Program.cs` `ServerInfo.Version` and do NOT rebuild the exe.** The binary is unchanged; its version (0.4.1) and the plugin version (→0.5.0) version different things. Only `.claude-plugin/plugin.json` is bumped.
- Design/skill docs carry **no version numbers or dates in the body** (that lives in the CHANGELOG). The SKILL.md may reference tool names and behaviors, not "as of vX".
- Every gotcha/safety claim must trace to a real behavior (this repo's `CLAUDE.md`, or the memory entries `feedback_prefer_windows_mcp`, `feedback_process_cleanup_pass`, `project_windows_mcp_startup_report`). Verify with `honest-claude`.
- Files are LF-normalized on commit (repo default); do not introduce CRLF.

---

### Task 1: Author `skills/windows/SKILL.md`

**Files:**
- Create: `C:\Users\danie\Github\windows-mcp\skills\windows\SKILL.md`
- Reference (read for style, do not modify): `C:\Users\danie\Github\dropbox-mcp\skills\dropbox\SKILL.md`

**Interfaces:**
- Produces: a skill whose frontmatter `name: windows` makes it load as `windows-mcp:windows`. Task 2's README points at it; Task 3 releases it.

**Deliverable:** one Markdown file with the frontmatter below (verbatim) and a body containing exactly the five sections specified. Write connecting prose in the same voice/structure as the `dropbox` SKILL.md (tables for matrices, fenced blocks for tool sequences, a "When to use / Do NOT use" split).

- [ ] **Step 1: Write the frontmatter verbatim**

```yaml
---
name: windows
description: "Playbook for driving Windows via the windows-mcp server's 60 tools — UI automation, system inspection, files, registry, services, processes, disk, network, security, and startup analysis. Use when the user says 'automate this Windows app', 'click/type into that window', 'take a screenshot' or 'OCR the screen', 'audit my startup items', 'why is my PC booting slowly', 'clean up orphaned processes', 'what's running', 'check Defender/firewall status', 'run a security audit', 'read/set a registry value', 'inspect a service or scheduled task', 'find/hash/inspect a file', 'check disk or storage health', or any Windows desktop-automation or system-inspection task. Steers toward the windows-mcp tools over ad-hoc PowerShell, gives composed multi-tool workflows, and flags destructive tools. Does NOT add tools; it is guidance over the windows-mcp server. Not cross-platform; the server runs unelevated so admin-only operations may need elevation the skill cannot grant."
---
```

- [ ] **Step 2: Write Section 1 — intro + skill root + slash trigger**

A short intro (2–4 sentences): the skill is the judgment layer over the `windows-mcp` server's 60 atomic tools; it adds no tools. State: **Skill root** — ships inside the `windows-mcp` plugin (repo `danielsimonjr/windows-mcp`, `skills/windows/`); slash trigger `/windows`. Mirror the dropbox skill's "Skill root" paragraph.

- [ ] **Step 3: Write Section 2 — Tool-selection: windows-mcp tools vs. raw PowerShell**

Encode `feedback_prefer_windows_mcp`. Include this heuristic and matrix (write as prose + a Markdown table):

- **Default to the MCP tool.** It is faster than a PowerShell cold-start, returns structured JSON, and runs unelevated in one place.
- **Fall back to the `powershell` tool** only for one-off scripting the 60 tools don't express. Gotcha: the `powershell` tool's stdin can arrive empty — pass a script via a temp `.ps1` and invoke that, rather than piping a heredoc.
- The MCP **runs unelevated**; admin-only ops (`registry_set` under HKLM, `service` start/stop, some `scheduled_task`) can return access-denied — recognize that signature instead of retrying blindly.

| The task | Reach for |
|---|---|
| Inspect OS/memory/disk/GPU/battery | `system_info`, `wmi_query` |
| List/inspect/kill a process | `process`, `process_inspect` |
| Read/search/hash a file | `file_read`, `file_search`, `file_hash`, `file_info` |
| Read a registry value | `registry_get` |
| Drive a GUI app | `get_state` → `click`/`type` → `assert_element` |
| One-off scripting no tool covers | `powershell` (temp `.ps1`) |

- [ ] **Step 4: Write Section 3 — The 60 tools, grouped by domain**

One line of purpose per domain, then the tool list. Use EXACTLY these 60 tools (verified against the live server in Step 9):

- **UI automation / input (24):** `click`, `drag`, `hover`, `key`, `type`, `scroll`, `focus`, `get_state`, `get_element`, `get_text`, `get_table`, `find_element`, `assert_element`, `interact_element`, `wait_for`, `switch_to_window`, `window`, `multi_monitor`, `screenshot`, `ocr`, `clipboard`, `file_dialog`, `notification`, `launch`
- **Processes / shell (4):** `process`, `process_inspect`, `start_process`, `powershell`
- **System (7):** `system_info`, `wmi_query`, `env`, `reliability`, `event_log`, `driver_list`, `power_action`
- **Files (7):** `file_read`, `file_write`, `file_manage`, `file_search`, `file_info`, `file_hash`, `file_streams`
- **Disk / storage (2):** `disk_inspect`, `storage_health`
- **Services / tasks (2):** `service`, `scheduled_task`
- **Registry (2):** `registry_get`, `registry_set`
- **Network / web (4):** `network`, `firewall`, `http_request`, `scrape`
- **Security (5):** `security_audit`, `defender_status`, `cert_store`, `verify_signature`, `startup_report`
- **Misc (3):** `shortcut`, `archive`, `audio`

(24+4+7+7+2+2+2+4+5+3 = 60.) Add one sentence: "If a `windows-mcp` tool isn't loaded, fetch its schema via `ToolSearch select:mcp__plugin_windows-mcp_Windows-mcp__<tool>`."

- [ ] **Step 5: Write Section 4 — Workflow playbooks**

Five recipes. For each, give the tool sequence as a fenced block and 2–4 sentences of decision guidance:

1. **Startup / boot-slowness triage:** `startup_report` → for each suspicious entry: `verify_signature` + `cert_store` (is it signed / trusted?) → corroborate with `reliability` (recent crashes/hangs) + `event_log` (boot/service errors). Decision: unsigned or untrusted autoruns with recent reliability drops are the prime suspects.
2. **Process cleanup (whitelist — never kill-all):** `process` (list) → `process_inspect` (identity/parentage of candidates) → terminate only whitelisted orphans. **Hard rail:** never terminate `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `explorer`, or user apps (encodes `feedback_process_cleanup_pass`). Confirm the kill list with the user first.
3. **Security audit sweep:** `security_audit` + `defender_status` + `firewall` (status) + `startup_report`, composed into one health summary with a per-area verdict.
4. **UI-automation loop:** `get_state` (read the tree) → act (`click` / `type` / `key`) → `assert_element` or `wait_for` (confirm the state changed) → repeat. Call out: **the target app must be foregrounded on an interactive desktop**; these tools fail headless/background. Prefer `find_element`/`get_element` to locate targets over raw coordinates.
5. **File forensics:** `file_search` (locate) → `file_info` (size/timestamps/attrs) + `file_hash` (SHA-256) + `file_streams` (alternate data streams) + `verify_signature` (Authenticode/catalog).

- [ ] **Step 6: Write Section 5 — Safety rails & gotchas**

- **Confirm before destructive tools:** `registry_set`, `service`, `scheduled_task`, `power_action`, `file_write`, `file_manage`, `firewall`. Modifying system/security settings is gated — run the read-only counterpart first (`registry_get` before `registry_set`; `process_inspect` before killing; `service` query before start/stop).
- **`storage_health` can wedge on external / USB drives** — scope to internal disks, or warn before running against removable media (matches the `Everything`/USB-hang note in memory and `storage_health` behavior).
- **Runs unelevated** — admin-only operations fail with access-denied; surface that, don't loop.
- **UIAutomation tools need the target app foregrounded** on an interactive desktop.
- **`powershell` stdin-empty → temp `.ps1`** (see Section 2).

- [ ] **Step 7: Self-review the Markdown (doc task — replaces TDD RED/GREEN)**

Read the file top to bottom. Confirm: frontmatter is valid YAML and `name: windows`; all five sections present; no placeholder text (`TBD`, `TODO`, "add X here"); tables render; tool sequences are fenced. Confirm no version numbers/dates in the body.

- [ ] **Step 8: Verify claims with honest-claude**

Invoke the `honest-claude` skill. For every gotcha/rail in Sections 2, 5, and every whitelist name in Workflow 2, confirm it traces to a real source (`CLAUDE.md` in this repo, or the named memory entries). Fix or remove any claim that can't be grounded.

- [ ] **Step 9: Verify the tool-domain map against the LIVE server**

Cross-check the 60 tools in Section 3 against the live registry:
```
ToolSearch  query: "+windows-mcp"   max_results: 80
```
Every tool listed in Section 3 must appear in the results; no live tool may be missing from Section 3. If the live count differs from 60, correct Section 3 (and the domain subtotals) to match the server — the server is the source of truth, not this plan.

- [ ] **Step 10: Commit**

```bash
cd "C:/Users/danie/Github/windows-mcp"
git add skills/windows/SKILL.md
git commit -m "feat(skill): add windows-mcp:windows playbook SKILL.md"
```

---

### Task 2: Author `skills/windows/README.md`

**Files:**
- Create: `C:\Users\danie\Github\windows-mcp\skills\windows\README.md`
- Reference (read for style): `C:\Users\danie\Github\dropbox-mcp\skills\dropbox\README.md`

**Interfaces:**
- Consumes: the SKILL.md from Task 1 (names/sections it points at).
- Produces: nothing later tasks depend on beyond its existence.

- [ ] **Step 1: Write the README**

A short human-facing overview (~20–40 lines): what the skill is (a playbook over the `windows-mcp` server, not new tools); how it loads (`windows-mcp:windows`, slash `/windows`); a one-line list of the five workflows (startup triage, process cleanup, security sweep, UI-automation loop, file forensics); and a pointer to `SKILL.md` for the full playbook. Do not duplicate the SKILL body. No version/date.

- [ ] **Step 2: Self-review**

Read it; confirm no placeholders, links/paths correct, no duplication of SKILL.md content, no version/date.

- [ ] **Step 3: Commit**

```bash
cd "C:/Users/danie/Github/windows-mcp"
git add skills/windows/README.md
git commit -m "docs(skill): add windows skill README"
```

---

### Task 3: Release — plugin version bump, repo docs, push

**Files:**
- Modify: `C:\Users\danie\Github\windows-mcp\.claude-plugin\plugin.json` (version `0.4.1` → `0.5.0`)
- Modify: `C:\Users\danie\Github\windows-mcp\CHANGELOG.md`
- Modify: `C:\Users\danie\Github\windows-mcp\README.md`
- **Do NOT modify:** `src/WindowsMcp/Program.cs` (binary version stays 0.4.1 — see Global Constraints).

**Interfaces:**
- Consumes: `skills/windows/` from Tasks 1–2.

- [ ] **Step 1: Bump `plugin.json`**

Change the `"version"` field from `"0.4.1"` to `"0.5.0"`. Leave `name` and `description` unchanged. (This is what `/plugin marketplace update` keys on to re-clone; without the bump the new skill won't be pulled.)

- [ ] **Step 2: Fix the orphaned CHANGELOG entry and add the 0.5.0 section**

The current `CHANGELOG.md` starts with a `### Changed` block (the `Screenshot` default change) that has **no version header** — an orphan for an unreleased binary change that this release does NOT ship (the binary is unchanged). Fix by (a) giving that block a proper `## [Unreleased]` header, and (b) inserting a new `## [0.5.0]` section for the skill, above `## [0.4.1]`:

```markdown
## [Unreleased]

### Changed
- **`Screenshot` tool defaults to `output="file"` instead of inline base64** — saves image to
  `%TEMP%\WindowsMcp\screenshot_<timestamp>.<ext>` and returns the file path. A full-screen
  1080p PNG was embedding ~240k tokens of base64 directly in the conversation history; the file
  path response is ~4 tokens. Pass `output="base64"` to restore the previous inline behavior.

## [0.5.0] - 2026-07-04

### Added
- **Companion `windows` skill** (`skills/windows/`, loads as `windows-mcp:windows`, slash
  `/windows`) — a guidance/playbook over the server's 60 tools: tool selection (prefer the MCP
  over raw PowerShell), a 60-tool domain map, five workflow playbooks (startup/boot triage,
  process cleanup, security sweep, UI-automation loop, file forensics), and safety rails for
  destructive tools. No new tools; the server binary is unchanged (still reports 0.4.1).

## [0.4.1] - 2026-06-26
```

Preserve the exact text of the existing `Screenshot` bullet — only move it under the new `## [Unreleased]` header.

- [ ] **Step 3: Update the repo `README.md`**

The plugin now ships a skill in addition to the server. Add a short subsection (e.g., under the intro or a new "## Companion skill" heading) noting: the plugin also ships a `windows` skill (`windows-mcp:windows`, `/windows`) — a playbook that steers Claude toward these tools with composed workflows and safety rails; see `skills/windows/SKILL.md`. Keep it to 2–4 lines; no version/date in the README body.

- [ ] **Step 4: Verify no unintended version drift**

```bash
cd "C:/Users/danie/Github/windows-mcp"
grep -n '"version"' .claude-plugin/plugin.json        # expect 0.5.0
grep -n 'Version = "0.4.1"' src/WindowsMcp/Program.cs  # expect STILL 0.4.1 (unchanged)
```
Expected: `plugin.json` = 0.5.0, `Program.cs` = 0.4.1 (intentional).

- [ ] **Step 5: Atomic commit + push**

```bash
cd "C:/Users/danie/Github/windows-mcp"
git add .claude-plugin/plugin.json CHANGELOG.md README.md
git commit -m "release: windows-mcp 0.5.0 — ship the windows companion skill"
git push origin main
```

- [ ] **Step 6: Verify the push reached the remote (second method)**

```bash
git -C "C:/Users/danie/Github/windows-mcp" ls-remote origin -h refs/heads/main
git -C "C:/Users/danie/Github/windows-mcp" rev-parse HEAD
```
Expected: the two SHAs match (local HEAD == remote main).

---

## Delivery (post-plan, controller/user step — not a task)

After all tasks pass review: `/plugin marketplace update local-marketplace` then `/reload-plugins` (fresh re-clone lands `skills/windows/` in the cache). Final verification: confirm `windows-mcp:windows` appears in the reloaded skills list and `/windows` triggers it.

## Self-Review (plan vs. spec)

- **Spec coverage:** Placement/load model → Tasks 1–2 + Delivery. Tool-selection §2 → Task 1 Step 3. 60-tool map §3 → Task 1 Step 4 + Step 9 verify. Five workflows §4 → Task 1 Step 5. Safety rails §5 → Task 1 Step 6. README → Task 2. Release (plugin.json/README/CHANGELOG/commit/push) → Task 3. Success criteria 1–5 all mapped (criterion 5, load verification, is the Delivery step). No gaps.
- **Placeholder scan:** none — frontmatter, tool list, workflow sequences, CHANGELOG block all given verbatim.
- **Consistency:** skill name `windows` and load id `windows-mcp:windows` identical across all tasks; version target 0.5.0 (plugin.json) with Program.cs explicitly held at 0.4.1 stated in Global Constraints and re-checked in Task 3 Step 4; tool count 60 asserted in Task 1 Step 4 and verified in Step 9.
