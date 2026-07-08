using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ProcessToolsTests
{
    private static ProcessTools MakeTools(
        IProcessService? process = null,
        IServiceControlService? service = null,
        ITaskSchedulerService? scheduler = null,
        IEventLogService? eventLog = null)
    {
        return new ProcessTools(
            process   ?? new Mock<IProcessService>().Object,
            service   ?? new Mock<IServiceControlService>().Object,
            scheduler ?? new Mock<ITaskSchedulerService>().Object,
            eventLog  ?? new Mock<IEventLogService>().Object);
    }

    private static ProcessTools Make(IProcessService process) => MakeTools(process: process);

    [Fact]
    public async Task Process_kill_requires_confirm_true()
    {
        var mock = new Mock<IProcessService>();
        var tools = MakeTools(process: mock.Object);

        Func<Task> act = () => tools.Process("kill", pid: 1234, confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.KillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Service_stop_requires_confirm_true()
    {
        var mock = new Mock<IServiceControlService>();
        var tools = MakeTools(service: mock.Object);

        Func<Task> act = () => tools.Service("stop", name: "Spooler", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduledTask_delete_requires_confirm_true()
    {
        var mock = new Mock<ITaskSchedulerService>();
        var tools = MakeTools(scheduler: mock.Object);

        Func<Task> act = () => tools.ScheduledTask("delete", name: "MyTask", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Process_orphans_calls_ListLineageAsync_with_orphansOnly_true()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);
        var json = await tools.Process("orphans");
        mock.Verify(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Be("[]");
    }

    [Fact]
    public async Task Process_list_includeLineage_calls_ListLineageAsync_false()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", name: "node", includeLineage: true);
        mock.Verify(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_list_groupByRoot_calls_GroupByRootAsync()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.GroupByRootAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", groupByRoot: true);
        mock.Verify(m => m.GroupByRootAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_kill_tree_requires_confirm_and_calls_KillTreeAsync()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var tools = Make(mock.Object);
        var noConfirm = () => tools.Process("kill", pid: 1234, tree: true);
        await noConfirm.Should().ThrowAsync<System.ArgumentException>();
        var json = await tools.Process("kill", pid: 1234, tree: true, confirm: true);
        mock.Verify(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Contain("3");
    }
}
