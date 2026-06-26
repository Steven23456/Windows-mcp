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
