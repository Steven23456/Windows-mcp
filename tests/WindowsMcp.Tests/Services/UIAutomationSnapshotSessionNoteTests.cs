using System.ComponentModel;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8 "snapshot - and everything that renders one": <c>UIAutomationService.SnapshotAsync</c> asks
/// the session service once per snapshot and sets <see cref="SnapshotResult.SessionNote"/> to
/// <see cref="SessionState.Note"/>. A fault in the probe never fails a snapshot: it is caught and the
/// note is null. The service is an OPTIONAL constructor parameter, so every existing construction
/// gets no note.
/// </summary>
/// <remarks>
/// The same harness as <c>UIAutomationSnapshotArgumentTests</c>: the window list is EMPTY, so nothing
/// is walked and no desktop is needed - only the header, and now the session note.
/// </remarks>
[Trait("Category", "Unit")]
public class UIAutomationSnapshotSessionNoteTests
{
    private const string Locked =
        "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it. Stop and tell the user.";

    private static readonly SessionState Available = new(
        Elevated: false, IntegrityLevel: "Medium", UserName: "CONTOSO\\alice", SessionId: 1, Interactive: true,
        ConnectState: "Active", Locked: false, InputDesktop: "Default", InputDesktopAccess: "Opened", InputDesktopError: null,
        InputAvailable: true, Note: null);

    private static readonly SessionState LockedState = Available with { Locked = true, InputAvailable = false, Note = Locked };

    private static Mock<IWindowService> NoWindows()
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WindowInfo>());
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        return mock;
    }

    private static Mock<IInputService> Cursor()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        return mock;
    }

    private static Mock<ISessionService> Session(SessionState state)
    {
        var mock = new Mock<ISessionService>();
        mock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        return mock;
    }

    private static UIAutomationService NewService(ISessionService? session, Mock<IWindowService>? windows = null)
        => new(Cursor().Object, (windows ?? NoWindows()).Object, session: session);

    [Fact]
    public async Task SnapshotAsync_carries_the_session_services_note()
    {
        var session = Session(LockedState);
        using var svc = NewService(session.Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.SessionNote.Should().Be(Locked);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once, "once per snapshot");
    }

    [Fact]
    public async Task SnapshotAsync_takes_the_note_verbatim_rather_than_rebuilding_it()
    {
        // The note is SessionState.Note, whatever it says: one source for the sentence (SessionNotes).
        using var svc = NewService(Session(Available with { InputAvailable = false, Note = "custom sentence." }).Object);

        (await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop))).SessionNote.Should().Be("custom sentence.");
    }

    [Fact]
    public async Task SnapshotAsync_has_no_note_when_the_session_service_says_input_is_available()
    {
        var session = Session(Available);
        using var svc = NewService(session.Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.SessionNote.Should().BeNull();
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once,
            "the service is asked even when the answer turns out to be 'available'");
    }

    [Fact]
    public async Task SnapshotAsync_asks_for_the_note_in_an_observation_too()
    {
        // E-5's observe takes its snapshot through the same call with IncludePopups.
        var session = Session(LockedState);
        using var svc = NewService(session.Object);

        (await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludePopups: true))).SessionNote.Should().Be(Locked);
    }

    [Fact]
    public async Task SnapshotAsync_asks_again_on_every_snapshot_so_a_lock_that_ended_is_not_reported()
    {
        var session = new Mock<ISessionService>();
        session.SetupSequence(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(LockedState)
            .ReturnsAsync(Available);
        using var svc = NewService(session.Object);

        var first = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
        var second = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        first.SessionNote.Should().Be(Locked);
        second.SessionNote.Should().BeNull("the user unlocked between the two snapshots");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ---- a probe fault never fails the snapshot ----------------------------------------------

    public static TheoryData<Exception, bool> Faults => new()
    {
        { new InvalidOperationException("probe broke"), false },
        { new Win32Exception(5), false },
        { new NullReferenceException(), false },
        { new InvalidOperationException("probe broke"), true },
        { new Win32Exception(5), true },
        { new NullReferenceException(), true },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task SnapshotAsync_a_failing_session_service_leaves_the_note_null_and_the_snapshot_whole(Exception fault, bool synchronous)
    {
        var session = new Mock<ISessionService>();
        if (synchronous)
            session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).Throws(fault);
        else
            session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(fault);
        using var svc = NewService(session.Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.SessionNote.Should().BeNull();
        snap.Cursor.Should().Be(new CursorPosition(10, 10), "the rest of the snapshot is still there");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once, "the service WAS asked; its fault was caught");
    }

    [Fact]
    public async Task SnapshotAsync_an_OperationCanceledException_from_the_probe_on_a_live_call_is_just_a_fault()
    {
        // The probe's own cancellation (a timeout inside it, say) is not the CALLER cancelling: the
        // snapshot still comes back, without a note.
        var session = new Mock<ISessionService>();
        session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException("probe gave up"));
        using var svc = NewService(session.Object);
        using var cts = new CancellationTokenSource();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        snap.SessionNote.Should().BeNull();
        snap.Cursor.Should().Be(new CursorPosition(10, 10));
    }

    [Fact]
    public async Task SnapshotAsync_cancelled_while_the_session_is_asked_is_cancelled()
    {
        // "Never throws, cancellation aside": the caller's cancellation is honoured, not swallowed
        // into a null note and a snapshot nobody is waiting for.
        using var cts = new CancellationTokenSource();
        var session = new Mock<ISessionService>();
        session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(LockedState);
            });
        using var svc = NewService(session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SnapshotAsync_passes_the_calls_token_to_the_session_service()
    {
        var session = Session(LockedState);
        using var svc = NewService(session.Object);
        using var cts = new CancellationTokenSource();

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        session.Verify(s => s.GetAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task SnapshotAsync_without_a_session_service_has_no_note()
    {
        // Every construction in the tree before E-8 - and every test - passes no session service.
        using var svc = new UIAutomationService(Cursor().Object, NoWindows().Object);

        (await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop))).SessionNote.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotAsync_refuses_bad_arguments_before_asking_the_session_service()
    {
        // Derived (test-agent): the argument rules are decided before the desktop is touched
        // (UIAutomationSnapshotArgumentTests), and the session probe is part of the desktop.
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        using var svc = NewService(session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "   "));

        await act.Should().ThrowAsync<ArgumentException>();
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

/// <summary>
/// E-8 GREEN: the real <see cref="UIAutomationService"/> with the real <see cref="SessionService"/>
/// and the real window/input services (as <c>UIAutomationSnapshotIntegrationTests</c> builds them).
/// A foreground snapshot only reads: no input, no capture, no desktop switch.
/// </summary>
[Trait("Category", "Integration")]
public class UIAutomationSnapshotSessionNoteIntegrationTests
{
    [Fact]
    public async Task SnapshotAsync_on_this_machine_carries_exactly_the_real_sessions_note()
    {
        var session = new SessionService();
        using var svc = new UIAutomationService(new InputService(), new WindowService(), session: session);

        var state = await session.GetAsync();
        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        // On an unlocked interactive desktop (the normal test host) both are null; on a locked or
        // service-hosted runner the snapshot must carry the very note the session reports.
        snap.SessionNote.Should().Be(state.Note);
        if (Environment.UserInteractive && state.Locked != true)
        {
            state.InputAvailable.Should().BeTrue("the test host's desktop is the user's");
            snap.SessionNote.Should().BeNull("a working desktop carries no warning");
        }
    }
}
