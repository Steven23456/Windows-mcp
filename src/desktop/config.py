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

# Block dangerous arguments that enable code injection or privilege escalation.
# Matched case-insensitively against each whitespace-separated token.
BLOCKED_ARGUMENTS: set[str] = {
    "-enc",
    "-encodedcommand",
    "-e",
    "-command",
    "--exec",
    "--interactive",
    "-i",
    "--login",
    "--system",
}

MAX_COMMAND_LENGTH = 2000

# Restrict workingDir to these paths (and their subdirectories).
# Set to None to disable restriction.
import os

ALLOWED_PATHS: list[str] = [
    os.path.expanduser("~"),
    os.getcwd(),
]
