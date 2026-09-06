# B-11 — `start_process` with an argv list and a working directory

**Checklist item:** [B-11](../upstream-parity-checklist.md#b-11--start_process-with-argv-list-and-cwd--p2--s) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 1, last item ·
**Status:** implemented 2026-09-05 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~1 h including the RED/GREEN passes.

## Problem

`start_process(command)` took one command-line string, split it at the first space (or after a
quoted executable) and had no working directory. An argument with a space or a quote had to be
escaped by the caller for Windows' command-line rules, and got them wrong as often as not.
Upstream's `launch_executable` takes `executable`, an `args` list and `cwd`, and spawns with
`shell=False`.

## Decision

- **`args_json` is a JSON array of strings**, parsed by a pure `ArgvJson.Parse`: null or blank
  means "no argv list" and the command keeps its whole-command-line meaning; an array (even an
  empty one) means argv mode, where `command` is the executable and every item goes to
  `ProcessStartInfo.ArgumentList` verbatim — no quoting, no splitting, no trimming; a JSON
  object, a bare string, a non-string item or malformed JSON is an `ArgumentException` naming
  `args_json`, raised in the tool before anything is spawned. The parameter is a `string?`, so
  an array sent as JSON text and one sent stringified (the Claude Desktop quirk) arrive the same
  way.
- **`cwd`** must be an existing directory (`DirectoryNotFoundException` naming it, before the
  spawn; blank is "not given"); **`use_shell_execute`** is carried through, default false.
- **One pure builder**, `ProcessService.BuildStartInfo(ProcessStart)`, holds every decision
  (the split, the list, the cwd check, the shell flag) so it is testable without spawning; the
  old `StartDetachedAsync(string)` delegates to the new `StartDetachedAsync(ProcessStart)` with
  a command-only spec and is byte-for-byte what it was. The tool returns `{pid, executable,
  args, cwd}` for both forms (`args: []`, `cwd: null` for the old one) — a shape change from the
  old `"started (pid=N)"` string.

## Changes

- `Abstractions`: `ProcessStart(Command, Args, Cwd, UseShellExecute)`; `IProcessService
  +StartDetachedAsync(ProcessStart)`.
- `Services/ArgvJson.cs` (new); `Services/ProcessService.cs` (`SplitCommand`, `BuildStartInfo`,
  the spec overload).
- `Tools/ProcessTools.cs` — the three parameters, the JSON result, the description.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Array → argv in order and verbatim (empty strings, Unicode, newlines kept); null/blank → null; `[]` → empty; eleven non-array shapes refused naming `args_json` | `ArgvJsonTests` (9 methods, 20+ cases) | Unit |
| R2 | Without argv the split is unchanged (six shapes, the unmatched quote); with argv the command is the executable and the list is verbatim; cwd existing/blank/missing/a file; `use_shell_execute` through and default | `ProcessServiceStartTests` (13) | Unit |
| R3 | A real spawn from a spec; an argument with a space arrives whole; the child runs in the given cwd; a missing cwd spawns nothing; the string overload unchanged | `ProcessServiceStartSpecIntegrationTests` (5) | Integration |
| R4 | Tool: the command-only shape and its JSON; `args_json` parsed and echoed; `use_shell_execute` through; bad `args_json` refused before the service; the service's cwd refusal not swallowed; the description | `ProcessToolsTests` (6 methods) | Unit |

## Deviations and follow-ups

- The RED pass could not confirm how the MCP SDK binds a genuine JSON array to a `string?`
  parameter; the tool accepts the stringified form for certain and the raw-array form is the
  one to verify over the wire in the live e2e sweep. If the SDK refuses, the parameter becomes
  `JsonElement?` and `ArgvJson.Parse` stays the inner parser.
- `start_process` is still detached with no output capture; `powershell`/`job` remain the
  tools for a command whose output matters.
