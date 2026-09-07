using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
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

    [Fact]
    public async Task ListLineageAsync_includes_current_process_with_a_parent()
    {
        var svc = Make(new WmiService());
        var self = System.Environment.ProcessId;
        var rows = await svc.ListLineageAsync(orphansOnly: false, nameFilter: null);
        var me = rows.Should().ContainSingle(r => r.Pid == self).Subject;
        me.ParentPid.Should().NotBeNull();
        me.CommandLine.Should().NotBeNullOrEmpty();
        me.RootPid.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListLineageAsync_name_filter_matches_name_or_commandline()
    {
        var svc = Make(new WmiService());
        // Filter by the current test process's own name — guaranteed present — so the assertion is
        // non-vacuous (OnlyContain passes trivially on an empty set): it must return at least our
        // process, and every returned row must actually match the filter on name or command line.
        var selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var filtered = await svc.ListLineageAsync(false, selfName);
        filtered.Should().NotBeEmpty();
        filtered.Should().OnlyContain(r =>
            r.Name.Contains(selfName, System.StringComparison.OrdinalIgnoreCase) ||
            (r.CommandLine ?? "").Contains(selfName, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GroupByRootAsync_returns_groups_covering_all_processes()
    {
        var svc = Make(new WmiService());
        var groups = await svc.GroupByRootAsync();
        groups.Should().NotBeEmpty();
        groups.Sum(g => g.DescendantCount).Should().BeGreaterThan(0);
        groups.Should().OnlyContain(g => g.ChildPids.Length == g.DescendantCount);
    }

    [Fact]
    public async Task KillGuardedAsync_aborts_on_start_time_mismatch()
    {
        var svc = Make(new WmiService());
        // The guard must see a LIVE process to compare start times. `cmd /c pause`
        // blocks only with an interactive console — on a headless/service session
        // (CI runners) it hits EOF on stdin and exits immediately, so the process
        // is already gone when KillGuardedAsync runs. `ping -n 60` blocks ~59 s
        // independently of any console, keeping the process reliably alive.
        var pid = await svc.StartDetachedAsync("\"C:\\Windows\\System32\\cmd.exe\" /c ping -n 60 127.0.0.1");
        try
        {
            var act = () => svc.KillGuardedAsync(pid, new System.DateTime(2000, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            await act.Should().ThrowAsync<System.InvalidOperationException>();
        }
        finally { try { await svc.KillAsync(pid); } catch { } }
    }

    [Fact]
    public async Task KillTreeAsync_kills_parent_and_child()
    {
        var svc = Make(new WmiService());
        // cmd that spawns a child cmd that pauses; both should die.
        var pid = await svc.StartDetachedAsync(
            "\"C:\\Windows\\System32\\cmd.exe\" /c start /wait cmd /c pause");
        await System.Threading.Tasks.Task.Delay(400); // let the child spawn
        var killed = await svc.KillTreeAsync(pid, null);
        killed.Should().BeGreaterThanOrEqualTo(1);
        var act = () => System.Diagnostics.Process.GetProcessById(pid);
        act.Should().Throw<System.ArgumentException>(); // root gone
    }
    // ---- C-3 R2: the CPU column, the order and the cap, on the live process table --------------

    [Fact]
    public async Task ListAsync_with_options_reports_a_cpu_percent_in_range_for_every_row()
    {
        var svc = Make();

        var rows = await svc.ListAsync(new ProcessListOptions());

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(p => p.CpuPercent >= 0 && p.CpuPercent <= 100);
        rows.Sum(p => p.CpuPercent).Should().BeLessThanOrEqualTo(125,
            "the percentage is normalised across ALL cores, so the whole box sums to about 100 - "
            + "forgetting the core count would put this near 100 x cores; the headroom is for the "
            + "skew between per-process sample windows");
    }

    [Fact]
    public async Task ListAsync_measures_a_busy_process_above_zero_and_sorts_the_busiest_first()
    {
        // A thread spinning inside THIS process for the sample window: no child to spawn, no
        // PowerShell to wait for, and the row we assert on is the one we made busy.
        var svc = Make();
        using var stop = new CancellationTokenSource();
        var spinner = new Thread(() => { while (!stop.IsCancellationRequested) { } }) { IsBackground = true };
        spinner.Start();

        WindowsMcp.Abstractions.Models.ProcessDto[] rows;
        try { rows = await svc.ListAsync(new ProcessListOptions(SortBy: ProcessSort.Cpu)); }
        finally { stop.Cancel(); spinner.Join(TimeSpan.FromSeconds(5)); }

        var self = rows.Should().ContainSingle(r => r.Pid == System.Environment.ProcessId).Subject;
        self.CpuPercent.Should().BeGreaterThan(0, "a core was busy in this process for the whole sample window");
        rows.Select(r => r.CpuPercent).Should().BeInDescendingOrder("sort_by:cpu is descending");
    }

    /// <summary>
    /// C-3 deviation, pinned: the old <c>ListAsync(nameFilter)</c> overload does NOT sample CPU.
    /// The name kill and the startup report enumerate through it, and neither may pay the 250 ms
    /// window — so every row it returns carries the default 0, filter and all.
    /// </summary>
    [Fact]
    public async Task ListAsync_with_a_name_filter_still_filters_and_does_not_sample_cpu()
    {
        var svc = Make();
        var selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        var rows = await svc.ListAsync(selfName);

        rows.Should().NotBeEmpty();
        rows.Should().Contain(r => r.Pid == System.Environment.ProcessId);
        rows.Should().OnlyContain(r => r.Name.Contains(selfName, StringComparison.OrdinalIgnoreCase),
            "the substring filter on the name is unchanged");
        rows.Should().OnlyContain(r => r.CpuPercent == 0,
            "the non-sampling overload leaves the CPU column at its default - measuring here would "
            + "put a 250 ms window in front of every name kill and every startup report");
    }

    [Fact]
    public async Task ListAsync_with_a_limit_returns_that_many_rows_after_the_sort()
    {
        var svc = Make();

        var all = await svc.ListAsync(new ProcessListOptions());
        var capped = await svc.ListAsync(new ProcessListOptions(Limit: 3));

        all.Length.Should().BeGreaterThan(3, "a live box runs more than three processes");
        capped.Should().HaveCount(3);
        capped.Select(r => r.MemoryMb).Should().BeInDescendingOrder("memory is the default order");
    }

    [Fact]
    public async Task ListAsync_options_keeps_the_substring_name_filter()
    {
        var svc = Make();
        var selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        var rows = await svc.ListAsync(new ProcessListOptions(NameFilter: selfName));

        rows.Should().NotBeEmpty();
        rows.Should().Contain(r => r.Pid == System.Environment.ProcessId);
        rows.Should().OnlyContain(r => r.Name.Contains(selfName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// C-3 R4: the two samples are taken around ONE injected wait, so a future unit test does not
    /// have to sleep 250 ms - and the wait is the ~250 ms budget the roadmap accepted, not a
    /// second of latency added to every process(list).
    /// </summary>
    [Fact]
    public async Task ListAsync_samples_cpu_across_a_single_injected_delay()
    {
        var delays = new List<TimeSpan>();
        var svc = new ProcessService(
            new Mock<IWmiService>().Object,
            new ProcessServiceKillTests.FakeProcessWindows(),
            (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var rows = await svc.ListAsync(new ProcessListOptions(Limit: 5));

        var wait = delays.Should().ContainSingle("one wait separates the before and after readings").Subject;
        wait.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
        wait.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "R4 budgeted ~250 ms per plain list");
        rows.Should().HaveCount(5);
    }

}
