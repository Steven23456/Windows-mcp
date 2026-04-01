"""Tests for Storage-Tool: breakdown, stale, compress, dedup, archive actions."""

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
    """Prevent COM initialization in all tests."""
    with patch("main.ensure_com"):
        yield


# ── Input Validation ─────────────────────────────────────────────────────────


class TestStorageToolValidation:
    def test_path_injection_blocked(self):
        from main import storage_tool

        result = storage_tool(action="breakdown", path="C:\\foo'; malicious")
        assert "Error" in result

    def test_extension_injection_blocked(self):
        from main import storage_tool

        with patch("main.ALLOWED_PATHS", []):
            result = storage_tool(
                action="stale", path="C:\\Users", extensions='pdf"; Remove-Item'
            )
            assert "Error" in result

    def test_days_clamped_min(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("stale results", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="stale", path="C:\\Users", days=-5)
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "AddDays(-1)" in call_args

    def test_days_clamped_max(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("stale results", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="stale", path="C:\\Users", days=99999)
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "AddDays(-3650)" in call_args

    def test_allowed_paths_enforced(self):
        from main import storage_tool

        with patch("main.ALLOWED_PATHS", ["C:\\Users\\safe"]):
            result = storage_tool(action="breakdown", path="C:\\Windows")
            assert "Error" in result


# ── Breakdown ────────────────────────────────────────────────────────────────


class TestStorageBreakdown:
    def test_breakdown_returns_extension_info(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== File Type Breakdown ===\n  .pdf   12.50 GB  150 files  (45%)",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="breakdown", path="C:\\Users")
                assert "Breakdown" in result
                assert ".pdf" in result

    def test_breakdown_error_propagated(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("access denied", 1)
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="breakdown", path="C:\\Users")
                assert "failed" in result.lower()


# ── Stale ────────────────────────────────────────────────────────────────────


class TestStorageStale:
    def test_stale_returns_file_list(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Stale Files ===\n  100.5 MB  400d ago  C:\\old\\file.dat",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="stale", path="C:\\Users", days=30)
                assert "Stale" in result

    def test_stale_with_extensions(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("results", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="stale", path="C:\\Users", extensions="pdf,docx")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "pdf" in call_args
                assert "docx" in call_args

    def test_stale_with_min_size(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("results", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="stale", path="C:\\Users", minSizeMB=10)
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "10485760" in call_args  # 10 * 1048576


# ── Compress ─────────────────────────────────────────────────────────────────


class TestStorageCompress:
    def test_compress_preview_shows_summary(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Compression Preview ===\n  500 MB  20 files  C:\\old\nTotal: 20 files\nWould create .zip",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="compress", path="C:\\Users")
                assert "Preview" in result or "compress" in result.lower()

    def test_compress_excludes_archives(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("preview", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="compress", path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert ".zip" in call_args
                assert ".7z" in call_args
                assert ".gz" in call_args

    def test_compress_run_creates_archives(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "Created 3 archives, saved 450 MB\nOriginal files moved to Recycle Bin",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="compress-run", path="C:\\Users")
                assert "Recycle Bin" in result


# ── Dedup ────────────────────────────────────────────────────────────────────


class TestStorageDedup:
    def test_dedup_preview_shows_sets(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Duplicate Preview ===\n--- Set 1 ---\n  KEEP:   C:\\a.txt\n  REMOVE: C:\\b.txt\nFound 1 duplicate sets",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="dedup", path="C:\\Users")
                assert "KEEP" in result
                assert "REMOVE" in result

    def test_dedup_uses_sha256(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("preview", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="dedup", path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "SHA256" in call_args

    def test_dedup_keeps_first_by_path(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("preview", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="dedup", path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "Sort-Object FullName" in call_args

    def test_dedup_run_uses_recycle_bin(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "Removed 5 files to Recycle Bin",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="dedup-run", path="C:\\Users")
                assert "Recycle Bin" in result

    def test_dedup_run_script_has_shell_application(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("done", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="dedup-run", path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                assert "Shell.Application" in call_args


# ── Archive ──────────────────────────────────────────────────────────────────


class TestStorageArchive:
    def test_archive_preview_shows_quarters(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Archive Preview ===\n  _archive/2025-Q3/  10 files  200 MB\nWould move to _archive/",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="archive", path="C:\\Users")
                assert "_archive" in result

    def test_archive_run_moves_files(self):
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "Moved 15 files (300 MB) to _archive/ subfolders\nOrganized by quarter",
                0,
            )
            with patch("main.ALLOWED_PATHS", []):
                result = storage_tool(action="archive-run", path="C:\\Users")
                assert "Moved" in result
                assert "quarter" in result.lower()

    def test_archive_only_root_files(self):
        """Archive should only process files in the root of the target, not subfolders."""
        from main import storage_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("preview", 0)
            with patch("main.ALLOWED_PATHS", []):
                storage_tool(action="archive", path="C:\\Users")
                call_args = mock_desktop.execute_command.call_args[0][0]
                # Should NOT have -Recurse flag
                assert "-Recurse" not in call_args


class TestStorageUnknownAction:
    def test_unknown_action(self):
        from main import storage_tool

        with patch("main.ALLOWED_PATHS", []):
            result = storage_tool(action="delete-everything", path="C:\\Users")
            assert "Unknown action" in result
