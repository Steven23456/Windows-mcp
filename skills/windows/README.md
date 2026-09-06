# windows

Playbook for driving Windows applications and system inspection over the `windows-mcp` server.

## Purpose

A judgment layer over the `windows-mcp` server's 69 atomic tools for Windows desktop automation and system inspection. This skill adds no tools of its own—every action composes existing MCP tools into multi-step workflows with the right safety checks. It steers tool selection (MCP vs. raw PowerShell), sequences operations correctly, and flags destructive operations.

Supports five core workflows:
- **Startup triage** — diagnose slow boot, audit autoruns, verify signatures
- **Process cleanup** — list, inspect, and kill orphaned processes (whitelist-only, never all)
- **Security sweep** — Defender status, firewall rules, certificate trust, full audit
- **UI automation** — click, type, screenshot, OCR, drive foreground applications
- **File forensics** — locate, hash, inspect streams, verify signatures

## Files

| File | Purpose |
|---|---|
| `SKILL.md` | Full playbook: when-to-use, tool selection, workflow recipes, safety rails |
| `README.md` | This overview |

## Triggers

Loads as `windows-mcp:windows`; explicit slash trigger: `/windows`.

Auto-loads on queries mentioning desktop automation, Windows diagnostics, or system inspection (e.g., "click this", "automate this app", "why is my PC slow", "audit startup", "check Defender").

## Scope

Windows-only; server runs unelevated (admin-only operations may return access-denied). For details on capabilities and limitations, see `SKILL.md`.
