using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Read-only integration: every Windows host has a populated Winsock catalog with the
// base MSAFD TCP/IP providers.
[Trait("Category", "Integration")]
public class LspEnumeratorTests
{
    [Fact]
    public void Enumerate_returns_base_winsock_providers_with_paths()
    {
        var providers = new LspEnumerator().Enumerate();

        providers.Should().NotBeEmpty();
        providers.Should().Contain(p => p.ProtocolName.Contains("MSAFD", StringComparison.OrdinalIgnoreCase));
        providers.Should().Contain(p => !string.IsNullOrEmpty(p.ProviderPath));
        providers.Should().OnlyContain(p => p.CatalogEntryId > 0);
    }
}
