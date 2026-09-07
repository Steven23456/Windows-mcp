using System.Diagnostics;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-3 R3/R4: the graceful kill. The window seam is faked so the posts are visible without a
/// desktop, but the process being killed is real — a kill needs a live process, and a test that
/// pointed the service at the test host's own pid would take the runner down the day the
/// start-time guard regressed. Every child is windowless and started by this class, and every
/// test kills it in a finally.
/// </summary>
[Trait("Category", "Integration")]
public class ProcessServiceKillTests
{
    /// <summary>Records what the graceful path asked the desktop to do, and answers with fixtures.</summary>
    internal sealed class FakeProcessWindows : IProcessWindowNative
    {
        private readonly Dictionary<int, long[]> _windows = new();

        public List<int> Enumerated { get; } = new();
        public List<long> Posted { get; } = new();

        public void GiveWindows(int pid, params long[] handles) => _windows[pid] = handles;

        public long[] TopLevelWindowsOf(int pid)
        {
            Enumerated.Add(pid);
            return _windows.TryGetValue(pid, out var handles) ? handles : [];
        }

        /// <summary>Lets a test play the part of a window that ANSWERS the close.</summary>
        public Action<long>? OnPost { get; set; }

        public bool PostClose(long hwnd)
        {
            Posted.Add(hwnd);
            OnPost?.Invoke(hwnd);
            return true;
        }
    }

    private static ProcessService Make(IProcessWindowNative windows)
        => new(new Mock<IWmiService>().Object, windows);

    /// <summary>A child that stays alive and owns no window: cmd running a long ping.</summary>
    private static Process StartWindowlessChild()
    {
        var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        child.Should().NotBeNull();
        return child!;
    }

    private static void KillQuietly(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { /* already gone */ }
        child.Dispose();
    }

