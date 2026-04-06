import os
from contextlib import asynccontextmanager
from fastmcp.utilities.types import Image as MCPImage
from humancursor import SystemCursor
from platform import system, release
from markdownify import markdownify
from src.desktop.config import (
    ALLOWED_PATHS,
    BLOCKED_ARGUMENTS,
    BLOCKED_COMMANDS,
    BLOCKED_OPERATORS,
    MAX_COMMAND_LENGTH,
)
from src.desktop import Desktop
from urllib.parse import urlparse
from datetime import datetime
from fastmcp import FastMCP
from textwrap import dedent
from typing import Literal
import uiautomation as ua
import pyautogui as pg
import pyperclip as pc
import ipaddress
import requests
import socket
import time
from PIL import Image, ImageChops
import subprocess
import comtypes


def ensure_com():
    """Initialize COM on the current thread if not already initialized."""
    try:
        comtypes.CoInitialize()
    except OSError:
        pass  # Already initialized


# PyAutoGUI safety: FAILSAFE=True allows aborting by moving mouse to corner
# Set to False only if corner-abort causes issues with your automation
pg.FAILSAFE = True
pg.PAUSE = 1.0

# Security: Block internal/private network URLs to prevent SSRF attacks
BLOCKED_IP_RANGES = [
    ipaddress.ip_network("10.0.0.0/8"),
    ipaddress.ip_network("172.16.0.0/12"),
    ipaddress.ip_network("192.168.0.0/16"),
    ipaddress.ip_network("127.0.0.0/8"),
    ipaddress.ip_network("169.254.0.0/16"),  # Link-local
    ipaddress.ip_network("::1/128"),  # IPv6 localhost
    ipaddress.ip_network("fc00::/7"),  # IPv6 private
    ipaddress.ip_network("fe80::/10"),  # IPv6 link-local
]


def is_url_safe(url: str) -> tuple[bool, str]:
    """Validate URL to prevent SSRF attacks on internal networks."""
    try:
        parsed = urlparse(url)
        if parsed.scheme not in ("http", "https"):
            return False, f"Invalid scheme: {parsed.scheme}. Only http/https allowed."

        hostname = parsed.hostname
        if not hostname:
            return False, "No hostname provided."

        # Resolve hostname to IP
        try:
            ip = ipaddress.ip_address(socket.gethostbyname(hostname))
        except socket.gaierror:
            return False, f"Could not resolve hostname: {hostname}"

        # Check against blocked ranges
        for blocked_range in BLOCKED_IP_RANGES:
            if ip in blocked_range:
                return False, f"Access to internal/private networks is blocked: {ip}"

        return True, "OK"
    except Exception as e:
        return False, f"URL validation error: {str(e)}"


# Input validation constants
MAX_SCREEN_COORD = 10000  # Reasonable max for multi-monitor setups
MAX_TEXT_LENGTH = 10000  # Limit text input length
MAX_WAIT_DURATION = 300  # 5 minutes max wait
MAX_WHEEL_TIMES = 100  # Reasonable scroll limit
MAX_CLICKS = 3  # Triple-click max


def validate_coordinates(
    loc: tuple[int, int], param_name: str = "loc"
) -> tuple[bool, str]:
    """Validate screen coordinates are within reasonable bounds."""
    if not isinstance(loc, (tuple, list)) or len(loc) != 2:
        return False, f"{param_name} must be a tuple of (x, y) coordinates."
    x, y = loc
    if not isinstance(x, int) or not isinstance(y, int):
        return False, f"{param_name} coordinates must be integers."
    if x < 0 or y < 0:
        return False, f"{param_name} coordinates cannot be negative."
    if x > MAX_SCREEN_COORD or y > MAX_SCREEN_COORD:
        return False, f"{param_name} coordinates exceed maximum ({MAX_SCREEN_COORD})."
    return True, "OK"


def validate_text(text: str, max_length: int = MAX_TEXT_LENGTH) -> tuple[bool, str]:
    """Validate text input length."""
    if not isinstance(text, str):
        return False, "Text must be a string."
    if len(text) > max_length:
        return False, f"Text exceeds maximum length ({max_length} chars)."
    return True, "OK"


def validate_command(command: str, *, trusted: bool = False) -> tuple[bool, str]:
    """Validate a PowerShell command against security rules.

    Args:
        command: The PowerShell command string to validate.
        trusted: If True, skip operator checks (`;`, `` ` ``). Use for
                 server-generated scripts where semicolons and backticks are
                 legitimate PS syntax. User-supplied values in these scripts
                 must be pre-validated with validate_text() / _sanitize_path().
    """
    if len(command) > MAX_COMMAND_LENGTH:
        return False, f"Command exceeds maximum length ({MAX_COMMAND_LENGTH} chars)."

    # Check for blocked operators (injection protection) — only for user input
    if not trusted:
        # Backticks are always blocked (escape injection: `n, `0, etc.)
        if "`" in command:
            return False, "Command contains blocked operator: '`'"
        # Semicolons: only block at top-level (brace depth 0) where they act as
        # statement separators (command chaining). Inside {} they are legitimate
        # PowerShell hashtable/scriptblock syntax (e.g., @{N='x';E={$_.Val}}).
        depth = 0
        for ch in command:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth = max(0, depth - 1)
            elif ch == ";" and depth == 0:
                return False, "Command contains blocked operator: ';'"

    # Extract first token and check against blocked commands
    first_token = command.strip().split()[0].lower() if command.strip() else ""
    # Strip path and extension (e.g., C:\Windows\system32\format.exe → format)
    first_token = first_token.replace("/", "\\").rsplit("\\", 1)[-1]
    first_token = (
        first_token.removesuffix(".exe").removesuffix(".cmd").removesuffix(".bat")
    )

    if first_token in BLOCKED_COMMANDS:
        return False, f"Blocked command: {first_token}"

    # Check for blocked arguments (e.g., -enc, -encodedcommand)
    tokens = command.strip().split()
    for token in tokens[1:]:
        if token.lower() in BLOCKED_ARGUMENTS:
            return False, f"Blocked argument: {token}"

    return True, "OK"


os_name = system()
os_version = release()

instructions = dedent(f"""
Windows MCP server provides tools to interact directly with the {os_name} {os_version} desktop,
thus enabling to operate the desktop like an actual USER.
""")


@asynccontextmanager
async def lifespan(app: FastMCP):
    """Runs initialization code before the server starts and cleanup code after it shuts down."""
    # Server is ready immediately - no artificial delay needed
    yield


desktop = Desktop()
cursor = SystemCursor()
command_history: list[dict] = []
MAX_HISTORY_SIZE = 100
mcp = FastMCP(name="windows-mcp", instructions=instructions, lifespan=lifespan)


# ── MCP Resources ──────────────────────────────────────────────────────────


@mcp.resource("windows-mcp://current-directory")
def get_current_directory() -> str:
    """The server's current working directory."""
    return os.getcwd()


@mcp.resource("windows-mcp://security-config")
def get_security_config() -> str:
    """Active security configuration for command validation."""
    return (
        f"Blocked commands: {', '.join(sorted(BLOCKED_COMMANDS))}\n"
        f"Blocked arguments: {', '.join(sorted(BLOCKED_ARGUMENTS))}\n"
        f"Blocked operators: {BLOCKED_OPERATORS}\n"
        f"Max command length: {MAX_COMMAND_LENGTH}\n"
        f"Allowed paths: {ALLOWED_PATHS}"
    )


# ── MCP Tools ──────────────────────────────────────────────────────────────


@mcp.tool(
    name="Launch-Tool",
    description='Launch an application from the Windows Start Menu by name (e.g., "notepad", "calculator", "chrome")',
)
def launch_tool(name: str) -> str:
    ensure_com()
    result, status = desktop.launch_app(name)
    if status != 0:
        return f"Failed: {result}"
    return f"Launched {name.title()}."


@mcp.tool(
    name="Start-Process-Tool",
    description='Launch a detached process that runs independently (survives after MCP server restarts). Returns the PID for tracking. Set gui=True for scripts that create windows (WPF, tkinter, GUI apps). Examples: executable="python" args=["script.py"], executable="powershell" args=["-File", "app.ps1"] gui=True.',
)
def start_process_tool(
    executable: str,
    args: list[str] = None,
    workingDir: str = None,
    gui: bool = False,
) -> str:
    ensure_com()
    # Validate executable against blocked commands
    exe_name = os.path.basename(executable).lower()
    exe_name = exe_name.removesuffix(".exe").removesuffix(".cmd").removesuffix(".bat")
    if exe_name in BLOCKED_COMMANDS:
        return f"Security Error: Blocked executable: {exe_name}"

    # Validate args against blocked arguments
    if args:
        for arg in args:
            if arg.lower() in BLOCKED_ARGUMENTS:
                return f"Security Error: Blocked argument: {arg}"

    # Validate working directory against allowed paths
    if workingDir and ALLOWED_PATHS:
        resolved = os.path.normpath(os.path.abspath(workingDir)).lower()
        if not any(
            resolved.startswith(os.path.normpath(p).lower()) for p in ALLOWED_PATHS
        ):
            return f"Security Error: Working directory '{workingDir}' is outside allowed paths."

    cmd = [executable] + (args or [])

    try:
        if gui:
            # GUI mode: CREATE_NEW_PROCESS_GROUP only — keeps window station
            # intact so ShowDialog/WPF Dispatcher can render windows properly.
            # CREATE_NO_WINDOW hides the console but preserves the message pump.
            creation_flags = (
                subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NO_WINDOW
            )
            proc = subprocess.Popen(
                cmd,
                cwd=workingDir,
                creationflags=creation_flags,
                stdin=subprocess.DEVNULL,
            )
        else:
            # Background mode: fully detached, no console, no stdio
            creation_flags = (
                subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS
            )
            proc = subprocess.Popen(
                cmd,
                cwd=workingDir,
                creationflags=creation_flags,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                stdin=subprocess.DEVNULL,
            )
        mode = "GUI" if gui else "detached"
        return f"Started {mode} process: {executable} (PID: {proc.pid})"
    except FileNotFoundError:
        return f"Error: Executable not found: {executable}"
    except Exception as e:
        return f"Error starting process: {e}"


@mcp.tool(
    name="Powershell-Tool",
    description="Execute PowerShell commands and return the output with status code",
)
def powershell_tool(command: str, workingDir: str = None) -> str:
    ensure_com()
    # Validate command against security rules
    valid, msg = validate_command(command)
    if not valid:
        return f"Security Error: {msg}"

    # Validate working directory against allowed paths
    if workingDir and ALLOWED_PATHS:
        resolved = os.path.normpath(os.path.abspath(workingDir)).lower()
        if not any(
            resolved.startswith(os.path.normpath(p).lower()) for p in ALLOWED_PATHS
        ):
            return f"Security Error: Working directory '{workingDir}' is outside allowed paths."

    response, status = desktop.execute_command(command, working_dir=workingDir)

    # Record in history
    command_history.append(
        {
            "command": command,
            "workingDir": workingDir,
            "timestamp": datetime.now().isoformat(),
            "exitCode": status,
            "output": response[:500],
        }
    )
    if len(command_history) > MAX_HISTORY_SIZE:
        command_history.pop(0)

    return f"Status Code: {status}\nResponse: {response}"


@mcp.tool(
    name="Command-History-Tool",
    description="Get the history of executed PowerShell commands with timestamps, exit codes, and truncated output.",
)
def command_history_tool(limit: int = 10) -> str:
    ensure_com()
    if limit < 1:
        limit = 1
    if limit > MAX_HISTORY_SIZE:
        limit = MAX_HISTORY_SIZE
    entries = command_history[-limit:]
    if not entries:
        return "No command history."
    lines = []
    for i, entry in enumerate(entries, 1):
        lines.append(
            f"{i}. [{entry['timestamp']}] (exit {entry['exitCode']}) {entry['command']}"
        )
        if entry.get("workingDir"):
            lines.append(f"   cwd: {entry['workingDir']}")
    return "\n".join(lines)


@mcp.tool(
    name="State-Tool",
    description="Capture comprehensive desktop state including focused/opened applications, interactive UI elements (buttons, text fields, menus), informative content (text, labels, status), and scrollable areas. Optionally includes visual screenshot when use_vision=True. Essential for understanding current desktop context and available UI interactions.",
)
def state_tool(use_vision: bool = False) -> str | list:
    ensure_com()
    desktop_state = desktop.get_state(use_vision=use_vision)
    interactive_elements = desktop_state.tree_state.interactive_elements_to_string()
    informative_elements = desktop_state.tree_state.informative_elements_to_string()
    scrollable_elements = desktop_state.tree_state.scrollable_elements_to_string()
    apps = desktop_state.apps_to_string()
    active_app = desktop_state.active_app_to_string()
    state_text = dedent(f"""
    Focused App:
    {active_app}

    Opened Apps:
    {apps}

    List of Interactive Elements:
    {interactive_elements or "No interactive elements found."}

    List of Informative Elements:
    {informative_elements or "No informative elements found."}

    List of Scrollable Elements:
    {scrollable_elements or "No scrollable elements found."}
    """)
    if use_vision:
        return [state_text, MCPImage(data=desktop_state.screenshot, format="png")]
    return state_text


@mcp.tool(
    name="Clipboard-Tool",
    description='Copy text to clipboard or retrieve current clipboard content. Use "copy" mode with text parameter to copy, "paste" mode to retrieve.',
)
def clipboard_tool(mode: Literal["copy", "paste"], text: str = None) -> str:
    if mode == "copy":
        if text:
            pc.copy(text)  # Copy text to system clipboard
            return f'Copied "{text}" to clipboard'
        else:
            raise ValueError("No text provided to copy")
    elif mode == "paste":
        clipboard_content = pc.paste()  # Get text from system clipboard
        return f'Clipboard Content: "{clipboard_content}"'
    else:
        raise ValueError('Invalid mode. Use "copy" or "paste".')


