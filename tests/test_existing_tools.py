"""Tests for PowerShell-Tool, System-Info-Tool, and Launch-Tool, plus validate_command."""

import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

# Add project root to path so we can import from main
sys.path.insert(0, str(Path(__file__).parent.parent))


def _make_passthrough_decorator(*args, **kwargs):
    """Return a decorator that returns the original function unchanged."""

    def decorator(func):
        return func

    return decorator


# FastMCP must use passthrough decorators so tool functions remain plain callables.
_fastmcp_instance_mock = MagicMock()
_fastmcp_instance_mock.tool = _make_passthrough_decorator
_fastmcp_instance_mock.resource = _make_passthrough_decorator

_fastmcp_module_mock = MagicMock()
_fastmcp_module_mock.FastMCP.return_value = _fastmcp_instance_mock

sys.modules["humancursor"] = MagicMock()
sys.modules["humancursor.SystemCursor"] = MagicMock()
sys.modules["markdownify"] = MagicMock()
sys.modules["markdownify.markdownify"] = MagicMock()
sys.modules["uiautomation"] = MagicMock()
sys.modules["pyautogui"] = MagicMock()
sys.modules["PIL"] = MagicMock()
sys.modules["PIL.Image"] = MagicMock()
sys.modules["fastmcp"] = _fastmcp_module_mock
sys.modules["fastmcp.utilities"] = MagicMock()
sys.modules["fastmcp.utilities.types"] = MagicMock()

import pytest


@pytest.fixture(autouse=True)
def _mock_ensure_com():
    """Prevent COM initialization in all tests — no Windows COM runtime needed."""
    with patch("main.ensure_com"):
        yield


# ── validate_command ─────────────────────────────────────────────────────────


