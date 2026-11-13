using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8, PR #46 review: "ids are valid until the next snapshot", and the empty <c>Unavailable</c>
/// snapshot returned when the snapshot fails on an unavailable desktop IS the next snapshot. So the
/// fallback retires the previous snapshot generation exactly as a normal snapshot does: snapshot ids
/// (<c>AddSnapshot</c>) expire, find_element / get_state ids (the LRU, <c>Add</c>) survive.
/// <para>
/// The previous snapshot is seeded straight into the service's cache with <c>AddSnapshot</c> - what
/// a populated snapshot does for every walked node - so no desktop is walked. The baseline row
/// proves that seed behaves like a real previous snapshot under a normal next snapshot.
/// </para>
/// <para>
/// A failure that returns NO snapshot (input available, the original exception propagates) is not
/// "the next snapshot": ids stay. Today a normal snapshot evicts on the STA thread AFTER the walk and
/// the popup pass, just before it mints the new ids - so a failure before or during the walk never
/// evicted, and these rows pin that. A failure after the eviction (while minting / projecting the
/// new ids) would already have evicted; no seam reaches that point, so it is not pinned.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotUnavailableEvictionTests : IDisposable
{
    private const string LockedNote =
        "The workstation is locked: nothing on the user's desktop can be seen or driven until the user unlocks it. Stop and tell the user.";

    private const long Hwnd = 0x5A5A;

    private static readonly SessionState Available = new(
        Elevated: false, IntegrityLevel: "Medium", UserName: "CONTOSO\\alice", SessionId: 1, Interactive: true,
        ConnectState: "Active", Locked: false, InputDesktop: "Default", InputDesktopAccess: "Opened", InputDesktopError: null,
        InputAvailable: true, Note: null);

    private static readonly SessionState Locked = Available with
    {
        Locked = true,
        InputDesktop = null,
        InputDesktopAccess = "Denied",
        InputDesktopError = 5,
        InputAvailable = false,
        Note = LockedNote,
    };

    private readonly UIA3Automation _automation = new();
    private readonly List<UIAutomationService> _services = [];

    public void Dispose()
    {
        foreach (var svc in _services) svc.Dispose();
        _automation.Dispose();
    }

    /// <summary>An element nobody reads: these rows only ask whether its id is still in the cache.</summary>
    private AutomationElement FakeElement() => new(new Mock<FrameworkAutomationElementBase>(MockBehavior.Loose, _automation).Object);

    private static ElementCache<AutomationElement> CacheOf(UIAutomationService svc) => (ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(svc)!;

    private static Mock<IInputService> Cursor(bool refused)
    {
        var mock = new Mock<IInputService>();
        if (refused)
            mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("GetCursorPos failed (Win32 error 5)."));
        else
            mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        return mock;
    }

    private static Mock<IWindowService> Windows(params WindowInfo[] list)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(list);
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        return mock;
    }

    private static Mock<ISessionService> Session(SessionState state)
    {
        var mock = new Mock<ISessionService>();
        mock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        return mock;
    }

    /// <summary>The service, with a previous snapshot's id and an earlier find's id already issued.</summary>
    private (UIAutomationService Svc, string SnapshotId, string FindId) Arrange(
        bool cursorRefused, SessionState? session, Mock<IWindowService>? windows = null)
    {
        var svc = new UIAutomationService(Cursor(cursorRefused).Object, (windows ?? Windows()).Object,
            session: session is null ? null : Session(session).Object);
        _services.Add(svc);
        var cache = CacheOf(svc);
        var findId = cache.Add(FakeElement());              // find_element / get_state: the LRU
        cache.EvictSnapshot();                              // what a snapshot does first...
        var snapshotId = cache.AddSnapshot(FakeElement());  // ...then one id per walked node
        return (svc, snapshotId, findId);
    }

    private static async Task ShouldBeExpired(UIAutomationService svc, string id)
    {
        CacheOf(svc).TryGet(id, out _).Should().BeFalse($"{id} belonged to the previous snapshot generation");
        Func<Task> lookup = () => svc.GetElementAsync(id);
        await lookup.Should().ThrowAsync<KeyNotFoundException>().WithMessage($"Element '{id}' has expired*",
            "the same expired-id refusal a normal next snapshot produces");
    }

    private static void ShouldBeLive(UIAutomationService svc, string id, string because)
        => CacheOf(svc).TryGet(id, out _).Should().BeTrue(because);

    // ---- baseline: what a normal next snapshot does to the two kinds of id ---------------------

    [Fact]
    public async Task A_normal_next_snapshot_expires_the_previous_snapshot_ids_and_keeps_the_find_ids()
    {
        var (svc, snapshotId, findId) = Arrange(cursorRefused: false, session: Available);

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        await ShouldBeExpired(svc, snapshotId);
        ShouldBeLive(svc, findId, "find_element ids live in the LRU, not in the snapshot generation");
    }

    // ---- (1) (2) the Unavailable fallback is the next snapshot -----------------------------------

    [Fact]
    public async Task The_unavailable_fallback_expires_the_previous_snapshot_ids()
    {
        var (svc, snapshotId, _) = Arrange(cursorRefused: true, session: Locked);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Unavailable.Should().BeTrue("precondition: this is the fallback snapshot");
        await ShouldBeExpired(svc, snapshotId);
    }

    [Fact]
    public async Task The_unavailable_fallback_after_a_walk_timeout_expires_the_previous_snapshot_ids()
    {
        // The failure happens inside the STA walk, before the normal eviction point: the fallback
        // must still retire the generation.
        var (svc, snapshotId, _) = Arrange(cursorRefused: false, session: Locked with { Note = LockedNote },
            windows: Windows(Window("Save As", hwnd: Hwnd, isActive: true)));
        svc.ElementFromHandle = _ => throw new TimeoutException("the UIA provider did not answer");

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Hwnd, IncludePopups: true));

        snap.Unavailable.Should().BeTrue("precondition: this is the fallback snapshot");
        await ShouldBeExpired(svc, snapshotId);
    }

    [Fact]
    public async Task The_unavailable_fallback_keeps_the_find_ids()
    {
        var (svc, _, findId) = Arrange(cursorRefused: true, session: Locked);

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        ShouldBeLive(svc, findId, "a snapshot - this one included - only retires snapshot ids");
    }

    [Fact]
    public async Task Ids_a_snapshot_after_the_fallback_issues_are_unaffected()
    {
        var (svc, _, _) = Arrange(cursorRefused: true, session: Locked);
        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        var next = CacheOf(svc).AddSnapshot(FakeElement());   // the generation after the fallback

        ShouldBeLive(svc, next, "the fallback retired the generation before it, not the one after");
    }

    // ---- (3) a failure that returns no snapshot is not the next snapshot -----------------------

    [Fact]
    public async Task A_refused_cursor_with_input_available_propagates_and_keeps_every_id()
    {
        var (svc, snapshotId, findId) = Arrange(cursorRefused: true, session: Available);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        await act.Should().ThrowAsync<InvalidOperationException>();
        ShouldBeLive(svc, snapshotId, "no snapshot was returned, so the previous one is still the latest");
        ShouldBeLive(svc, findId, "nothing touches the LRU");
    }

    [Fact]
    public async Task A_refused_cursor_with_no_session_service_propagates_and_keeps_every_id()
    {
        var (svc, snapshotId, _) = Arrange(cursorRefused: true, session: null);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        await act.Should().ThrowAsync<InvalidOperationException>();
        ShouldBeLive(svc, snapshotId, "today's behaviour: a snapshot that fails before the walk evicts nothing");
    }

    [Fact]
    public async Task A_walk_timeout_with_input_available_propagates_and_keeps_every_id()
    {
        // Today's behaviour for a failure DURING the walk: the eviction point (after the walk, on the
        // STA thread) is never reached, so the previous snapshot's ids stay.
        var (svc, snapshotId, findId) = Arrange(cursorRefused: false, session: Available,
            windows: Windows(Window("Save As", hwnd: Hwnd, isActive: true)));
        svc.ElementFromHandle = _ => throw new TimeoutException("the UIA provider did not answer");

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Hwnd, IncludePopups: true));

        await act.Should().ThrowAsync<TimeoutException>();
        ShouldBeLive(svc, snapshotId, "a failure before the eviction point evicts nothing");
        ShouldBeLive(svc, findId, "nothing touches the LRU");
    }

    [Fact]
    public async Task Bad_arguments_on_a_locked_desktop_keep_every_id()
    {
        var (svc, snapshotId, _) = Arrange(cursorRefused: true, session: Locked);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "   "));

        await act.Should().ThrowAsync<ArgumentException>();
        ShouldBeLive(svc, snapshotId, "a refused call is not a snapshot");
    }

    [Fact]
    public async Task A_caller_cancelled_snapshot_on_a_locked_desktop_keeps_every_id()
    {
        var (svc, snapshotId, _) = Arrange(cursorRefused: true, session: Locked);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ShouldBeLive(svc, snapshotId, "a cancelled call returned no snapshot");
    }

    // ---- (4) a successful snapshot with a note behaves as today -----------------------------------

    [Fact]
    public async Task A_successful_snapshot_with_a_note_expires_snapshot_ids_and_keeps_find_ids_as_today()
    {
        var (svc, snapshotId, findId) = Arrange(cursorRefused: false, session: Locked);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Unavailable.Should().BeFalse();
        snap.SessionNote.Should().Be(LockedNote);
        await ShouldBeExpired(svc, snapshotId);
        ShouldBeLive(svc, findId, "find_element ids survive a snapshot");
    }
}