@mcp.tool(
    name="Click-Tool",
    description="Click on UI elements at specific coordinates. Supports left/right/middle mouse buttons and single/double/triple clicks. Use coordinates from State-Tool output.",
)
def click_tool(
    loc: tuple[int, int],
    button: Literal["left", "right", "middle"] = "left",
    clicks: int = 1,
) -> str:
    ensure_com()
    # Input validation
    valid, msg = validate_coordinates(loc)
    if not valid:
        return f"Validation Error: {msg}"
    if clicks < 1 or clicks > MAX_CLICKS:
        return f"Validation Error: clicks must be between 1 and {MAX_CLICKS}."

    x, y = loc
    cursor.move_to(loc)
    control = desktop.get_element_under_cursor()
    pg.mouseDown()
    try:
        pg.click(button=button, clicks=clicks)
    finally:
        pg.mouseUp()
    num_clicks = {1: "Single", 2: "Double", 3: "Triple"}
    return f"{num_clicks.get(clicks)} {button} Clicked on {control.Name} Element with ControlType {control.ControlTypeName} at ({x},{y})."


@mcp.tool(
    name="Type-Tool",
    description="Type text into input fields, text areas, or focused elements. Set clear=True to replace existing text, False to append. Click on target element coordinates first.",
)
def type_tool(loc: tuple[int, int], text: str, clear: bool = False) -> str:
    ensure_com()
    # Input validation
    valid, msg = validate_coordinates(loc)
    if not valid:
        return f"Validation Error: {msg}"
    valid, msg = validate_text(text)
    if not valid:
        return f"Validation Error: {msg}"

    x, y = loc
    cursor.click_on(loc)
    control = desktop.get_element_under_cursor()
    if clear:
        pg.hotkey("ctrl", "a")
        pg.press("backspace")
    # Use clipboard for Unicode support (typewrite only supports ASCII)
    if text.isascii():
        pg.typewrite(text, interval=0.05)
    else:
        # Save current clipboard, paste text, restore clipboard
        old_clipboard = pc.paste()
        pc.copy(text)
        pg.hotkey("ctrl", "v")
        pg.sleep(0.1)
        pc.copy(old_clipboard if old_clipboard else "")
    return f"Typed {text} on {control.Name} Element with ControlType {control.ControlTypeName} at ({x},{y})."


@mcp.tool(
    name="Switch-Tool",
    description='Switch to a specific application window (e.g., "notepad", "calculator", "chrome", etc.) and bring to foreground.',
)
def switch_tool(name: str) -> str:
    ensure_com()
    result, status = desktop.switch_app(name)
    if status != 0:
        return f"Failed: {result}"
    return result


@mcp.tool(
    name="Scroll-Tool",
    description="Scroll at specific coordinates or current mouse position. Use wheel_times to control scroll amount (1 wheel = ~3-5 lines). Essential for navigating lists, web pages, and long content.",
)
def scroll_tool(
    loc: tuple[int, int] = None,
    type: Literal["horizontal", "vertical"] = "vertical",
    direction: Literal["up", "down", "left", "right"] = "down",
    wheel_times: int = 1,
) -> str:
    ensure_com()
    # Input validation
    if loc:
        valid, msg = validate_coordinates(loc)
        if not valid:
            return f"Validation Error: {msg}"
        cursor.move_to(loc)
    if wheel_times < 1 or wheel_times > MAX_WHEEL_TIMES:
        return f"Validation Error: wheel_times must be between 1 and {MAX_WHEEL_TIMES}."

    match type:
        case "vertical":
            match direction:
                case "up":
                    ua.WheelUp(wheel_times)
                case "down":
                    ua.WheelDown(wheel_times)
                case _:
                    return 'Invalid direction. Use "up" or "down".'
        case "horizontal":
            match direction:
                case "left":
                    pg.keyDown("Shift")
                    pg.sleep(0.05)
                    ua.WheelUp(wheel_times)
                    pg.sleep(0.05)
                    pg.keyUp("Shift")
                case "right":
                    pg.keyDown("Shift")
                    pg.sleep(0.05)
                    ua.WheelDown(wheel_times)
                    pg.sleep(0.05)
                    pg.keyUp("Shift")
                case _:
                    return 'Invalid direction. Use "left" or "right".'
        case _:
            return 'Invalid type. Use "horizontal" or "vertical".'
    return f"Scrolled {type} {direction} by {wheel_times} wheel times."


@mcp.tool(
    name="Drag-Tool",
    description="Drag and drop operation from source coordinates to destination coordinates. Useful for moving files, resizing windows, or drag-and-drop interactions.",
)
def drag_tool(from_loc: tuple[int, int], to_loc: tuple[int, int]) -> str:
    ensure_com()
    # Input validation
    valid, msg = validate_coordinates(from_loc, "from_loc")
    if not valid:
        return f"Validation Error: {msg}"
    valid, msg = validate_coordinates(to_loc, "to_loc")
    if not valid:
        return f"Validation Error: {msg}"

    control = desktop.get_element_under_cursor()
    x1, y1 = from_loc
    x2, y2 = to_loc
    cursor.drag_and_drop(from_loc, to_loc)
    return f"Dragged the {control.Name} element with ControlType {control.ControlTypeName} from ({x1},{y1}) to ({x2},{y2})."


@mcp.tool(
    name="Move-Tool",
    description="Move mouse cursor to specific coordinates without clicking. Useful for hovering over elements or positioning cursor before other actions.",
)
def move_tool(to_loc: tuple[int, int]) -> str:
    # Input validation
    valid, msg = validate_coordinates(to_loc, "to_loc")
    if not valid:
        return f"Validation Error: {msg}"

    x, y = to_loc
    cursor.move_to(to_loc)
    return f"Moved the mouse pointer to ({x},{y})."


@mcp.tool(
    name="Shortcut-Tool",
    description='Execute keyboard shortcuts using key combinations. Pass keys as list (e.g., ["ctrl", "c"] for copy, ["alt", "tab"] for app switching, ["win", "r"] for Run dialog).',
)
def shortcut_tool(shortcut: list[str]) -> str:
    ensure_com()
    if not shortcut:
        return "Validation Error: shortcut must be a non-empty list of key names."
    if len(shortcut) > 10:
        return "Validation Error: shortcut cannot contain more than 10 keys."
    for k in shortcut:
        valid, msg = validate_text(k, max_length=50)
        if not valid:
            return f"Validation Error: {msg}"
    try:
        pg.hotkey(*shortcut)
        return f"Pressed {'+'.join(shortcut)}."
    except Exception as e:
        return f"Error pressing shortcut: {e}"


@mcp.tool(
    name="Key-Tool",
    description='Press individual keyboard keys. Supports special keys like "enter", "escape", "tab", "space", "backspace", "delete", arrow keys ("up", "down", "left", "right"), function keys ("f1"-"f12").',
)
def key_tool(key: str = "") -> str:
    ensure_com()
    if not key:
        return "Validation Error: key must be a non-empty string."
    valid, msg = validate_text(key, max_length=50)
    if not valid:
        return f"Validation Error: {msg}"
    try:
        pg.press(key)
        return f"Pressed the key {key}."
    except Exception as e:
        return f"Error pressing key: {e}"


@mcp.tool(
    name="Wait-Tool",
    description="Pause execution for specified duration in seconds. Useful for waiting for applications to load, animations to complete, or adding delays between actions.",
)
def wait_tool(duration: int) -> str:
    # Input validation
    if duration < 0:
        return "Validation Error: duration cannot be negative."
    if duration > MAX_WAIT_DURATION:
        return (
            f"Validation Error: duration exceeds maximum ({MAX_WAIT_DURATION} seconds)."
        )

    pg.sleep(duration)
    return f"Waited for {duration} seconds."


@mcp.tool(
    name="Scrape-Tool",
    description="Fetch and convert webpage content to markdown format. Provide full URL including protocol (http/https). Returns structured text content suitable for analysis.",
)
def scrape_tool(url: str) -> str:
    # Security: Validate URL to prevent SSRF attacks
    is_safe, message = is_url_safe(url)
    if not is_safe:
        return f"Security Error: {message}"

    response = requests.get(url, timeout=10)
    # Limit response size to 1MB to prevent memory issues
    MAX_RESPONSE_SIZE = 1_000_000
    html = response.text[:MAX_RESPONSE_SIZE]
    content = markdownify(html=html)
    truncated = " (truncated)" if len(response.text) > MAX_RESPONSE_SIZE else ""
    return f"Scraped the contents of the entire webpage{truncated}:\n{content}"


@mcp.tool(
    name="Screenshot-Tool",
    description="Take a screenshot of the entire screen or a specific region. Much faster than State-Tool when you only need a visual check. Optionally specify a region as (x, y, width, height) to capture a portion of the screen.",
)
def screenshot_tool(region: tuple[int, int, int, int] = None):
    ensure_com()
    screenshot = desktop.get_screenshot()
    if region:
        x, y, w, h = region
        # Validate region bounds
        for val, name in [(x, "x"), (y, "y"), (w, "width"), (h, "height")]:
            if not isinstance(val, int) or val < 0:
                return f"Validation Error: {name} must be a non-negative integer."
            if val > MAX_SCREEN_COORD:
                return f"Validation Error: {name} exceeds maximum ({MAX_SCREEN_COORD})."
        if w == 0 or h == 0:
            return "Validation Error: width and height must be greater than 0."
        screenshot = screenshot.crop((x, y, x + w, y + h))
    image_bytes = desktop.screenshot_in_bytes(screenshot=screenshot)
    return MCPImage(data=image_bytes, format="png")


@mcp.tool(
    name="Window-Tool",
    description='Manage application windows: minimize, maximize, restore, or close by name. Uses fuzzy matching to find the window (e.g., "notepad", "chrome").',
)
def window_tool(
    name: str, action: Literal["minimize", "maximize", "restore", "close"]
) -> str:
    ensure_com()
    valid, msg = validate_text(name, max_length=200)
    if not valid:
        return f"Validation Error: {msg}"
    result, status = desktop.manage_window(name, action)
    if status != 0:
        return f"Failed: {result}"
    return result