    [Fact]
    public async Task Graceful_kill_posts_wm_close_to_every_top_level_window_of_the_pid()
    {
        // A multi-window process is the case CloseMainWindow alone does not cover: the seam has to
        // reach EVERY top-level window the pid owns.
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        try
        {
            windows.GiveWindows(child.Id, 0x1111, 0x2222, 0x3333);

            var result = await Make(windows).KillAsync(child.Id, new KillOptions(Graceful: true, GraceMs: 200));

            windows.Posted.Should().Equal(new long[] { 0x1111, 0x2222, 0x3333 },
                "every window of the pid is asked to close, not just the first");
            windows.Enumerated.Should().Contain(child.Id);
            result.Pid.Should().Be(child.Id);
            result.Graceful.Should().BeTrue();
            result.ExitedGracefully.Should().BeFalse("nothing answered the posts - these handles are fictional");
            result.Forced.Should().BeTrue("the grace period expired with the process still alive");
            child.WaitForExit(10_000).Should().BeTrue("the fallback kill still has to end the process");
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public async Task Graceful_kill_of_a_process_with_no_window_forces_at_once_and_says_so()
    {
        // A console child or a service has nothing to close: the honest answer is an immediate
        // forced kill with a zero wait, not three seconds of pretending.
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        try
        {
            var result = await Make(windows).KillAsync(child.Id, new KillOptions(Graceful: true, GraceMs: 5000));

            windows.Posted.Should().BeEmpty("there was no window to post to");
            result.Graceful.Should().BeTrue("the caller asked for a graceful kill and gets told what happened");
            result.ExitedGracefully.Should().BeFalse();
            result.Forced.Should().BeTrue();
            result.WaitedMs.Should().Be(0, "nothing was sent, so there is nothing to wait for");
            child.WaitForExit(10_000).Should().BeTrue();
        }
        finally { KillQuietly(child); }
    }

    /// <summary>
    /// The branch every other headless test misses: the process leaves DURING the grace window, so
    /// TerminateProcess is never reached. Without this, <c>exitedGracefully:true</c> is only ever
    /// proven by the Notepad test, i.e. only on a live desktop. The fake window answers the close
    /// by ending the child — the same observable event a real window's close handler produces.
    /// </summary>
    [Fact]
    public async Task Graceful_kill_of_a_window_that_answers_is_not_forced()
    {
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        Task? answering = null;
        try
        {
            windows.GiveWindows(child.Id, 0x6666);
            windows.OnPost = _ => answering = Task.Run(async () =>
            {
                await Task.Delay(150);                  // the process takes a moment to shut down
                try { child.Kill(); } catch { /* already gone */ }
            });

            var result = await Make(windows).KillAsync(child.Id, new KillOptions(Graceful: true, GraceMs: 30_000));

            result.Graceful.Should().BeTrue();
            result.ExitedGracefully.Should().BeTrue("the process left on its own inside the grace window");
            result.Forced.Should().BeFalse("the whole point of the graceful path is that Kill() is not reached");
            result.WaitedMs.Should().BeGreaterThan(0, "the wait is measured, not assumed")
                .And.BeLessThan(30_000, "and it stops when the process goes, not when the grace runs out");
        }
        finally
        {
            if (answering is not null) await answering;
            KillQuietly(child);
        }
    }

    [Fact]
    public async Task A_hard_kill_never_touches_the_window_seam()
    {
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        try
        {
            windows.GiveWindows(child.Id, 0x4444);

            var result = await Make(windows).KillAsync(child.Id, new KillOptions(Graceful: false));

            windows.Enumerated.Should().BeEmpty("graceful:false is today's hard kill, unchanged");
            windows.Posted.Should().BeEmpty();
            result.Graceful.Should().BeFalse();
            result.ExitedGracefully.Should().BeFalse();
            result.Forced.Should().BeTrue();
            result.WaitedMs.Should().Be(0);
            child.WaitForExit(10_000).Should().BeTrue();
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public async Task The_start_time_guard_runs_first_and_a_mismatch_kills_nothing()
    {
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        try
        {
            windows.GiveWindows(child.Id, 0x5555);

            var act = () => Make(windows).KillAsync(child.Id, new KillOptions(
                Graceful: true, GraceMs: 200,
                ExpectedStartUtc: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

            await act.Should().ThrowAsync<InvalidOperationException>();
            windows.Enumerated.Should().BeEmpty("the guard aborts before anything is sent or killed");
            windows.Posted.Should().BeEmpty();
            child.HasExited.Should().BeFalse("a PID-reuse guard that kills anyway is worse than no guard");
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public async Task A_matching_start_time_guard_lets_the_kill_through()
    {
        var windows = new FakeProcessWindows();
        var child = StartWindowlessChild();
        try
        {
            var result = await new ProcessService(new Mock<IWmiService>().Object, windows).KillAsync(
                child.Id,
                new KillOptions(ExpectedStartUtc: child.StartTime.ToUniversalTime()));

            result.Pid.Should().Be(child.Id);
            child.WaitForExit(10_000).Should().BeTrue();
        }
        finally { KillQuietly(child); }
    }

    // ---- KillGuardedAsync: still on the interface, no longer called by the tool ----------------

    /// <summary>
    /// C-3 kept <c>KillGuardedAsync</c> on <see cref="IProcessService"/> and moved the tool onto
    /// <c>KillAsync(pid, KillOptions)</c>. Nothing else exercises it now, so the guard it owns
    /// would rot silently: an implementer refactoring the new path could break the old one and
    /// every test would stay green.
    /// </summary>
    [Fact]
    public async Task KillGuardedAsync_kills_when_the_start_time_matches()
    {
        var child = StartWindowlessChild();
        try
        {
            await Make(new FakeProcessWindows()).KillGuardedAsync(child.Id, child.StartTime.ToUniversalTime());

            child.WaitForExit(10_000).Should().BeTrue();
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public async Task KillGuardedAsync_aborts_and_kills_nothing_when_the_start_time_does_not_match()
    {
        var child = StartWindowlessChild();
        try
        {
            var act = () => Make(new FakeProcessWindows()).KillGuardedAsync(
                child.Id, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().ContainEquivalentOf("start time");
            child.HasExited.Should().BeFalse("a PID-reuse guard that kills anyway is worse than no guard");
        }
        finally { KillQuietly(child); }
    }

    // ---- R4: the same path through the REAL window seam ---------------------------------------

    /// <summary>
    /// The mocked seam above proves the service's own decisions; this proves the production seam
    /// agrees — a real console child really has no top-level window, so the honest result is a
    /// forced kill at once. Uses the PUBLIC constructor, i.e. the wiring the server ships.
    /// </summary>
    [Fact]
    public async Task A_real_console_child_reports_a_forced_kill_through_the_real_window_seam()
    {
        var svc = new ProcessService(new Mock<IWmiService>().Object);
        var child = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep 30")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        child.Should().NotBeNull();
        try
        {
            var result = await svc.KillAsync(child!.Id, new KillOptions(Graceful: true, GraceMs: 3000));

            result.Pid.Should().Be(child.Id);
            result.Name.Should().ContainEquivalentOf("powershell");
            result.Graceful.Should().BeTrue();
            result.ExitedGracefully.Should().BeFalse("a windowless console child has nothing to close");
            result.Forced.Should().BeTrue();
            result.WaitedMs.Should().Be(0, "no grace period is burned when nothing could be asked to close");
            child.WaitForExit(15_000).Should().BeTrue();
        }
        finally { KillQuietly(child!); }
    }
}
