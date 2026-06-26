using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class NetworkServiceTests
{
    private static NetworkService Make(IPowerShellService? ps = null)
        => new(new Mock<ILogger<NetworkService>>().Object, ps ?? new Mock<IPowerShellService>().Object);

    [Fact]
    public async Task ListAdaptersAsync_returns_at_least_the_loopback()
    {
        var adapters = await Make().ListAdaptersAsync();
        adapters.Should().NotBeEmpty();
        adapters.Should().OnlyContain(a => a.Name != null && a.Status != null);
    }

    [Fact]
    public async Task ListPortsAsync_returns_ports_with_owning_pid()
    {
        // Real PowerShell so Get-NetTCPConnection actually runs.
        var ports = await Make(new PowerShellService(NullLogger.Instance)).ListPortsAsync();
        ports.Should().NotBeNull();
        ports.Should().OnlyContain(p => p.LocalPort >= 0);
        // At least one endpoint should carry an owning PID (the enrichment this tool adds).
        ports.Should().Contain(p => p.OwningPid.HasValue && p.OwningPid > 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParsePorts_reads_pid_and_process_name()
    {
        var ports = NetworkService.ParsePorts(
            """[{"LocalAddress":"0.0.0.0","LocalPort":135,"RemoteAddress":"0.0.0.0","RemotePort":0,"State":"Listen","OwningPid":1044,"ProcessName":"svchost"}]""");

        ports.Should().ContainSingle();
        ports[0].LocalPort.Should().Be(135);
        ports[0].OwningPid.Should().Be(1044);
        ports[0].ProcessName.Should().Be("svchost");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParsePorts_handles_single_object_and_blank()
    {
        NetworkService.ParsePorts("""{"LocalAddress":"::","LocalPort":445,"RemoteAddress":"::","RemotePort":0,"State":"Listen","OwningPid":4,"ProcessName":"System"}""")
            .Should().ContainSingle().Which.OwningPid.Should().Be(4);
        NetworkService.ParsePorts("").Should().BeEmpty();
    }

    [Fact]
    public async Task PingAsync_succeeds_against_loopback()
    {
        var result = await Make().PingAsync("127.0.0.1");
        result.Host.Should().Be("127.0.0.1");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task PingAsync_reports_failure_for_an_unresolvable_host()
    {
        var result = await Make().PingAsync("nonexistent.invalid");
        result.Success.Should().BeFalse();
        result.RoundtripMs.Should().BeNull();
    }

    [Fact]
    public async Task DnsLookupAsync_resolves_localhost_to_a_loopback_address()
    {
        var addrs = await Make().DnsLookupAsync("localhost");
        addrs.Should().NotBeEmpty();
        addrs.Should().Contain(a => a == "127.0.0.1" || a == "::1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetWifiAsync_returns_the_managed_api_placeholder()
    {
        var wifi = await Make().GetWifiAsync();
        wifi.Status.Should().Be("ManagedAPIRequired");
    }
}