@mcp.tool(
    name="Find-Element-Tool",
    description="Search for UI elements by text. Returns matching elements with their coordinates, control type, and bounding box. Faster than parsing State-Tool output when looking for a specific element.",
)
def find_element_tool(
    search: str,
    element_type: Literal["interactive", "text", "scrollable"] = "interactive",
) -> str:
    ensure_com()
    valid, msg = validate_text(search, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"

    desktop_state = desktop.get_state(use_vision=False)
    ts = desktop_state.tree_state

    if element_type == "interactive":
        nodes = ts.interactive_nodes
    elif element_type == "text":
        nodes = ts.informative_nodes
    else:
        nodes = ts.scrollable_nodes

    search_lower = search.lower()
    matches = [n for n in nodes if search_lower in n.name.lower()]

    if not matches:
        return f'No {element_type} elements found matching "{search}".'

    lines = [f'Found {len(matches)} {element_type} element(s) matching "{search}":']
    for n in matches:
        if hasattr(n, "center"):
            cx, cy = n.center.x, n.center.y
            coord = f"center=({cx},{cy})"
        else:
            coord = ""
        if hasattr(n, "bounding_box"):
            bb = n.bounding_box
            coord += f" rect={bb.to_string()}"
        ctrl = f" [{n.control_type}]" if hasattr(n, "control_type") else ""
        lines.append(f"  - {n.name}{ctrl} {coord}".strip())
    return "\n".join(lines)


@mcp.tool(
    name="Wait-For-Tool",
    description="Wait until a UI element or text appears on screen, polling at regular intervals. Returns element details when found or a timeout message. Useful for waiting for apps to load or dialogs to appear.",
)
def wait_for_tool(text: str, timeout: int = 10, poll_interval: float = 1.0) -> str:
    ensure_com()
    valid, msg = validate_text(text, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"
    if timeout < 1 or timeout > MAX_WAIT_DURATION:
        return f"Validation Error: timeout must be between 1 and {MAX_WAIT_DURATION}."
    if poll_interval < 0.5:
        return "Validation Error: poll_interval must be at least 0.5 seconds."

    text_lower = text.lower()
    start = time.time()

    while True:
        elapsed = time.time() - start
        if elapsed >= timeout:
            return f'Timeout after {timeout}s: "{text}" not found.'

        desktop_state = desktop.get_state(use_vision=False)
        ts = desktop_state.tree_state
        all_nodes = ts.interactive_nodes + ts.informative_nodes

        for n in all_nodes:
            if text_lower in n.name.lower():
                ctrl = f" [{n.control_type}]" if hasattr(n, "control_type") else ""
                coord = (
                    f" center=({n.center.x},{n.center.y})"
                    if hasattr(n, "center")
                    else ""
                )
                return f'Found "{text}" after {elapsed:.1f}s: {n.name}{ctrl}{coord}'

        time.sleep(poll_interval)


@mcp.tool(
    name="Notification-Tool",
    description="Show a Windows toast notification. Useful for alerting the user after long-running automations complete.",
)
def notification_tool(title: str, message: str) -> str:
    ensure_com()
    valid, msg = validate_text(title, max_length=200)
    if not valid:
        return f"Validation Error (title): {msg}"
    valid, msg = validate_text(message, max_length=1000)
    if not valid:
        return f"Validation Error (message): {msg}"

    # Escape XML special characters
    import html

    safe_title = html.escape(title)
    safe_message = html.escape(message)

    ps_script = dedent(f"""\
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
        $template = @"
        <toast>
            <visual>
                <binding template="ToastGeneric">
                    <text>{safe_title}</text>
                    <text>{safe_message}</text>
                </binding>
            </visual>
        </toast>
        "@
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml($template)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("Windows-MCP").Show($toast)
    """)
    response, status = desktop.execute_command(ps_script, timeout=10)
    if status != 0:
        return f"Failed to show notification: {response}"
    return f"Notification shown: {title}"


@mcp.tool(
    name="Process-Tool",
    description='List running processes or kill a process by name/PID. Use action="list" with optional name filter, or action="kill" with name or pid.',
)
def process_tool(
    action: Literal["list", "kill"], name: str = None, pid: int = None
) -> str:
    ensure_com()
    if action == "list":
        if name:
            valid, msg = validate_text(name, max_length=200)
            if not valid:
                return f"Validation Error: {msg}"
            cmd = f'Get-Process -Name "{name}" -ErrorAction Stop | Select-Object Name, Id, @{{N="MemoryMB";E={{[math]::Round($_.WorkingSet64/1MB,1)}}}} | Format-Table -AutoSize | Out-String'
        else:
            cmd = 'Get-Process | Select-Object Name, Id, @{N="MemoryMB";E={[math]::Round($_.WorkingSet64/1MB,1)}} | Sort-Object MemoryMB -Descending | Select-Object -First 30 | Format-Table -AutoSize | Out-String'
        valid, msg = validate_command(cmd, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(cmd, timeout=15)
        if status != 0:
            return f"Failed to list processes: {response}"
        return response.strip()

    elif action == "kill":
        if not name and not pid:
            return "Error: Provide either 'name' or 'pid' to kill a process."
        if name:
            valid, msg = validate_text(name, max_length=200)
            if not valid:
                return f"Validation Error: {msg}"
            # Security: check name against blocked commands
            if name.lower().replace(".exe", "") in BLOCKED_COMMANDS:
                return f"Security Error: Cannot kill blocked process: {name}"
            cmd = f'Stop-Process -Name "{name}" -Force -ErrorAction Stop'
        else:
            if pid < 0 or pid > 99999:
                return "Validation Error: PID must be between 0 and 99999."
            cmd = f"Stop-Process -Id {pid} -Force -ErrorAction Stop"
        valid, msg = validate_command(cmd, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(cmd, timeout=10)
        if status != 0:
            return f"Failed to kill process: {response}"
        target = name if name else f"PID {pid}"
        return f"Killed process: {target}"

    return f"Unknown action: {action}"


@mcp.tool(
    name="File-Dialog-Tool",
    description="Type a file path into an active Open/Save file dialog and confirm it. Useful for automating file selection in native Windows dialogs.",
)
def file_dialog_tool(path: str, action: Literal["open", "save"] = "open") -> str:
    ensure_com()
    valid, msg = validate_text(path, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"

    desktop_state = desktop.get_state(use_vision=False)
    ts = desktop_state.tree_state

    # Look for an Edit control in interactive nodes (filename field in dialog)
    edit_node = None
    for n in ts.interactive_nodes:
        if hasattr(n, "control_type") and "Edit" in n.control_type:
            name_lower = n.name.lower()
            if any(
                kw in name_lower for kw in ["file name", "filename", "name", "edit"]
            ):
                edit_node = n
                break

    # Fallback: find any Edit control
    if edit_node is None:
        for n in ts.interactive_nodes:
            if hasattr(n, "control_type") and "Edit" in n.control_type:
                edit_node = n
                break

    if edit_node is None:
        return "Error: No file name input field found. Make sure a file dialog is open."

    # Click on the edit field, clear it, type the path, press Enter
    cx, cy = edit_node.center.x, edit_node.center.y
    cursor.click_on((cx, cy))
    pg.hotkey("ctrl", "a")
    pg.press("backspace")
    # Use clipboard for paths with Unicode characters
    old_clipboard = pc.paste()
    pc.copy(path)
    pg.hotkey("ctrl", "v")
    pg.sleep(0.3)
    pc.copy(old_clipboard if old_clipboard else "")
    pg.press("enter")
    return f"Typed path '{path}' into file dialog and confirmed ({action})."


@mcp.tool(
    name="Multi-Monitor-Tool",
    description="Get display count, resolutions, and positions for all connected monitors. Includes virtual screen dimensions.",
)
def multi_monitor_tool() -> str:
    ensure_com()
    info = desktop.get_monitors()
    monitors = info["monitors"]
    vs = info["virtual_screen"]
    lines = [f"Monitor count: {len(monitors)}"]
    for i, m in enumerate(monitors, 1):
        primary = " (Primary)" if m["primary"] else ""
        lines.append(
            f"  Monitor {i}{primary}: {m['width']}x{m['height']} at ({m['x']},{m['y']})"
        )
    lines.append(
        f"Virtual screen: {vs['width']}x{vs['height']} at ({vs['x']},{vs['y']})"
    )
    return "\n".join(lines)


@mcp.tool(
    name="System-Info-Tool",
    description="Get comprehensive system information including OS, CPU, memory, disks, GPU, network, battery, and top processes. No parameters needed.",
)
def system_info_tool() -> str:
    ensure_com()
    ps_script = r"""
$r = ""

# OS
$os = Get-CimInstance Win32_OperatingSystem
$uptime = (Get-Date) - $os.LastBootUpTime
$r += "=== OS ===`n"
$r += "  Edition: $($os.Caption)`n"
$r += "  Build: $($os.BuildNumber) ($($os.OSArchitecture))`n"
$r += "  Install Date: $($os.InstallDate.ToString('yyyy-MM-dd'))`n"
$r += "  Uptime: $([int]$uptime.TotalDays)d $($uptime.Hours)h $($uptime.Minutes)m`n"

# CPU
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$r += "`n=== CPU ===`n"
$r += "  Name: $($cpu.Name.Trim())`n"
$r += "  Cores: $($cpu.NumberOfCores) physical, $($cpu.NumberOfLogicalProcessors) logical`n"
$r += "  Clock: $($cpu.MaxClockSpeed) MHz`n"
$r += "  Load: $($cpu.LoadPercentage)%`n"

# Memory
$totalMB = [math]::Round($os.TotalVisibleMemorySize / 1024)
$freeMB = [math]::Round($os.FreePhysicalMemory / 1024)
$usedMB = $totalMB - $freeMB
$pct = [math]::Round($usedMB / $totalMB * 100, 1)
$r += "`n=== Memory ===`n"
$r += "  Total: $([math]::Round($totalMB / 1024, 1)) GB`n"
$r += "  Used: $([math]::Round($usedMB / 1024, 1)) GB ($pct%)`n"
$r += "  Available: $([math]::Round($freeMB / 1024, 1)) GB`n"

# Disks
$r += "`n=== Disks ===`n"
$vols = Get-Volume | Where-Object { $_.DriveLetter -and $_.Size -gt 0 }
foreach ($v in $vols) {
    $sizeGB = [math]::Round($v.Size / 1GB, 1)
    $freeGB = [math]::Round($v.SizeRemaining / 1GB, 1)
    $usedPct = [math]::Round(($v.Size - $v.SizeRemaining) / $v.Size * 100, 1)
    $r += "  $($v.DriveLetter): $sizeGB GB ($freeGB GB free, $usedPct% used) [$($v.FileSystemType)] $($v.HealthStatus)`n"
}

# GPU
$r += "`n=== GPU ===`n"
$gpus = Get-CimInstance Win32_VideoController
foreach ($g in $gpus) {
    $vram = [math]::Round($g.AdapterRAM / 1GB, 1)
    $r += "  $($g.Name) | VRAM: ${vram} GB | Driver: $($g.DriverVersion) | $($g.Status)`n"
}

# Network
$r += "`n=== Network ===`n"
$adapters = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }
foreach ($a in $adapters) {
    $ips = (Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress -join ', '
    $r += "  $($a.Name): $ips | MAC: $($a.MacAddress) | $($a.LinkSpeed)`n"
}
if (-not $adapters) { $r += "  No active physical adapters`n" }

# Battery
$bat = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
if ($bat) {
    $r += "`n=== Battery ===`n"
    $r += "  Charge: $($bat.EstimatedChargeRemaining)%`n"
    $plugged = if ($bat.BatteryStatus -eq 2) { "Yes" } else { "No" }
    $r += "  Plugged in: $plugged`n"
    if ($bat.EstimatedRunTime -and $bat.EstimatedRunTime -lt 71582788) {
        $r += "  Est. runtime: $($bat.EstimatedRunTime) min`n"
    }
}

# Top Processes
$r += "`n=== Top Processes (by CPU) ===`n"
$procs = Get-Process | Sort-Object CPU -Descending | Select-Object -First 5
foreach ($p in $procs) {
    $memMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
    $cpuSec = [math]::Round($p.CPU, 1)
    $r += "  $($p.ProcessName) (PID $($p.Id)) | CPU: ${cpuSec}s | Mem: ${memMB} MB`n"
}

$r
"""
    response, status = desktop.execute_command(ps_script, timeout=30)
    if status != 0:
        return f"Error collecting system info (exit {status}): {response}"
    return response.strip()


@mcp.tool(
    name="OCR-Tool",
    description="Extract text from the screen or a specific region using Windows built-in OCR (Windows 10+). Returns recognized text.",
)
def ocr_tool(region: tuple[int, int, int, int] = None, language: str = "en") -> str:
    ensure_com()
    if region:
        for val, name in [
            (region[0], "x"),
            (region[1], "y"),
            (region[2], "width"),
            (region[3], "height"),
        ]:
            if not isinstance(val, int) or val < 0:
                return f"Validation Error: {name} must be a non-negative integer."
            if val > MAX_SCREEN_COORD:
                return f"Validation Error: {name} exceeds maximum ({MAX_SCREEN_COORD})."
        if region[2] == 0 or region[3] == 0:
            return "Validation Error: width and height must be greater than 0."

    valid, msg = validate_text(language, max_length=10)
    if not valid:
        return f"Validation Error: {msg}"

    import tempfile

    screenshot = desktop.get_screenshot(scale=1.0)
    if region:
        x, y, w, h = region
        screenshot = screenshot.crop((x, y, x + w, y + h))

    tmp_path = os.path.join(tempfile.gettempdir(), "wmcp_ocr_tmp.png")
    screenshot.save(tmp_path, format="PNG")

    ps_script = dedent(f"""\
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
        [Windows.Storage.StorageFile, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null

        function Await($WinRtTask, $ResultType) {{
            $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {{ $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }})[0]
            $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
            $netTask = $asTask.Invoke($null, @($WinRtTask))
            $netTask.Wait(-1) | Out-Null
            $netTask.Result
        }}

        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync("{tmp_path}")) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($engine -eq $null) {{ Write-Error "OCR engine unavailable"; exit 1 }}
        $ocrResult = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        $ocrResult.Text
    """)

    response, status = desktop.execute_command(ps_script, timeout=30)

    # Clean up temp file
    try:
        os.remove(tmp_path)
    except OSError:
        pass

    if status != 0:
        return f"OCR failed: {response}"
    text = response.strip()
    if not text:
        return "No text detected in the captured region."
    return text


@mcp.tool(
    name="Audio-Tool",
    description='Get or set system volume, mute or unmute. Use action="get" to read current volume, "set" with level (0-100) to change it, "mute" or "unmute" to toggle mute.',
)
def audio_tool(
    action: Literal["get", "set", "mute", "unmute"], level: int = None
) -> str:
    ensure_com()
    if action == "set" and level is None:
        return "Error: 'level' parameter required for 'set' action."
    if level is not None and (level < 0 or level > 100):
        return "Validation Error: level must be between 0 and 100."

    # PowerShell COM interop with Windows Core Audio API
    # Uses _VtblGap to skip unused vtable slots in COM interfaces
    audio_setup = dedent("""\
        Add-Type -TypeDefinition @"
        using System;
        using System.Runtime.InteropServices;

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioEndpointVolume {
            int _VtblGap1_4();
            int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int _VtblGap2_4();
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator {
            int _VtblGap1_1();
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumeratorCom {}

        public static class AudioHelper {
            static IAudioEndpointVolume GetVol() {
                var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
                IMMDevice dev;
                enumerator.GetDefaultAudioEndpoint(0, 1, out dev);
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                return (IAudioEndpointVolume)o;
            }
            public static float GetVolume() { float v; GetVol().GetMasterVolumeLevelScalar(out v); return v; }
            public static void SetVolume(float v) { GetVol().SetMasterVolumeLevelScalar(v, Guid.Empty); }
            public static bool GetMute() { bool m; GetVol().GetMute(out m); return m; }
            public static void SetMute(bool m) { GetVol().SetMute(m, Guid.Empty); }
        }
"@
    """)

    if action == "get":
        cmd = audio_setup + dedent("""\
            $v = [AudioHelper]::GetVolume()
            $m = [AudioHelper]::GetMute()
            "Volume: $([math]::Round($v * 100))%, Muted: $m"
        """)
    elif action == "set":
        scalar = level / 100.0
        cmd = audio_setup + dedent(f"""\
            [AudioHelper]::SetVolume([float]{scalar})
            "Volume set to {level}%"
        """)
    elif action == "mute":
        cmd = audio_setup + dedent("""\
            [AudioHelper]::SetMute($true)
            "Muted"
        """)
    elif action == "unmute":
        cmd = audio_setup + dedent("""\
            [AudioHelper]::SetMute($false)
            "Unmuted"
        """)
    else:
        return f"Unknown action: {action}"

    response, status = desktop.execute_command(cmd, timeout=10)
    if status != 0:
        return f"Audio control failed: {response}"
    return response.strip()


# ── Shared Helper: Element Lookup ─────────────────────────────────────────


INTERACTIVE_CONTROL_TYPES = {
    "ButtonControl",
    "CheckBoxControl",
    "ComboBoxControl",
    "EditControl",
    "HyperlinkControl",
    "ListControl",
    "ListItemControl",
    "MenuBarControl",
    "MenuControl",
    "MenuItemControl",
    "RadioButtonControl",
    "ScrollBarControl",
    "SliderControl",
    "SpinnerControl",
    "TabControl",
    "TabItemControl",
    "TreeControl",
    "TreeItemControl",
    "DataGridControl",
}


def find_control_by_search(search: str) -> "ua.Control | None":
    """Find a UI Automation Control by text search on its Name (DFS).

    Prefers interactive controls (buttons, inputs, combos) over static
    text/labels when multiple elements match the search string.
    """
    ensure_com()
    search_lower = search.lower()
    root = ua.GetRootControl()
    MAX_DEPTH = 50
    first_match = None
    stack = [(root, 0)]
    while stack:
        current, depth = stack.pop()
        if depth > MAX_DEPTH:
            continue
        try:
            if search_lower in current.Name.lower() and current.Name.strip():
                # Interactive control? Return immediately.
                if current.ControlTypeName in INTERACTIVE_CONTROL_TYPES:
                    return current
                # Otherwise remember first match as fallback
                if first_match is None:
                    first_match = current
        except Exception:
            pass
        try:
            children = current.GetChildren()
            for child in reversed(children):
                stack.append((child, depth + 1))
        except Exception:
            pass
    return first_match


def resolve_control(
    search: str = None, loc: tuple[int, int] = None
) -> "tuple[ua.Control | None, str]":
    """Resolve a UI control by search text or coordinates. Returns (control, error_msg)."""
    ensure_com()
    if loc:
        valid, msg = validate_coordinates(loc)
        if not valid:
            return None, f"Validation Error: {msg}"
        x, y = loc
        try:
            control = ua.ControlFromPoint(x, y)
            if control:
                return control, ""
            return None, f"No element found at ({x},{y})."
        except Exception as e:
            return None, f"Error finding element at ({x},{y}): {e}"
    elif search:
        valid, msg = validate_text(search, max_length=500)
        if not valid:
            return None, f"Validation Error: {msg}"
        control = find_control_by_search(search)
        if control:
            return control, ""
        return None, f'No element found matching "{search}".'
    else:
        return None, "Provide either 'search' or 'loc' parameter."


def get_control_properties(control: "ua.Control") -> dict:
    """Extract all available properties and patterns from a UI control."""
    props = {
        "name": control.Name.strip(),
        "control_type": control.ControlTypeName,
        "is_enabled": control.IsEnabled,
        "is_offscreen": control.IsOffscreen,
        "is_keyboard_focusable": control.IsKeyboardFocusable,
    }

    # Bounding box
    try:
        box = control.BoundingRectangle
        if not box.isempty():
            props["bounding_box"] = {
                "left": box.left,
                "top": box.top,
                "right": box.right,
                "bottom": box.bottom,
                "width": box.width(),
                "height": box.height(),
            }
    except Exception:
        pass

    # ValuePattern
    try:
        vp = control.GetValuePattern()
        if vp:
            props["value"] = vp.Value
            props["is_readonly"] = vp.IsReadOnly
    except Exception:
        pass

    # TogglePattern
    try:
        tp = control.GetTogglePattern()
        if tp:
            state_map = {0: "off", 1: "on", 2: "indeterminate"}
            props["toggle_state"] = state_map.get(tp.ToggleState, str(tp.ToggleState))
    except Exception:
        pass

    # SelectionItemPattern
    try:
        sip = control.GetSelectionItemPattern()
        if sip:
            props["is_selected"] = sip.IsSelected
    except Exception:
        pass

    # RangeValuePattern
    try:
        rvp = control.GetRangeValuePattern()
        if rvp:
            props["range_value"] = rvp.Value
            props["range_min"] = rvp.Minimum
            props["range_max"] = rvp.Maximum
    except Exception:
        pass

    # ExpandCollapsePattern
    try:
        ecp = control.GetExpandCollapsePattern()
        if ecp:
            state_map = {
                0: "collapsed",
                1: "expanded",
                2: "partially_expanded",
                3: "leaf",
            }
            props["expand_state"] = state_map.get(
                ecp.ExpandCollapseState, str(ecp.ExpandCollapseState)
            )
    except Exception:
        pass

    return props


# ── Phase 1: Inspection Tools ─────────────────────────────────────────────


@mcp.tool(
    name="Get-Element-Property-Tool",
    description="Read detailed properties of a UI element including value, checked state, enabled/disabled, bounding box, and automation patterns. Find by text search or coordinates.",
)
def get_element_property_tool(search: str = None, loc: tuple[int, int] = None) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    props = get_control_properties(control)
    lines = [f"Element: {props.get('name', '(unnamed)')}"]
    lines.append(f"  ControlType: {props.get('control_type', 'unknown')}")
    lines.append(f"  Enabled: {props.get('is_enabled', 'unknown')}")
    lines.append(f"  Offscreen: {props.get('is_offscreen', 'unknown')}")
    lines.append(
        f"  KeyboardFocusable: {props.get('is_keyboard_focusable', 'unknown')}"
    )

    if "bounding_box" in props:
        bb = props["bounding_box"]
        lines.append(
            f"  BoundingBox: ({bb['left']},{bb['top']},{bb['right']},{bb['bottom']}) {bb['width']}x{bb['height']}"
        )

    if "value" in props:
        lines.append(f"  Value: {props['value']}")
        lines.append(f"  ReadOnly: {props.get('is_readonly', 'unknown')}")

    if "toggle_state" in props:
        lines.append(f"  ToggleState: {props['toggle_state']}")

    if "is_selected" in props:
        lines.append(f"  Selected: {props['is_selected']}")

    if "range_value" in props:
        lines.append(
            f"  RangeValue: {props['range_value']} (min={props['range_min']}, max={props['range_max']})"
        )

    if "expand_state" in props:
        lines.append(f"  ExpandState: {props['expand_state']}")

    return "\n".join(lines)


@mcp.tool(
    name="Get-Text-Tool",
    description="Extract the text content of a specific UI element (input field value, label text, status message). Find by text search or coordinates. Faster and more precise than OCR.",
)
def get_text_tool(search: str = None, loc: tuple[int, int] = None) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    # Try ValuePattern first (input fields, editable text)
    try:
        vp = control.GetValuePattern()
        if vp and vp.Value:
            return vp.Value
    except Exception:
        pass

    # Try Name property (labels, buttons, static text)
    name = control.Name.strip()
    if name:
        return name

    # Try LegacyIAccessible as fallback
    try:
        legacy = control.GetLegacyIAccessiblePattern()
        if legacy and legacy.Value:
            return legacy.Value
    except Exception:
        pass

    return f"No text found for element (ControlType: {control.ControlTypeName})."


@mcp.tool(
    name="Assert-Element-Tool",
    description='Verify a UI element\'s state for testing. Returns PASS or FAIL. Properties: "exists", "enabled", "disabled", "checked", "unchecked", "value", "name", "visible", "focused". For "value" and "name", provide expected text.',
)
def assert_element_tool(
    search: str,
    property: str,
    expected: str = "",
    loc: tuple[int, int] = None,
) -> str:
    ensure_com()
    valid, msg = validate_text(search, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"

    # Special case: "exists" check
    if property == "exists":
        control, _ = resolve_control(search, loc)
        if control:
            return f'PASS: Element "{search}" exists ({control.ControlTypeName}: {control.Name.strip()}).'
        return f'FAIL: Element "{search}" does not exist.'

    control, err = resolve_control(search, loc)
    if err:
        return f"FAIL: Cannot find element — {err}"

    props = get_control_properties(control)

    match property:
        case "enabled":
            actual = props.get("is_enabled", False)
            if actual:
                return f'PASS: "{search}" is enabled.'
            return f'FAIL: "{search}" is not enabled.'

        case "disabled":
            actual = props.get("is_enabled", True)
            if not actual:
                return f'PASS: "{search}" is disabled.'
            return f'FAIL: "{search}" is not disabled (it is enabled).'

        case "checked":
            actual = props.get("toggle_state", None)
            if actual == "on":
                return f'PASS: "{search}" is checked.'
            return f'FAIL: "{search}" is not checked (toggle_state={actual}).'

        case "unchecked":
            actual = props.get("toggle_state", None)
            if actual == "off":
                return f'PASS: "{search}" is unchecked.'
            return f'FAIL: "{search}" is not unchecked (toggle_state={actual}).'

        case "value":
            actual = props.get("value", "")
            if str(actual) == str(expected):
                return f'PASS: "{search}" value is "{expected}".'
            return f'FAIL: "{search}" value is "{actual}", expected "{expected}".'

        case "name":
            actual = props.get("name", "")
            if expected.lower() in actual.lower():
                return (
                    f'PASS: "{search}" name contains "{expected}" (full: "{actual}").'
                )
            return f'FAIL: "{search}" name is "{actual}", expected to contain "{expected}".'

        case "visible":
            offscreen = props.get("is_offscreen", True)
            if not offscreen:
                return f'PASS: "{search}" is visible (on-screen).'
            return f'FAIL: "{search}" is offscreen.'

        case "focused":
            try:
                focused = ua.GetFocusedControl()
                if focused and search.lower() in focused.Name.lower():
                    return f'PASS: "{search}" is focused.'
                actual_name = focused.Name.strip() if focused else "(none)"
                return f'FAIL: "{search}" is not focused (focused: "{actual_name}").'
            except Exception as e:
                return f"FAIL: Could not check focus — {e}"

        case _:
            return f'Unknown property: "{property}". Use: exists, enabled, disabled, checked, unchecked, value, name, visible, focused.'


# ── Phase 2: Pattern Interaction Tools ────────────────────────────────────


@mcp.tool(
    name="Checkbox-Toggle-Tool",
    description='Toggle a checkbox by name. Use target_state="on" to check, "off" to uncheck, or "toggle" to flip. Returns before/after state.',
)
def checkbox_toggle_tool(
    search: str,
    loc: tuple[int, int] = None,
    target_state: Literal["on", "off", "toggle"] = "toggle",
) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    # Read current state
    try:
        tp = control.GetTogglePattern()
    except Exception:
        tp = None

    if tp is None:
        # Fallback: try clicking the element
        try:
            box = control.BoundingRectangle
            x, y = box.xcenter(), box.ycenter()
            cursor.click_on((x, y))
            return f'Clicked "{control.Name.strip()}" (no TogglePattern available — used click fallback).'
        except Exception as e:
            return f"Error: Element has no TogglePattern and click fallback failed: {e}"

    state_map = {0: "off", 1: "on", 2: "indeterminate"}
    before = state_map.get(tp.ToggleState, str(tp.ToggleState))

    if target_state != "toggle" and before == target_state:
        return f'Already in desired state: "{control.Name.strip()}" is {before}.'

    tp.Toggle()
    time.sleep(0.3)

    # Re-read state
    try:
        tp2 = control.GetTogglePattern()
        after = state_map.get(tp2.ToggleState, str(tp2.ToggleState))
    except Exception:
        after = "unknown"

    return f'Toggled "{control.Name.strip()}": {before} → {after}'


@mcp.tool(
    name="Select-Option-Tool",
    description="Select an item from a dropdown, combobox, or list by text. Expands the control if needed and selects the matching option.",
)
def select_option_tool(search: str, option: str, loc: tuple[int, int] = None) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    valid, msg = validate_text(option, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"

    # Try to expand the control first
    try:
        ecp = control.GetExpandCollapsePattern()
        if ecp and ecp.ExpandCollapseState == 0:  # Collapsed
            ecp.Expand()
            time.sleep(0.5)
    except Exception:
        # No expand pattern — try clicking to open
        try:
            box = control.BoundingRectangle
            cursor.click_on((box.xcenter(), box.ycenter()))
            time.sleep(0.5)
        except Exception:
            pass

    # Search children for the option
    option_lower = option.lower()
    MAX_DEPTH = 10
    stack = [(control, 0)]
    found = None
    while stack:
        current, depth = stack.pop()
        if depth > MAX_DEPTH:
            continue
        try:
            if option_lower in current.Name.lower() and current.Name.strip():
                found = current
                break
        except Exception:
            pass
        try:
            children = current.GetChildren()
            for child in reversed(children):
                stack.append((child, depth + 1))
        except Exception:
            pass

    if not found:
        # Collapse back if we expanded
        try:
            ecp = control.GetExpandCollapsePattern()
            if ecp:
                ecp.Collapse()
        except Exception:
            pass
        return f'Option "{option}" not found in "{control.Name.strip()}".'

    # Try SelectionItemPattern
    try:
        sip = found.GetSelectionItemPattern()
        if sip:
            sip.Select()
            return f'Selected "{found.Name.strip()}" in "{control.Name.strip()}".'
    except Exception:
        pass

    # Fallback: click the option
    try:
        box = found.BoundingRectangle
        cursor.click_on((box.xcenter(), box.ycenter()))
        return f'Clicked option "{found.Name.strip()}" in "{control.Name.strip()}".'
    except Exception as e:
        return f"Error selecting option: {e}"


@mcp.tool(
    name="Focus-Tool",
    description="Set keyboard focus to a UI element by name or coordinates, without clicking. Useful for testing tab order and focus behavior.",
)
def focus_tool(search: str = None, loc: tuple[int, int] = None) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    try:
        control.SetFocus()
        return f'Focused: "{control.Name.strip()}" ({control.ControlTypeName})'
    except Exception as e:
        return f"Failed to set focus: {e}"


# ── Phase 3: Independent Tools ────────────────────────────────────────────


@mcp.tool(
    name="Hover-Tool",
    description="Move cursor to coordinates and hover for a duration. Triggers hover states like tooltips and dropdown menus. Returns the element name under cursor.",
)
def hover_tool(loc: tuple[int, int], duration: float = 1.0) -> str:
    ensure_com()
    valid, msg = validate_coordinates(loc)
    if not valid:
        return f"Validation Error: {msg}"
    if duration < 0 or duration > 30:
        return "Validation Error: duration must be between 0 and 30 seconds."

    x, y = loc
    cursor.move_to(loc)
    time.sleep(duration)

    # Report what's under cursor after hover
    try:
        control = ua.ControlFromPoint(x, y)
        name = control.Name.strip() if control else "(unknown)"
        ctrl_type = control.ControlTypeName if control else ""
        return f"Hovered at ({x},{y}) for {duration}s — element: {name} [{ctrl_type}]"
    except Exception:
        return f"Hovered at ({x},{y}) for {duration}s."


@mcp.tool(
    name="Compare-Screenshot-Tool",
    description="Compare current screen (or region) against a baseline image file. Returns MATCH/MISMATCH with pixel difference percentage. Saves current as baseline if file doesn't exist.",
)
def compare_screenshot_tool(
    baseline: str,
    region: tuple[int, int, int, int] = None,
    threshold: float = 5.0,
) -> str:
    ensure_com()
    valid, msg = validate_text(baseline, max_length=500)
    if not valid:
        return f"Validation Error: {msg}"
    try:
        baseline = _sanitize_path(baseline)
        _check_allowed_path(baseline)
    except ValueError as e:
        return f"Error: {e}"
    if threshold < 0 or threshold > 100:
        return "Validation Error: threshold must be between 0 and 100."

    # Capture current screenshot
    current = desktop.get_screenshot(scale=1.0)
    if region:
        x, y, w, h = region
        for val, name in [(x, "x"), (y, "y"), (w, "width"), (h, "height")]:
            if not isinstance(val, int) or val < 0:
                return f"Validation Error: {name} must be a non-negative integer."
            if val > MAX_SCREEN_COORD:
                return f"Validation Error: {name} exceeds maximum ({MAX_SCREEN_COORD})."
        current = current.crop((x, y, x + w, y + h))

    # Load or create baseline
    if not os.path.exists(baseline):
        current.save(baseline, format="PNG")
        return f"No baseline found. Saved current screenshot as baseline: {baseline}"

    try:
        baseline_img = Image.open(baseline)
    except Exception as e:
        return f"Error loading baseline: {e}"

    # Resize if dimensions differ
    if current.size != baseline_img.size:
        return (
            f"MISMATCH: Size differs — current {current.size} vs baseline {baseline_img.size}. "
            f"Delete baseline to regenerate."
        )

    # Pixel-by-pixel comparison
    diff = ImageChops.difference(current.convert("RGB"), baseline_img.convert("RGB"))
    pixels = list(diff.getdata())
    total_pixels = len(pixels)
    diff_pixels = sum(1 for r, g, b in pixels if r + g + b > 30)  # threshold per pixel
    diff_pct = (diff_pixels / total_pixels) * 100 if total_pixels > 0 else 0

    if diff_pct <= threshold:
        return f"MATCH (diff: {diff_pct:.2f}%, threshold: {threshold}%)"
    else:
        # Save diff image for inspection
        diff_path = baseline.replace(".png", "_diff.png")
        try:
            diff_enhanced = ImageChops.multiply(
                diff, Image.new("RGB", diff.size, (5, 5, 5))
            )
            diff_enhanced.save(diff_path, format="PNG")
        except Exception:
            diff_path = "(could not save diff)"
        return f"MISMATCH (diff: {diff_pct:.2f}%, threshold: {threshold}%). Diff saved: {diff_path}"


# ── Phase 4: Complex Tools ────────────────────────────────────────────────


@mcp.tool(
    name="Get-Table-Tool",
    description="Extract tabular data from a grid, table, or list control. Returns data as a markdown table. Find the table by text search or coordinates.",
)
def get_table_tool(
    search: str = None, loc: tuple[int, int] = None, max_rows: int = 50
) -> str:
    ensure_com()
    control, err = resolve_control(search, loc)
    if err:
        return err

    if max_rows < 1 or max_rows > 500:
        return "Validation Error: max_rows must be between 1 and 500."

    # Try GridPattern first
    try:
        gp = control.GetGridPattern()
        if gp:
            row_count = min(gp.RowCount, max_rows)
            col_count = gp.ColumnCount
            if row_count == 0 or col_count == 0:
                return "Table is empty (0 rows or 0 columns)."

            rows = []
            for r in range(row_count):
                row = []
                for c in range(col_count):
                    try:
                        cell = gp.GetItem(r, c)
                        row.append(cell.Name.strip() if cell else "")
                    except Exception:
                        row.append("")
                rows.append(row)

            # Build markdown table
            if not rows:
                return "No data extracted from table."
            header = rows[0]
            lines = ["| " + " | ".join(header) + " |"]
            lines.append("| " + " | ".join(["---"] * len(header)) + " |")
            for row in rows[1:]:
                lines.append("| " + " | ".join(row) + " |")
            return "\n".join(lines)
    except Exception:
        pass

    # Fallback: traverse children to extract row/cell data
    rows = []
    try:
        children = control.GetChildren()
        for i, child in enumerate(children):
            if i >= max_rows:
                break
            # Try to get cells from row children
            cells = child.GetChildren()
            if cells:
                row = [c.Name.strip() for c in cells]
                rows.append(row)
            elif child.Name.strip():
                rows.append([child.Name.strip()])
    except Exception as e:
        return f"Error extracting table data: {e}"

    if not rows:
        return f'No tabular data found in "{control.Name.strip()}".'

    # Normalize column count
    max_cols = max(len(r) for r in rows)
    for row in rows:
        while len(row) < max_cols:
            row.append("")

    header = rows[0]
    lines = ["| " + " | ".join(header) + " |"]
    lines.append("| " + " | ".join(["---"] * len(header)) + " |")
    for row in rows[1:]:
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines)


@mcp.tool(
    name="Record-Replay-Tool",
    description='Save and replay sequences of UI actions. Use action="save" with steps and name to save, "replay" with name to execute, "load" to view steps, "list" to see all recordings.',
)
def record_replay_tool(
    action: Literal["save", "replay", "load", "list"],
    name: str = None,
    steps: list[dict] = None,
) -> str:
    ensure_com()
    import json

    recordings_dir = os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "recordings"
    )
    os.makedirs(recordings_dir, exist_ok=True)

    if action == "list":
        files = [f for f in os.listdir(recordings_dir) if f.endswith(".json")]
        if not files:
            return "No recordings saved."
        return "Saved recordings:\n" + "\n".join(
            f"  - {f.replace('.json', '')}" for f in files
        )

    if not name:
        return "Error: 'name' parameter required for save/replay/load."

    valid, msg = validate_text(name, max_length=100)
    if not valid:
        return f"Validation Error: {msg}"

    # Sanitize name for filename
    safe_name = "".join(c for c in name if c.isalnum() or c in "-_ ").strip()
    filepath = os.path.join(recordings_dir, f"{safe_name}.json")

    if action == "save":
        if not steps:
            return "Error: 'steps' parameter required for save action."
        if len(steps) > 100:
            return "Validation Error: Maximum 100 steps per recording."
        # Validate step format
        for i, step in enumerate(steps):
            if "tool" not in step or "params" not in step:
                return f'Validation Error: Step {i} must have "tool" and "params" keys.'
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump({"name": name, "steps": steps}, f, indent=2)
        return f'Saved recording "{name}" with {len(steps)} steps to {filepath}.'

    if action == "load":
        if not os.path.exists(filepath):
            return f'Recording "{name}" not found.'
        with open(filepath, "r", encoding="utf-8") as f:
            data = json.load(f)
        steps_text = json.dumps(data["steps"], indent=2)
        return f'Recording "{name}" ({len(data["steps"])} steps):\n{steps_text}'

    if action == "replay":
        if not os.path.exists(filepath):
            return f'Recording "{name}" not found.'
        with open(filepath, "r", encoding="utf-8") as f:
            data = json.load(f)

        # Map tool names to functions
        tool_map = {
            "Click-Tool": click_tool,
            "Type-Tool": type_tool,
            "Key-Tool": key_tool,
            "Shortcut-Tool": shortcut_tool,
            "Move-Tool": move_tool,
            "Scroll-Tool": scroll_tool,
            "Wait-Tool": wait_tool,
            "Drag-Tool": drag_tool,
            "Launch-Tool": launch_tool,
            "Switch-Tool": switch_tool,
            "Focus-Tool": focus_tool,
            "Checkbox-Toggle-Tool": checkbox_toggle_tool,
            "Select-Option-Tool": select_option_tool,
            "Hover-Tool": hover_tool,
        }

        results = []
        failed_step = None
        for i, step in enumerate(data["steps"]):
            tool_name = step["tool"]
            params = step["params"]
            func = tool_map.get(tool_name)
            if not func:
                results.append(f"Step {i + 1}: SKIP — unknown tool '{tool_name}'")
                continue
            try:
                result = func(**params)
                results.append(f"Step {i + 1} ({tool_name}): {result}")
            except Exception as e:
                results.append(f"Step {i + 1} ({tool_name}): ERROR — {e}")
                failed_step = i + 1
                break  # Stop on error
        if failed_step is not None:
            return f'Replay "{name}" FAILED at step {failed_step}:\n' + "\n".join(
                results
            )
        return f'Replay "{name}" complete:\n' + "\n".join(results)

    return f"Unknown action: {action}"


# ── Security & Network Tools ──────────────────────────────────────────────────


@mcp.tool(
    name="Security-Audit-Tool",
    description="Run a comprehensive Windows security audit. Returns quick boolean summary followed by detailed sections: Defender, Firewall, UAC, BitLocker, pending updates, PowerShell execution policy, open shares, Remote Desktop status. No parameters needed.",
)
def security_audit_tool() -> str:
    ensure_com()
    ps_script = r"""
$r = ""

# --- Defender ---
$def = try { Get-MpComputerStatus -ErrorAction Stop } catch { $null }
if ($def) {
    $defOn = $def.AntivirusEnabled -and $def.RealTimeProtectionEnabled
    $defStatus = if ($defOn) { "ON" } else { "OFF" }
    $r += "=== Windows Defender ===`n"
    $r += "  Antivirus Enabled: $($def.AntivirusEnabled)`n"
    $r += "  Real-Time Protection: $($def.RealTimeProtectionEnabled)`n"
    $r += "  Signatures Updated: $($def.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm'))`n"
    $r += "  Last Quick Scan: $($def.QuickScanEndTime.ToString('yyyy-MM-dd HH:mm'))`n"
    $r += "  Last Full Scan: $($def.FullScanEndTime.ToString('yyyy-MM-dd HH:mm'))`n"
    $threats = try { Get-MpThreatDetection -ErrorAction Stop | Select-Object -First 5 } catch { @() }
    if ($threats.Count -gt 0) {
        $r += "  Recent Threats ($($threats.Count)):`n"
        foreach ($t in $threats) {
            $r += "    - $($t.ThreatID) at $($t.InitialDetectionTime.ToString('yyyy-MM-dd HH:mm'))`n"
        }
    } else { $r += "  Recent Threats: None`n" }
} else {
    $defStatus = "N/A"
    $r += "=== Windows Defender ===`n  Not available`n"
}

# --- Firewall ---
$fw = Get-NetFirewallProfile -ErrorAction SilentlyContinue
$fwOn = 0; $fwTotal = 0
if ($fw) {
    $r += "`n=== Firewall ===`n"
    foreach ($p in $fw) {
        $fwTotal++
        if ($p.Enabled) { $fwOn++ }
        $r += "  $($p.Name): $(if ($p.Enabled) {'Enabled'} else {'DISABLED'})`n"
    }
}
$fwStatus = if ($fwTotal -eq 0) { "N/A" } elseif ($fwOn -eq $fwTotal) { "ON" } elseif ($fwOn -gt 0) { "PARTIAL" } else { "OFF" }

# --- UAC ---
$uac = try { Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA -ErrorAction Stop } catch { -1 }
$consent = try { Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -ErrorAction Stop } catch { -1 }
$uacStatus = if ($uac -eq 1) { "ON" } elseif ($uac -eq 0) { "OFF" } else { "N/A" }
$r += "`n=== UAC ===`n"
$r += "  Enabled: $(if ($uac -eq 1) {'Yes'} elseif ($uac -eq 0) {'No'} else {'Unknown'})`n"
$consentDesc = switch ($consent) { 0 {"Elevate without prompting"} 1 {"Prompt for credentials on secure desktop"} 2 {"Prompt for consent on secure desktop"} 3 {"Prompt for credentials"} 4 {"Prompt for consent"} 5 {"Prompt for consent for non-Windows binaries (default)"} default {"Unknown ($consent)"} }
$r += "  Admin Prompt Behavior: $consentDesc`n"

# --- BitLocker ---
$bl = try { Get-BitLockerVolume -ErrorAction Stop } catch { $null }
$blStatus = "N/A"
$r += "`n=== BitLocker ===`n"
if ($bl) {
    foreach ($v in $bl) {
        $r += "  $($v.MountPoint) $($v.VolumeType): $($v.ProtectionStatus)`n"
        if ($v.ProtectionStatus -eq 'On') { $blStatus = "ON" }
        elseif ($blStatus -ne "ON") { $blStatus = "OFF" }
    }
} else { $r += "  Not available (Windows Home or not supported)`n" }

# --- Pending Updates ---
$r += "`n=== Updates ===`n"
$hotfixes = Get-CimInstance -ClassName Win32_QuickFixEngineering -ErrorAction SilentlyContinue | Sort-Object InstalledOn -Descending | Select-Object -First 5
if ($hotfixes) {
    $r += "  Last Installed Patches:`n"
    foreach ($h in $hotfixes) {
        $date = if ($h.InstalledOn) { $h.InstalledOn.ToString('yyyy-MM-dd') } else { 'Unknown' }
        $r += "    $($h.HotFixID) ($date) $($h.Description)`n"
    }
}
$pending = try {
    $s = New-Object -ComObject Microsoft.Update.Session
    $s.CreateUpdateSearcher().Search('IsInstalled=0').Updates.Count
} catch { -1 }
if ($pending -ge 0) { $r += "  Pending Updates: $pending`n" }
else { $r += "  Pending Updates: Could not check`n" }

# --- PowerShell Execution Policy ---
$r += "`n=== PowerShell Execution Policy ===`n"
$policies = Get-ExecutionPolicy -List | Where-Object { $_.Policy -ne 'Undefined' }
foreach ($p in $policies) { $r += "  $($p.Scope): $($p.Policy)`n" }
if (-not $policies) { $r += "  All scopes: Undefined`n" }

# --- Open Shares ---
$shares = Get-SmbShare -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^\w\$|^IPC\$|^ADMIN\$' }
$r += "`n=== Open Shares ===`n"
if ($shares) {
    foreach ($s in $shares) { $r += "  $($s.Name): $($s.Path) ($($s.ShareType))`n" }
} else { $r += "  None (only default admin shares)`n" }

# --- Remote Desktop ---
$rdp = try { Get-ItemPropertyValue -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -ErrorAction Stop } catch { -1 }
$rdpStatus = if ($rdp -eq 0) { "ON" } elseif ($rdp -eq 1) { "OFF" } else { "N/A" }
$r += "`n=== Remote Desktop ===`n"
$r += "  $(if ($rdp -eq 0) {'ENABLED (connections allowed)'} elseif ($rdp -eq 1) {'Disabled (connections denied)'} else {'Unknown'})`n"

# --- Summary ---
$summary = "DEFENDER: $defStatus | FIREWALL: $fwStatus | UAC: $uacStatus | BITLOCKER: $blStatus | RDP: $rdpStatus"
Write-Output "=== Quick Summary ===`n  $summary`n`n$r"
"""
    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=60)
    if status != 0:
        return f"Security audit failed (exit {status}): {response.strip()}"
    return response.strip()


def _validate_hostname(target: str) -> tuple[bool, str]:
    """Validate a hostname/IP for network operations: length, characters, SSRF."""
    if not target or not target.strip():
        return False, "Target is required."
    target = target.strip()
    valid, msg = validate_text(target, max_length=253)
    if not valid:
        return False, msg
    # Only allow safe hostname characters (alphanumeric, dots, hyphens, colons for IPv6)
    import re

    if not re.match(r"^[a-zA-Z0-9._:\-]+$", target):
        return False, f"Target contains invalid characters: {target}"
    # SSRF check: resolve and check against blocked IP ranges
    try:
        ip = ipaddress.ip_address(socket.gethostbyname(target))
        for blocked in BLOCKED_IP_RANGES:
            if ip in blocked:
                return False, f"Target resolves to blocked IP range: {ip}"
    except socket.gaierror:
        return False, f"Could not resolve hostname: {target}"
    return True, "OK"


@mcp.tool(
    name="Network-Tool",
    description='Network diagnostics and information. Actions: "status" (default) shows adapters/IPs/gateway/DNS/connectivity, "connections" shows active TCP and listening ports with process names (top 30), "ping" pings a host (requires target), "dns" resolves a hostname (requires target), "wifi" shows current connection and available networks.',
)
def network_tool(
    action: Literal["status", "connections", "ping", "dns", "wifi"] = "status",
    target: str = None,
) -> str:
    ensure_com()

    if action == "status":
        ps_script = r"""
$r = ""

# Adapters
$r += "=== Network Adapters ===`n"
$adapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' }
foreach ($a in $adapters) {
    $r += "  $($a.Name) ($($a.InterfaceDescription)): $($a.LinkSpeed)`n"
    $ips = Get-NetIPAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.AddressFamily -eq 'IPv4' }
    foreach ($ip in $ips) { $r += "    IPv4: $($ip.IPAddress)/$($ip.PrefixLength)`n" }
    $ips6 = Get-NetIPAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.AddressFamily -eq 'IPv6' -and $_.IPAddress -notlike 'fe80*' }
    foreach ($ip in $ips6) { $r += "    IPv6: $($ip.IPAddress)/$($ip.PrefixLength)`n" }
}

# Gateway
$gw = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($gw) { $r += "`nDefault Gateway: $($gw.NextHop)`n" }

# DNS Servers
$dns = Get-DnsClientServerAddress -ErrorAction SilentlyContinue | Where-Object { $_.ServerAddresses.Count -gt 0 -and $_.AddressFamily -eq 2 } | Select-Object -First 3
$r += "`nDNS Servers:`n"
foreach ($d in $dns) { $r += "  $($d.InterfaceAlias): $($d.ServerAddresses -join ', ')`n" }

# Connectivity
$ping = Test-Connection -ComputerName 8.8.8.8 -Count 1 -Quiet -ErrorAction SilentlyContinue
$r += "`nInternet Connectivity: $(if ($ping) {'OK'} else {'UNREACHABLE'})`n"

Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=30)
        if status != 0:
            return f"Network status failed: {response.strip()}"
        return response.strip()

    elif action == "connections":
        ps_script = r"""
$r = "=== Active Connections ===`n"
$conns = Get-NetTCPConnection -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq 'Established' -or $_.State -eq 'Listen' } |
    Sort-Object State, LocalPort |
    Select-Object -First 30

foreach ($c in $conns) {
    $proc = try { (Get-Process -Id $c.OwningProcess -ErrorAction Stop).ProcessName } catch { "PID:$($c.OwningProcess)" }
    $remote = if ($c.State -eq 'Listen') { '*' } else { "$($c.RemoteAddress):$($c.RemotePort)" }
    $r += "  $($c.State.ToString().PadRight(12)) $($c.LocalAddress):$($c.LocalPort) -> $remote [$proc]`n"
}

$listening = (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue).Count
$established = (Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue).Count
$r += "`nTotal: $listening listening, $established established`n"

Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=15)
        if status != 0:
            return f"Connection listing failed: {response.strip()}"
        return response.strip()

    elif action == "ping":
        if not target:
            return "Error: 'target' parameter is required for ping action."
        ok, msg = _validate_hostname(target)
        if not ok:
            return f"Validation Error: {msg}"
        ps_script = f"Test-Connection -ComputerName '{target}' -Count 4 | Format-Table -Property Address, Latency, Status -AutoSize | Out-String"
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=30)
        if status != 0:
            return f"Ping failed: {response.strip()}"
        return f"Ping {target}:\n{response.strip()}"

    elif action == "dns":
        if not target:
            return "Error: 'target' parameter is required for dns action."
        ok, msg = _validate_hostname(target)
        if not ok:
            return f"Validation Error: {msg}"
        ps_script = f"Resolve-DnsName -Name '{target}' -ErrorAction Stop | Format-Table -AutoSize | Out-String"
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=15)
        if status != 0:
            return f"DNS resolution failed: {response.strip()}"
        return f"DNS lookup for {target}:\n{response.strip()}"

    elif action == "wifi":
        ps_script = r"""
$r = "=== WiFi Status ===`n"
$iface = netsh wlan show interfaces 2>&1
if ($LASTEXITCODE -eq 0 -and $iface -notmatch 'not running') {
    $r += ($iface | Out-String).Trim() + "`n"
} else {
    $r += "  WiFi service not available`n"
}

$r += "`n=== Available Networks ===`n"
$nets = netsh wlan show networks 2>&1
if ($LASTEXITCODE -eq 0 -and $nets -notmatch 'not running') {
    $r += ($nets | Out-String).Trim() + "`n"
} else {
    $r += "  Could not scan networks`n"
}

Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=15)
        if status != 0:
            return f"WiFi info failed: {response.strip()}"
        return response.strip()

    return f"Unknown action: {action}"


# ── Storage Tool ──────────────────────────────────────────────────────────────


@mcp.tool(
    name="Storage-Tool",
    description='Storage analysis and cleanup. Actions: "breakdown" (file type breakdown by extension with sizes), '
    '"stale" (find files untouched for N days), "compress" (preview zip compression of stale files), '
    '"compress-run" (execute compression), "dedup" (preview duplicate removal), '
    '"dedup-run" (remove duplicates to Recycle Bin, keep first by path), '
    '"archive" (preview moving old files to dated archive folders), '
    '"archive-run" (execute archive move). All destructive actions use Recycle Bin.',
)
def storage_tool(
    action: Literal[
        "breakdown",
        "stale",
        "compress",
        "compress-run",
        "dedup",
        "dedup-run",
        "archive",
        "archive-run",
    ],
    path: str,
    days: int = 365,
    minSizeMB: float = 0,
    extensions: str = "",
) -> str:
    ensure_com()
    try:
        path = _sanitize_path(path)
        _check_allowed_path(path)
    except ValueError as e:
        return f"Error: {e}"
    days = max(1, min(3650, days))

    # Build extension filter clause for PS (shared by several actions)
    ext_filter = ""
    if extensions.strip():
        for ext in extensions.split(","):
            ext = ext.strip().lstrip(".")
            try:
                _validate_extension(ext)
            except ValueError as e:
                return f"Error: {e}"
        ext_list = "|".join(
            f"\\.{e.strip().lstrip('.')}" for e in extensions.split(",")
        )
        ext_filter = f"| Where-Object {{ $_.Extension -match '^({ext_list})$' }}"

    min_bytes_filter = ""
    if minSizeMB > 0:
        min_bytes_filter = (
            f"| Where-Object {{ $_.Length -ge {int(minSizeMB * 1048576)} }}"
        )

    if action == "breakdown":
        ps_script = f"""
