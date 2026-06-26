using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ProcessServiceTests
{
    [Fact]
    public async Task ListAsync_includes_the_current_process()
    {
        var svc = new ProcessService();

        var processes = await svc.ListAsync();

        processes.Should().NotBeEmpty();
        var self = System.Environment.ProcessId;
        processes.Should().Contain(p => p.Pid == self);
        processes.Should().OnlyContain(p => p.MemoryMb >= 0);
    }

    [Fact]
    public async Task KillAsync_throws_for_a_pid_that_does_not_exist()
    {
        var svc = new ProcessService();

        // Pick a PID well above any live one so it is guaranteed not running;
        // GetProcessById throws ArgumentException for a non-running id.
        int bogusPid = System.Diagnostics.Process.GetProcesses().Max(p => p.Id) + 100_000;
        var act = () => svc.KillAsync(bogusPid);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StartDetachedAsync_launches_a_quoted_executable_and_returns_its_pid()
    {
        var svc = new ProcessService();

        // Quoted exe path exercises the first-quote parsing branch; `/c exit` returns immediately.
        var pid = await svc.StartDetachedAsync("\"C:\\Windows\\System32\\cmd.exe\" /c exit");

        pid.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StartDetachedAsync_throws_on_unmatched_opening_quote()
    {
        var svc = new ProcessService();

        var act = () => svc.StartDetachedAsync("\"C:\\nope\\foo.exe");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
