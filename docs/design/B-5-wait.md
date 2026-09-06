# B-5 — a plain `wait` tool

**Checklist item:** [B-5](../upstream-parity-checklist.md#b-5--plain-wait-tool--p1--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 1, first item ·
**Status:** implemented 2026-09-05 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~1 h including the RED/GREEN passes.

## Problem

Agents pause constantly between an action and the next look, and the only way to do it here was
`powershell("Start-Sleep 2")`: a PowerShell cold start (seconds to tens of seconds under
Defender on this box) plus the serialization gate, for a two-second sleep. Upstream has
`Wait(duration)`.

## Decision

- **A new tool, `wait(seconds)`**, on `InputTools` because it sits between the verbs an agent
  interleaves it with (roadmap C3: 65 → 66 tools). `seconds` is accepted in `(0, 60]`; `0`, a
  negative number, anything above 60, NaN and ±∞ are `ArgumentException`s naming the parameter,
  the ceiling and `wait_for` — a longer or conditional wait is a condition, not a sleep, and the
  refusal says so. The wait is `Task.Delay` on the call's cancellation token, so the transport
  can cut it short; the response is `{"waited": <seconds as given>}`.
- **Annotated** `ReadOnly = true, Idempotent = true` on the `[McpServerTool]` attribute (the
  ModelContextProtocol 2.2.0 attribute carries the hints), so a client that gates side effects
  can run it without asking; C-7 will do the same for the other tools.
- The skill playbook offers `wait` in the agent loop and no longer mentions `Start-Sleep`; a
  test reads the playbook and the README and pins the tool count they quote to the assembly's.

## Changes

- `Tools/InputTools.cs` — `Wait(double seconds, CancellationToken)`.
- `skills/windows/SKILL.md`, `README.md`, `docs/architecture/*` — the count and the playbook
  line (docs-agent).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | The open interval up to 60 accepted and echoed; 0, negative, > 60, NaN, ±∞ rejected naming `seconds`, `60` and `wait_for`; a 50 ms wait really waits and does not overshoot; a cancelled token (before and during) cuts it short; no collaborator is touched; the attribute's two hints; the signature | `InputToolsTests` (11 methods) | Unit |
| R2 | The hints reach the wire (`readOnlyHint`/`idempotentHint` in `tools/list`); a wait and an out-of-range refusal over real HTTP | `HttpTransportTests` (2) | Integration |
| R3 | 66 tools with `wait` among them; no shipped description says "not implemented"; the playbook offers `wait` and never `Start-Sleep`; README and SKILL.md quote the real count | `ToolInventoryTests` (4 methods) | Unit |

Coverage and bite check: see the GREEN summary in the phase-1 CHANGELOG entry; `Wait` is
100 % line and branch (the range check has one arm per rejected shape).

## Deviations and follow-ups

- The ceiling is 60 s, not upstream's unbounded sleep: a wait longer than that on an MCP call is
  a timeout waiting to happen, and `wait_for` polls up to 120 s with a condition.
- `ToolInventoryTests` pins the counts quoted in README and SKILL.md only; the architecture docs
  quote per-group counts no regex separates, and stay docs-agent's job.
