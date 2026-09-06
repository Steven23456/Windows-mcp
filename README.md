# Windows-mcp

An MCP server for Windows desktop automation, written in C# on the official
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK. See [Tool reference](#tool-reference) for the 69 tools.

## Build

```powershell
git clone https://github.com/Steven23456/Windows-mcp.git
cd Windows-mcp
.\scripts\build-release.ps1
```

Output: one file, `bundle/WindowsMcp.exe` (~77 MB self-contained — bundles the .NET and
ASP.NET Core runtimes plus the native SkiaSharp library; nothing to install on the target
machine). The script runs this publish:

```powershell
dotnet publish src/WindowsMcp -c Release -o bundle -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none
```

`IncludeNativeLibrariesForSelfExtract` is what makes it genuinely one file — without it
`libSkiaSharp.dll` and `aspnetcorev2_inprocess.dll` are left loose next to the exe. One
file survives every publish flag, `libSkiaSharp.pdb` (a native asset of the SkiaSharp
package); the script deletes it afterwards. `bundle/` is gitignored — binaries are never
committed.

Requires the .NET 10 SDK for building. End users only need Windows 10 1703+
(for per-monitor DPI awareness V2) and System PowerShell (always present on
Windows 7+ at `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`).

## Register with Claude Code (or any MCP host)

Point your MCP host at the published exe. With Claude Code:

```powershell
claude mcp add --transport stdio Windows-mcp -- C:\path\to\Windows-mcp\bundle\WindowsMcp.exe
```

or in a `.mcp.json`:

The `env` block is belt-and-braces: `Hosting/EnvironmentRepair` already repairs a host-stripped
`PATHEXT` at startup, so setting it here is no longer required.

```json
"Windows-mcp": {
  "type": "stdio",
  "command": "C:/Development/Windows-mcp/bundle/WindowsMcp.exe",
  "args": [],
  "env": {
    "PATHEXT": ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL"
  }
}
```

Reconnect (`/mcp`) or start a new session. Tools appear as `mcp__Windows-mcp__*`.

The `.mcp.json` at the repo root is the **plugin** manifest: its
`${CLAUDE_PLUGIN_ROOT}/bundle/WindowsMcp.exe` path only resolves when the repo is installed
as a Claude Code plugin **and** a locally built `bundle/WindowsMcp.exe` is present (`bundle/`
is gitignored, so a fresh clone has none). Opening this repo as a plain project shows that
server as disconnected. Register the exe explicitly as above instead.

## Run over HTTP/HTTPS (remote)

The same exe can listen on a TCP port instead of stdin/stdout — for example inside
an RDP session on a remote Windows box, driven by Claude Code on your own machine:

```powershell
# On the remote machine, inside the interactive (RDP) session:
$env:WINDOWSMCP_API_KEY = "<a long random secret>"
.\WindowsMcp.exe --transport http --port 8765 --cert-thumbprint <thumbprint>
```

| Option | Env fallback | Default | Meaning |
|---|---|---|---|
| `--transport stdio\|http` | `WINDOWSMCP_TRANSPORT` | `stdio` | `http` = Streamable HTTP at `/mcp` |
| `--port <n>` | `WINDOWSMCP_PORT` | `8765` | TCP port |
| `--bind <ip>` | `WINDOWSMCP_BIND` | `0.0.0.0` | Listen address; `127.0.0.1` = this machine only |
| `--cert-thumbprint <hex>` | `WINDOWSMCP_CERT_THUMBPRINT` | — | Certificate in `LocalMachine\My` or `CurrentUser\My`; makes the port **HTTPS only** |
| `--api-key <key>` | `WINDOWSMCP_API_KEY` | — | Bearer token (≥ 16 printable ASCII chars). **Required** unless `--bind` is loopback |
| `--screenshot-scale <0.1-1.0>` | `WINDOWSMCP_SCREENSHOT_SCALE` | `1.0` | Multiplies every `screenshot` call's own `scale`; also applies to **stdio** |
| `--max-tree-elements <n>` | `WINDOWSMCP_MAX_TREE_ELEMENTS` | `500` | Element budget for `snapshot`/`get_state` when a call names none; also applies to **stdio** |
| `--flash <on\|off>` | `WINDOWSMCP_FLASH` | `on` | Orange glow around the captured area for ~3.5 s after every `screenshot` — the signal to a person at the machine; also applies to **stdio** |
| `--profile-snapshot <on\|off>` | `WINDOWSMCP_PROFILE_SNAPSHOT` | `off` | Per-stage timings on `snapshot`/`screenshot` results, also logged to stderr; also applies to **stdio** |
| `--screenshot-backend <auto\|gdi\|wgc>` | `WINDOWSMCP_SCREENSHOT_BACKEND` | `auto` | Which backend reads the screen when a `screenshot` call says `auto`: `wgc` = Windows.Graphics.Capture, `gdi` = the classic screen copy; also applies to **stdio** |

`WindowsMcp.exe --help` prints the same options (it lists `--screenshot-scale`,
`--max-tree-elements`, `--flash`, `--profile-snapshot` and `--screenshot-backend` under a
"Capture options (both transports)" heading, since none of them is HTTP-only). No arguments =
stdio, unchanged.

**Security model.** Every tool — `powershell`, `file_write`, `registry_set`,
`process kill`, … — is reachable on that port. So the server refuses to start on a
non-loopback address without an API key, and warns when it serves plain HTTP
off-loopback (the key and all tool traffic would cross the network in the clear).
Use HTTPS, and put the port behind a VPN or a firewall rule scoped to your client's
IP. The bearer check is constant-time; there is no rate limiting, so keep the key long.

**Setup on the remote machine**

1. **Certificate** (for HTTPS). A self-signed one is fine for a private box — create
   it in the *current user* store so its private key is readable without elevation:
   ```powershell
   $cert = New-SelfSignedCertificate -DnsName "<remote-host-name>" -CertStoreLocation Cert:\CurrentUser\My
   $cert.Thumbprint                                            # → --cert-thumbprint
   Export-Certificate -Cert $cert -FilePath windows-mcp.cer    # public part, for the client
   certutil -encode windows-mcp.cer windows-mcp.pem            # Node wants PEM
   ```
   A `LocalMachine\My` certificate made from an elevated prompt has a key ACL of
   SYSTEM + Administrators; the server's startup error says so if it can't open the key.
2. **Firewall**:
   `New-NetFirewallRule -DisplayName "Windows-mcp" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow`
   (add `-RemoteAddress <client-ip>` to scope it).
3. **Session**. The input, screenshot, window and UI-automation tools need the
   **interactive desktop**, so run the exe inside the logged-in RDP session — not as a
   service or a Session-0 scheduled task. Disconnecting RDP keeps the session alive but
   can leave it without a rendered desktop (black screenshots, failed input): stay
   connected, or hand the session to the physical console first with
   `tscon $env:SESSIONNAME /dest:console` (elevated).

**Client (Claude Code on your machine)**

```powershell
$env:NODE_EXTRA_CA_CERTS = "C:\path\to\windows-mcp.pem"   # trust the self-signed cert
claude mcp add --transport http windows-mcp-remote https://<remote-host-name>:8765/mcp `
    --header "Authorization: Bearer <the api key>"
```

or in `.mcp.json`:

```json
{
  "mcpServers": {
    "windows-mcp-remote": {
      "type": "http",
      "url": "https://<remote-host-name>:8765/mcp",
      "headers": { "Authorization": "Bearer <the api key>" }
    }
  }
}
```

The host name in the URL must match the certificate's `-DnsName`.

## Companion skill

The plugin also ships a `windows` skill (`windows-mcp:windows`, `/windows`) —
a playbook that steers Claude toward these tools over raw PowerShell, with
composed workflows for common tasks and safety rails for destructive
operations. See [`skills/windows/SKILL.md`](skills/windows/SKILL.md).

## Tool reference

69 tools, grouped:

| Category | Tools |
|---|---|
| Input | `click`, `drag`, `hover`, `type`, `key`, `shortcut`, `scroll`, `wait`, `multi_select`, `multi_edit`, `clipboard` |
| Screen | `screenshot`, `ocr` |
| Window | `window`, `switch_to_window`, `launch`, `focus`, `multi_monitor` |
| UI Automation | `snapshot`, `get_state`, `find_element`, `get_element`, `get_text`, `assert_element`, `interact_element`, `get_table`, `wait_for` |
| Process / Shell | `process`, `process_inspect`, `start_process`, `powershell` (with `background: true` for jobs), `job`, `service`, `scheduled_task`, `event_log` |
| File | `file_search`, `file_manage`, `file_dialog`, `file_read`, `file_write`, `file_info`, `file_hash`, `file_streams`, `archive` |
| Disk | `disk_inspect`, `storage_health` |
| System | `system_info`, `audio`, `notification`, `security_audit`, `reliability`, `driver_list`, `wmi_query`, `env`, `power_action` |
| Security | `verify_signature`, `defender_status`, `cert_store` |
| Startup | `startup_report` |
| Network | `network`, `firewall` |
| Registry | `registry_get`, `registry_set`, `registry_delete` |
| Web | `scrape`, `http_request` |
| Monitoring | `integrity` (file-integrity tripwire), `fs_changes` (NTFS USN journal), `watch` (live directory watch) |

`click`, `type`, `scroll` and `drag` take a target the same way: `x` and `y` in virtual-desktop
pixels, or an `el_N` `element_id` from `snapshot`/`find_element` whose centre they aim at (an
off-screen element, or one with no bounds, is refused with the reason instead of clicking a
meaningless point). Giving both, or half a coordinate pair, is an error. `scroll` with no target
scrolls under the current cursor and `drag`'s origin defaults to it; each response says which of
`point`/`element`/`cursor` was used and where. `click(clicks: 0)` hovers, `scroll(shift_wheel:
true)` holds Shift and uses the vertical wheel for `left`/`right`, and `drag(duration_ms, steps)`
presses, nudges past the system drag threshold, moves through interpolated points and releases —
which is what file managers, canvases and browser drag-and-drop need to see.
`type(text, element_id?, clear?, caret?, press_enter?)` does the whole field edit in one call:
click the target, `clear` (Ctrl+A, Backspace), place the caret (`start`/`end`), type, press
Enter. Newlines are typed as Enter and tabs as Tab; text of 200+ characters with no other control
characters goes through the clipboard as one paste and the previous clipboard text is put back
(the result's `method` says `keys` or `paste`).

`multi_select(targets_json, ctrl?)` and `multi_edit(entries_json)` do a whole batch in one call.
`targets_json` is a JSON array of `{x, y}` points or `{element_id}` objects (a JSON string holding
that array is accepted too); `multi_edit`'s entries add `text` (required) plus optional `clear`
and `press_enter`, and each entry clicks its target then types through the same path as `type`.
Every target is resolved before any input is sent, so an off-screen element refuses the whole
batch with nothing done, and `multi_select` holds Ctrl from before the first click until after the
last and always releases it. Neither is atomic: they stop at the first failure and return
`failedIndex` and `error` alongside the per-entry `results` produced so far.

Name a window by `Title` — matched exact, then substring, then fuzzy (score ≥ 70), so
`switch_to_window("notepad")` finds `Untitled - Notepad` — or by the `Hwnd` from
`window(action:"list")`, which wins over a title. `switch_to_window`/`focus` return
`{Window, MatchStrategy, Score, Restored, Strategy, Success}`: a minimised window is restored
first, then the tool climbs a `SetForegroundWindow` → `AttachThreadInput` → ALT-nudge ladder and
re-reads the foreground window after each rung, so `Success` is observed rather than assumed. A
title that matches nothing is an error listing the open windows.

`launch(app_name, wait_for_window?, timeout_ms?)` takes the name a person would say. A path, or
an executable name that exists, is started outright; anything else is resolved against an
in-process catalog of Start Menu shortcuts and packaged (Store/MSIX) apps — matched exact, then
by prefix, then fuzzy (score ≥ 70) — so `launch("calc")` opens Calculator and `launch("vs code")`
opens Visual Studio Code, with no PowerShell anywhere in the path. It then polls the window
inventory for up to `timeout_ms` (default 10 s) for a window of the launched process, or a new
window whose title matches, and returns `{MatchedName, Kind, Score, Strategy, Pid, Hwnd, Title,
WindowDetected}`. A wait that runs out is `WindowDetected: false` with the pid, not an error; a
name that matches nothing lists the five nearest apps with their scores.

`window(action: "move" | "resize" | "set_bounds", ...)` places a window: `move` needs `x`/`y`,
`resize` needs `width`/`height`, `set_bounds` all four. The target is a `title`/`hwnd` matched the
same way, or the foreground window when neither is given; a minimized or maximized window is
refused naming its state unless `restore_first: true`. The window is never raised or activated,
and the `After` bounds in `{Window, Before, After, MatchStrategy, Score, Restored}` are re-read
from the window rather than echoed back from the request.

`wait(seconds)` pauses in-process (more than 0, at most 60) — use it between an action and the
next `snapshot` instead of a PowerShell sleep. `wait_for(text, …, condition?, use_dom?)` is the
conditional wait: `element_exists` (default), `element_enabled`, `focused_element`, `text_exists`
(the text anywhere in a snapshot of the scope, `use_dom: true` reading a browser page instead of
the window chrome) or `active_window` (the foreground window's title, exact → substring → fuzzy
70+); the aliases `element`, `enabled`, `focused`, `text` and `window` are accepted. It always
returns `{Satisfied, Condition, ElapsedMs, Attempts, Detail, Element?}` — a timeout is
`Satisfied: false` carrying the last `Detail`, not an error and not the string `"null"`.
`timeout_ms` is 0–120000 and `interval_ms` 0–5000.
`start_process(command, args_json?, cwd?, use_shell_execute?)` passes a JSON array of arguments
verbatim (no quoting) and returns `{pid, executable, args, cwd}`. `multi_monitor` reports each
monitor's `WorkArea`, `Orientation` (0/90/180/270), `EffectiveDpi` and `Scale` alongside its
bounds and primary flag.

`process(action: "list")` rows carry `CpuPercent` beside `Pid`, `Name`, `Path` and `MemoryMb` —
two `TotalProcessorTime` readings 250 ms apart, normalised across **all** cores, so a process
saturating one of eight reads `12.5`, as Task Manager shows. `sort_by`
(`memory` default | `cpu` | `name` | `pid`) and `limit` (`0` = all) order and cap that plain
list; both are refused with `includeLineage`, `groupByRoot` or `orphans`, which have their own
shapes and no CPU column. `process(action: "kill", …, graceful?, grace_ms?)` asks first: it posts
`WM_CLOSE` to every visible top-level window of the pid, waits up to `grace_ms` (default 3000,
max 60000) and only then forces the process, so an editor can show its save prompt. A process
with no window is forced at once and says so; `graceful` cannot be combined with `tree`. Kills by
pid or name return `{killed:[{pid, name, graceful, exitedGracefully, forced, waitedMs}]}` (the
tree kill keeps its text count).

Every file tool takes **absolute** paths — `file_read`, `file_write`, `file_manage`'s `src` and
`dst`, and `file_search`'s `root` refuse a relative one naming the parameter, since the server's
working directory is not the caller's. `file_read(path, max_bytes?, encoding?, offset_lines?,
limit_lines?)` returns plain text by default and a JSON window
`{path, totalLines, offset, returned, truncated, content}` when either window parameter is given
(`offset_lines` is 1-based, `limit_lines: 0` runs to the end) — the way to page a large log,
since `max_bytes` bounds the file, not the window. `file_write(…, append?, create_parents?)`
appends instead of replacing and creates a missing parent directory unless `create_parents:
false` refuses it. `file_manage(action, src, dst?, confirm?, overwrite?, recursive?, pattern?,
include_hidden?)`: `copy`/`move` refuse an existing destination unless `overwrite: true` (a
directory is copied as a tree, a cross-volume move is a copy then a delete), `delete` refuses a
non-empty directory unless `recursive: true`, and `list` returns
`[{Path, Name, IsDirectory, Size, Modified, Hidden}]` — `pattern` is a case-insensitive name
glob, `recursive` descends, and hidden or system entries are skipped unless
`include_hidden: true`.

`registry_get(hive, path)` without `value_name` returns the whole key —
`{Path, Values: [{Path, Name, Data, Kind}], SubKeys: [...]}`, an empty path listing the hive
root — instead of the value names joined with commas; with a `value_name` it is unchanged.
`registry_delete(hive, path, value_name?, recursive?, confirm)` removes that one value, or the
key itself when no `value_name` is given: a key with sub-keys also needs `recursive: true`, the
hive root and the profile/OS roots (`Software`, `Software\Microsoft`, `System`,
`SYSTEM\CurrentControlSet`, `Environment`, …) are refused outright, and deleting what is not
there is `existed: false` rather than an error. It returns
`{hive, path, valueName?, deleted, existed, subKeysRemoved?}`.

`notification(title, message, app_id?)` shows the toast in-process through WinRT — no PowerShell
— under an AppUserModelId. The default `Windows-MCP` is registered under
`HKCU\Software\Classes\AppUserModelId` by the server on first use; a packaged app's AUMID (the
`Package_hash!App` form) shows the toast under that app, and any other id must already be
registered or Windows drops the toast. Returns `{shown, appId, registered, note?}`, where a
dropped toast is `shown: false` with the reason in `note`.

Every tool also carries MCP annotations — a title plus `readOnlyHint`, `destructiveHint`,
`idempotentHint` and `openWorldHint` — so a client can auto-approve reads and confirm the
destructive ones without a per-tool allowlist.

## Safety rails

Destructive tools require `confirm: true` as an argument and throw
`ArgumentException` otherwise:

- `file_write`, `file_manage(action="delete")`
- `process(action="kill")`, `service(action="stop"|"restart")`,
  `scheduled_task(action="delete")`
- `registry_set`, `registry_delete`
- `power_action`
- `firewall(action="add"|"remove")`
- `env(action="set")`

Beyond the confirm gate, the destructive file actions ask for the specific permission they need:
a copy or a move over an existing destination needs `overwrite: true`, and deleting a non-empty
directory needs `recursive: true`. Neither used to be asked for — both defaults now refuse rather
than destroy data the caller did not name.

`env(get|list)` redacts values for variables whose name contains
`KEY/TOKEN/SECRET/PASSWORD/AUTH/CREDENTIAL/PRIVATE/PAT` (case-insensitive).
Pass `include_secrets: true` to opt out.

`scrape` and `http_request` reject private IP ranges (RFC1918, link-local,
loopback, IPv6 `fc00::/7` + `fe80::/10`) including via DNS rebinding —
public URLs only by default.

## Performance notes

On first launch, the single-file binary extracts native dependencies
(SkiaSharp, etc.) to `%TEMP%\.net\WindowsMcp\<hash>\`, adding ~3-5 sec
startup. Subsequent launches are warm.

If you hit the 30s Claude Code startup timeout, add a Defender exclusion
for the `bundle/` folder.

## Development

```powershell
dotnet build                                       # incremental
dotnet test --filter "Category=Unit"               # fast loop (mocked, seconds)
dotnet test --filter "Category=Integration"        # exercises real Windows APIs
dotnet test --filter "Category=UIAutomation"       # needs an interactive desktop (Notepad fixture, real input injection)
dotnet test                                        # full suite
```

Architecture docs live in [`docs/architecture/`](docs/architecture/) (OVERVIEW,
ARCHITECTURE, COMPONENTS, DATAFLOW). The feature backlog against the upstream
Python server is [`docs/upstream-parity-checklist.md`](docs/upstream-parity-checklist.md).

## License

MIT — see [LICENSE](LICENSE).
