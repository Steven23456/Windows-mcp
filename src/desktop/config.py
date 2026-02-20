from typing import Set

AVOIDED_APPS: Set[str] = set(["Recording toolbar"])

EXCLUDED_APPS: Set[str] = set(["Program Manager", "Taskbar"]).union(AVOIDED_APPS)

# Command security - block dangerous commands from Powershell-Tool
BLOCKED_COMMANDS: set[str] = {
    "format",
    "shutdown",
    "restart",
    "regedit",
    "del",
    "rmdir",
    "rm",
    "takeown",
    "icacls",
    "net",
    "netsh",
}

# Block command chaining (;) and backtick injection (`). Allow & and | (essential PS operators).
BLOCKED_OPERATORS: list[str] = [";", "`"]

MAX_COMMAND_LENGTH = 2000
