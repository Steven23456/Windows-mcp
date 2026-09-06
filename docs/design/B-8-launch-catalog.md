# B-8 — `launch` by Start Menu name: an in-process app catalog, fuzzy matching, and a window wait

**Checklist item:** [B-8](../upstream-parity-checklist.md#b-8--launch-by-start-menu-name-with-fuzzy-match-and-window-wait--p1--m) ·
**Roadmap:** [B-roadmap](B-roadmap.md) phase 3, first item — decision C7 (no PowerShell) settled
in section 7 ·
**Status:** implemented 2026-09-06 (build clean, headless suite green, 3/3 desktop launch tests
green — see CHANGELOG [Unreleased]) ·
**Effort:** ~4 h including the spike and the RED/GREEN passes.

## Problem

`launch(app_name)` handed the string to `ShellExecute` and returned a pid. That works for a
path, an executable on `PATH`, or an exact shortcut name, and fails for what a person types:
`"calc"` happened to work because `calc.exe` exists, `"edge"` and `"visual studio code"` did
not, and packaged apps (Calculator, Terminal, Notepad) have no executable to run at all. Nothing
waited for the window, so the agent had no handle to act on. Upstream builds a name-to-app map
from `Get-StartApps`, fuzzy-matches the request, launches by path or AUMID, and waits for the
window by pid, then by title.

## What the spike found

- `PackageManager.FindPackagesForUser("")` lists 147 packages in ~100 ms and
  `GetAppListEntriesAsync()` over them yields 69 launchable entries (display name + AUMID) in
  ~800 ms, in-process, from the `net10.0-windows10.0.19041.0` projection with no new package.
  A few packages refuse the call and are skipped.
- `IApplicationActivationManager.ActivateApplication(aumid, …, out pid)` activates a packaged
  app and returns its process id; it is the first vtable method, so it is the only one declared.
- The two Start Menu `Programs` folders hold 88 `.lnk` files here in 17 ms; desktop apps that
  `Get-StartApps` lists under a registered AUMID (Edge as `MSEdge`, VS Code) all have one.
- PowerShell 7 cannot load the WinRT types at all, which is one more reason the catalog is not
  a PowerShell call.

## Decision

- **Two sources, one pure merge** (`AppCatalog.Merge`): shortcuts from both Start Menu roots
  (name = file name without extension, target = the `.lnk` path; `ShellExecute` opens a `.lnk`
  directly, so `ShortcutResolver` is not involved) and packaged apps (name = display name,
  target = AUMID, source = the package family). One entry per name, ordinal ignoring case; a
  shortcut beats a packaged entry of the same name because the `.lnk` carries the user's
  intent; the first of two shortcuts sharing a name wins. Ordered by name.
- **One pure matcher** (`AppCatalog.Match`): exact name → the shortest name the request is a
  prefix of (`"calc"` → Calculator) → the highest `max(PartialRatio, TokenSetRatio)` at 70 or
  more (`"edge"` → Microsoft Edge 100, `"vs code"` → Visual Studio Code 73), ties to the
  shortest name. Nothing → a `KeyNotFoundException` naming the request and the five nearest
  names with their scores, so a miss is actionable. The scorers are B-10's (C6).
- **A cached service** (`AppCatalogService`, `IAppCatalogService`, a singleton): the sources
  are read at most once per five minutes, a source that throws is skipped so an unreadable
  folder cannot empty the catalog, and a miss refreshes once (the app may have just been
  installed) and then stands until the TTL turns over. The sources and the clock are
  constructor seams (`Func<IEnumerable<AppEntry>>`, `TimeProvider`), so the cache rules are
  unit-tested with fakes and the real scans are Integration.
- **`launch` in three steps** (`WindowService.LaunchAsync(name, waitForWindow, timeoutMs)`):
  an existing file or directory, or an **explicit** `.exe` name on `PATH`, starts outright
  through `ShellExecute` with `Strategy: "path"` and no catalog; a bare word always goes to the
  catalog, even when a same-named executable exists — `"calc"` was short-circuited to `calc.exe`
  in the first cut and the unit tests caught it; a packaged entry is activated by AUMID
  (`Win32AppActivator`, the pid comes back), a shortcut through `ShellExecute`. With
  `waitForWindow` the inventory is polled every 250 ms up to `timeoutMs` (1–60 000, default
  10 000) for a window of that pid — any title, frontmost first, the strongest evidence — or,
  because packaged apps and browsers hand off to another process, a window that was **not** in
  the inventory before the launch whose title matches the app exact → substring → fuzzy. A
  timeout is `WindowDetected: false` with the pid, not an error (`LaunchWait`, pure and
  unit-tested with a fake inventory). The result is JSON: `{MatchedName, Kind, Score, Pid,
  Hwnd, Title, WindowDetected, Strategy}`; the old `"launched (pid=N)"` string is gone.

## Changes

- `Abstractions`: `AppEntry`, `AppMatch`, `LaunchResult`; `IAppCatalogService`;
  `IWindowService.LaunchAsync(name, waitForWindow, timeoutMs)` (the single-argument overload
  stays).
- `Services/AppCatalog.cs`, `AppCatalogService.cs`, `LaunchWait.cs`, `AppActivator.cs`
  (`IAppActivator`), `Win32AppActivator.cs` (new); `Services/WindowService.cs` (the catalog and
  activator seams, `LaunchAsync`); `Hosting/WindowsMcpHost.cs` (the 39th registration).
- `Tools/WindowTools.cs` — `launch(app_name, wait_for_window, timeout_ms)` and its description.

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | Merge: order, the shortcut-wins and first-wins rules, case-insensitive dedupe, a packaged app nothing shadows, empty sources | `AppCatalogTests` (Merge, 6) | Unit |
| R2 | Match: exact ignoring case and returning the whole entry; exact over prefix; prefix (`calc`), shortest prefix wins, prefix before fuzzy; the fuzzy table (`edge`, `code`, `vs code` 73, `visual code`, `terminal`); highest score wins; ties to the shortest; the floor at exactly 70; a miss names the request and exactly five nearest with scores; empty catalog; blank name | `AppCatalogTests` (Match, 13 methods) | Unit |
| R3 | Service: both sources merged; two lists inside the TTL read once; exactly five minutes still fresh, a millisecond more refreshes; a resolve hit reads nothing; a miss refreshes once and finds the new app, never twice; blank name refused before any scan; cancellation; empty sources; a throwing source skipped | `AppCatalogServiceTests` (14) | Unit |
| R4 | Real sources: Calculator and Notepad packaged with AUMIDs, Microsoft Edge a shortcut whose `.lnk` exists, both roots and subfolders scanned, unique ordered names, the second list served from the cache; `edge` and `visual studio code`/`vs code` resolve; a real miss lists the nearest | `AppCatalogServiceIntegrationTests` (13) | Integration |
| R5 | The wait: pid wins, any title, frontmost first, over a title match; the title fallback only for a new window, exact → substring → fuzzy, below the floor null; polls immediately, returns on the third poll, gives up at the timeout without throwing, honours cancellation | `LaunchWaitTests` (18) | Unit |
| R6 | The service on a recording activator: a path and an explicit `.exe` on `PATH` short-circuit with `path`; a bare name goes to the catalog once; packaged → AUMID, shortcut → the `.lnk`; the matcher's verdict passed through; a miss propagates and starts nothing; no wait → no window immediately; blank name and the timeout range refused; cancellation; the old overload kept | `WindowServiceLaunchTests` (16), `WindowServiceLaunchWaitTests` (2, the real inventory) | Unit / Integration |
| R7 | Tool: every field serialised, a timeout reported as data, the old string gone, defaults, the flag and timeout forwarded, a path passed through, blank and out-of-range refused, the description; the schema over HTTP; the singleton registered and handed to the service | `WindowToolsLaunchTests` (17), `HttpTransportTests` (1), `WindowsMcpHostTests` (2) | Unit / Integration |
| R8 | `launch("calc")` opens Calculator and returns an Hwnd `window list` shows; `launch("notepad")` opens a Notepad window; a miss lists the nearest — the windows closed and the tab state swept afterwards | `LaunchDesktopTests` (3) | UIAutomation |

## Deviations and follow-ups

- **`launch("edge")` and `launch("visual studio code")`** resolve to the right entries in the
  Integration tests but are not opened on the desktop (too heavy for a test); the done-when
  bar's launches are Calculator and Notepad.
- **`Get-StartApps` lists a few more names** than the two sources (desktop apps registered
  with an AUMID but no shortcut); none was found on this box, and the `shell:AppsFolder`
  enumeration stays the fallback if one turns up.
- **A packaged app that is already running** is activated again, which Windows turns into
  "bring the existing window forward"; the pid returned is that app's, so the wait finds it.
- The catalog scan blocks on the WinRT async call (`AsTask().GetAwaiter().GetResult()`, the
  `CLAUDE.md` rule) inside the cache gate; the first `launch` after start pays ~1 s.
