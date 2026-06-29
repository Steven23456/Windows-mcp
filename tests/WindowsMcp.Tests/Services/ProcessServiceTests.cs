using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ProcessServiceTests
{
    // List/Kill/Start don't touch WMI, so a mock is fine; InspectAsync uses a real WmiService.
    private static ProcessService Make(IWmiService? wmi = null)
        => new(wmi ?? new Mock<IWmiService>().Object);

    [Fact]
    public async Task ListAsync_includes_the_current_process()
    {
        var svc = Make();

        var processes = await svc.ListAsync();

        processes.Should().NotBeEmpty();
        var self = System.Environment.ProcessId;
        processes.Should().Contain(p => p.Pid == self);
        processes.Should().OnlyContain(p => p.MemoryMb >= 0);
    }

    [Fact]
    public async Task KillAsync_throws_for_a_pid_that_does_not_exist()
    {
        var svc = Make();

        // Pick a PID well above any live one so it is guaranteed not running;
        // GetProcessById throws ArgumentException for a non-running id.
        int bogusPid = System.Diagnostics.Process.GetProcesses().Max(p => p.Id) + 100_000;
        var act = () => svc.KillAsync(bogusPid);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StartDetachedAsync_launches_a_quoted_executable_and_returns_its_pid()
    {
        var svc = Make();

        // Quoted exe path exercises the first-quote parsing branch; `/c exit` returns immediately.
        var pid = await svc.StartDetachedAsync("\"C:\\Windows\\System32\\cmd.exe\" /c exit");

        pid.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task StartDetachedAsync_throws_on_unmatched_opening_quote()
    {
        var svc = Make();

        var act = () => svc.StartDetachedAsync("\"C:\\nope\\foo.exe");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InspectAsync_returns_detail_for_the_current_process()
    {
        var svc = Make(new WmiService()); // real WMI for parent PID + command line
        var self = System.Environment.ProcessId;

        var detail = await svc.InspectAsync(self);

        detail.Pid.Should().Be(self);
        detail.Name.Should().NotBeNullOrEmpty();
        detail.CommandLine.Should().NotBeNullOrEmpty();
        detail.ParentPid.Should().NotBeNull();
        // The test host has loaded modules and we can read our own process.
        detail.Modules.Should().NotBeEmpty();
        detail.ModulesError.Should().BeNull();
    }
}