$target = '{path}'
$r = "=== File Type Breakdown: $target ===`n`n"
$files = [System.IO.Directory]::EnumerateFiles($target, '*', [System.IO.SearchOption]::AllDirectories)
$extMap = @{{}}
$totalSize = [long]0
$totalCount = 0
foreach ($f in $files) {{
    try {{
        $info = [System.IO.FileInfo]::new($f)
        $ext = if ($info.Extension) {{ $info.Extension.ToLower() }} else {{ '(no ext)' }}
        if (-not $extMap.ContainsKey($ext)) {{ $extMap[$ext] = @{{ Count = 0; Size = [long]0 }} }}
        $extMap[$ext].Count++
        $extMap[$ext].Size += $info.Length
        $totalSize += $info.Length
        $totalCount++
    }} catch {{ }}
}}
$sorted = $extMap.GetEnumerator() | Sort-Object {{ $_.Value.Size }} -Descending | Select-Object -First 30
foreach ($s in $sorted) {{
    $pct = if ($totalSize -gt 0) {{ [math]::Round($s.Value.Size / $totalSize * 100, 1) }} else {{ 0 }}
    $r += "  $($s.Key.PadRight(12)) $([math]::Round($s.Value.Size/1GB,2).ToString('0.00').PadLeft(8)) GB  $($s.Value.Count.ToString().PadLeft(6)) files  ($pct%)`n"
}}
$r += "`nTotal: $([math]::Round($totalSize/1GB,2)) GB in $totalCount files`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=300)
        if status != 0:
            return f"Breakdown failed: {response.strip()}"
        return response.strip()

    elif action == "stale":
        ps_script = f"""
$target = '{path}'
$cutoff = (Get-Date).AddDays(-{days})
$minBytes = {int(minSizeMB * 1048576)}
$r = "=== Stale Files (untouched for {days}+ days) in $target ===`n`n"
$files = Get-ChildItem -Path $target -Recurse -File -Force -ErrorAction SilentlyContinue {ext_filter} {min_bytes_filter} | Where-Object {{ $_.LastWriteTime -lt $cutoff -and $_.LastAccessTime -lt $cutoff }} | Sort-Object Length -Descending | Select-Object -First 50
$totalSize = [long]0
$count = 0
foreach ($f in $files) {{
    $count++
    $totalSize += $f.Length
    $sizeMB = [math]::Round($f.Length / 1MB, 1)
    $age = [math]::Round(((Get-Date) - $f.LastWriteTime).TotalDays)
    $r += "  $($sizeMB.ToString().PadLeft(10)) MB  $($age.ToString().PadLeft(5))d ago  $($f.FullName)`n"
}}
$r += "`nShowing $count files, total: $([math]::Round($totalSize/1MB,1)) MB`n"
if ($count -eq 50) {{ $r += "(Results capped at 50 — use extensions or minSizeMB to narrow)`n" }}
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=120)
        if status != 0:
            return f"Stale scan failed: {response.strip()}"
        return response.strip()

    elif action == "compress":
        ps_script = f"""
