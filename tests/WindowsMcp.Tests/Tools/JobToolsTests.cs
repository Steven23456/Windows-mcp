using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class JobToolsTests
{
    private static readonly JobInfo SampleJob = new(
        "j1", "completed", 4242, "'hi'", DateTime.UtcNow, DateTime.UtcNow, 0, 4, 0, 0, 0);

    private static JobTools MakeTools(IJobService? jobs = null)
        => new(jobs ?? new Mock<IJobService>().Object);

    [Fact]
    public void Job_unknown_mode_throws()
    {
        var tools = MakeTools();
        Action act = () => tools.Job("frobnicate");
        act.Should().Throw<ArgumentException>().WithMessage("*status|output|cancel|list*");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("cancel")]
    public void Job_id_modes_require_id(string mode)
    {
        var tools = MakeTools();
        Action act = () => tools.Job(mode);
        act.Should().Throw<ArgumentException>().WithMessage("*'id'*");
    }

    [Fact]
    public void Job_status_unknown_id_is_forgiving()
    {
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.GetStatus("j9")).Returns((JobInfo?)null);
        var tools = MakeTools(jobs.Object);

        tools.Job("status", id: "j9").Should().Contain("\"found\":false").And.Contain("j9");
    }

    [Fact]
    public void Job_status_known_id_serializes_the_job()
    {
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.GetStatus("j1")).Returns(SampleJob);
        var tools = MakeTools(jobs.Object);

        tools.Job("status", id: "j1").Should().Contain("\"Id\":\"j1\"").And.Contain("\"State\":\"completed\"");
    }

    [Fact]
    public void Job_output_passes_tail_through_and_is_forgiving_when_unknown()
    {
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.GetOutput("j1", 5))
            .Returns(new JobOutput("j1", "completed", "hello", "", 0, 0, 0));
        var tools = MakeTools(jobs.Object);

        tools.Job("output", id: "j1", tail: 5).Should().Contain("\"Stdout\":\"hello\"");
        jobs.Verify(j => j.GetOutput("j1", 5), Times.Once);

        tools.Job("output", id: "j9").Should().Contain("\"found\":false");
    }

    [Fact]
    public void Job_cancel_reports_the_service_verdict()
    {
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.Cancel("j1")).Returns(true);
        jobs.Setup(j => j.Cancel("j9")).Returns(false);
        var tools = MakeTools(jobs.Object);

        tools.Job("cancel", id: "j1").Should().Be("{\"cancelled\":true}");
        tools.Job("cancel", id: "j9").Should().Be("{\"cancelled\":false}");
    }

    [Fact]
    public void Job_list_serializes_all_jobs()
    {
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.List()).Returns(new[] { SampleJob });
        var tools = MakeTools(jobs.Object);

        tools.Job("list").Should().Contain("\"Id\":\"j1\"");
    }
}
