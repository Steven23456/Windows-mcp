using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8: the window wait. Which window a launch "produced" is a real decision — a packaged app or
/// Edge hands its command to a process that is not the one the activation reported, so a PID
/// match cannot be the only rule and a title match cannot be allowed to claim a window that was
/// already open. Both halves are pure enough to pin here: <see cref="LaunchWait.Pick"/> over one
/// hand-built inventory, and the polling loop over a fake inventory that changes its answer.
/// </summary>
[Trait("Category", "Unit")]
public class LaunchWaitTests
{
    private static WindowInfo Win(
        string title, long hwnd, int pid, int zOrder = 0, string process = "app")
        => new(title, hwnd, pid, process, WindowState.Normal, new Bounds(0, 0, 800, 600),
               zOrder, false, false, 0);

    // ---- Pick: the PID first ------------------------------------------------------------------

    [Fact]
    public void Pick_takes_a_window_of_the_launched_process_whatever_its_title_is()
    {
        var inventory = new[] { Win("Something Else Entirely", 0x10, 4242) };

        var picked = LaunchWait.Pick(inventory, pid: 4242, matchedName: "Calculator", before: []);

        picked!.Hwnd.Should().Be(0x10, "the process we started is the strongest evidence there is");
    }

    [Fact]
    public void Pick_takes_a_window_of_the_launched_process_even_with_no_title_at_all()
    {
        var inventory = new[] { Win("", 0x10, 4242) };

        LaunchWait.Pick(inventory, 4242, "Calculator", []).Should().NotBeNull();
    }

    [Fact]
    public void Pick_takes_the_frontmost_window_of_the_launched_process()
    {
        var inventory = new[]
        {
            Win("Calculator", 0x20, 4242, zOrder: 3),
            Win("Calculator", 0x10, 4242, zOrder: 1),
        };

        LaunchWait.Pick(inventory, 4242, "Calculator", [])!.Hwnd.Should().Be(0x10,
            "with several windows of one process the frontmost is the one the launch just raised");
    }

    [Fact]
    public void Pick_prefers_the_pid_over_a_title_that_also_matches()
    {
        var inventory = new[]
        {
            Win("Calculator", 0x20, 999, zOrder: 0),    // a stranger's window with the right title
            Win("Untitled", 0x10, 4242, zOrder: 1),     // ours
        };

        LaunchWait.Pick(inventory, 4242, "Calculator", [])!.Hwnd.Should().Be(0x10);
    }

    // ---- Pick: the title fallback, and only for a NEW window ----------------------------------

    [Fact]
    public void Pick_falls_back_to_a_new_window_whose_title_matches_the_app()
    {
        // Calculator's activation returns a pid whose window never appears - the window belongs to
        // an app-host process. Without this fallback launch("calc") always says windowDetected:false.
        var inventory = new[] { Win("Calculator", 0x10, 777) };

        LaunchWait.Pick(inventory, pid: 4242, matchedName: "Calculator", before: [])!.Hwnd.Should().Be(0x10);
    }

    [Fact]
    public void Pick_never_claims_a_window_that_was_already_open()
    {
        // The window existed before the launch, so it is not what the launch produced - reporting
        // it would be a lie the agent then acts on.
        var inventory = new[] { Win("Calculator", 0x10, 777) };

        LaunchWait.Pick(inventory, 4242, "Calculator", before: [0x10]).Should().BeNull();
    }

    [Fact]
    public void Pick_still_takes_a_pre_existing_window_when_the_pid_matches()
    {
        // The "before" set only guards the title fallback: a window of the process we just
        // started is ours by definition, however the inventory raced.
        var inventory = new[] { Win("Calculator", 0x10, 4242) };

        LaunchWait.Pick(inventory, 4242, "Calculator", before: [0x10])!.Hwnd.Should().Be(0x10);
    }

    [Theory]
    [InlineData("Calculator", "Calculator")]                        // exact
    [InlineData("Microsoft Edge", "Probe page - Microsoft Edge")]   // substring: the app name is in the title
    [InlineData("Windows Terminal", "Terminal")]                    // fuzzy: the window is titled less than the app
    public void Pick_matches_a_new_window_title_exact_substring_or_fuzzy(string app, string title)
    {
        var inventory = new[] { Win(title, 0x10, 777) };

        LaunchWait.Pick(inventory, 4242, app, [])!.Title.Should().Be(title);
    }

    [Fact]
    public void Pick_prefers_an_exact_title_over_a_window_that_merely_contains_the_name()
    {
        var inventory = new[]
        {
            Win("Calculator - Standard", 0x20, 777, zOrder: 0),
            Win("Calculator", 0x10, 778, zOrder: 1),
        };

        LaunchWait.Pick(inventory, 4242, "Calculator", [])!.Hwnd.Should().Be(0x10,
            "exact beats substring even when the substring match is in front (roadmap C5's order)");
    }

    [Fact]
    public void Pick_prefers_a_substring_title_over_a_merely_fuzzy_one()
    {
        var inventory = new[]
        {
            Win("Terminal", 0x20, 777, zOrder: 0),
            Win("PowerShell - Windows Terminal", 0x10, 778, zOrder: 1),
        };

        LaunchWait.Pick(inventory, 4242, "Windows Terminal", [])!.Hwnd.Should().Be(0x10);
    }

