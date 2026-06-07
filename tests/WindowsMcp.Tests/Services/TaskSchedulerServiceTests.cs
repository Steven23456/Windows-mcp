using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Read-only integration: a real Windows host always has registered tasks (Microsoft
// maintenance/telemetry tasks ship with the OS), so these assertions are stable.
[Trait("Category", "Integration")]
public class TaskSchedulerServiceTests
{
    [Fact]
    public async Task ListDetailed_returns_tasks_across_folders_with_paths()
    {
        var svc = new TaskSchedulerService();

        var tasks = await svc.ListDetailedAsync();

        tasks.Should().NotBeEmpty();
        tasks.Should().OnlyContain(t => !string.IsNullOrEmpty(t.Path));
        // The full tree spans sub-folders, not just the root folder.
        tasks.Should().Contain(t => t.Path.TrimStart('\\').Contains('\\'));
    }

    [Fact]
    public async Task ListDetailed_extracts_action_paths_and_triggers()
    {
        var svc = new TaskSchedulerService();

        var tasks = await svc.ListDetailedAsync();

        tasks.Should().Contain(t => t.ActionPath != null);   // exec-action extraction works
        tasks.Should().Contain(t => t.Triggers.Length > 0);  // trigger extraction works
    }
}
