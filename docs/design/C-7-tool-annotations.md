# C-7 — Tool annotations on every tool

**Checklist item:** [C-7](../upstream-parity-checklist.md#c-7--tool-annotations-on-all-69-tools--p2--s) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 1, first item — decision R10 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green — see CHANGELOG
[Unreleased]) ·
**Effort:** ~2 h including the RED/GREEN passes.

## Problem

Every tool is `[McpServerTool]` with nothing set (except `wait`, which B-5 gave `ReadOnly` and
`Idempotent`). The SDK then advertises the MCP defaults for all of them: not read-only,
destructive, not idempotent, open-world. A client that auto-approves on `readOnlyHint` cannot
approve `screenshot`; one that confirms on `destructiveHint` confirms `get_text`. Upstream sets
all four hints and a title on every tool.

## Decision

- **Every `[McpServerTool]` names `Title`, `ReadOnly`, `Destructive`, `Idempotent` and
  `OpenWorld` explicitly**, even where the value equals the SDK default. The test reads
  `CustomAttributeData.NamedArguments`, which lists exactly the arguments written in source —
  the only way to tell "set to the default" from "forgotten".
- **The hint is per tool, so a multi-action tool takes its most conservative action**: it is
  `ReadOnly` only when every action reads; `Destructive` when any action removes or replaces
  something durable; `Idempotent` when repeating the same call leaves the same state (a second
  `registry_set` of the same value: yes; a second `type`: no).
- **`OpenWorld` means "reaches past this machine or runs arbitrary code"**: `scrape`,
  `http_request`, `network` (ping, dns), `powershell`, `start_process`, `launch`. Driving a
  desktop app is closed-world: the tool acts on this machine's state.
- **`Destructive` is the README Safety-rails set plus the tools that end or replace things
  without a `confirm` gate** (`window` closes, `archive` overwrites, `job` cancels,
  `http_request` may `DELETE`). A `confirm` parameter is our gate; the hint is the client's.
- **Read-only is about durable state**, so `screenshot` (the flash overlay draws and vanishes)
  and `storage_health` (a SMART read that may wake a drive) are read-only, while `hover`
  (moves the pointer) and `clipboard` (`set` replaces the text) are not.
- Titles are short imperative or noun phrases in sentence case.
- No behaviour changes; no service touched; the count stays at what it is when the item lands
  (68 before C-2, 69 after).

## The table