    [Fact]
    public void Pick_refuses_a_new_window_whose_title_is_only_vaguely_like_the_app()
    {
        // "Microsoft Edge" against "Untitled - Notepad" scores 36 - far below the shared floor of
        // 70. Grabbing it would hand the agent the wrong window handle.
        var inventory = new[] { Win("Untitled - Notepad", 0x10, 777) };

        LaunchWait.Pick(inventory, 4242, "Microsoft Edge", []).Should().BeNull();
    }

    [Fact]
    public void Pick_returns_null_when_the_inventory_is_empty()
    {
        LaunchWait.Pick([], 4242, "Calculator", []).Should().BeNull();
    }

    // ---- ForWindowAsync: the polling loop ----------------------------------------------------

    /// <summary>An inventory that answers differently on each poll and counts the polls.</summary>
    private sealed class FakeInventory
    {
        private readonly Func<int, WindowInfo[]> _answers;
        public int Polls { get; private set; }
        public FakeInventory(Func<int, WindowInfo[]> answers) => _answers = answers;
        public Task<WindowInfo[]> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Polls++;
            return Task.FromResult(_answers(Polls));
        }
    }

    [Fact]
    public async Task ForWindowAsync_returns_the_window_as_soon_as_it_appears()
    {
        var inventory = new FakeInventory(poll => poll < 3 ? [] : [Win("Calculator", 0x10, 4242)]);

        var found = await LaunchWait.ForWindowAsync(
            inventory.ListAsync, 4242, "Calculator", [], timeoutMs: 5000, pollMs: 20);

        found!.Hwnd.Should().Be(0x10);
        inventory.Polls.Should().Be(3, "the loop stops the moment the window is there, not at the timeout");
    }

    [Fact]
    public async Task ForWindowAsync_polls_immediately_before_it_ever_waits()
    {
        // A window that is already up must not cost a poll interval of latency.
        var inventory = new FakeInventory(_ => [Win("Calculator", 0x10, 4242)]);
        var stopwatch = Stopwatch.StartNew();

        await LaunchWait.ForWindowAsync(inventory.ListAsync, 4242, "Calculator", [], 5000, pollMs: 1000);

        stopwatch.Stop();
        inventory.Polls.Should().Be(1);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(500, "the first look happens before the first sleep");
    }

    [Fact]
    public async Task ForWindowAsync_gives_up_at_the_timeout_and_returns_null_rather_than_throwing()
    {
        // Roadmap C11 / the checklist's "sent, window not detected": a timeout is an outcome the
        // agent acts on with the pid it was given, not an error.
        var inventory = new FakeInventory(_ => []);
        var stopwatch = Stopwatch.StartNew();

        var found = await LaunchWait.ForWindowAsync(inventory.ListAsync, 4242, "Calculator", [], timeoutMs: 200, pollMs: 20);

        stopwatch.Stop();
        found.Should().BeNull();
        inventory.Polls.Should().BeGreaterThan(1, "it kept looking for the whole budget");
        stopwatch.ElapsedMilliseconds.Should().BeInRange(150, 2000, "it waited about the budget it was given");
    }

    [Fact]
    public async Task ForWindowAsync_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var inventory = new FakeInventory(poll =>
        {
            if (poll == 2) cts.Cancel();
            return [];
        });

        var act = () => LaunchWait.ForWindowAsync(
            inventory.ListAsync, 4242, "Calculator", [], timeoutMs: 30_000, pollMs: 10, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void DefaultPollMs_is_the_quarter_second_the_roadmap_names()
    {
        LaunchWait.DefaultPollMs.Should().Be(250);
    }

    [Fact]
    public void Pick_keeps_the_best_scoring_new_window_when_a_later_one_scores_lower()
    {
        // "Terminal" scores 100 against "Windows Terminal" and "Untitled - Notepad" scores 38.
        // A loop that took the last candidate it looked at, or that reset its best on every
        // iteration, would hand back Notepad - and the agent would type into the user's document.
        var inventory = new[]
        {
            Win("Terminal", 0x10, 777, zOrder: 0),
            Win("Untitled - Notepad", 0x20, 778, zOrder: 1),
        };

        LaunchWait.Pick(inventory, 4242, "Windows Terminal", [])!.Hwnd.Should().Be(0x10);
    }

    [Fact]
    public void Pick_takes_the_best_scoring_new_window_even_when_a_worse_one_is_in_front()
    {
        // The mirror: the better title is behind the worse one, so z-order must not decide a
        // fuzzy comparison.
        var inventory = new[]
        {
            Win("Untitled - Notepad", 0x20, 778, zOrder: 0),
            Win("Terminal", 0x10, 777, zOrder: 1),
        };

        LaunchWait.Pick(inventory, 4242, "Windows Terminal", [])!.Hwnd.Should().Be(0x10);
    }

    [Fact]
    public async Task ForWindowAsync_never_sleeps_past_the_budget_it_was_given()
    {
        // timeout 60ms with a 5s poll interval: the wait is capped by the timeout, not by the
        // interval, so `launch(timeout_ms: 100)` returns in about 100ms and not in five seconds.
        var inventory = new FakeInventory(_ => []);
        var stopwatch = Stopwatch.StartNew();

        var found = await LaunchWait.ForWindowAsync(
            inventory.ListAsync, 4242, "Calculator", [], timeoutMs: 60, pollMs: 5000);

        stopwatch.Stop();
        found.Should().BeNull();
        inventory.Polls.Should().BeGreaterThanOrEqualTo(2, "it looked once before the wait and once after it");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000,
            "the sleep is min(pollMs, what is left of the budget), never the whole interval");
    }
}
