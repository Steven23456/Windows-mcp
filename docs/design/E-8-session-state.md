# E-8 — Session state: elevation, lock, secure desktop

Checklist item: `docs/upstream-parity-checklist.md` § E-8.

**Status:** implemented 2026-11-13 (see CHANGELOG [Unreleased]; not verified live — no real UAC
prompt, lock or disconnect was exercised: those states were driven only through a fake probe, and
the `UIAutomation` category was not run). The review's changes are folded in below ("What the
review changed"). Gaps logged as parity D-66 and D-68 … D-73; D-67 was fixed by the review.

## Problem

When the workstation is locked, the session is disconnected, or a UAC prompt is up on the secure
desktop, `snapshot` returns an empty or stale tree — or fails outright — and input goes nowhere,
and nothing tells the model why. Elevation is visible only as a by-product of `startup_report` (a
full autorun inventory to read one boolean).

This item **reports**. It never elevates, unlocks, switches desktop or dismisses anything.

## The probe

A new `ISessionService` (registered in `AddWindowsMcp`):

```csharp
Task<SessionState> GetAsync(CancellationToken ct = default);
```

```csharp
public record SessionState(
    bool Elevated,              // the shared elevation check startup_report uses
    string IntegrityLevel,      // Untrusted | Low | Medium | MediumPlus | High | System | Protected | Unknown
    string UserName,            // DOMAIN\user of the server process; "" when it cannot be read
    int SessionId,              // the server process's terminal-services session; -1 when unknown
    bool Interactive,           // the process has an interactive window station
    string ConnectState,        // Active | Connected | Disconnected | … (WTS_CONNECTSTATE_CLASS) | Unknown
    bool? Locked,               // true / false, or null when Windows does not say
    string? InputDesktop,       // the name of the desktop receiving input ("Default" is the user's);
                                // null when it was not read (InputDesktopAccess says why)
    string InputDesktopAccess,  // Opened | Denied (a secure desktop) | Unknown (any other failure)
    int? InputDesktopError,     // the Win32 error OpenInputDesktop failed with; null when it opened
    bool InputAvailable,        // input and UI reads reach the user's desktop
    string? Note);              // one sentence when InputAvailable is false, else null
```

`GetAsync` **never throws** (cancellation aside): each fact is read independently and a read that
fails yields that field's "unknown" value (`Unknown`, `""`, `-1`, `null`; `Elevated` false;
`Interactive` **true** — a failed read is not evidence of a service host, so it must not raise
rule 1's note; `InputDesktopAccess` `Unknown`; `InputDesktopError` null). With every read failing, the state is "available,
no note": unknown is not a reason to tell the model to stop.

### Where each fact comes from

Behind an internal seam, `ISessionProbe`, with one member per raw read (so the mapping is
unit-tested on a fake, and `Win32SessionProbe` is the only caller of the APIs; it declares its own
`DllImport`s):

| fact | source |
|---|---|
| `Elevated` | `ProcessElevation.IsElevated()` — the exact check `StartupReportService` had privately (`WindowsPrincipal.IsInRole(Administrator)`: the process token is a full administrator token), moved to one shared internal helper that both use. `startup_report`'s output does not change. |
| `IntegrityLevel` | `GetTokenInformation(TokenIntegrityLevel)` → the label SID's last sub-authority, mapped by the pure `IntegrityLevels.Name(rid)`: `< 0x1000` Untrusted, `< 0x2000` Low, `< 0x2100` Medium, `< 0x3000` MediumPlus, `< 0x4000` High, `< 0x5000` System, else Protected. |
| `UserName` | `WindowsIdentity.GetCurrent().Name` |
| `SessionId` | the current process's session id |
| `Interactive` | `Environment.UserInteractive` |
| `ConnectState` | `WTSQuerySessionInformation(WTS_CURRENT_SERVER, WTS_CURRENT_SESSION, WTSConnectState)`, the enum member's name without the `WTS` prefix (out of range → `Unknown`) |
| `Locked` | `WTSQuerySessionInformation(…, WTSSessionInfoEx)` → `WTSINFOEX_LEVEL1.SessionFlags`: `WTS_SESSIONSTATE_LOCK` (0) → true, `WTS_SESSIONSTATE_UNLOCK` (1) → false, anything else → null |
| `InputDesktop`, `InputDesktopAccess`, `InputDesktopError` | `ISessionProbe.InputDesktop()` → `(string? Name, int Error)`: `OpenInputDesktop(DESKTOP_READOBJECTS)` + `GetUserObjectInformation(UOI_NAME)`, closed again. It returns the name with `Error` 0 when the input desktop was opened, or a null name with the Win32 error the open failed with; it throws only when the desktop was opened but its name could not be read. The **service** decides: a name → `Opened`; error 5 (`ERROR_ACCESS_DENIED`, what a secure desktop looks like to a process that is not SYSTEM) → `Denied`; any other error, or a throw → `Unknown`. `InputDesktopError` is that error when the open failed, else null. That a secure desktop yields error 5 has not been observed on a live UAC prompt; `InputDesktopError` is what a live check reads (D-72). |

