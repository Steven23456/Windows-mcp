using FluentAssertions;
using ModelContextProtocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ShellToolsTests
{
    // The SDK's NullProgress is internal, so tests use a tiny recording fake.
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Reports { get; } = new();
        public void Report(ProgressNotificationValue value) => Reports.Add(value);
    }

    private static readonly PSResult SampleResult =
        new(Success: true, Stdout: "ok", Stderr: "", ExitCode: 0, Errors: Array.Empty<string>());

    private static readonly JobInfo SampleJob = new(
        "j1", "running", 4242, "Start-Sleep 30", DateTime.UtcNow, null, null, 0, 0, 0, 0);

    private static ShellTools MakeTools(
        IPowerShellService? ps = null,
        IJobService? jobs = null,
        TimeSpan? heartbeatInterval = null)
    {
        return new ShellTools(
            ps ?? new Mock<IPowerShellService>().Object,
            jobs ?? new Mock<IJobService>().Object,
            heartbeatInterval ?? TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Powershell_fast_command_returns_result_without_heartbeats()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(SampleResult);
        var progress = new RecordingProgress();
        var tools = MakeTools(ps: ps.Object);

        var json = await tools.Powershell("'hi'", progress);

        json.Should().Contain("\"Stdout\":\"ok\"");
        progress.Reports.Should().BeEmpty("a fast command must not tick the heartbeat");
    }

    [Fact]
    public async Task Powershell_slow_command_emits_monotonic_heartbeats_and_still_returns_result()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .Returns(async (string _, CancellationToken ct) =>
          {
              await Task.Delay(150, ct);
              return SampleResult;
          });
        var progress = new RecordingProgress();
        var tools = MakeTools(ps: ps.Object, heartbeatInterval: TimeSpan.FromMilliseconds(20));

        var json = await tools.Powershell("Start-Sleep 1", progress);

        json.Should().Contain("\"Stdout\":\"ok\"");
        progress.Reports.Should().NotBeEmpty("a long command must emit heartbeats");
        progress.Reports.Select(r => r.Progress).Should().BeInAscendingOrder(
            "the SDK requires Progress to increase monotonically");
        progress.Reports.Should().OnlyContain(r => r.Message != null && r.Message.Contains("powershell running"));
    }

    [Fact]
    public async Task Powershell_background_starts_a_job_and_never_runs_foreground()
    {
        var ps = new Mock<IPowerShellService>();
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleJob);
        var tools = MakeTools(ps: ps.Object, jobs: jobs.Object);

        var json = await tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(), background: true);

        json.Should().Contain("\"Id\":\"j1\"").And.Contain("\"State\":\"running\"");
        jobs.Verify(j => j.StartAsync("msiexec /i app.msi /qn", It.IsAny<CancellationToken>()), Times.Once);
        ps.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