class TestValidateCommand:
    """Unit tests for the validate_command security gate."""

    def test_clean_command_allowed(self):
        from main import validate_command

        ok, msg = validate_command("Get-Process")
        assert ok is True
        assert msg == "OK"

    def test_command_too_long(self):
        from main import validate_command, MAX_COMMAND_LENGTH

        long_cmd = "A" * (MAX_COMMAND_LENGTH + 1)
        ok, msg = validate_command(long_cmd)
        assert ok is False
        assert "exceeds maximum length" in msg

    def test_exactly_max_length_allowed(self):
        from main import validate_command, MAX_COMMAND_LENGTH

        # A command of exactly MAX_COMMAND_LENGTH characters should be allowed
        cmd = "Get-Process" + " " * (MAX_COMMAND_LENGTH - len("Get-Process"))
        ok, _ = validate_command(cmd)
        assert ok is True

    # BLOCKED_OPERATORS tests (semicolon and backtick)

    def test_semicolon_blocked(self):
        from main import validate_command

        ok, msg = validate_command("Get-Process; Remove-Item C:\\important")
        assert ok is False
        assert "blocked operator" in msg.lower()

    def test_backtick_blocked(self):
        from main import validate_command

        ok, msg = validate_command("Get-Process `n Remove-Item")
        assert ok is False
        assert "blocked operator" in msg.lower()

    def test_semicolon_inside_braces_allowed(self):
        """Semicolons inside {} are hashtable syntax, not command chaining."""
        from main import validate_command

        # Calculated property — common PowerShell pattern
        cmd = "Get-Process | Select-Object @{N='MemMB';E={[math]::Round($_.WorkingSet64/1MB,1)}}"
        ok, _ = validate_command(cmd)
        assert ok is True

    def test_semicolon_in_nested_braces_allowed(self):
        from main import validate_command

        cmd = "Get-Process | ForEach-Object { @{N='x';E={$_.Name}} }"
        ok, _ = validate_command(cmd)
        assert ok is True

    def test_semicolon_after_braces_blocked(self):
        """Semicolon at top-level after a closed brace is still command chaining."""
        from main import validate_command

        cmd = "Get-Process | ForEach-Object { $_ }; Remove-Item C:\\"
        ok, msg = validate_command(cmd)
        assert ok is False
        assert "blocked operator" in msg.lower()

    def test_pipe_allowed(self):
        """| is a legitimate PowerShell operator and must NOT be blocked."""
        from main import validate_command

        ok, _ = validate_command("Get-Process | Select-Object Name")
        assert ok is True

    def test_ampersand_allowed(self):
        """& is used for call operator and must NOT be blocked."""
        from main import validate_command

        ok, _ = validate_command("& notepad.exe")
        assert ok is True

    # BLOCKED_COMMANDS tests

    def test_format_blocked(self):
        from main import validate_command

        ok, msg = validate_command("format C:")
        assert ok is False
        assert "format" in msg.lower()

    def test_shutdown_blocked(self):
        from main import validate_command

        ok, msg = validate_command("shutdown /r")
        assert ok is False
        assert "shutdown" in msg.lower()

    def test_rm_blocked(self):
        from main import validate_command

        ok, msg = validate_command("rm -rf /")
        assert ok is False
        assert "rm" in msg.lower()

    def test_del_blocked(self):
        from main import validate_command

        ok, msg = validate_command("del /f /q C:\\Windows\\System32")
        assert ok is False
        assert "del" in msg.lower()

    def test_regedit_blocked(self):
        from main import validate_command

        ok, msg = validate_command("regedit /s malicious.reg")
        assert ok is False
        assert "regedit" in msg.lower()

    def test_net_blocked(self):
        from main import validate_command

        ok, msg = validate_command("net user administrator password123")
        assert ok is False
        assert "net" in msg.lower()

    def test_command_with_path_prefix_blocked(self):
        """Blocked command with full path should still be caught."""
        from main import validate_command

        ok, msg = validate_command(r"C:\Windows\system32\format.exe C:")
        assert ok is False
        assert "format" in msg.lower()

    # BLOCKED_ARGUMENTS tests

    def test_enc_argument_blocked(self):
        from main import validate_command

        ok, msg = validate_command("powershell -enc ZQBjAGgAbwAgAA==")
        assert ok is False
        assert "blocked argument" in msg.lower()

    def test_encodedcommand_blocked(self):
        from main import validate_command

        ok, msg = validate_command("powershell -EncodedCommand ZQBjAGgAbwAgAA==")
        assert ok is False
        assert "blocked argument" in msg.lower()

    def test_command_arg_blocked(self):
        from main import validate_command

        ok, msg = validate_command("powershell -command 'Remove-Item C:\\'")
        assert ok is False
        assert "blocked argument" in msg.lower()

    def test_exec_arg_blocked(self):
        from main import validate_command

        ok, msg = validate_command("pwsh --exec bypass")
        assert ok is False
        assert "blocked argument" in msg.lower()

    def test_interactive_arg_blocked(self):
        from main import validate_command

        ok, msg = validate_command("pwsh --interactive")
        assert ok is False
        assert "blocked argument" in msg.lower()

    def test_empty_command_allowed(self):
        """Empty command string should not crash validate_command."""
        from main import validate_command

        ok, _ = validate_command("")
        assert ok is True

    def test_complex_safe_command_allowed(self):
        from main import validate_command

        ok, _ = validate_command(
            "Get-ChildItem C:\\Users\\danie\\Documents -Filter *.pdf | Measure-Object"
        )
        assert ok is True


# ── Powershell-Tool ──────────────────────────────────────────────────────────


