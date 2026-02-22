import os
from contextlib import asynccontextmanager
from fastmcp.utilities.types import Image
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


def validate_command(command: str) -> tuple[bool, str]:
    """Validate a PowerShell command against security rules."""
    if len(command) > MAX_COMMAND_LENGTH:
        return False, f"Command exceeds maximum length ({MAX_COMMAND_LENGTH} chars)."

    # Check for blocked operators (injection protection)
    for op in BLOCKED_OPERATORS:
        if op in command:
            return False, f"Command contains blocked operator: {op!r}"

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
    _, status = desktop.launch_app(name)
    if status != 0:
        return f"Failed to launch {name.title()}."
    else:
        return f"Launched {name.title()}."


@mcp.tool(
    name="Powershell-Tool",
    description="Execute PowerShell commands and return the output with status code",
)
def powershell_tool(command: str, workingDir: str = None) -> str:
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
        return [state_text, Image(data=desktop_state.screenshot, format="png")]
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
    pg.click(button=button, clicks=clicks)
    pg.mouseUp()
    num_clicks = {1: "Single", 2: "Double", 3: "Triple"}
    return f"{num_clicks.get(clicks)} {button} Clicked on {control.Name} Element with ControlType {control.ControlTypeName} at ({x},{y})."


@mcp.tool(
    name="Type-Tool",
    description="Type text into input fields, text areas, or focused elements. Set clear=True to replace existing text, False to append. Click on target element coordinates first.",
)
def type_tool(loc: tuple[int, int], text: str, clear: bool = False) -> str:
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
    _, status = desktop.switch_app(name)
    if status != 0:
        return f"Failed to switch to {name.title()} window."
    else:
        return f"Switched to {name.title()} window."


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
    pg.hotkey(*shortcut)
    return f"Pressed {'+'.join(shortcut)}."


@mcp.tool(
    name="Key-Tool",
    description='Press individual keyboard keys. Supports special keys like "enter", "escape", "tab", "space", "backspace", "delete", arrow keys ("up", "down", "left", "right"), function keys ("f1"-"f12").',
)
def key_tool(key: str = "") -> str:
    pg.press(key)
    return f"Pressed the key {key}."


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
    return Image(data=image_bytes, format="png")


@mcp.tool(
    name="Window-Tool",
    description='Manage application windows: minimize, maximize, restore, or close by name. Uses fuzzy matching to find the window (e.g., "notepad", "chrome").',
)
def window_tool(
    name: str, action: Literal["minimize", "maximize", "restore", "close"]
) -> str:
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
        rect = n.bounding_rect
        cx = rect.left + rect.width() // 2
        cy = rect.top + rect.height() // 2
        lines.append(
            f"  - {n.name} [{n.control_type}] center=({cx},{cy}) "
            f"rect=({rect.left},{rect.top},{rect.right},{rect.bottom})"
        )
    return "\n".join(lines)


@mcp.tool(
    name="Wait-For-Tool",
    description="Wait until a UI element or text appears on screen, polling at regular intervals. Returns element details when found or a timeout message. Useful for waiting for apps to load or dialogs to appear.",
)
def wait_for_tool(text: str, timeout: int = 10, poll_interval: float = 1.0) -> str:
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
                rect = n.bounding_rect
                cx = rect.left + rect.width() // 2
                cy = rect.top + rect.height() // 2
                return (
                    f'Found "{text}" after {elapsed:.1f}s: '
                    f"{n.name} [{n.control_type}] center=({cx},{cy})"
                )

        time.sleep(poll_interval)


if __name__ == "__main__":
    mcp.run()