$target = '{path}'
$cutoff = (Get-Date).AddDays(-{days})
$minBytes = {int(minSizeMB * 1048576)}
$r = "=== Compression Preview: files untouched for {days}+ days ===`n`n"
$files = Get-ChildItem -Path $target -Recurse -File -Force -ErrorAction SilentlyContinue {ext_filter} {min_bytes_filter} | Where-Object {{ $_.LastWriteTime -lt $cutoff -and $_.LastAccessTime -lt $cutoff -and $_.Extension -ne '.zip' -and $_.Extension -ne '.7z' -and $_.Extension -ne '.gz' }}
$totalSize = [long]0
$count = 0
$byFolder = @{{}}
foreach ($f in $files) {{
    $count++
    $totalSize += $f.Length
    $folder = $f.DirectoryName
    if (-not $byFolder.ContainsKey($folder)) {{ $byFolder[$folder] = @{{ Count = 0; Size = [long]0 }} }}
    $byFolder[$folder].Count++
    $byFolder[$folder].Size += $f.Length
}}
$sorted = $byFolder.GetEnumerator() | Sort-Object {{ $_.Value.Size }} -Descending | Select-Object -First 20
foreach ($s in $sorted) {{
    $r += "  $([math]::Round($s.Value.Size/1MB,1).ToString().PadLeft(10)) MB  $($s.Value.Count.ToString().PadLeft(5)) files  $($s.Key)`n"
}}
$r += "`nTotal: $count files, $([math]::Round($totalSize/1MB,1)) MB`n"
$r += "Would create .zip archives per folder. Use 'compress-run' to execute.`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=120)
        if status != 0:
            return f"Compress preview failed: {response.strip()}"
        return response.strip()

    elif action == "compress-run":
        ps_script = f"""
$target = '{path}'
$cutoff = (Get-Date).AddDays(-{days})
$minBytes = {int(minSizeMB * 1048576)}
$r = "=== Compression Run ===`n`n"
$files = Get-ChildItem -Path $target -Recurse -File -Force -ErrorAction SilentlyContinue {ext_filter} {min_bytes_filter} | Where-Object {{ $_.LastWriteTime -lt $cutoff -and $_.LastAccessTime -lt $cutoff -and $_.Extension -ne '.zip' -and $_.Extension -ne '.7z' -and $_.Extension -ne '.gz' }}

# Group by parent folder
$groups = $files | Group-Object DirectoryName
$archiveCount = 0
$totalSaved = [long]0
$shell = New-Object -ComObject Shell.Application

foreach ($g in $groups) {{
    $folder = $g.Name
    $stamp = Get-Date -Format 'yyyyMMdd'
    $zipName = Join-Path $folder "_archive_$stamp.zip"
    $counter = 1
    while (Test-Path $zipName) {{
        $zipName = Join-Path $folder "_archive_$($stamp)_$counter.zip"
        $counter++
    }}
    try {{
        Compress-Archive -Path ($g.Group | ForEach-Object {{ $_.FullName }}) -DestinationPath $zipName -CompressionLevel Optimal -ErrorAction Stop
        $zipSize = (Get-Item $zipName).Length
        $origSize = ($g.Group | Measure-Object -Property Length -Sum).Sum
        $saved = $origSize - $zipSize

        # Move originals to Recycle Bin
        foreach ($f in $g.Group) {{
            $shell.Namespace(0).ParseName($f.FullName).InvokeVerb('delete')
        }}

        $archiveCount++
        $totalSaved += $saved
        $r += "  Created: $zipName ($([math]::Round($zipSize/1MB,1)) MB from $([math]::Round($origSize/1MB,1)) MB, $($g.Count) files)`n"
    }} catch {{
        $r += "  FAILED: $folder - $($_.Exception.Message)`n"
    }}
}}
$r += "`nCreated $archiveCount archives, saved $([math]::Round($totalSaved/1MB,1)) MB`n"
$r += "Original files moved to Recycle Bin (recoverable).`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=600)
        if status != 0:
            return f"Compression failed: {response.strip()}"
        return response.strip()

    elif action == "dedup":
        ps_script = f"""