class TestPowershellTool:
    """Tests for powershell_tool input validation and execution."""

    def test_blocked_command_returns_security_error(self):
        from main import powershell_tool

        result = powershell_tool("rm -rf /")
        assert "Security Error" in result

    def test_semicolon_injection_blocked(self):
        from main import powershell_tool

        result = powershell_tool("Get-Date; Remove-Item C:\\Windows")
        assert "Security Error" in result

    def test_encoded_command_blocked(self):
        from main import powershell_tool

        result = powershell_tool("powershell -enc ZQBjAGgAbwAgAA==")
        assert "Security Error" in result

    def test_working_dir_outside_allowed_blocked(self):
        from main import powershell_tool

        with patch("main.ALLOWED_PATHS", ["C:\\Users\\danie"]):
            result = powershell_tool("Get-Date", workingDir="C:\\Windows\\System32")
            assert "Security Error" in result
            assert "outside allowed paths" in result

    def test_working_dir_inside_allowed_passes(self):
        from main import powershell_tool
        import os

        home = os.path.expanduser("~")
        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("2026-03-29", 0)
            with patch("main.ALLOWED_PATHS", [home]):
                result = powershell_tool("Get-Date", workingDir=home)
                assert "Status Code: 0" in result
                assert "2026-03-29" in result

    def test_successful_command_returns_status_and_response(self):
        from main import powershell_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("hello world", 0)
            with patch("main.ALLOWED_PATHS", []):
                result = powershell_tool("Write-Output 'hello world'")
                assert "Status Code: 0" in result
                assert "hello world" in result

    def test_failed_command_status_code_nonzero(self):
        from main import powershell_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("error message", 1)
            with patch("main.ALLOWED_PATHS", []):
                result = powershell_tool("Get-Item C:\\nonexistent")
                assert "Status Code: 1" in result

    def test_command_recorded_in_history(self):
        from main import powershell_tool, command_history

        history_len_before = len(command_history)
        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            with patch("main.ALLOWED_PATHS", []):
                powershell_tool("Get-Date")
        assert len(command_history) == history_len_before + 1
        assert command_history[-1]["command"] == "Get-Date"
        assert command_history[-1]["exitCode"] == 0

    def test_no_working_dir_passes_none(self):
        """workingDir=None should not trigger path validation."""
        from main import powershell_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            with patch("main.ALLOWED_PATHS", ["C:\\restricted"]):
                result = powershell_tool("Get-Date")
                # workingDir is None, so path check is skipped
                assert "Status Code:" in result


# ── System-Info-Tool ─────────────────────────────────────────────────────────


class TestSystemInfoTool:
    """Tests for system_info_tool."""

    def test_returns_system_info_string(self):
        from main import system_info_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== OS ===\n  Edition: Windows 11 Home\n=== CPU ===\n  Name: Intel",
                0,
            )
            result = system_info_tool()
            assert "OS" in result
            assert "CPU" in result

    def test_error_propagated_on_failure(self):
        from main import system_info_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("WMI error", 5)
            result = system_info_tool()
            assert "Error" in result
            assert "5" in result  # exit code shown

    def test_calls_execute_command_with_timeout(self):
        """system_info_tool must specify a timeout to avoid hanging."""
        from main import system_info_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("info", 0)
            system_info_tool()
            _, kwargs = mock_desktop.execute_command.call_args
            assert "timeout" in kwargs
            assert kwargs["timeout"] > 0

    def test_output_stripped(self):
        """Trailing whitespace in PS output should be stripped."""
        from main import system_info_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("  info  \n\n", 0)
            result = system_info_tool()
            assert result == "info"


# ── Launch-Tool ──────────────────────────────────────────────────────────────


class TestLaunchTool:
    """Tests for launch_tool."""

    def test_success_returns_launched_message(self):
        from main import launch_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.launch_app.return_value = (None, 0)
            result = launch_tool("notepad")
            assert "Launched" in result
            assert "Notepad" in result

    def test_failure_returns_failed_message(self):
        from main import launch_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.launch_app.return_value = (None, 1)
            result = launch_tool("nonexistent_app")
            assert "Failed" in result

    def test_name_is_title_cased_in_message(self):
        """App name should be title-cased in the return message."""
        from main import launch_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.launch_app.return_value = (None, 0)
            result = launch_tool("calculator")
            assert "Calculator" in result

    def test_launch_app_receives_original_name(self):
        """The exact name passed in should be forwarded to desktop.launch_app."""
        from main import launch_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.launch_app.return_value = (None, 0)
            launch_tool("chrome")
            mock_desktop.launch_app.assert_called_once_with("chrome")
