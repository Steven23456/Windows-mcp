# Windows-mcp

An MCP server for Windows desktop automation, written in C# on the official
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
SDK. **64 tools** across input, screen, window, UI automation, process/shell,
file, disk, system, security, startup, network, registry, and web categories.

> **History:** Versions 0.x through 0.8.5 were written in Python. v0.2.0 (2026-05-26)
> is a complete C# rewrite — see [CHANGELOG.md](CHANGELOG.md) for the migration
> notes. The Python source tree is preserved in
> `legacy/python-pre-csharp-conversion-archive-2026-05-26.zip`.

## Build

```powershell
git clone https://github.com/danielsimonjr/Windows-mcp.git
cd Windows-mcp
dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true
```

Output: `dist/WindowsMcp.exe` (~66 MB self-contained — bundles the .NET and
ASP.NET Core runtimes; nothing to install on the target machine).

Requires the .NET 10 SDK for building. End users only need Windows 10 1703+
(for per-monitor DPI awareness V2) and System PowerShell (always present on
Windows 7+ at `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`).

## Register with Claude Code (or any MCP host)

Add to your MCP host config (e.g.,
`~/.claude/local-marketplace/mcp-host/.mcp.json`):

```json
{
  "mcpServers": {
    "Windows-mcp": {
      "type": "stdio",
      "command": "C:/path/to/Windows-mcp/dist/WindowsMcp.exe",
      "args": []
    }
  }
}
```

Run `/reload-plugins`. Tools appear as `mcp__Windows-mcp__*`.

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

`WindowsMcp.exe --help` prints the same table. No arguments = stdio, unchanged.

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

64 tools, grouped:

| Category | Tools |
|---|---|
| Input | `click`, `drag`, `hover`, `type`, `key`, `shortcut`, `scroll`, `clipboard` |
| Screen | `screenshot`, `ocr` |
| Window | `window`, `switch_to_window`, `launch`, `focus`, `multi_monitor` |
| UI Automation | `get_state`, `find_element`, `get_element`, `get_text`, `assert_element`, `interact_element`, `get_table`, `wait_for` |
| Process / Shell | `process`, `start_process`, `powershell` (with `background: true` for jobs), `job`, `service`, `scheduled_task`, `event_log` |
| File | `file_search`, `file_manage`, `file_dialog`, `file_read`, `file_write`, `file_info`, `file_hash`, `file_streams`, `archive` |
| Disk | `disk_inspect`, `storage_health` |
| System | `system_info`, `audio`, `notification`, `security_audit`, `reliability`, `driver_list`, `wmi_query`, `env`, `power_action` |
| Security | `verify_signature`, `defender_status`, `cert_store` |
| Startup | `startup_report` |
| Network | `network`, `firewall` |
| Registry | `registry_get`, `registry_set` |
| Web | `scrape`, `http_request` |

## Safety rails

Destructive tools require `confirm: true` as an argument and throw
`ArgumentException` otherwise:

- `file_write`, `file_manage(action="delete")`
- `process(action="kill")`, `service(action="stop"|"restart")`,
  `scheduled_task(action="delete")`
- `registry_set`
- `power_action`
- `firewall(action="add"|"remove")`
- `env(action="set")`

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
for the `dist/` folder.

## Development

```powershell
dotnet build                                       # incremental
dotnet test --filter "Category=Unit"               # fast loop (29 tests, ~1s)
dotnet test --filter "Category=Integration"        # exercises real Windows APIs
dotnet test --filter "Category=UIAutomation"       # launches Notepad fixture
dotnet test                                        # full suite
```

See `docs/superpowers/specs/2026-05-24-windows-mcp-csharp-conversion-design.md`
for the architecture spec and
`docs/superpowers/plans/2026-05-24-windows-mcp-csharp-conversion.md` for the
22-task implementation plan that produced this version.

## License

MIT — see [LICENSE](LICENSE).