$target = '{path}'
$minBytes = {int(max(minSizeMB, 0.001) * 1048576)}
$r = "=== Duplicate Preview ===`n`n"
$files = Get-ChildItem -Path $target -Recurse -File -Force -ErrorAction SilentlyContinue {ext_filter} | Where-Object {{ $_.Length -ge $minBytes }}

# Group by size first (fast pre-filter)
$sizeGroups = $files | Group-Object Length | Where-Object {{ $_.Count -gt 1 }}
$dupSets = 0
$reclaimable = [long]0
$dupDetails = @()

foreach ($sg in $sizeGroups) {{
    # Hash files with same size
    $hashes = @{{}}
    foreach ($f in $sg.Group) {{
        $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
        if ($hash) {{
            if (-not $hashes.ContainsKey($hash)) {{ $hashes[$hash] = @() }}
            $hashes[$hash] += $f
        }}
    }}
    foreach ($h in $hashes.GetEnumerator()) {{
        if ($h.Value.Count -gt 1) {{
            $dupSets++
            $sorted = $h.Value | Sort-Object FullName
            $keep = $sorted[0]
            $extras = $sorted[1..($sorted.Count-1)]
            $setSize = ($extras | Measure-Object -Property Length -Sum).Sum
            $reclaimable += $setSize
            $sizeMB = [math]::Round($keep.Length / 1MB, 1)
            $r += "--- Set $dupSets ($sizeMB MB each, $($h.Value.Count) copies) ---`n"
            $r += "  KEEP:   $($keep.FullName)`n"
            foreach ($e in $extras) {{
                $r += "  REMOVE: $($e.FullName)`n"
            }}
            $r += "`n"
            if ($dupSets -ge 20) {{ break }}
        }}
    }}
    if ($dupSets -ge 20) {{ break }}
}}
$r += "Found $dupSets duplicate sets, $([math]::Round($reclaimable/1MB,1)) MB reclaimable`n"
$r += "Keep strategy: first by path (logical location). Use 'dedup-run' to execute.`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=300)
        if status != 0:
            return f"Dedup preview failed: {response.strip()}"
        return response.strip()

    elif action == "dedup-run":
        ps_script = f"""
$target = '{path}'
$minBytes = {int(max(minSizeMB, 0.001) * 1048576)}
$r = "=== Duplicate Removal ===`n`n"
$files = Get-ChildItem -Path $target -Recurse -File -Force -ErrorAction SilentlyContinue {ext_filter} | Where-Object {{ $_.Length -ge $minBytes }}

$sizeGroups = $files | Group-Object Length | Where-Object {{ $_.Count -gt 1 }}
$dupSets = 0
$removed = 0
$reclaimedBytes = [long]0
$shell = New-Object -ComObject Shell.Application

foreach ($sg in $sizeGroups) {{
    $hashes = @{{}}
    foreach ($f in $sg.Group) {{
        $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
        if ($hash) {{
            if (-not $hashes.ContainsKey($hash)) {{ $hashes[$hash] = @() }}
            $hashes[$hash] += $f
        }}
    }}
    foreach ($h in $hashes.GetEnumerator()) {{
        if ($h.Value.Count -gt 1) {{
            $dupSets++
            $sorted = $h.Value | Sort-Object FullName
            $extras = $sorted[1..($sorted.Count-1)]
            foreach ($e in $extras) {{
                try {{
                    $shell.Namespace(0).ParseName($e.FullName).InvokeVerb('delete')
                    $removed++
                    $reclaimedBytes += $e.Length
                }} catch {{
                    $r += "  FAILED: $($e.FullName) - $($_.Exception.Message)`n"
                }}
            }}
        }}
    }}
}}
$r += "Processed $dupSets duplicate sets`n"
$r += "Removed $removed files ($([math]::Round($reclaimedBytes/1MB,1)) MB) to Recycle Bin`n"
$r += "Kept first copy by path (logical location) for each set.`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=600)
        if status != 0:
            return f"Dedup run failed: {response.strip()}"
        return response.strip()

    elif action == "archive":
        ps_script = f"""