| Tool | Title | ReadOnly | Destructive | Idempotent | OpenWorld | Reason when not obvious |
|---|---|---|---|---|---|---|
| snapshot | Desktop snapshot | ✔ | ✘ | ✔ | ✘ | |
| click | Click | ✘ | ✘ | ✘ | ✘ | |
| drag | Drag | ✘ | ✘ | ✘ | ✘ | |
| hover | Hover | ✘ | ✘ | ✔ | ✘ | moves the pointer; the same point twice is the same state |
| key | Press key | ✘ | ✘ | ✘ | ✘ | |
| shortcut | Press shortcut | ✘ | ✘ | ✘ | ✘ | |
| type | Type text | ✘ | ✘ | ✘ | ✘ | |
| scroll | Scroll | ✘ | ✘ | ✘ | ✘ | |
| focus | Focus element | ✘ | ✘ | ✔ | ✘ | |
| get_state | Get UI state | ✔ | ✘ | ✔ | ✘ | |
| get_element | Get element | ✔ | ✘ | ✔ | ✘ | |
| get_text | Get text | ✔ | ✘ | ✔ | ✘ | |
| get_table | Get table | ✔ | ✘ | ✔ | ✘ | |
| find_element | Find element | ✔ | ✘ | ✔ | ✘ | |
| assert_element | Assert element | ✔ | ✘ | ✔ | ✘ | |
| interact_element | Interact with element | ✘ | ✘ | ✘ | ✘ | |
| multi_select | Multi-select | ✘ | ✘ | ✘ | ✘ | |
| multi_edit | Multi-edit | ✘ | ✘ | ✘ | ✘ | |
| wait | Wait | ✔ | ✘ | ✔ | ✘ | as B-5 set it |
| wait_for | Wait for condition | ✔ | ✘ | ✔ | ✘ | |
| switch_to_window | Switch to window | ✘ | ✘ | ✔ | ✘ | |
| window | Window action | ✘ | ✔ | ✘ | ✘ | `close` can discard unsaved state |
| multi_monitor | List monitors | ✔ | ✘ | ✔ | ✘ | |
| screenshot | Take screenshot | ✔ | ✘ | ✔ | ✘ | the flash overlay is transient |
| ocr | OCR the screen | ✔ | ✘ | ✔ | ✘ | |
| clipboard | Clipboard | ✘ | ✘ | ✔ | ✘ | `set` replaces the text; nothing durable |
| file_dialog | Type into file dialog | ✘ | ✘ | ✘ | ✘ | |
| notification | Show notification | ✘ | ✘ | ✘ | ✘ | each call shows another toast |
| launch | Launch app | ✘ | ✘ | ✘ | ✔ | starts arbitrary programs |
| process | Processes | ✘ | ✔ | ✘ | ✘ | `kill` |
| process_inspect | Inspect process | ✔ | ✘ | ✔ | ✘ | |
| start_process | Start process | ✘ | ✘ | ✘ | ✔ | |
| powershell | Run PowerShell | ✘ | ✔ | ✘ | ✔ | arbitrary code |
| job | Background job | ✘ | ✔ | ✔ | ✘ | `cancel` ends a job; a second cancel is a no-op |
| system_info | System info | ✔ | ✘ | ✔ | ✘ | |
| wmi_query | WMI query | ✔ | ✘ | ✔ | ✘ | |
| env | Environment variables | ✘ | ✔ | ✔ | ✘ | `set` with a null value deletes |
| reliability | Reliability report | ✔ | ✘ | ✔ | ✘ | |
| event_log | Event log | ✔ | ✘ | ✔ | ✘ | |
| driver_list | Driver list | ✔ | ✘ | ✔ | ✘ | |
| power_action | Power action | ✘ | ✔ | ✘ | ✘ | |
| audio | Audio | ✘ | ✘ | ✔ | ✘ | |
| file_read | Read file | ✔ | ✘ | ✔ | ✘ | |
| file_write | Write file | ✘ | ✔ | ✘ | ✘ | C-1's `append`: twice is not the same state twice |
| file_manage | Manage files | ✘ | ✔ | ✘ | ✘ | |
| file_search | Search files | ✔ | ✘ | ✔ | ✘ | |
| file_info | File info | ✔ | ✘ | ✔ | ✘ | |
| file_hash | Hash file | ✔ | ✘ | ✔ | ✘ | |
| file_streams | File streams | ✔ | ✘ | ✔ | ✘ | |
| archive | Zip or unzip | ✘ | ✔ | ✔ | ✘ | overwrites an existing archive or extracted files |
| disk_inspect | Inspect disk | ✔ | ✘ | ✔ | ✘ | |
| storage_health | Storage health | ✔ | ✘ | ✔ | ✘ | SMART is a read, even if it wakes a drive |
| service | Windows service | ✘ | ✔ | ✘ | ✘ | `stop`/`restart` |
| scheduled_task | Scheduled task | ✘ | ✔ | ✘ | ✘ | `delete`; `create` twice is two tasks |
| registry_get | Read registry | ✔ | ✘ | ✔ | ✘ | |
| registry_set | Write registry | ✘ | ✔ | ✔ | ✘ | |
| registry_delete | Delete registry key or value | ✘ | ✔ | ✔ | ✘ | C-2; deleting what is gone is a no-op |
| network | Network info | ✔ | ✘ | ✔ | ✔ | `ping`/`dns` reach outside |
| firewall | Firewall rules | ✘ | ✔ | ✘ | ✘ | |
| http_request | HTTP request | ✘ | ✔ | ✘ | ✔ | any method, any body |
| scrape | Scrape web page | ✔ | ✘ | ✔ | ✔ | |
| defender_status | Defender status | ✔ | ✘ | ✔ | ✘ | |
| security_audit | Security audit | ✔ | ✘ | ✔ | ✘ | |
| verify_signature | Verify signature | ✔ | ✘ | ✔ | ✘ | |
| cert_store | Certificate store | ✔ | ✘ | ✔ | ✘ | |
| integrity | File integrity | ✘ | ✘ | ✔ | ✘ | `baseline` writes a baseline; additive |
| startup_report | Startup report | ✔ | ✘ | ✔ | ✘ | |
| fs_changes | File system changes | ✔ | ✘ | ✔ | ✘ | |
| watch | Watch directory | ✘ | ✘ | ✘ | ✘ | `start` creates a session |

## Changes

- `Tools/*.cs` — every `[McpServerTool]` attribute gains the five named arguments (`Name` is
  left to the SDK, as today).
- No `Abstractions`, `Services` or `Hosting` change.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Every tool method's `McpServerToolAttribute` names `Title`, `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld` in `CustomAttributeData.NamedArguments`; a title is non-blank and ≤ 40 chars | `ToolInventoryTests` | Unit |
| R2 | The read-only set equals the table's literal list; the open-world set equals the table's; no tool is both `ReadOnly` and `Destructive`; every read-only tool is idempotent | `ToolInventoryTests` | Unit |
| R3 | Every tool named in README's "Safety rails" section (parsed from the file) is `Destructive`; `registry_delete` joins that section in C-2 | `ToolInventoryTests` | Unit |
| R4 | Over HTTP, `ListToolsAsync` returns `Annotations` with `Title` and all four hints non-null for every tool; `screenshot` is read-only, `file_manage` destructive, `scrape` open-world | `HttpTransportTests` | Integration |

## Deviations and follow-ups

- Roadmap R10 listed `job` among the open-world tools; the table keeps it closed-world: `job`
  reads and cancels a job on this machine, and `powershell` is the call that runs the code.
- Three tools (`job`, `watch`, `verify_signature`) return a plain `string` rather than a
  `Task`; the sweep and the tests find tools by the attribute, not the return type.
- `file_write` was `Idempotent` when this table was written; C-1's `append` changed that and the
  row above is the current answer (`Idempotent = false`, pinned in `ToolInventoryTests`).
- `Name` is not set on any attribute: the SDK's naming is what every client already sees, and
  a test pins the snake_case names through the tool list, not the attribute.
