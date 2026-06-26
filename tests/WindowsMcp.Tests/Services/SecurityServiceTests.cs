using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class SecurityServiceTests
{
    private static PSResult Ok(string stdout) => new(true, stdout, "", 0, Array.Empty<string>());

    private static SecurityService Make(Mock<IPowerShellService> ps) => new(ps.Object);

    [Fact]
    public async Task AuditAsync_parses_probe_results()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""{"FirewallEnabled":true,"DefenderRunning":true,"UacLevel":5,"BitlockerStatus":"On"}"""));

        var audit = await Make(ps).AuditAsync();

        audit.FirewallEnabled.Should().BeTrue();
        audit.DefenderRunning.Should().BeTrue();
        audit.UacLevel.Should().Be(5);
        audit.BitlockerStatus.Should().Be("On");
        audit.Note.Should().BeNull();
    }

    [Fact]
    public async Task AuditAsync_returns_null_fields_with_note_on_empty_output()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok(""));

        var audit = await Make(ps).AuditAsync();

        audit.FirewallEnabled.Should().BeNull();
        audit.BitlockerStatus.Should().BeNull();
        audit.Note.Should().Contain("no admin");
    }

    [Fact]
    public async Task GetDefenderStatusAsync_parses_status()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""{"RealTimeProtectionEnabled":true,"AntivirusEnabled":true,"IsTamperProtected":true,"BehaviorMonitorEnabled":true,"AntivirusSignatureVersion":"1.405.0.0","AntivirusSignatureLastUpdated":"2026-06-26T00:00:00","QuickScanEndTime":null,"FullScanEndTime":null}"""));

        var s = await Make(ps).GetDefenderStatusAsync();

        s.RealTimeProtectionEnabled.Should().BeTrue();
        s.IsTamperProtected.Should().BeTrue();
        s.AntivirusSignatureVersion.Should().Be("1.405.0.0");
        s.Note.Should().BeNull();
    }

    [Fact]
    public async Task GetDefenderStatusAsync_degrades_gracefully_on_legacy_date_json()
    {
        // PowerShell 5.1 ConvertTo-Json emits DateTime as "\/Date(ms)\/", which System.Text.Json
        // cannot parse into DateTime? — the tool must surface a note, not throw.
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""{"RealTimeProtectionEnabled":true,"AntivirusSignatureLastUpdated":"\/Date(1782454172000)\/"}"""));

        var s = await Make(ps).GetDefenderStatusAsync();

        s.Note.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDefenderStatusAsync_returns_note_when_unavailable()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok(""));

        var s = await Make(ps).GetDefenderStatusAsync();

        s.RealTimeProtectionEnabled.Should().BeNull();
        s.Note.Should().Contain("unavailable");
    }

    [Fact]
    public async Task AuditAsync_tolerates_partial_results()
    {
        // BitLocker probe failed (unelevated) → null; others present.
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""{"FirewallEnabled":true,"DefenderRunning":false,"UacLevel":null,"BitlockerStatus":null}"""));

        var audit = await Make(ps).AuditAsync();

        audit.FirewallEnabled.Should().BeTrue();
        audit.DefenderRunning.Should().BeFalse();
        audit.BitlockerStatus.Should().BeNull();
    }
}
