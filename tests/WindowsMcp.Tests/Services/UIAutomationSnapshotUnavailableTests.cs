using System.ComponentModel;
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
/// E-8 R2, review decision 1 (F1): "the warning must arrive when the snapshot cannot be taken". On a
/// locked or secure desktop GetCursorPos is refused, and the cursor read is the first thing a
/// snapshot does - so in R1 the snapshot threw a bare cursor error before the note was ever asked
/// for, and the feature missed its main case.
/// <list type="bullet">
/// <item>Success: the session is asked once and its Note attached (UIAutomationSnapshotSessionNoteTests).</item>
/// <item>Failure, other than the caller's cancellation or an ArgumentException: the session is asked
/// once. InputAvailable:false -> the call RETURNS an empty snapshot carrying the note. Input
/// available, or the probe faults -> the ORIGINAL exception propagates unchanged.</item>
/// <item>Bad arguments are refused before the session is asked.</item>
/// </list>
/// The real <see cref="UIAutomationService"/> on the fake harness: mocked input and window services,
/// and walk seams that throw - nothing on the real desktop is read or walked.
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationSnapshotUnavailableTests
{
    private const string LockedNote =
        "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it. Stop and tell the user.";

    private const string SecureNote =
        "A secure desktop has the input (a UAC prompt, or the sign-in or lock screen): the user's desktop cannot be seen or driven until the user answers it. Stop and tell the user.";

    private const string CursorError = "GetCursorPos failed (Win32 error 5).";

    private static readonly SessionState Available = new(
        Elevated: false, IntegrityLevel: "Medium", UserName: "CONTOSO\\alice", SessionId: 1, Interactive: true,
        ConnectState: "Active", Locked: false, InputDesktop: "Default", InputDesktopAccess: "Opened", InputDesktopError: null,
        InputAvailable: true, Note: null);

    private static readonly SessionState Locked = Available with
    {
        Locked = true,
        InputDesktop = null,
        InputDesktopAccess = "Denied", InputDesktopError = 5,
        InputAvailable = false,
        Note = LockedNote,
    };

    private static readonly SessionState SecureDesktop = Available with
    {
        InputDesktop = null,
        InputDesktopAccess = "Denied", InputDesktopError = 5,
        InputAvailable = false,
        Note = SecureNote,
    };

    private const long Hwnd = 0x5A5A;

    private static Mock<IWindowService> Windows(params WindowInfo[] list)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(list);
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        return mock;
    }

    private static Mock<IInputService> CursorOk()
    {
        var mock = new Mock<IInputService>();
        mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        return mock;
    }

    /// <summary>The cursor read refused, as GetCursorPos is when the input desktop is not the caller's.</summary>
    private static (Mock<IInputService> Mock, InvalidOperationException Thrown) CursorRefused()
    {
        var thrown = new InvalidOperationException(CursorError);
        var mock = new Mock<IInputService>();
        mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(thrown);
        return (mock, thrown);
    }

    private static Mock<ISessionService> Session(SessionState state)
    {
        var mock = new Mock<ISessionService>();
        mock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        return mock;
    }

    private static UIAutomationService NewService(Mock<IInputService> input, Mock<IWindowService>? windows, ISessionService? session)
        => new(input.Object, (windows ?? Windows()).Object, session: session);

    private static void ShouldBeTheEmptySnapshotCarrying(SnapshotResult snap, string note)
    {
        snap.Windows.Should().BeEmpty();
        snap.ActiveWindow.Should().BeNull();
        snap.Cursor.Should().Be(new CursorPosition(0, 0));
        snap.CursorMonitorIndex.Should().Be(-1, "the cursor is on no display");
        snap.Interactive.Should().BeEmpty();
        snap.Scrollable.Should().BeEmpty();
        snap.Truncated.Should().BeFalse();
        snap.ElementCount.Should().Be(0);
        snap.SessionNote.Should().Be(note);
        snap.Unavailable.Should().BeTrue("E-8 R3 decision A: a caller can tell \"could not be taken\" from \"empty\"");
    }

    // ---- (a) the cursor read fails and the session says input is unavailable ------------------

    [Fact]
    public async Task SnapshotAsync_a_refused_cursor_on_a_locked_workstation_returns_the_empty_snapshot_with_the_note()
    {
        var (input, _) = CursorRefused();
        var session = Session(Locked);
        using var svc = NewService(input, null, session.Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        ShouldBeTheEmptySnapshotCarrying(snap, LockedNote);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once, "asked exactly once on failure");
    }

    [Fact]
    public async Task SnapshotAsync_the_empty_snapshot_renders_the_warning_over_an_empty_desktop()
    {
        var (input, _) = CursorRefused();
        using var svc = NewService(input, null, Session(SecureDesktop).Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        SnapshotRenderer.Render(snap).Should().Be(Lines(
            "Warning: " + SecureNote,
            "Cursor: (0, 0) on no display",
            "Active window: none",
            "Windows (z-order, topmost first):",
            "Interactive (0 of 0, ids valid until the next snapshot):",
            "Scrollable (0):"));
    }

    [Theory]
    [InlineData(SnapshotScope.Desktop)]
    [InlineData(SnapshotScope.Foreground)]
    public async Task SnapshotAsync_returns_the_empty_snapshot_in_every_scope(SnapshotScope scope)
    {
        var (input, _) = CursorRefused();
        using var svc = NewService(input, null, Session(Locked).Object);

        ShouldBeTheEmptySnapshotCarrying(await svc.SnapshotAsync(new SnapshotRequest(scope)), LockedNote);
    }

    [Fact]
    public async Task SnapshotAsync_returns_the_empty_snapshot_for_a_window_by_title_too()
    {
        var (input, _) = CursorRefused();
        using var svc = NewService(input, Windows(Window("Untitled - Notepad", hwnd: Hwnd)), Session(Locked).Object);

        ShouldBeTheEmptySnapshotCarrying(
            await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Notepad")), LockedNote);
    }

    // ---- (b) (c) the original exception, unchanged, when the session does not explain it ------

    [Fact]
    public async Task SnapshotAsync_a_refused_cursor_with_input_available_propagates_the_original_exception()
    {
        var (input, thrown) = CursorRefused();
        var session = Session(Available);
        using var svc = NewService(input, null, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Should().BeSameAs(thrown, "unchanged: same exception, same type, same message");
        ex.Message.Should().Be(CursorError);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once, "the session WAS asked, once");
    }

    public static TheoryData<Exception, bool> ProbeFaults => new()
    {
        { new InvalidOperationException("probe broke"), false },
        { new Win32Exception(5), false },
        { new InvalidOperationException("probe broke"), true },
        { new Win32Exception(5), true },
    };

    [Theory]
    [MemberData(nameof(ProbeFaults))]
    public async Task SnapshotAsync_a_refused_cursor_with_a_faulting_session_propagates_the_original_exception(Exception fault, bool synchronous)
    {
        var (input, thrown) = CursorRefused();
        var session = new Mock<ISessionService>();
        if (synchronous) session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).Throws(fault);
        else session.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(fault);
        using var svc = NewService(input, null, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown,
            "the snapshot's own failure is the answer, not the probe's");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SnapshotAsync_a_refused_cursor_with_no_session_service_propagates_the_original_exception()
    {
        var (input, thrown) = CursorRefused();
        using var svc = NewService(input, null, session: null);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown);
    }

    // ---- (d) a failure later in the snapshot -----------------------------------------------

    [Fact]
    public async Task SnapshotAsync_a_failing_window_inventory_on_a_locked_workstation_returns_the_empty_snapshot()
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("EnumWindows failed"));
        var session = Session(Locked);
        using var svc = NewService(CursorOk(), windows, session.Object);

        ShouldBeTheEmptySnapshotCarrying(await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop)), LockedNote);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SnapshotAsync_a_failing_window_inventory_with_input_available_propagates()
    {
        var thrown = new InvalidOperationException("EnumWindows failed");
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(thrown);
        using var svc = NewService(CursorOk(), windows, Session(Available).Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task SnapshotAsync_a_walk_timeout_in_an_observation_on_a_secure_desktop_returns_the_empty_snapshot()
    {
        // An observation's walk lets a TimeoutException out (a hung provider must not be skipped
        // quietly); on a secure desktop that is the session's doing, so the warning is the answer.
        var session = Session(SecureDesktop);
        using var svc = NewService(CursorOk(), Windows(Window("Save As", hwnd: Hwnd, isActive: true)), session.Object);
        svc.ElementFromHandle = _ => throw new TimeoutException("the UIA provider did not answer");

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Hwnd, IncludePopups: true));

        ShouldBeTheEmptySnapshotCarrying(snap, SecureNote);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SnapshotAsync_a_walk_timeout_with_input_available_propagates_the_timeout()
    {
        var thrown = new TimeoutException("the UIA provider did not answer");
        using var svc = NewService(CursorOk(), Windows(Window("Save As", hwnd: Hwnd, isActive: true)), Session(Available).Object);
        svc.ElementFromHandle = _ => throw thrown;

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Hwnd, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task SnapshotAsync_a_window_that_closed_on_a_locked_workstation_returns_the_empty_snapshot()
    {
        // Derived (test-agent): "anything but the caller's cancellation or an ArgumentException"
        // includes the KeyNotFoundException of a window asked for by handle that is gone.
        using var svc = NewService(CursorOk(), Windows(Window("Save As", hwnd: Hwnd, isActive: true)), Session(Locked).Object);
        svc.ElementFromHandle = _ => null;

        ShouldBeTheEmptySnapshotCarrying(
            await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Hwnd, IncludePopups: true)), LockedNote);
    }

    // ---- (e) a bad call is a bad call whatever the session ------------------------------------

    [Fact]
    public async Task SnapshotAsync_bad_arguments_are_refused_without_asking_the_session_even_when_locked()
    {
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        var (input, _) = CursorRefused();
        using var svc = NewService(input, null, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "   "));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*windowTitle*");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SnapshotAsync_an_ArgumentException_from_a_collaborator_propagates_without_asking_the_session()
    {
        var thrown = new ArgumentException("not a valid flag", "includeHidden");
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(thrown);
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        using var svc = NewService(CursorOk(), windows, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Should().BeSameAs(thrown);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- (f) the caller's cancellation ----------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_cancelled_by_the_caller_mid_snapshot_propagates_without_asking_the_session()
    {
        using var cts = new CancellationTokenSource();
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new CursorPosition(1, 1));
            });
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        using var svc = NewService(input, null, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SnapshotAsync_cancelled_before_the_call_never_asks_the_session()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        using var svc = NewService(CursorOk(), null, session.Object);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SnapshotAsync_an_OperationCanceledException_that_is_not_the_callers_counts_as_a_failure()
    {
        // Derived (test-agent): only the CALLER's cancellation is exempt. A collaborator's own
        // TaskCanceledException on a live token is a failure like any other.
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException("cursor read timed out"));
        var session = Session(Locked);
        using var svc = NewService(input, null, session.Object);
        using var cts = new CancellationTokenSource();

        ShouldBeTheEmptySnapshotCarrying(await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token), LockedNote);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- (g) success: unchanged, asked exactly once --------------------------------------------

    [Fact]
    public async Task SnapshotAsync_a_snapshot_that_succeeds_is_never_replaced_by_the_empty_one()
    {
        // "A mistaken note can never hide a working snapshot": a header was read, so it is returned.
        var session = Session(Locked);
        using var svc = NewService(CursorOk(), Windows(Window("Untitled - Notepad", hwnd: 0, isActive: true)), session.Object);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Cursor.Should().Be(new CursorPosition(10, 10));
        snap.Windows.Should().ContainSingle();
        snap.SessionNote.Should().Be(LockedNote);
        snap.Unavailable.Should().BeFalse("it was taken: a note on a working snapshot is not Unavailable");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- (h) the observation through the real service -----------------------------------------

    [Fact]
    public async Task Observe_on_a_locked_workstation_shows_the_warning_not_observation_failed()
    {
        var (input, _) = CursorRefused();
        using var svc = NewService(input, null, Session(Locked).Object);
        var observation = new Observation(svc) { Delay = (_, _) => Task.CompletedTask };

        var text = await observation.AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("pressed enter"));

        text.Should().StartWith("pressed enter\n\n");
        text.Should().Contain("\nWarning: " + LockedNote + "\n", "the observation carries the session's warning line");
        text.Should().NotContain("Observation failed");
        text.Should().NotContain(CursorError);
    }

    [Fact]
    public async Task Observe_with_input_available_still_reports_the_cursor_failure_as_observation_failed()
    {
        var (input, _) = CursorRefused();
        using var svc = NewService(input, null, Session(Available).Object);
        var observation = new Observation(svc) { Delay = (_, _) => Task.CompletedTask };

        var text = await observation.AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("pressed enter"));

        text.Should().Contain("Observation failed").And.Contain(CursorError);
        text.Should().NotContain("Warning:");
    }
}
