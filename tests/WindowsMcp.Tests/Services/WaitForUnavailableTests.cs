using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8 R3, decision A: a <c>wait_for</c> poll whose snapshot is <c>Unavailable</c> (it could not be
/// taken while the user's desktop is unavailable) ENDS THE WAIT AT ONCE with Satisfied:false and the
/// session note as Detail - not the whole timeout followed by "not found anywhere on screen", which
/// would claim the screen was read. Only text_exists and focused_element read a snapshot; the other
/// conditions are untouched. A snapshot that merely CARRIES a note was taken and is judged normally.
/// </summary>
/// <remarks>
/// <c>WaitLoopAsync</c> has no delay seam, so "at once" is pinned by the attempt count plus a
/// wall-clock bound enforced by cancellation: the waits ask for 120 s with a 5 s interval and the
/// token cancels after 4 s. A loop that polls again throws OperationCanceledException at 4 s
/// instead of hanging the suite for two minutes.
/// </remarks>
[Trait("Category", "Unit")]
public class WaitForUnavailableTests
{
    private const string LockedNote =
        "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it. Stop and tell the user.";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(4);

    private static WaitRequest Long(WaitCondition condition) => new(condition, "Save", TimeoutMs: 120_000, IntervalMs: 5_000);

    /// <summary>What UIAutomationService.SnapshotAsync returns when the snapshot could not be taken.</summary>
    private static SnapshotResult Unavailable =>
        new([], null, new CursorPosition(0, 0), -1, [], [], null, false, 0, 0, 0, SessionNote: LockedNote, Unavailable: true);

    /// <summary>A gatherer that hands out the given snapshots in order, then repeats the last one, and counts.</summary>
    private sealed class Polls(params SnapshotResult[] snapshots)
    {
        public int Count { get; private set; }

        public Task<WaitEvidence> Gather(CancellationToken _)
            => Task.FromResult(new WaitEvidence(Snapshot: snapshots[Math.Min(Count++, snapshots.Length - 1)]));
    }

    public static TheoryData<WaitCondition> SnapshotConditions => new() { WaitCondition.TextExists, WaitCondition.FocusedElement };

    [Theory]
    [MemberData(nameof(SnapshotConditions))]
    public async Task WaitLoop_ends_after_the_first_poll_when_the_snapshot_is_unavailable(WaitCondition condition)
    {
        var polls = new Polls(Unavailable);
        using var cts = new CancellationTokenSource(Bound);
        var clock = Stopwatch.StartNew();

        var result = await UIAutomationService.WaitLoopAsync(Long(condition), polls.Gather, cts.Token);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1, "the first unavailable poll ends the wait");
        polls.Count.Should().Be(1);
        result.Detail.Should().Contain(LockedNote, "Detail is the session note");
        result.Detail.Should().NotContain("not found anywhere on screen", "the screen was not read");
        result.Element.Should().BeNull();
        clock.Elapsed.Should().BeLessThan(Bound, "no interval delay is awaited");
    }

    [Theory]
    [MemberData(nameof(SnapshotConditions))]
    public async Task WaitLoop_ends_on_the_first_unavailable_poll_even_after_ordinary_ones(WaitCondition condition)
    {
        // The desktop locks during the wait: the first poll reads an ordinary screen, the second cannot.
        var polls = new Polls(Result(interactive: [Element("el_1", name: "Open")]), Unavailable);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var result = await UIAutomationService.WaitLoopAsync(
            new WaitRequest(condition, "Save", TimeoutMs: 120_000, IntervalMs: 10), polls.Gather, cts.Token);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(2);
        result.Detail.Should().Contain(LockedNote);
    }

    [Fact]
    public async Task WaitLoop_an_unavailable_snapshot_without_a_note_still_ends_the_wait_with_a_reason()
    {
        // Defensive: the service only builds an Unavailable snapshot WITH a note, but the loop is a seam
        // and Detail must never be empty.
        var polls = new Polls(Unavailable with { SessionNote = null });
        using var cts = new CancellationTokenSource(Bound);

        var result = await UIAutomationService.WaitLoopAsync(Long(WaitCondition.TextExists), polls.Gather, cts.Token);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1);
        result.Detail.Should().Be("The user's desktop cannot be read.");
    }

    [Fact]
    public async Task WaitLoop_a_snapshot_that_merely_carries_a_note_can_still_satisfy_text_exists()
    {
        // Taken, with a note: judged normally. A mistaken note can never hide a working desktop.
        var polls = new Polls(Result(interactive: [Element("el_1", name: "Save")]) with { SessionNote = LockedNote });

        var result = await UIAutomationService.WaitLoopAsync(Long(WaitCondition.TextExists), polls.Gather, CancellationToken.None);

        result.Satisfied.Should().BeTrue();
        result.Detail.Should().NotContain(LockedNote);
    }

    [Fact]
    public async Task WaitLoop_a_snapshot_that_merely_carries_a_note_can_still_satisfy_focused_element()
    {
        var polls = new Polls(Result(interactive: [Element("el_1", name: "Save", focused: true)]) with { SessionNote = LockedNote });

        var result = await UIAutomationService.WaitLoopAsync(Long(WaitCondition.FocusedElement), polls.Gather, CancellationToken.None);

        result.Satisfied.Should().BeTrue();
    }

    [Fact]
    public async Task WaitLoop_a_snapshot_that_merely_carries_a_note_keeps_polling_until_the_timeout()
    {
        var polls = new Polls(Result(interactive: [Element("el_1", name: "Open")]) with { SessionNote = LockedNote });

        var result = await UIAutomationService.WaitLoopAsync(
            new WaitRequest(WaitCondition.TextExists, "Save", TimeoutMs: 60, IntervalMs: 10), polls.Gather, CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().BeGreaterThan(1, "a note on a snapshot that was taken does not end the wait");
        result.Detail.Should().Be("'Save' not found anywhere on screen");
    }

    public static TheoryData<WaitCondition> OtherConditions => new()
    {
        WaitCondition.ElementExists, WaitCondition.ElementEnabled, WaitCondition.ActiveWindow,
    };

    [Theory]
    [MemberData(nameof(OtherConditions))]
    public async Task WaitLoop_conditions_that_do_not_read_a_snapshot_are_untouched(WaitCondition condition)
    {
        // Even evidence that happens to carry an Unavailable snapshot does not end these waits:
        // they are judged on their own evidence (Matches / Windows) to the timeout.
        int gathers = 0;
        Task<WaitEvidence> Gather(CancellationToken _)
        {
            gathers++;
            return Task.FromResult(new WaitEvidence(Matches: [], Snapshot: Unavailable, Windows: []));
        }

        var result = await UIAutomationService.WaitLoopAsync(
            new WaitRequest(condition, "Save", TimeoutMs: 60, IntervalMs: 10), Gather, CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        gathers.Should().BeGreaterThan(1);
        result.Detail.Should().NotContain(LockedNote);
    }

    // ---- through the real service: the cursor read refused, the session locked -----------------

    [Theory]
    [MemberData(nameof(SnapshotConditions))]
    public async Task WaitForAsync_on_a_locked_workstation_ends_at_once_with_the_note(WaitCondition condition)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GetCursorPos failed (Win32 error 5)."));
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<WindowInfo>());
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var session = new Mock<ISessionService>();
        session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SessionState(
            false, "Medium", "CONTOSO\\alice", 1, true, "Active", true, null, "Denied", 5, false, LockedNote));
        using var svc = new UIAutomationService(input.Object, windows.Object, session: session.Object);
        using var cts = new CancellationTokenSource(Bound);

        var result = await svc.WaitForAsync(Long(condition), cts.Token);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1);
        result.Detail.Should().Contain(LockedNote);
    }

    // ---- the description (decision C) ------------------------------------------------------

    [Fact]
    public void WaitFor_description_says_a_wait_ends_at_once_when_the_desktop_cannot_be_read()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchRegex("(?i)(at once|immediately)", "the wait ends at once");
        description.Should().MatchRegex("(?i)(cannot|could not|can't) be (read|seen|taken)|unavailable",
            "and says when: the desktop cannot be read");
        description.Should().MatchRegex("(?i)note[^.]*Detail|Detail[^.]*note", "with the session note as Detail");
    }
}
