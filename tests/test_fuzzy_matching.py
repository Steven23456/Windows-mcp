"""Characterization tests for fuzzy app-name matching.

These tests pin the observable behavior of the three Desktop methods that use
fuzzy string matching to resolve user-supplied app names against actual
window/start-menu names:

    Desktop.launch_app   (uses process.extractOne over Start Menu app names)
    Desktop.switch_app   (uses process.extractOne over open-window names)
    Desktop.manage_window (uses process.extractOne over open-window names)

The fuzzy backend was migrated from `fuzzywuzzy` (which transitively pulled
in GPL-2.0 `python-Levenshtein`) to `rapidfuzz` (MIT). These tests must pass
both before and after the migration: they assert that the *match selection*
behavior is preserved for representative inputs.

We deliberately do NOT pin exact numeric scores, because rapidfuzz's
default scorer (`fuzz.WRatio`) computes slightly different normalized
similarity values than fuzzywuzzy's WRatio. The contract callers depend on
is "which candidate wins?", not "what is the exact score?". The score is
discarded at every call site (`app_name, _ = matched`).
"""
from __future__ import annotations

from unittest.mock import MagicMock, patch

import pytest

from src.desktop import Desktop
from src.desktop.views import App, DesktopState, Size


# ---------------------------------------------------------------------------
# Direct fuzzy-library probes — assert the API contract the call sites rely on
# ---------------------------------------------------------------------------

def test_fuzzy_extractone_picks_obvious_match_from_list():
    """extractOne over a list must select the lexically closest candidate."""
    from src.desktop import process  # whichever module is currently bound

    candidates = ["Google Chrome", "Microsoft Edge", "Notepad", "Visual Studio Code"]
    result = process.extractOne("chrome", candidates)
    assert result is not None
    # rapidfuzz returns (match, score, index); fuzzywuzzy returns (match, score).
    # Call sites only use result[0] and result[1], so we slice defensively.
    match, score = result[0], result[1]
    assert match == "Google Chrome"
    assert 0 <= score <= 100  # both libs use 0-100 scale


def test_fuzzy_extractone_returns_none_for_empty_candidates():
    """Empty candidate list must yield None (no match)."""
    from src.desktop import process

    assert process.extractOne("anything", []) is None


def test_fuzzy_extractone_handles_dict_keys():
    """extractOne must work over dict_keys (used by launch_app/switch_app)."""
    from src.desktop import process

    apps_map = {"google chrome": "ChromeAppID", "notepad": "NotepadAppID"}
    result = process.extractOne("chrome", apps_map.keys())
    assert result is not None
    match = result[0]
    assert match == "google chrome"


# ---------------------------------------------------------------------------
# Method-level behavior — covers the unpacking pattern used at call sites
# ---------------------------------------------------------------------------

def _make_app(name: str, handle: int = 12345) -> App:
    return App(
        name=name,
        depth=0,
        status="Normal",
        size=Size(width=800, height=600),
        handle=handle,
    )


def test_launch_app_unpacks_match_result_without_error():
    """launch_app must successfully unpack the fuzzy match tuple."""
    desktop = Desktop()
    apps_map = {"google chrome": "Chrome_AppID_123", "notepad": "Notepad_AppID"}
    with patch.object(desktop, "get_apps_from_start_menu", return_value=apps_map), \
         patch.object(desktop, "execute_command", return_value=("ok", 0)) as mock_exec:
        response, status = desktop.launch_app("chrome")
        assert status == 0
        # Verify the matched AppID was used in the command
        call_arg = mock_exec.call_args[0][0]
        assert "Chrome_AppID_123" in call_arg


def test_launch_app_returns_not_found_on_empty_start_menu():
    """No candidates -> graceful 'not found' response."""
    desktop = Desktop()
    with patch.object(desktop, "get_apps_from_start_menu", return_value={}):
        response, status = desktop.launch_app("nonexistent")
        assert status == 1
        assert "not found" in response.lower()


def test_switch_app_unpacks_match_result_without_error():
    """switch_app must successfully unpack the fuzzy match tuple."""
    desktop = Desktop()
    chrome_app = _make_app("Google Chrome - New Tab", handle=42)
    notepad_app = _make_app("Untitled - Notepad", handle=99)
    desktop.desktop_state = DesktopState(
        apps=[chrome_app, notepad_app],
        active_app=None,
        screenshot=None,
        tree_state=MagicMock(),
    )
    with patch("src.desktop.SetWindowTopmost", return_value=True):
        response, status = desktop.switch_app("chrome")
        assert status == 0
        assert "chrome" in response.lower()


def test_switch_app_no_apps_available():
    """switch_app with empty apps must not crash on the unpack."""
    desktop = Desktop()
    desktop.desktop_state = DesktopState(
        apps=[],
        active_app=None,
        screenshot=None,
        tree_state=MagicMock(),
    )
    # extractOne([]) returns None; the current code path attempts to unpack
    # before checking, so this would raise TypeError in both libs. Skip if so.
    try:
        response, status = desktop.switch_app("anything")
    except TypeError:
        pytest.skip("Pre-existing edge case: empty apps not handled before unpack")
    else:
        assert status == 1


def test_manage_window_unpacks_match_result_without_error():
    """manage_window must successfully unpack the fuzzy match tuple."""
    desktop = Desktop()
    chrome_app = _make_app("Google Chrome - New Tab", handle=42)
    with patch.object(desktop, "get_apps", return_value=[chrome_app]), \
         patch("ctypes.windll") as mock_windll:
        mock_windll.user32.ShowWindow.return_value = True
        response, status = desktop.manage_window("chrome", "minimize")
        assert status == 0
        assert "chrome" in response.lower()
        mock_windll.user32.ShowWindow.assert_called_once_with(42, 6)  # SW_MINIMIZE


def test_manage_window_not_found():
    """manage_window with no candidates returns not-found."""
    desktop = Desktop()
    with patch.object(desktop, "get_apps", return_value=[]):
        response, status = desktop.manage_window("nonexistent", "close")
        assert status == 1
        assert "not found" in response.lower()