$target = '{path}'
$cutoff = (Get-Date).AddDays(-{days})
$minBytes = {int(minSizeMB * 1048576)}
$r = "=== Archive Preview: files untouched for {days}+ days ===`n`n"
$files = Get-ChildItem -Path $target -File -Force -ErrorAction SilentlyContinue {ext_filter} {min_bytes_filter} | Where-Object {{ $_.LastWriteTime -lt $cutoff -and $_.LastAccessTime -lt $cutoff }}
$totalSize = [long]0
$count = 0
$byQuarter = @{{}}
foreach ($f in $files) {{
    $count++
    $totalSize += $f.Length
    $yr = $f.LastWriteTime.Year
    $q = [math]::Ceiling($f.LastWriteTime.Month / 3)
    $key = "$yr-Q$q"
    if (-not $byQuarter.ContainsKey($key)) {{ $byQuarter[$key] = @{{ Count = 0; Size = [long]0 }} }}
    $byQuarter[$key].Count++
    $byQuarter[$key].Size += $f.Length
}}
$sorted = $byQuarter.GetEnumerator() | Sort-Object Name
foreach ($s in $sorted) {{
    $r += "  _archive/$($s.Key)/  $($s.Value.Count.ToString().PadLeft(5)) files  $([math]::Round($s.Value.Size/1MB,1).ToString().PadLeft(8)) MB`n"
}}
$r += "`nTotal: $count files, $([math]::Round($totalSize/1MB,1)) MB`n"
$r += "Would move to _archive/<year>-Q<quarter>/ subfolders. Use 'archive-run' to execute.`n"
$r += "Note: only archives files in the root of '$target', not subfolders.`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=60)
        if status != 0:
            return f"Archive preview failed: {response.strip()}"
        return response.strip()

    elif action == "archive-run":
        ps_script = f"""
$target = '{path}'
$cutoff = (Get-Date).AddDays(-{days})
$minBytes = {int(minSizeMB * 1048576)}
$r = "=== Archive Run ===`n`n"
$files = Get-ChildItem -Path $target -File -Force -ErrorAction SilentlyContinue {ext_filter} {min_bytes_filter} | Where-Object {{ $_.LastWriteTime -lt $cutoff -and $_.LastAccessTime -lt $cutoff }}
$moved = 0
$totalSize = [long]0

foreach ($f in $files) {{
    $yr = $f.LastWriteTime.Year
    $q = [math]::Ceiling($f.LastWriteTime.Month / 3)
    $archiveDir = Join-Path $target "_archive/$yr-Q$q"
    if (-not (Test-Path $archiveDir)) {{
        New-Item -Path $archiveDir -ItemType Directory -Force | Out-Null
    }}
    $dest = Join-Path $archiveDir $f.Name
    $counter = 1
    while (Test-Path $dest) {{
        $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
        $ext = $f.Extension
        $dest = Join-Path $archiveDir "$($base)_$counter$ext"
        $counter++
    }}
    try {{
        Move-Item -LiteralPath $f.FullName -Destination $dest -Force
        $moved++
        $totalSize += $f.Length
    }} catch {{
        $r += "  FAILED: $($f.Name) - $($_.Exception.Message)`n"
    }}
}}
$r += "Moved $moved files ($([math]::Round($totalSize/1MB,1)) MB) to _archive/ subfolders`n"
$r += "Organized by quarter (e.g., _archive/2025-Q3/).`n"
Write-Output $r
"""
        valid, msg = validate_command(ps_script, trusted=True)
        if not valid:
            return f"Security Error: {msg}"
        response, status = desktop.execute_command(ps_script, timeout=120)
        if status != 0:
            return f"Archive run failed: {response.strip()}"
        return response.strip()

    return f"Unknown action: {action}"


# ── Disk Analysis & File Management Tools ─────────────────────────────────────

import re as _re
from datetime import datetime as _datetime


def _sanitize_path(val: str) -> str:
    """Reject paths with PowerShell metacharacters that could enable injection."""
    if _re.search(r"['\"`$;|&{}\[\]]", val):
        raise ValueError(f"Path contains unsafe characters: {val!r}")
    return val


def _sanitize_name(val: str) -> str:
    """Reject filenames with path separators or PS metacharacters."""
    if _re.search(r"['\"`$;|&{}()\\/]", val):
        raise ValueError(f"Name contains unsafe characters: {val!r}")
    return val


def _validate_date(val: str, param: str) -> str:
    """Validate date is YYYY-MM-DD format."""
    try:
        _datetime.strptime(val, "%Y-%m-%d")
    except ValueError:
        raise ValueError(f"Invalid date for {param}: must be YYYY-MM-DD, got {val!r}")
    return val


def _validate_extension(val: str) -> str:
    """Validate extension is alphanumeric only."""
    cleaned = val.strip().strip(".")
    if not _re.match(r"^[a-zA-Z0-9]+$", cleaned):
        raise ValueError(f"Invalid extension: {val!r}")
    return cleaned


def _check_allowed_path(path: str) -> str:
    """Verify path is within ALLOWED_PATHS restrictions."""
    if ALLOWED_PATHS:
        resolved = os.path.normpath(os.path.abspath(path)).lower()
        if not any(
            resolved == norm
            or resolved.startswith(norm if norm.endswith(os.sep) else norm + os.sep)
            for ap in ALLOWED_PATHS
            for norm in [os.path.normpath(ap).lower()]
        ):
            raise ValueError(f"Path not in allowed paths: {path}")
    return path


