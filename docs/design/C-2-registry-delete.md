# C-2 — Registry listing and `registry_delete`

**Checklist item:** [C-2](../upstream-parity-checklist.md#c-2--registry-delete-and-subkey-listing-on-the-tool-surface--p2--s) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 1, second item — decision R6 (a new tool, 68 → 69,
with a root denylist) settled in section 7 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

`registry_get` without a `value_name` returns the value names joined with commas and nothing
about sub-keys, so an agent walking a key needs `powershell`. `RegistryService` has had
`EnumerateValuesAsync` and `EnumerateSubKeysAsync` since the startup report and nothing exposes
them. There is no delete at all. Upstream's `Registry(mode=list)` returns values and sub-keys in
one call; `mode=delete` removes a value, or a whole key recursively.

## Decision

- **One read shape.** `registry_get(hive, path)` with no `value_name` returns a
  `RegistryKeyDto(Path, Values, SubKeys)` — `Values` are the existing `RegistryValueDto`s
  (name, data, kind), `SubKeys` the immediate child names — through a new
  `IRegistryService.ListAsync(hive, path)`, which throws `KeyNotFoundException` for an absent
  key (today's message) rather than the enumerators' empty arrays; an empty path lists the hive
  root. With a `value_name` the tool is unchanged. Contract change (a comma string became an
  object) → CHANGELOG *Changed*.
- **New tool `registry_delete(hive, path, value_name?, recursive = false, confirm = false)`.**
  Refusals in order, all before the service is touched: `confirm` missing; then, for a key
  delete, the guard (below). A value delete (`value_name` given) removes the value and reports
  whether it existed; a key delete removes the key, and when the key has sub-keys it needs
  `recursive:true` or is refused naming the flag and the count. Deleting what is not there is
  not an error: `existed:false`. Result (camelCase, like the section-B verbs):
  `{hive, path, valueName?, deleted, existed, subKeysRemoved?}` where `subKeysRemoved` is the
  number of descendant keys removed, counted before the delete, present only for a key delete.
- **A pure guard, `RegistryGuard.Refusal(path)`**, returns the reason a *key* delete is refused
  or null: an empty path (a hive root) and a denylist of roots, compared after normalising
  (trim, `/` → `\`, leading/trailing separators dropped, doubled separators collapsed,
  ordinal-ignore-case): `Software`, `Software\Classes`, `Software\Microsoft`,
  `Software\Microsoft\Windows`, `Software\Microsoft\Windows\CurrentVersion`,
  `Software\Microsoft\Windows NT`, `Software\Microsoft\Windows NT\CurrentVersion`,
  `Software\Policies`, `Software\WOW6432Node`, `System`, `SYSTEM\CurrentControlSet`, `SAM`,
  `SECURITY`, `Environment`, `Control Panel`, `Volatile Environment`. The list is deliberately
  short — the catastrophic roots, not every unwise delete; `confirm` and the client's
  `destructiveHint` (C-7) do the rest. Value deletes under those keys are allowed. The guard
  applies regardless of `recursive`.
- Elevation failures (`UnauthorizedAccessException` under HKLM) pass through as the OS reports
  them. The tool is annotated `Destructive = true, Idempotent = true` (C-7's table).
- README's Safety-rails list gains `registry_delete`; SKILL.md's registry line and its
  "confirm before destructive tools" list gain it too.

## Changes

- `Abstractions/Models/FileSystemDtos.cs` — `RegistryKeyDto`, `RegistryKeyDeleteResult(Existed,
  SubKeysRemoved)`; `Abstractions/IRegistryService.cs` — `ListAsync`, `DeleteValueAsync`
  (returns whether it existed), `DeleteKeyAsync(hive, path, recursive)`.
- `Services/RegistryGuard.cs` (new, pure); `Services/RegistryService.cs` — the three methods
  (`DeleteValue` / `DeleteSubKey` / `DeleteSubKeyTree`, with the descendant count walked
  first).
- `Tools/RegistryTools.cs` — `registry_get`'s no-name branch, the new tool.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `registry_get` without a name returns the values and sub-keys as JSON; with a name unchanged; an absent key still `KeyNotFoundException` | `RegistryToolsTests`, `RegistryServiceTests` | Unit / Integration |
| R2 | The guard: every denylist entry, each with a different case, with `/`, with a trailing separator, with doubled separators; the empty and whitespace path; `Software\MyApp` and `Software\Microsoft\Windows\CurrentVersion\Run\Thing` allowed | `RegistryGuardTests` | Unit |
| R3 | `registry_delete` refuses without `confirm`, refuses a denylisted or empty path (Moq verifies no call), refuses a key with sub-keys without `recursive` (the service's `InvalidOperationException` reaches the caller naming `recursive`), forwards a value delete and a key delete, reports `existed:false` and `subKeysRemoved` | `RegistryToolsTests` | Unit |
| R4 | Under `HKCU\Software\WindowsMcpTests\<guid>`: create values and sub-keys → `ListAsync` shows both → delete a value → gone, `existed:true` → delete the key without `recursive` → refused → with `recursive` → gone with the descendant count → delete again → `existed:false`; a value delete on a missing key → `existed:false` | `RegistryServiceTests` | Integration |
| R5 | 69 tools with `registry_delete` named; annotations per C-7; the schema and a `confirm` refusal over HTTP; README and SKILL.md name the tool | `ToolInventoryTests`, `HttpTransportTests` | Unit / Integration |

## Deviations and follow-ups

- `deleted` and `existed` are the same boolean: a delete of what was never there is
  `deleted:false, existed:false`. Both are kept so a caller reading either name gets the answer.
- The service refuses an empty path itself (`ArgumentException`) as well as the tool's guard,
  so a direct caller cannot delete a hive root either.
- The denylist grew beyond R6's list (`CurrentVersion`, `Windows NT`, `Policies`,
  `WOW6432Node`, the three HKCU user-profile roots) — each is a root whose recursive loss
  breaks the profile or the OS, which is the list's one criterion.
