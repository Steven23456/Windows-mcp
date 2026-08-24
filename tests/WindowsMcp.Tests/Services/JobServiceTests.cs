using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Real-process integration tests: every job spawns a full powershell.exe cold-start, which under
// Defender scanning on a loaded box can be slow (see PowerShellServiceTests). Counts are kept
// small on purpose, and completion is awaited deterministically via WhenCompleted (no poll-sleeps).
[Trait("Category", "Integration")]
public sealed class JobServiceTests : IDisposable
{
    private readonly JobService _svc = new(NullLogger.Instance);

    public void Dispose() => _svc.Dispose();

    [Fact]
    public async Task StartAsync_echo_job_completes_with_captured_stdout()
    {
        var info = await _svc.StartAsync("'hello job'");
        info.State.Should().Be("running");
        info.Id.Should().StartWith("j");
        info.Pid.Should().BeGreaterThan(0);

        await _svc.WhenCompleted(info.Id)!;

        var status = _svc.GetStatus(info.Id)!;
        status.State.Should().Be("completed");
        status.ExitCode.Should().Be(0);
        status.EndedAtUtc.Should().NotBeNull();

        var output = _svc.GetOutput(info.Id)!;
        output.Stdout.Should().Contain("hello job");
    }

    [Fact]
    public async Task StartAsync_nonzero_exit_marks_the_job_failed()
    {
        var info = await _svc.StartAsync("exit 3");
        await _svc.WhenCompleted(info.Id)!;

        var status = _svc.GetStatus(info.Id)!;
        status.State.Should().Be("failed");
        status.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task Cancel_kills_a_running_job_and_is_idempotent()
    {
        var info = await _svc.StartAsync("Start-Sleep -Seconds 30");

        _svc.Cancel(info.Id).Should().BeTrue();
        await _svc.WhenCompleted(info.Id)!;

        _svc.GetStatus(info.Id)!.State.Should().Be("cancelled");
        _svc.Cancel(info.Id).Should().BeFalse("a finished job cannot be cancelled again");
        _svc.Cancel("j999").Should().BeFalse("unknown ids are forgiving");
    }

    [Fact]
    public async Task Backstop_tears_down_a_runaway_job_as_timedOut()
    {
        using var svc = new JobService(NullLogger.Instance, backstop: TimeSpan.FromMilliseconds(500));
        var info = await svc.StartAsync("Start-Sleep -Seconds 30");

        await svc.WhenCompleted(info.Id)!;

        svc.GetStatus(info.Id)!.State.Should().Be("timedOut");
    }

    [Fact]
    public async Task StartAsync_rejects_when_running_cap_is_reached_then_recovers()
    {
        using var svc = new JobService(NullLogger.Instance, maxRunning: 1);
        var first = await svc.StartAsync("Start-Sleep -Seconds 30");

        Func<Task> act = () => svc.StartAsync("'never runs'");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*limit*");

        svc.Cancel(first.Id).Should().BeTrue();
        await svc.WhenCompleted(first.Id)!;

        var second = await svc.StartAsync("'runs now'");
        await svc.WhenCompleted(second.Id)!;
        svc.GetStatus(second.Id)!.State.Should().Be("completed");
    }

    [Fact]
    public async Task Finished_jobs_beyond_retention_are_evicted_oldest_first()
    {
        using var svc = new JobService(NullLogger.Instance, maxFinishedRetained: 1);
        var first = await svc.StartAsync("'one'");
        await svc.WhenCompleted(first.Id)!;
        var second = await svc.StartAsync("'two'");
        await svc.WhenCompleted(second.Id)!;

        svc.GetStatus(first.Id).Should().BeNull("the oldest finished job is evicted beyond retention");
        svc.GetStatus(second.Id).Should().NotBeNull();
        svc.List().Should().HaveCount(1);
    }

    [Fact]
    public async Task GetOutput_tail_returns_only_the_last_chars()
    {
        var info = await _svc.StartAsync("'abcdef'");
        await _svc.WhenCompleted(info.Id)!;

        var full = _svc.GetOutput(info.Id)!;
        full.Stdout.Should().Contain("abcdef");

        var tail = _svc.GetOutput(info.Id, tailChars: 4)!;
        tail.Stdout.Length.Should().BeLessThanOrEqualTo(4);
        full.Stdout.Should().EndWith(tail.Stdout);
    }
}
