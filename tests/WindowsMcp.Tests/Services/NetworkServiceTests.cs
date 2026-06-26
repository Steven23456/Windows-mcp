using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class NetworkServiceTests
{
    private static NetworkService Make() => new(new Mock<ILogger<NetworkService>>().Object);

    [Fact]
    public async Task ListAdaptersAsync_returns_at_least_the_loopback()
    {
        var adapters = await Make().ListAdaptersAsync();
        adapters.Should().NotBeEmpty();
        adapters.Should().OnlyContain(a => a.Name != null && a.Status != null);
    }

    [Fact]
    public async Task ListPortsAsync_returns_a_non_null_array()
    {
        var ports = await Make().ListPortsAsync();
        ports.Should().NotBeNull();
        ports.Should().OnlyContain(p => p.LocalPort >= 0);
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
