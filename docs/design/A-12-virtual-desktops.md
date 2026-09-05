# A-12 (phase 1) — virtual desktops: the inventory, the current one, and which desktop a window is on

**Checklist item:** [A-12](../upstream-parity-checklist.md#a-12--virtual-desktops--p3--l) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 5, second item — phase 1 only (the documented
interface and the registry); phase 2 (create/switch/rename through the undocumented per-build
interface) is explicitly not planned ·
**Status:** implemented 2026-09-05 (build clean, 1822/1822 headless tests green, of which the
COM paths run for real on this session — see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the RED/GREEN passes.

## Problem

`window list` carried a `DesktopId` field reserved since A-1 and always null; nothing reported
which virtual desktops exist, which is current, or which desktop a window is on. Upstream heads
every snapshot with the active desktop and all desktops by name.

## Decision

- **Only the documented surface.** `IVirtualDesktopManager` (CLSID `aa509086-…`, IID
  `a5cd92ff-…`) declared per CLAUDE.md's COM rule with all three methods in vtable order —
  `IsWindowOnCurrentVirtualDesktop`, `GetWindowDesktopId`, `MoveWindowToDesktop` (the last is never
  called in phase 1 but has to occupy its slot) — created once, lazily; every COM refusal is a
  null answer. Desktop names and the list come from the registry through `IRegistryService`, so
  the parsing is unit-tested on byte arrays: `VirtualDesktopRegistry.Parse(ids, current, nameOf)`
  reads 16-byte GUIDs in order (`new Guid(bytes)`, Windows' own byte order), flags the one the
  current blob names, uses the stored `Name` or `Desktop N` when it is blank, and ignores a
  trailing partial GUID.
- **What this Windows actually keeps.** The RED pass read the registry on this build (11,
  10.0.28000): `HKCU\…\Explorer\VirtualDesktops` has **no** `VirtualDesktopIDs` and no
  `CurrentVirtualDesktop`; only the per-desktop subkeys `Desktops\{GUID}` exist (three here, all
  with an empty name), and `SessionInfo\<n>\VirtualDesktops` is value-less too. The contract as
  first written would have reported zero desktops. So the service falls back: the ids come from
  the `Desktops` subkey names in enumeration order when the blob is absent; the current desktop is
  read from `VirtualDesktops`, else `SessionInfo\<session>\VirtualDesktops`, else derived as **the
  desktop the foreground window is on** (COM). On this box the third path is the only one that
  answers, and it does.
- **`WindowInfo.DesktopId`** is filled by `WindowService.ListAsync` for the survivors of the filter
  only (one COM call per listed window), through an optional `IVirtualDesktopService` constructor
  parameter so the 39 direct `new WindowService()` constructions keep working; a throwing desktop
  service costs no windows, a cancellation propagates. `GetWindowDesktopIdAsync` reports `GUID_NULL`
  as null — measured: of ~288 top-level windows on this desktop only 3 answer at all, 280 refuse
  with `E_FAIL`, 5 return `GUID_NULL` — so most listed windows carry an id and the rest stay null.
- **`window(action:"desktops")`** → `{ current, all }` with the full `VirtualDesktopInfo(Id, Name,
  Index, IsCurrent)`; `current` is the flagged entry of the same list (one read, one truth). The
  unknown-action message names seven actions now.

## Changes

- `Abstractions`: `IVirtualDesktopService` (`ListAsync`, `GetCurrentAsync`,
  `GetWindowDesktopIdAsync`, `IsWindowOnCurrentDesktopAsync`); `VirtualDesktopInfo`.
- `Services/VirtualDesktopRegistry.cs`, `Services/VirtualDesktopService.cs` (new — the COM
  declaration lives here); `Services/WindowService.cs` (optional dependency, `DesktopId` fill).
- `Tools/WindowTools.cs` — `desktops` action, descriptions; `Hosting/WindowsMcpHost.cs` — the
  38th generic registration.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1–R2 | Parse: order, byte order, id format, names and `Desktop N`, blank names, current flag (present, absent, unknown, malformed), null/short/partial blobs, `GuidKey` | `VirtualDesktopRegistryTests` (29) | Unit |
| R3 | Service against a mocked registry: exact hive/path/value names, mapping, unreadable key/value/type → empty, one bad name costs one name, the subkey fallback (order kept, junk skipped, blob wins, enumeration failures → empty), the SessionInfo fallback (exact path with the real session id, main key wins), the COM fallback never invents a match for synthetic ids, cancellation is never "no data" | `VirtualDesktopServiceTests` (36) | Unit |
| R3b | Real registry and real COM on this session: never throws, at most one current, `GetCurrentAsync` agrees, hwnd 0 and a dead handle → null, a real window's id is a listed desktop and is exactly what COM reported, `GUID_NULL` → null, the foreground window is on the current desktop, **the desktops this registry really holds are found**, the foreground-window fallback answers here | `VirtualDesktopServiceIntegrationTests` (12) | Integration |
| R4 | `DesktopId` filled per listed window (not per probe), token forwarded, null stays null, no service → null, a throwing service costs nothing, a cancellation propagates, `active` carries it, both services real | `WindowServiceDesktopIdTests` (9) | Integration |
| R5–R6 | Tool envelope and `current` from the list, empty list, case-insensitive, no title, `list`/`active` untouched, the seven-action message, descriptions; DI registration; over real HTTP | `WindowToolsTests`, `WindowsMcpHostTests`, `HttpTransportTests` | Unit / Integration |

Coverage: `VirtualDesktopRegistry`, `WindowTools` 100 %; `VirtualDesktopService` 94 % line (the
rest is the no-manager and no-foreground-window arms). Bite check: eight breaks — big-endian
GUIDs, a lower-case subkey, `Desktop N` off by one, the subkey fallback removed, the wrong
session path, asking per probe instead of per survivor, a second registry read for `current`,
`GUID_NULL` reported as a zero GUID — all caught; the fallback removal was caught **only by mocks**
until the GREEN pass added a real-registry non-vacuity guard (every integration test bails on an
empty list, the `disk_inspect` shape).

## Deviations and follow-ups

- The roadmap's `{current, all:[{id, name}]}` shipped as the full record with PascalCase members;
  its `IsOnCurrentDesktop` on `WindowInfo` was not added (a second COM call per window for a fact
  the cloaking filter already implies — listed windows are on the current desktop).
- `IsWindowOnCurrentDesktopAsync` returns true for a handle that is not a window: the real COM
  object answers `S_OK`/1 rather than refusing. Only `hwnd 0` is null in practice; the doc says so.
- `Index` is the registry's enumeration order on builds without the ordered blob, which may not be
  the user's left-to-right order. A phase-2 concern.
- Cloaked windows on other desktops are still dropped by A-1's filter, so `window list` shows the
  current desktop's windows with their `DesktopId`; listing other desktops' windows would need the
  filter to admit cloaked-by-desktop windows — a separate item if wanted.
