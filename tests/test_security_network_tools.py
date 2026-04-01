"""Tests for Security-Audit-Tool and Network-Tool."""

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


# ── Security-Audit-Tool ─────────────────────────────────────────────────────


class TestSecurityAuditTool:
    def test_returns_security_audit_string(self):
        from main import security_audit_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Quick Summary ===\n  DEFENDER: ON | FIREWALL: ON\n\n=== Windows Defender ===\n  Antivirus Enabled: True",
                0,
            )
            result = security_audit_tool()
            assert "Quick Summary" in result
            assert "DEFENDER" in result

    def test_error_propagated_on_failure(self):
        from main import security_audit_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("access denied", 1)
            result = security_audit_tool()
            assert "failed" in result.lower()
            assert "1" in result

    def test_calls_with_timeout_60(self):
        from main import security_audit_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("ok", 0)
            security_audit_tool()
            _, kwargs = mock_desktop.execute_command.call_args
            assert kwargs.get("timeout") == 60

    def test_output_stripped(self):
        from main import security_audit_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("  audit results  \n\n", 0)
            result = security_audit_tool()
            assert result == "audit results"


# ── Network-Tool ─────────────────────────────────────────────────────────────


class TestNetworkToolStatus:
    def test_status_returns_network_info(self):
        from main import network_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Network Adapters ===\n  Wi-Fi: 866 Mbps\n\nInternet Connectivity: OK",
                0,
            )
            result = network_tool(action="status")
            assert "Network Adapters" in result
            assert "Connectivity" in result

    def test_status_is_default_action(self):
        from main import network_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = ("adapters info", 0)
            result = network_tool()
            assert "adapters info" in result


class TestNetworkToolConnections:
    def test_connections_returns_info(self):
        from main import network_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== Active Connections ===\n  Established 192.168.1.1:443 [chrome]",
                0,
            )
            result = network_tool(action="connections")
            assert "Active Connections" in result


class TestNetworkToolPing:
    def test_ping_requires_target(self):
        from main import network_tool

        result = network_tool(action="ping")
        assert "required" in result.lower()

    def test_ping_rejects_metacharacters(self):
        from main import network_tool

        result = network_tool(action="ping", target="host; rm -rf /")
        assert "Error" in result

    def test_ping_rejects_dollar_sign(self):
        from main import network_tool

        result = network_tool(action="ping", target="$env:SECRET")
        assert "Error" in result

    def test_ping_blocks_internal_ips(self):
        from main import network_tool

        with patch("socket.gethostbyname", return_value="192.168.1.1"):
            result = network_tool(action="ping", target="internal.host")
            assert "blocked" in result.lower()

    def test_ping_blocks_localhost(self):
        from main import network_tool

        with patch("socket.gethostbyname", return_value="127.0.0.1"):
            result = network_tool(action="ping", target="localhost")
            assert "blocked" in result.lower()

    def test_ping_success(self):
        from main import network_tool

        with patch("socket.gethostbyname", return_value="8.8.8.8"):
            with patch("main.desktop") as mock_desktop:
                mock_desktop.execute_command.return_value = (
                    "Address  Latency  Status\n8.8.8.8  12ms     Success",
                    0,
                )
                result = network_tool(action="ping", target="google.com")
                assert "8.8.8.8" in result
                assert "Ping google.com" in result

    def test_ping_unresolvable_host(self):
        from main import network_tool
        import socket

        with patch("socket.gethostbyname", side_effect=socket.gaierror("not found")):
            result = network_tool(action="ping", target="nonexistent.invalid")
            assert "resolve" in result.lower()


class TestNetworkToolDns:
    def test_dns_requires_target(self):
        from main import network_tool

        result = network_tool(action="dns")
        assert "required" in result.lower()

    def test_dns_rejects_metacharacters(self):
        from main import network_tool

        result = network_tool(action="dns", target="host`whoami`")
        assert "Error" in result

    def test_dns_blocks_internal_ips(self):
        from main import network_tool

        with patch("socket.gethostbyname", return_value="10.0.0.1"):
            result = network_tool(action="dns", target="internal.corp")
            assert "blocked" in result.lower()

    def test_dns_success(self):
        from main import network_tool

        with patch("socket.gethostbyname", return_value="142.250.80.46"):
            with patch("main.desktop") as mock_desktop:
                mock_desktop.execute_command.return_value = (
                    "Name      Type  TTL  Section  IPAddress\ngoogle.com  A  300  Answer   142.250.80.46",
                    0,
                )
                result = network_tool(action="dns", target="google.com")
                assert "DNS lookup" in result
                assert "google.com" in result


class TestNetworkToolWifi:
    def test_wifi_returns_info(self):
        from main import network_tool

        with patch("main.desktop") as mock_desktop:
            mock_desktop.execute_command.return_value = (
                "=== WiFi Status ===\n  SSID: MyNetwork\n  Signal: 95%",
                0,
            )
            result = network_tool(action="wifi")
            assert "WiFi" in result


class TestValidateHostname:
    def test_empty_target_rejected(self):
        from main import _validate_hostname

        ok, _ = _validate_hostname("")
        assert ok is False

    def test_none_target_rejected(self):
        from main import _validate_hostname

        ok, _ = _validate_hostname(None)
        assert ok is False

    def test_too_long_rejected(self):
        from main import _validate_hostname

        ok, msg = _validate_hostname("a" * 254)
        assert ok is False
        assert "length" in msg.lower()

    def test_metacharacters_rejected(self):
        from main import _validate_hostname

        for bad in ["host;cmd", "host|pipe", "host&amp", "$(cmd)", "host`tick"]:
            ok, _ = _validate_hostname(bad)
            assert ok is False, f"Should reject: {bad}"

    def test_valid_hostname_passes(self):
        from main import _validate_hostname

        with patch("socket.gethostbyname", return_value="8.8.8.8"):
            ok, msg = _validate_hostname("google.com")
            assert ok is True
            assert msg == "OK"

    def test_valid_ipv4_passes(self):
        from main import _validate_hostname

        with patch("socket.gethostbyname", return_value="8.8.8.8"):
            ok, msg = _validate_hostname("8.8.8.8")
            assert ok is True

    def test_blocked_ip_rejected(self):
        from main import _validate_hostname

        with patch("socket.gethostbyname", return_value="192.168.1.1"):
            ok, msg = _validate_hostname("internal.host")
            assert ok is False
            assert "blocked" in msg.lower()
