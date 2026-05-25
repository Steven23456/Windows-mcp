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
}
