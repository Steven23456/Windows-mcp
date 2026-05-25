using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class EventLogServiceTests
{
    [Fact]
    public async Task QueryAsync_returns_entries_from_application_log()
    {
        var svc = new EventLogService();
        var entries = await svc.QueryAsync("Application", null, null, DateTime.UtcNow.AddDays(-30), 5);
        entries.Should().NotBeEmpty();
    }
}
