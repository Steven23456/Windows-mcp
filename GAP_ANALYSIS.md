# windows-cli vs windows-mcp Gap Analysis

Comparison of `@simonb97/server-win-cli` (v1.x) and `windows-mcp` (v0.2.0).

## Feature Comparison

| Feature | windows-cli | windows-mcp | Status |
|---------|------------|-------------|--------|
| Blocked commands | 10 commands | 11 commands | **Done** |
| Injection protection (operators) | `& \| ; \`` blocked | `; \`` blocked (allows `& \|` for PS) | **Done** (smarter) |
| Max command length | 2000 chars | 2000 chars | **Done** |
| Working directory param | Yes | Yes | **Done** |
| Command history | Yes (1000 max) | Yes (100 max) | **Done** |
| Command timeout | 30s | 30s | Already had |
| Multiple shells (ps/cmd/gitbash) | Yes | PowerShell only | Not needed |
| Path restriction (`allowedPaths`) | Yes | No | Not added |
| Blocked arguments (`-enc`, `-e`) | Yes | No | Not added |
| SSH connections | Yes (full CRUD) | No | Out of scope |
| Configurable via config.json | Yes | No (hardcoded) | Not added |
| MCP Resources (cwd, config) | Yes | No | Not added |
| **UI automation** | **No** | **Yes (14 tools)** | Core advantage |
| **Screenshot/vision** | **No** | **Yes** | Core advantage |
| **A11y tree traversal** | **No** | **Yes** | Core advantage |
| **Mouse/keyboard/clipboard** | **No** | **Yes (10 tools)** | Core advantage |
| **SSRF protection** | **No** | **Yes** | Our advantage |
| **Input validation (coords/text)** | **No** | **Yes** | Our advantage |

## Remaining Gaps

### 1. Blocked Arguments (Medium Priority)

windows-cli blocks dangerous PowerShell arguments like `-enc`, `-encodedcommand`, `-e`, `-command`, `--exec`, `--interactive`, `--login`, `--system`. These prevent encoded command injection — a real attack vector where an LLM could be tricked into running base64-encoded malicious commands.

### 2. Path Restriction (Low Priority)

windows-cli restricts `workingDir` to an `allowedPaths` list (defaults to home dir + cwd). Prevents command execution in sensitive directories like `C:\Windows\System32`.

### 3. MCP Resources (Low Priority)

windows-cli exposes current working directory, SSH config, and CLI config as MCP Resources. This lets clients read server state without tool calls.

## Out of Scope

- **Multiple shell support** — windows-mcp is a desktop automation tool, not a CLI tool. PowerShell is sufficient.
- **SSH connections** — Remote execution is outside the scope of desktop UI automation.

## Design Differences

- windows-cli blocks `&` and `|` operators. windows-mcp intentionally allows them because they are essential PowerShell operators (pipelines, background jobs). Only `;` (command chaining) and `` ` `` (backtick escape injection) are blocked.
- windows-cli uses a JSON config file for all settings. windows-mcp uses hardcoded constants in `src/desktop/config.py` for simplicity.
