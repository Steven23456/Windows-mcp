from uiautomation import (
    GetScreenSize,
    Control,
    GetRootControl,
    ControlType,
    GetFocusedControl,
    SetWindowTopmost,
)
from src.desktop.views import DesktopState, App, Size
from src.desktop.config import EXCLUDED_APPS
from fuzzywuzzy import process
from time import sleep
from io import BytesIO
from PIL import Image
import subprocess
import pyautogui
import csv
import io


class Desktop:
    def __init__(self):
        self.desktop_state = None

    def get_state(self, use_vision: bool = False) -> DesktopState:
        # Lazy import to avoid circular dependency (tree imports desktop.config)
        from src.tree import Tree

        tree = Tree(self)
        tree_state = tree.get_state()
        if use_vision:
            nodes = tree_state.interactive_nodes
            annotated_screenshot = (
                tree.annotated_screenshot(nodes=nodes, scale=0.5)
                if use_vision
                else None
            )
            screenshot = self.screenshot_in_bytes(screenshot=annotated_screenshot)
        else:
            screenshot = None
        apps = self.get_apps()
        active_app, apps = (apps[0], apps[1:]) if len(apps) > 0 else (None, [])
        self.desktop_state = DesktopState(
            apps=apps,
            active_app=active_app,
            screenshot=screenshot,
            tree_state=tree_state,
        )
        return self.desktop_state

    def get_taskbar(self) -> Control:
        root = GetRootControl()
        taskbar = root.GetFirstChildControl()
        return taskbar

    def get_app_status(self, control: Control) -> str:
        taskbar = self.get_taskbar()
        screen_width, screen_height = GetScreenSize()
        window = control.BoundingRectangle
        taskbar_height = taskbar.BoundingRectangle.height()
        window_width, window_height = window.width(), window.height()
        if window.isempty():
            return "Minimized"
        if (
            window_width >= screen_width
            and window_height >= screen_height - taskbar_height
        ):
            return "Maximized"
        return "Normal"

    def get_element_under_cursor(self) -> Control:
        return GetFocusedControl()

    def get_apps_from_start_menu(self) -> dict[str, str]:
        command = "Get-StartApps | ConvertTo-Csv -NoTypeInformation"
        apps_info, _ = self.execute_command(command)
        reader = csv.DictReader(io.StringIO(apps_info))
        return {row.get("Name").lower(): row.get("AppID") for row in reader}

    def execute_command(
        self, command: str, timeout: int = 30, working_dir: str = None
    ) -> tuple[str, int]:
        try:
            result = subprocess.run(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command", command],
                capture_output=True,
                check=True,
                timeout=timeout,
                cwd=working_dir,
            )
            return (result.stdout.decode("utf-8", errors="replace"), result.returncode)
        except subprocess.TimeoutExpired:
            return ("Command timed out.", 1)
        except subprocess.CalledProcessError as e:
            return (
                e.stdout.decode("utf-8", errors="replace") if e.stdout else str(e),
                e.returncode,
            )

    def launch_app(self, name: str):
        apps_map = self.get_apps_from_start_menu()
        matched_app = process.extractOne(name, apps_map.keys(), score_cutoff=40)
        if matched_app is None:
            return (f"Application {name.title()} not found in start menu.", 1)
        app_name, _ = matched_app
        appid = apps_map.get(app_name)
        if appid is None:
            return (f"Application {name.title()} not found in start menu.", 1)
        if name.endswith(".exe"):
            response, status = self.execute_command(f'Start-Process "{appid}"')
        else:
            response, status = self.execute_command(
                f'Start-Process "shell:AppsFolder\\{appid}"'
            )
        return response, status

    def switch_app(self, name: str) -> tuple[str, int]:
        import ctypes

        apps_list = self.get_apps()
        apps = {app.name: app for app in apps_list}
        matched_app = process.extractOne(name, list(apps.keys()), score_cutoff=40)
        if matched_app is None:
            return (f"No window matching '{name}' found.", 1)
        app_name, score = matched_app
        app = apps.get(app_name)
        # Restore if minimized, then bring to foreground
        SW_RESTORE = 9
        ctypes.windll.user32.ShowWindow(app.handle, SW_RESTORE)
        if SetWindowTopmost(app.handle, isTopmost=True):
            # Clear topmost so window behaves normally after switching
            SetWindowTopmost(app.handle, isTopmost=False)
            return (f"Switched to '{app_name}' (score: {score}).", 0)
        else:
            return (f"Failed to bring '{app_name}' to foreground.", 1)

    def get_app_size(self, control: Control):
        window = control.BoundingRectangle
        if window.isempty():
            return Size(width=0, height=0)
        return Size(width=window.width(), height=window.height())

    def is_app_visible(self, app) -> bool:
        is_not_minimized = self.get_app_status(app) != "Minimized"
        size = self.get_app_size(app)
        area = size.width * size.height
        is_overlay = self.is_overlay_app(app)
        return not is_overlay and is_not_minimized and area > 10

    def is_overlay_app(self, element: Control) -> bool:
        no_children = len(element.GetChildren()) == 0
        is_name = "Overlay" in element.Name.strip()
        return no_children or is_name

    def get_apps(self) -> list[App]:
        try:
            sleep(0.75)
            desktop = GetRootControl()  # Get the desktop control
            elements = desktop.GetChildren()
            apps = []
            for depth, element in enumerate(elements):
                if element.Name in EXCLUDED_APPS or self.is_overlay_app(element):
                    continue
                if element.ControlType in [
                    ControlType.WindowControl,
                    ControlType.PaneControl,
                ]:
                    status = self.get_app_status(element)
                    size = self.get_app_size(element)
                    apps.append(
                        App(
                            name=element.Name,
                            depth=depth,
                            status=status,
                            size=size,
                            handle=element.NativeWindowHandle,
                        )
                    )
        except Exception as ex:
            print(f"Error: {ex}")
            apps = []
        return apps

    def screenshot_in_bytes(self, screenshot: Image.Image) -> bytes:
        buffer = BytesIO()
        screenshot.save(buffer, format="PNG")
        image_bytes = buffer.getvalue()
        return image_bytes

    def manage_window(self, name: str, action: str) -> tuple[str, int]:
        """Minimize, maximize, restore, or close a window by name."""
        import ctypes

        apps = self.get_apps()
        app_names = {app.name: app for app in apps}
        matched = process.extractOne(name, list(app_names.keys()), score_cutoff=40)
        if matched is None:
            return (f"No window matching '{name}' found.", 1)
        app_name, score = matched
        app = app_names[app_name]
        handle = app.handle

        SW_MAXIMIZE = 3
        SW_MINIMIZE = 6
        SW_RESTORE = 9
        WM_CLOSE = 0x0010

        user32 = ctypes.windll.user32
        if action == "minimize":
            user32.ShowWindow(handle, SW_MINIMIZE)
        elif action == "maximize":
            user32.ShowWindow(handle, SW_MAXIMIZE)
        elif action == "restore":
            user32.ShowWindow(handle, SW_RESTORE)
        elif action == "close":
            user32.PostMessageW(handle, WM_CLOSE, 0, 0)
        else:
            return (f"Unknown action: {action}", 1)

        return (f"{action.title()}d {app_name}.", 0)

    def get_monitors(self) -> list[dict]:
        """Get info about all connected monitors using ctypes Win32 API."""
        import ctypes
        import ctypes.wintypes

        monitors = []
        user32 = ctypes.windll.user32

        # Get virtual screen dimensions
        SM_CXVIRTUALSCREEN = 78
        SM_CYVIRTUALSCREEN = 79
        SM_XVIRTUALSCREEN = 76
        SM_YVIRTUALSCREEN = 77
        virtual_w = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
        virtual_h = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)
        virtual_x = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
        virtual_y = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)

        # MONITORINFO struct
        class MONITORINFO(ctypes.Structure):
            _fields_ = [
                ("cbSize", ctypes.wintypes.DWORD),
                ("rcMonitor", ctypes.wintypes.RECT),
                ("rcWork", ctypes.wintypes.RECT),
                ("dwFlags", ctypes.wintypes.DWORD),
            ]

        MONITORINFOF_PRIMARY = 0x00000001

        def callback(hMonitor, hdcMonitor, lprcMonitor, dwData):
            info = MONITORINFO()
            info.cbSize = ctypes.sizeof(MONITORINFO)
            user32.GetMonitorInfoW(hMonitor, ctypes.byref(info))
            rc = info.rcMonitor
            monitors.append(
                {
                    "x": rc.left,
                    "y": rc.top,
                    "width": rc.right - rc.left,
                    "height": rc.bottom - rc.top,
                    "primary": bool(info.dwFlags & MONITORINFOF_PRIMARY),
                }
            )
            return True

        MONITORENUMPROC = ctypes.WINFUNCTYPE(
            ctypes.c_bool,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.wintypes.RECT),
            ctypes.POINTER(ctypes.c_int),
        )
        user32.EnumDisplayMonitors(None, None, MONITORENUMPROC(callback), 0)

        return {
            "monitors": monitors,
            "virtual_screen": {
                "x": virtual_x,
                "y": virtual_y,
                "width": virtual_w,
                "height": virtual_h,
            },
        }

    def get_screenshot(self, scale: float = 0.7) -> Image.Image:
        screenshot = pyautogui.screenshot()
        size = (screenshot.width * scale, screenshot.height * scale)
        screenshot.thumbnail(size=size, resample=Image.Resampling.LANCZOS)
        return screenshot
