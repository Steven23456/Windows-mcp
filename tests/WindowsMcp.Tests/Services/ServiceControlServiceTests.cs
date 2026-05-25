using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ServiceControlServiceTests
{
    [Fact]
    public async Task List_includes_print_spooler_service()
    {
        var svc = new ServiceControlService();
        var services = await svc.ListAsync();
        services.Should().Contain(s => s.Name.Equals("Spooler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetStatus_returns_state_for_spooler()
    {
        var svc = new ServiceControlService();
        var state = await svc.GetStatusAsync("Spooler");
        state.Status.Should().BeOneOf("Running", "Stopped", "StartPending", "StopPending", "Paused");
    }
}
