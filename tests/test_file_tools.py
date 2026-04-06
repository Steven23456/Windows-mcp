"""Tests for the 5 disk analysis & file management tools and their security helpers."""

import os
import sys
import pytest
import tempfile
from pathlib import Path
from unittest.mock import patch, MagicMock

# Add project root to path so we can import from main
sys.path.insert(0, str(Path(__file__).parent.parent))


def _make_passthrough_decorator(*args, **kwargs):
    """Return a decorator that returns the original function unchanged."""

    def decorator(func):
        return func

    return decorator


# We need to mock the heavy imports that require Windows UI automation
# before importing main.py.
# IMPORTANT: fastmcp must use a passthrough for tool/resource so that the
# decorated functions remain callable plain Python functions in tests.
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


# ── Test the security helper functions directly ──────────────────────────────


class TestSanitizePath:
    def test_clean_path_passes(self):
        from main import _sanitize_path

        assert (
            _sanitize_path(r"C:\Users\danie\Documents") == r"C:\Users\danie\Documents"
        )

    def test_path_with_spaces_passes(self):
        from main import _sanitize_path

        assert (
            _sanitize_path(r"C:\Users\danie\My Documents")
            == r"C:\Users\danie\My Documents"
        )

    def test_single_quote_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo'; rm -rf /; echo '")

    def test_dollar_sign_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo$bar")

    def test_semicolon_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo;bar")

    def test_backtick_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo`bar")

    def test_pipe_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo|bar")

    def test_ampersand_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo&bar")

    def test_double_quote_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path('C:\\foo"bar')

    def test_parentheses_allowed(self):
        from main import _sanitize_path

        assert _sanitize_path("C:\\Program Files (x86)") == "C:\\Program Files (x86)"

    def test_curly_braces_rejected(self):
        from main import _sanitize_path

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_path("C:\\foo{bar}")


class TestSanitizeName:
    def test_clean_name_passes(self):
        from main import _sanitize_name

        # No assertion error = passes
        _sanitize_name("my_file.txt")

    def test_name_with_path_separator_rejected(self):
        from main import _sanitize_name

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_name("..\\..\\etc\\passwd")

    def test_name_with_forward_slash_rejected(self):
        from main import _sanitize_name

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_name("foo/bar.txt")

    def test_name_with_single_quote_rejected(self):
        from main import _sanitize_name

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_name("file'; rm -rf /")


class TestValidateDate:
    def test_valid_date_passes(self):
        from main import _validate_date

        assert _validate_date("2026-03-29", "test") == "2026-03-29"

    def test_invalid_format_rejected(self):
        from main import _validate_date

        with pytest.raises(ValueError, match="Invalid date"):
            _validate_date("not-a-date", "test")

    def test_partial_date_rejected(self):
        from main import _validate_date

        with pytest.raises(ValueError, match="Invalid date"):
            _validate_date("2026-03", "test")

    def test_injection_attempt_rejected(self):
        from main import _validate_date

        with pytest.raises(ValueError, match="Invalid date"):
            _validate_date("2026-01-01'; Remove-Item C:\\", "test")


class TestValidateExtension:
    def test_valid_extension(self):
        from main import _validate_extension

        assert _validate_extension("pdf") == "pdf"

    def test_extension_with_dot(self):
        from main import _validate_extension

        assert _validate_extension(".pdf") == "pdf"

    def test_extension_with_spaces(self):
        from main import _validate_extension

        assert _validate_extension(" pdf ") == "pdf"

    def test_injection_rejected(self):
        from main import _validate_extension

        with pytest.raises(ValueError, match="Invalid extension"):
            _validate_extension('pdf" -or $true')

    def test_special_chars_rejected(self):
        from main import _validate_extension

        with pytest.raises(ValueError, match="Invalid extension"):
            _validate_extension("pdf;exe")

    def test_empty_rejected(self):
        from main import _validate_extension

        with pytest.raises(ValueError, match="Invalid extension"):
            _validate_extension("")


class TestCheckAllowedPath:
    def test_allowed_path_passes(self):
        from main import _check_allowed_path, ALLOWED_PATHS

        # User home should always be allowed
        home = os.path.expanduser("~")
        if ALLOWED_PATHS:
            result = _check_allowed_path(home)
            assert result == home

    def test_with_no_restrictions(self):
        from main import _check_allowed_path

        # When ALLOWED_PATHS is empty/None, all paths pass
        with patch("main.ALLOWED_PATHS", []):
            assert _check_allowed_path("C:\\anywhere") == "C:\\anywhere"


# ── Test tool functions with mocked desktop.execute_command ──────────────────


class TestDiskAnalysisTool:
    def test_injection_blocked(self):
        from main import disk_analysis_tool

        result = disk_analysis_tool(path="C:\\foo'; malicious; echo '")
        assert "Error" in result or "unsafe" in result.lower()

    def test_depth_clamped(self):
        from main import disk_analysis_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("test output", 0)
            with patch("main.ALLOWED_PATHS", []):
                disk_analysis_tool(path="C:\\Users", depth=10)
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "$depth = 3" in call_args  # clamped to max 3

    def test_default_params_work(self):
        from main import disk_analysis_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Drive C: ===\nUsed: 300 GB",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = disk_analysis_tool()
                assert "Drive" in result


class TestDiskCleanupTool:
    def test_no_user_input_no_injection(self):
        """Disk-Cleanup-Tool takes no user input — should always be safe."""
        from main import disk_cleanup_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("User Temp: 3.74 GB", 0)
            result = disk_cleanup_tool()
            assert "Temp" in result

    def test_error_returns_generic_message(self):
        from main import disk_cleanup_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("internal error details", 1)
            result = disk_cleanup_tool()
            assert "failed" in result.lower()


class TestFileSearchTool:
    def test_date_injection_blocked(self):
        from main import file_search_tool

        result = file_search_tool(
            path="C:\\Users", newerThan="2026-01-01'; Remove-Item C:\\"
        )
        assert "Error" in result

    def test_pattern_injection_blocked(self):
        from main import file_search_tool

        result = file_search_tool(path="C:\\Users", pattern="*'; rm -rf /; echo '")
        assert "Error" in result or "unsafe" in result.lower()

    def test_valid_search(self):
        from main import file_search_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("Found 5 files:", 0)
            with patch("main.ALLOWED_PATHS", []):
                result = file_search_tool(path="C:\\Users", pattern="*.pdf")
                assert "Found" in result


class TestFileManageTool:
    def test_path_injection_blocked(self):
        from main import file_manage_tool

        result = file_manage_tool(action="info", path="C:\\foo'; malicious; echo '")
        assert "Error" in result

    def test_destination_injection_blocked(self):
        from main import file_manage_tool

        result = file_manage_tool(
            action="copy", path="C:\\safe", destination="C:\\foo'; malicious"
        )
        assert "Error" in result

    def test_rename_injection_blocked(self):
        from main import file_manage_tool

        result = file_manage_tool(
            action="rename",
            path="C:\\safe\\file.txt",
            newName="file'; malicious; echo '",
        )
        assert "Error" in result

    def test_rename_path_separator_blocked(self):
        from main import file_manage_tool

        result = file_manage_tool(
            action="rename", path="C:\\safe\\file.txt", newName="..\\..\\etc\\passwd"
        )
        assert "Error" in result

    def test_unknown_action(self):
        from main import file_manage_tool

        with patch("main.ALLOWED_PATHS", []):
            result = file_manage_tool(action="delete", path="C:\\safe\\file.txt")
            assert "Unknown action" in result

    def test_copy_requires_destination(self):
        from main import file_manage_tool

        with patch("main.ALLOWED_PATHS", []):
            result = file_manage_tool(action="copy", path="C:\\safe\\file.txt")
            assert "destination" in result.lower()

    def test_rename_requires_name(self):
        from main import file_manage_tool

        with patch("main.ALLOWED_PATHS", []):
            result = file_manage_tool(action="rename", path="C:\\safe\\file.txt")
            assert "newName" in result


class TestDuplicateFinderTool:
    def test_path_injection_blocked(self):
        from main import duplicate_finder_tool

        result = duplicate_finder_tool(path="C:\\foo'; malicious")
        assert "Error" in result

    def test_extension_injection_blocked(self):
        from main import duplicate_finder_tool

        with patch("main.ALLOWED_PATHS", []):
            result = duplicate_finder_tool(
                path="C:\\Users", extensions='pdf" -or $true; Remove-Item'
            )
            assert "Error" in result

    def test_valid_extensions(self):
        from main import duplicate_finder_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("Summary: 0 duplicate sets", 0)
            with patch("main.ALLOWED_PATHS", []):
                result = duplicate_finder_tool(
                    path="C:\\Users", extensions="pdf,jpg,png"
                )
                assert "duplicate" in result.lower() or "Summary" in result

    def test_uses_sha256(self):
        from main import duplicate_finder_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("Summary: 0", 0)
            with patch("main.ALLOWED_PATHS", []):
                duplicate_finder_tool(path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "SHA256" in call_args
                assert "MD5" not in call_args