### `InputAvailable` and `Note` — pure (`SessionNotes`)

`SessionNotes.Evaluate(locked, connectState, inputDesktop, inputDesktopAccess, interactive)`,
evaluated in this order; the first that applies decides:

1. `!Interactive` → not available. *"The server has no interactive desktop (it was started as a
   service or a scheduled task without one): UI tools cannot see or drive a desktop."* First,
   because a server in session 0 (a service, Task Scheduler "run whether user is logged on or
   not", OpenSSH) also reports `Disconnected`, and "reconnect" would be advice that can never help.
2. `Locked == true` → not available. *"The workstation is locked: nothing on the user's desktop
   can be seen or driven until the user unlocks it. Stop and tell the user."*
3. `ConnectState == "Disconnected"` → not available. *"The session is disconnected (no display or
   remote-desktop client is attached): nothing can be seen or driven until the user reconnects.
   Stop and tell the user."*
4. The input desktop: `Denied`, or `Opened` with the name `Winlogon` (ignoring case) → *"A secure
   desktop has the input (a UAC prompt, or the sign-in or lock screen): the user's desktop cannot
   be seen or driven until the user answers it. Stop and tell the user."* `Opened` with any other
   name that is not `Default` (e.g. `Screen-saver`) → *"The '<name>' desktop has the input, not the
   user's desktop: the user's desktop cannot be seen or driven until the user returns to it. Stop
   and tell the user."* — or *"An unnamed desktop has the input…"* when the cleaned name is
   empty. `Unknown` → no note.
5. Otherwise available, `Note` null. An `Unknown` connect state, a null `Locked` or an `Unknown`
   input desktop alone does not make input unavailable.

**A desktop name cannot add lines.** Any process can create a desktop with a name of its choosing,
so in a note every control character and U+0085, U+2028 and U+2029 become spaces, runs of spaces
collapse, and the name is cut at 64 characters (`...` appended).

## Surfaces

### `system_info(category:"session")`

Returns the `SessionState` as JSON (PascalCase, as the record); `session` matches in any case. The
other categories are unchanged; the unknown-category refusal lists `session` too, and a null
category is refused. No WMI query is made for `session`. The description says `Elevated` means the
process token is in the Administrators role (a full administrator token), and that
`IntegrityLevel` is what decides whether windows of elevated programs can be driven (Windows drops
a Medium server's input to a High window). It states the limits: a UAC prompt configured not to
use the secure desktop, an elevated foreground window and a minimised remote-desktop client are
not detected (input is dropped or the picture is stale with `InputAvailable` still true), and it
describes `InputDesktopError` (5 is the access-denied refusal; null otherwise).

### `snapshot` — and everything that renders one

`SnapshotResult` gains `string? SessionNote` (omitted from JSON when null) and `bool Unavailable`
(default false, omitted from JSON when false).
`UIAutomationService.SnapshotAsync` wraps the walk (`SnapshotCoreAsync`):

- The snapshot is attempted exactly as before. If it **succeeds**, the session is asked once and
  its `Note` attached — null in the normal case. A snapshot that succeeds is never replaced, so a
  mistaken note can never hide a working desktop.
- If it **throws** anything but the caller's cancellation or an `ArgumentException` (a bad call is
  a bad call whatever the session), the session is asked once. When it reports
  `InputAvailable:false` with a note, the call **returns an empty snapshot carrying the note** — no
  windows, no active window, cursor `(0,0)` on no display (`CursorMonitorIndex` −1), no elements,
  not truncated — with **`Unavailable = true`**, so a caller can tell "could not be taken" from
  "empty" (a snapshot that succeeded and merely carries a note is not `Unavailable`). Otherwise (input available, no probe, or the probe faults) the original exception
  propagates unchanged.
