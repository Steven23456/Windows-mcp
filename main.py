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
    pg.click(button=button, clicks=clicks)
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
                break  # Stop on error
        return f'Replay "{name}" complete:\n' + "\n".join(results)

    return f"Unknown action: {action}"


if __name__ == "__main__":
    mcp.run()