@mcp.tool(
    name="Disk-Analysis-Tool",
    description="Analyze disk usage for a folder or drive. Returns top subfolders by size, free/used space, and file count. Parameters: path (folder path, default C:\\), depth (subfolder depth 1-3, default 1), minSizeMB (minimum folder size to report, default 100).",
)
def disk_analysis_tool(path: str = "C:\\", depth: int = 1, minSizeMB: int = 100) -> str:
    try:
        path = _sanitize_path(path)
        _check_allowed_path(path)
    except ValueError as e:
        return f"Error: {e}"
    depth = max(1, min(3, depth))
    ps_script = f"""
$target = '{path}'
$depth = {depth}
$minBytes = {minSizeMB} * 1MB
$r = ""

# Drive info
$drive = Get-PSDrive -PSProvider FileSystem | Where-Object {{ $target.StartsWith($_.Root) }} | Select-Object -First 1
if ($drive) {{
    $r += "=== Drive $($drive.Name): ===`n"
    $r += "  Used: $([math]::Round($drive.Used/1GB,1)) GB`n"
    $r += "  Free: $([math]::Round($drive.Free/1GB,1)) GB`n"
    $r += "  Total: $([math]::Round(($drive.Used+$drive.Free)/1GB,1)) GB`n`n"
}}

# Folder sizes — single-pass: enumerate all files once, group by top-level subfolder
$r += "=== Folders > {minSizeMB} MB in $target (depth $depth) ===`n"
$totalFiles = 0
$folderSizes = @{{}}
foreach ($f in [System.IO.Directory]::EnumerateFiles($target, '*', [System.IO.SearchOption]::AllDirectories)) {{
    try {{
        $info = [System.IO.FileInfo]::new($f)
        $totalFiles++
        $rel = $f.Substring($target.TrimEnd('\\').Length + 1)
        $parts = $rel.Split('\\')
        if ($parts.Count -gt 1) {{
            $topKey = $parts[0]
            if ($depth -ge 2 -and $parts.Count -gt 2) {{ $topKey = $parts[0] + '\\' + $parts[1] }}
            if ($depth -ge 3 -and $parts.Count -gt 3) {{ $topKey = $parts[0] + '\\' + $parts[1] + '\\' + $parts[2] }}
            if (-not $folderSizes.ContainsKey($topKey)) {{ $folderSizes[$topKey] = [long]0 }}
            $folderSizes[$topKey] += $info.Length
        }}
    }} catch {{ }}
}}
$sorted = $folderSizes.GetEnumerator() | Where-Object {{ $_.Value -gt $minBytes }} | Sort-Object Value -Descending
foreach ($s in $sorted) {{
    $r += "  $([math]::Round($s.Value/1GB,2).ToString('0.00').PadLeft(8)) GB  $($s.Key)`n"
}}

$r += "`nTotal files: $totalFiles`n"

Write-Output $r
"""
    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=300)
    if status != 0:
        return f"Disk analysis failed: {response.strip()}"
    return response.strip()


@mcp.tool(
    name="Disk-Cleanup-Tool",
    description="Find reclaimable disk space: temp files, caches (npm, pip, bun, nuget), node_modules, Recycle Bin, browser caches, Windows Update cache. Returns sizes for each category. Does NOT delete anything — only reports.",
)
def disk_cleanup_tool() -> str:
    ps_script = r"""
$r = "=== Reclaimable Disk Space ===`n`n"

# User Temp
$size = (Get-ChildItem $env:TEMP -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
$r += "User Temp:           $([math]::Round($size,2)) GB  ($env:TEMP)`n"

# Windows Temp
$size = (Get-ChildItem "C:\Windows\Temp" -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
$r += "Windows Temp:        $([math]::Round($size,2)) GB  (C:\Windows\Temp)`n"

# npm cache
$npmPath = "$env:LOCALAPPDATA\npm-cache"
if (Test-Path $npmPath) {
    $size = (Get-ChildItem $npmPath -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    $r += "npm cache:           $([math]::Round($size,2)) GB  ($npmPath)`n"
}

# pip cache
$pipPath = "$env:LOCALAPPDATA\pip\Cache"
if (Test-Path $pipPath) {
    $size = (Get-ChildItem $pipPath -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    $r += "pip cache:           $([math]::Round($size,2)) GB  ($pipPath)`n"
}

# bun cache
$bunPath = "$env:USERPROFILE\.bun\install\cache"
if (Test-Path $bunPath) {
    $size = (Get-ChildItem $bunPath -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    $r += "bun cache:           $([math]::Round($size,2)) GB  ($bunPath)`n"
}

# Windows Update
$size = (Get-ChildItem "C:\Windows\SoftwareDistribution" -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
$r += "Windows Update:      $([math]::Round($size,2)) GB`n"

# Chrome cache
$chromePath = "$env:LOCALAPPDATA\Google\Chrome\User Data"
if (Test-Path $chromePath) {
    $size = (Get-ChildItem $chromePath -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    $r += "Chrome data:         $([math]::Round($size,2)) GB`n"
}

# Recycle Bin
try {
    $shell = New-Object -ComObject Shell.Application
    $rbSize = ($shell.NameSpace(0xA).Items() | ForEach-Object { $_.Size } | Measure-Object -Sum).Sum / 1GB
    $r += "Recycle Bin:         $([math]::Round($rbSize,2)) GB`n"
} catch {
    $r += "Recycle Bin:         (could not measure)`n"
}

$r += "`n=== node_modules (> 100 MB) ===`n"
Get-ChildItem -Path $env:USERPROFILE -Directory -Recurse -Force -Filter "node_modules" -EA SilentlyContinue -Depth 3 | Select-Object -First 20 | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -Force -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    if ($size -gt 0.1) { $r += "  $([math]::Round($size,2).ToString('0.00').PadLeft(6)) GB  $($_.FullName)`n" }
}

Write-Output $r
"""
    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=300)
    if status != 0:
        return f"Disk cleanup scan failed: {response.strip()}"
    return response.strip()


@mcp.tool(
    name="File-Search-Tool",
    description="Search for files by name pattern, extension, size, or date. Parameters: path (search root), pattern (wildcard, e.g. '*.pdf'), minSizeMB (optional), maxSizeMB (optional), newerThan (optional, e.g. '2026-01-01'), olderThan (optional), limit (max results, default 50).",
)
def file_search_tool(
    path: str,
    pattern: str = "*",
    minSizeMB: float = 0,
    maxSizeMB: float = 0,
    newerThan: str = "",
    olderThan: str = "",
    limit: int = 50,
) -> str:
    try:
        path = _sanitize_path(path)
        _check_allowed_path(path)
        if not _re.match(r"^[a-zA-Z0-9*?._\-\\ ]+$", pattern):
            return f"Error: pattern contains unsafe characters: {pattern!r}"
        if newerThan:
            newerThan = _validate_date(newerThan, "newerThan")
        if olderThan:
            olderThan = _validate_date(olderThan, "olderThan")
    except ValueError as e:
        return f"Error: {e}"
    limit = min(limit, 200)
    filters = []
    if minSizeMB > 0:
        filters.append(f"$_.Length -gt {int(minSizeMB * 1024 * 1024)}")
    if maxSizeMB > 0:
        filters.append(f"$_.Length -lt {int(maxSizeMB * 1024 * 1024)}")
    if newerThan:
        filters.append(f"$_.LastWriteTime -gt '{newerThan}'")
    if olderThan:
        filters.append(f"$_.LastWriteTime -lt '{olderThan}'")

    where_clause = ""
    if filters:
        where_clause = "| Where-Object { " + " -and ".join(filters) + " }"

    ps_script = f"""
$results = Get-ChildItem -Path '{path}' -Filter '{pattern}' -Recurse -Force -File -EA SilentlyContinue {where_clause} | Sort-Object Length -Descending | Select-Object -First {limit}
$r = "Found $($results.Count) files:`n`n"
foreach ($f in $results) {{
    $sizeMB = [math]::Round($f.Length/1MB, 1)
    $date = $f.LastWriteTime.ToString('yyyy-MM-dd')
    $r += "$($sizeMB.ToString().PadLeft(10)) MB  $date  $($f.FullName)`n"
}}
Write-Output $r
"""
    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=120)
    if status != 0:
        return f"File search failed: {response.strip()}"
    return response.strip()


@mcp.tool(
    name="File-Manage-Tool",
    description="Manage files: copy, move, rename, or get info. Actions: 'copy' (src→dst), 'move' (src→dst), 'rename' (path, newName), 'info' (path — returns size, dates, attributes), 'list' (path — list directory contents with sizes).",
)
def file_manage_tool(
    action: str,
    path: str,
    destination: str = "",
    newName: str = "",
) -> str:
    try:
        path = _sanitize_path(path)
        _check_allowed_path(path)
        if destination:
            destination = _sanitize_path(destination)
            _check_allowed_path(destination)
        if newName:
            _sanitize_name(newName)
    except ValueError as e:
        return f"Error: {e}"
    if action == "info":
        ps_script = f"""
$item = Get-Item -LiteralPath '{path}' -Force
$r = "Path: $($item.FullName)`n"
$r += "Type: $(if ($item.PSIsContainer) {{ 'Directory' }} else {{ 'File' }})`n"
if (-not $item.PSIsContainer) {{
    $r += "Size: $([math]::Round($item.Length/1MB,2)) MB ($($item.Length) bytes)`n"
}}
$r += "Created: $($item.CreationTime.ToString('yyyy-MM-dd HH:mm:ss'))`n"
$r += "Modified: $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))`n"
$r += "Accessed: $($item.LastAccessTime.ToString('yyyy-MM-dd HH:mm:ss'))`n"
$r += "Attributes: $($item.Attributes)`n"
if ($item.PSIsContainer) {{
    $childCount = (Get-ChildItem -LiteralPath '{path}' -Force -EA SilentlyContinue).Count
    $r += "Children: $childCount items`n"
}}
Write-Output $r
"""
    elif action == "list":
        ps_script = f"""
$items = Get-ChildItem -LiteralPath '{path}' -Force -EA SilentlyContinue | Sort-Object {{ -not $_.PSIsContainer }}, Name
$r = "Contents of {path}:`n`n"
foreach ($item in $items) {{
    if ($item.PSIsContainer) {{
        $r += "  [DIR]                $($item.Name)`n"
    }} else {{
        $sizeMB = [math]::Round($item.Length/1MB, 1)
        $r += "  $($sizeMB.ToString().PadLeft(10)) MB  $($item.Name)`n"
    }}
}}
$r += "`n$($items.Count) items`n"
Write-Output $r
"""
    elif action == "copy":
        if not destination:
            return "Error: 'destination' required for copy action"
        ps_script = f"Copy-Item -LiteralPath '{path}' -Destination '{destination}' -Recurse -Force; Write-Output 'Copied: {path} -> {destination}'"
    elif action == "move":
        if not destination:
            return "Error: 'destination' required for move action"
        ps_script = f"Move-Item -LiteralPath '{path}' -Destination '{destination}' -Force; Write-Output 'Moved: {path} -> {destination}'"
    elif action == "rename":
        if not newName:
            return "Error: 'newName' required for rename action"
        ps_script = f"Rename-Item -LiteralPath '{path}' -NewName '{newName}' -Force; Write-Output 'Renamed to: {newName}'"
    else:
        return f"Unknown action: {action}. Use: info, list, copy, move, rename"

    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=60)
    if status != 0:
        return f"File operation failed: {response.strip()}"
    return response.strip()


@mcp.tool(
    name="Duplicate-Finder-Tool",
    description="Find duplicate files by size + hash in a folder. Parameters: path (folder to scan), minSizeMB (minimum file size, default 1), extensions (comma-separated, e.g. 'pdf,jpg', default all).",
)
def duplicate_finder_tool(
    path: str,
    minSizeMB: float = 1,
    extensions: str = "",
) -> str:
    try:
        path = _sanitize_path(path)
        _check_allowed_path(path)
    except ValueError as e:
        return f"Error: {e}"

    ext_filter = ""
    if extensions:
        exts = extensions.replace(" ", "").split(",")
        validated_exts = []
        for e in exts:
            try:
                validated_exts.append(_validate_extension(e))
            except ValueError as ve:
                return f"Error: {ve}"
        ext_patterns = " -or ".join(
            [f'$_.Extension -eq ".{e}"' for e in validated_exts]
        )
        ext_filter = f"| Where-Object {{ {ext_patterns} }}"

    ps_script = f"""
$minBytes = {int(minSizeMB * 1024 * 1024)}
$files = Get-ChildItem -Path '{path}' -Recurse -Depth 5 -Force -File -EA SilentlyContinue | Where-Object {{ $_.Length -gt $minBytes }} {ext_filter}

# Group by size first (fast pre-filter)
$sizeGroups = $files | Group-Object Length | Where-Object {{ $_.Count -gt 1 }}
$r = "Scanning $($files.Count) files for duplicates...`n`n"
$dupSets = 0
$dupBytes = 0

foreach ($group in $sizeGroups) {{
    # Hash files with same size
    $hashes = @{{}}
    foreach ($f in $group.Group) {{
        try {{
            $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256 -EA SilentlyContinue).Hash
            if ($hash) {{
                if (-not $hashes[$hash]) {{ $hashes[$hash] = @() }}
                $hashes[$hash] += $f
            }}
        }} catch {{}}
    }}
    foreach ($h in $hashes.GetEnumerator()) {{
        if ($h.Value.Count -gt 1) {{
            $dupSets++
            $sizeMB = [math]::Round($h.Value[0].Length/1MB, 1)
            $wastedMB = [math]::Round($h.Value[0].Length * ($h.Value.Count - 1) / 1MB, 1)
            $dupBytes += $h.Value[0].Length * ($h.Value.Count - 1)
            $r += "=== Duplicate set ($($h.Value.Count) copies, $sizeMB MB each, $wastedMB MB wasted) ===`n"
            foreach ($f in $h.Value) {{
                $r += "  $($f.FullName)`n"
            }}
            $r += "`n"
        }}
    }}
}}

$r += "Summary: $dupSets duplicate sets, $([math]::Round($dupBytes/1MB,1)) MB reclaimable`n"
Write-Output $r
"""
    valid, msg = validate_command(ps_script, trusted=True)
    if not valid:
        return f"Security Error: {msg}"
    response, status = desktop.execute_command(ps_script, timeout=300)
    if status != 0:
        return f"Duplicate scan failed: {response.strip()}"
    return response.strip()


if __name__ == "__main__":
    mcp.run()