- A fault in the probe never fails a snapshot (`SessionStateAsync` catches it, logs at Debug and
  answers null).

The wrapper exists because on a locked or secure desktop `GetCursorPos` is refused (the input
desktop is not the caller's), and the cursor read is the first thing a snapshot does: without it
the snapshot threw a bare cursor error before the note was ever asked for. That reasoning is from
`GetCursorPos`'s documented behaviour; it was not run on a locked desktop.

`SnapshotRenderer.Render` prints the note as the **first line**, `Warning: <note>`, so the model
reads why the lists below are empty or stale before it reads them. Because the renderer is shared,
the same line opens an E-5 observation's snapshot (under its header) and an annotated
screenshot's element list. (`screenshot(annotate:true)` reads the cursor itself before its
snapshot, so on a locked desktop it still fails with that bare error — D-66.)

Two callers interpret a snapshot and act on `Unavailable`:

- `wait_for` (`text_exists`, `focused_element`): a poll whose snapshot is `Unavailable` **ends the
  wait at once** with `Satisfied:false` and the session note as `Detail` — not the full timeout
  followed by "not found anywhere on screen", which would claim the screen was read. Its
  description says the wait ends at once when the desktop cannot be read. (The element and
  `active_window` conditions do not take a snapshot and are unchanged — D-66.)
- `scrape(source:"dom")`: an `Unavailable` snapshot is an `InvalidOperationException` carrying
  the note, not "is not a browser window".

Any other caller that reads the lists has to check the flag itself (D-73).

The `snapshot` description names every reason for a `Warning:` line (no interactive desktop,
locked, disconnected, a secure desktop) and says the lists are empty, with `Unavailable:true` in
JSON, when the snapshot could not be taken at all.

`UIAutomationService` takes the service as an optional constructor parameter (as
`ScreenshotService` takes the indicator), so existing constructions and tests are unaffected and
get no note.

## What the review changed, and why

The first version attached the note only to a snapshot that succeeded, read the input desktop as
two-state (name or null, with any failure counted as a secure desktop), checked "locked" before
"no interactive desktop", and printed a desktop name with only its line breaks removed. The review
found that (1) on a locked or secure desktop the snapshot fails at its cursor read, so the warning
could never arrive — hence the wrapper above; (2) a session-0 server reports `Disconnected` and was
told to reconnect — hence rule 1 first; (3) a read that merely failed raised the secure-desktop
note — hence `InputDesktopAccess` and the tri-state probe contract; (4) a crafted desktop name
could add a line to the snapshot — hence the name hygiene; and (5) the `system_info` description
did not say what `Elevated` and `IntegrityLevel` mean or what goes undetected.

A second review pass found that (A) a caller could not tell the empty snapshot returned for an
unreachable desktop from a real empty desktop, so `wait_for` waited out its timeout and answered
"not found anywhere on screen" and `scrape(source:"dom")` said "not a browser window" — hence
`Unavailable` and the two callers' policies; (B) the secure-desktop signature (error 5) was
assumed and invisible — hence the `(Name, Error)` probe, the service deciding, and
`InputDesktopError` reported so one live check settles it; and (C) the `snapshot` description
omitted the no-interactive-desktop case and the empty-lists meaning. On the way it found an
unrelated `wait_for` bug, fixed separately: the loop could spin through hundreds of polls in its
last millisecond, because `Task.Delay` rounds a sub-millisecond wait to zero; it now stops when
less than 1 ms remains.

Still not changed (logged as D-items): the other tools carry no warning (D-66); the probe runs on
every snapshot with no cache or timeout (D-71); every other snapshot consumer needs its own
`Unavailable` policy (D-73).

## Not in scope

- Refusing or delaying input tools while input is unavailable (they still act, as today).
- Detecting *which* secure desktop (UAC vs lock vs Ctrl+Alt+Del) — a non-SYSTEM process cannot
  open it to ask; `Locked` distinguishes the lock case when Windows reports it.
- Any elevation, unlock or desktop switch.
