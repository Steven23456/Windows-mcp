using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// PR #43 sixth review round.
/// <para>
/// W1: in the Foreground fallback (no inventory window flagged active) a PLAIN snapshot reads no
/// native handle of the root - before E-5 none was read, and a provider that would time out on that
/// read must not fail <c>snapshot(scope:"foreground")</c>. An observation reads it, and a timeout on
/// it propagates.
/// </para>
/// <para>
/// W2: in an observation, a <see cref="TimeoutException"/> from the Name read used for a window's
/// LABEL propagates as that same instance at each site: (a) the Foreground-fallback root, (b) a
/// window resolved by handle that the inventory does not list, (c) an owned window the inventory
/// does not list, (d) a desktop-level popup. The reviewer's regression: the label read times out
/// but the walk, reading the same Name again, would succeed - so the fakes fail the FIRST Name read
/// only. Without <c>IncludePopups</c> (sites a and b) the label read stays lenient: "" and the
/// snapshot succeeds. A non-timeout failure keeps today's behaviour in both modes ("" - and for an
/// owned window or a popup the label <c>popup</c>).
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotObservationLabelTests : IDisposable
{
    private const int WindowType = 50032;
    private const long Listed = 0xAB10;
    private const long Palette = 0xAD00;
    private const long Unlisted = 0xAB01;
    private const long Owned = 0xAC01;
    private const long Popup = 0xAB30;

    private readonly FakeUiWindows _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private static Dictionary<string, Exception> NameFailsOnce(Exception e) => new() { ["Name"] = e };

    private static TimeoutException Hung(string site) => new($"{site} label read timed out (wmcp-e5)");

    private AutomationElement Window(string name, long handle, IReadOnlyDictionary<string, Exception>? firstReads = null,
        IReadOnlyDictionary<string, Exception>? fails = null, int controlType = WindowType) =>
        _fakes.Window(name, handle, controlType: controlType, bounds: (100, 100, 400, 300),
            children: [_fakes.Window(name + " button", 0, bounds: (110, 110, 50, 20))],
            firstReadFailures: firstReads, propertyFailures: fails);

    /// <param name="activeListed">Whether the inventory flags a window active (false = the Foreground fallback).</param>
    private UIAutomationService NewService(bool activeListed, Dictionary<long, AutomationElement>? byHandle = null,
        AutomationElement? foregroundRoot = null, Dictionary<long, long[]>? owned = null, AutomationElement[]? desktop = null)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([UiTree.SnapshotFixtures.Window("Listed app", hwnd: Listed, pid: 4242, isActive: activeListed)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object);
        svc.ElementFromHandle = h => byHandle?.GetValueOrDefault((long)h);
        if (foregroundRoot is not null) svc.ForegroundRoot = () => foregroundRoot;
        svc.OwnedWindowsOf = h => owned?.GetValueOrDefault((long)h)?.Select(x => (nint)x).ToArray() ?? [];
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => desktop ?? [];
        return svc;
    }

    private static string[] Rows(SnapshotResult snap) => snap.Interactive.Select(e => $"{e.Window}: {e.Name}").ToArray();

    // ============================================================================================
    // W1: the plain Foreground fallback reads no handle
    // ============================================================================================

    [Fact]
    public async Task W1_a_plain_foreground_fallback_reads_no_native_handle_of_its_root()
    {
        using var svc = NewService(activeListed: false, foregroundRoot: Window("Tool palette", Palette));

        await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        _fakes.HandleReads.Should().Be(0, "before E-5 the fallback read no handle; only an observation needs it");
    }

    [Fact]
    public async Task W1_a_plain_foreground_fallback_whose_handle_read_would_time_out_still_succeeds()
    {
        var root = Window("Tool palette", Palette, fails: new Dictionary<string, Exception>
        {
            ["NativeWindowHandle"] = new TimeoutException("handle read timed out (wmcp-e5)"),
        });
        using var svc = NewService(activeListed: false, foregroundRoot: root);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        Rows(snap).Should().Equal(["Tool palette: Tool palette button"]);
    }

    [Fact]
    public async Task W1_guard_an_observations_foreground_fallback_reads_the_handle_and_a_timeout_on_it_propagates()
    {
        var hung = new TimeoutException("handle read timed out (wmcp-e5)");
        using var svc = NewService(activeListed: false,
            foregroundRoot: Window("Tool palette", Palette, fails: new Dictionary<string, Exception> { ["NativeWindowHandle"] = hung }));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    // ============================================================================================
    // W2: label reads in an observation
    // ============================================================================================

    [Fact]
    public void Harness_a_first_read_failure_fails_once_and_the_next_read_answers()
    {
        var root = Window("Palette", Palette, firstReads: NameFailsOnce(Hung("harness")));

        var first = () => root.Properties.Name.Value;

        first.Should().Throw<TimeoutException>();
        root.Properties.Name.Value.Should().Be("Palette");
    }

    [Fact]
    public async Task W2a_the_foreground_fallback_roots_label_read_timing_out_propagates()
    {
        var hung = Hung("foreground root");
        using var svc = NewService(activeListed: false, foregroundRoot: Window("Tool palette", Palette, firstReads: NameFailsOnce(hung)));

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task W2b_an_unlisted_by_handle_windows_label_read_timing_out_propagates()
    {
        var hung = Hung("unlisted window");
        using var svc = NewService(activeListed: true,
            byHandle: new() { [Unlisted] = Window("Untitled tool", Unlisted, firstReads: NameFailsOnce(hung)) });

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Unlisted, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task W2c_an_unlisted_owned_windows_label_read_timing_out_propagates()
    {
        var hung = Hung("owned window");
        var dialog = Window("Owned dialog", Owned, firstReads: NameFailsOnce(hung));
        var host = _fakes.Window("Listed app", Listed, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [_fakes.Window("Host button", 0, bounds: (110, 110, 50, 20)), dialog]);
        using var svc = NewService(activeListed: true,
            byHandle: new() { [Listed] = host, [Owned] = dialog }, owned: new() { [Listed] = [Owned] });

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Listed, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    [Fact]
    public async Task W2d_a_desktop_level_popups_label_read_timing_out_propagates()
    {
        var hung = Hung("popup");
        var popup = _fakes.Window("Context menu", Popup, controlType: 50009, bounds: (150, 150, 120, 80),
            children: [_fakes.Window("Menu item A", 0, bounds: (155, 155, 100, 20))], firstReadFailures: NameFailsOnce(hung));
        using var svc = NewService(activeListed: true, byHandle: new() { [Listed] = Window("Listed app", Listed) }, desktop: [popup]);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Listed, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    // ---- guards: the plain snapshot (sites a and b exist there too) --------------------------------

    [Fact]
    public async Task W2_guard_a_plain_foreground_fallback_whose_label_read_times_out_succeeds_with_an_empty_label()
    {
        using var svc = NewService(activeListed: false,
            foregroundRoot: Window("Tool palette", Palette, firstReads: NameFailsOnce(Hung("foreground root"))));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        Rows(snap).Should().Equal([": Tool palette button"], "the label is \"\" and the walk goes on");
    }

    [Fact]
    public async Task W2_guard_a_plain_by_handle_snapshot_of_an_unlisted_window_whose_label_read_times_out_succeeds()
    {
        using var svc = NewService(activeListed: true,
            byHandle: new() { [Unlisted] = Window("Untitled tool", Unlisted, firstReads: NameFailsOnce(Hung("unlisted window"))) });

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Unlisted));

        Rows(snap).Should().Equal([": Untitled tool button"]);
    }

    // ---- guards: a non-timeout label failure keeps today's behaviour ------------------------------

    private static System.Runtime.InteropServices.COMException NameReadFailed() =>
        new("name read failed (wmcp-e5)", unchecked((int)0x80004005));

    [Fact]
    public async Task W2_guard_an_observation_whose_fallback_root_label_read_fails_otherwise_labels_it_empty()
    {
        using var svc = NewService(activeListed: false, foregroundRoot: Window("Tool palette", Palette, firstReads: NameFailsOnce(NameReadFailed())));

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        Rows(snap).Should().Equal([": Tool palette button"]);
    }

    [Fact]
    public async Task W2_guard_an_observation_whose_owned_window_label_read_fails_otherwise_labels_it_popup()
    {
        var dialog = Window("Owned dialog", Owned, firstReads: NameFailsOnce(NameReadFailed()));
        var host = _fakes.Window("Listed app", Listed, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [_fakes.Window("Host button", 0, bounds: (110, 110, 50, 20)), dialog]);
        using var svc = NewService(activeListed: true,
            byHandle: new() { [Listed] = host, [Owned] = dialog }, owned: new() { [Listed] = [Owned] });

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Listed, IncludePopups: true));

        Rows(snap).Should().Equal(["Listed app: Host button", "popup: Owned dialog button"]);
    }

    [Fact]
    public async Task W2_guard_an_observation_whose_popup_label_read_fails_otherwise_labels_it_popup()
    {
        var popup = _fakes.Window("Context menu", Popup, controlType: 50009, bounds: (150, 150, 120, 80),
            children: [_fakes.Window("Menu item A", 0, bounds: (155, 155, 100, 20))], firstReadFailures: NameFailsOnce(NameReadFailed()));
        using var svc = NewService(activeListed: true, byHandle: new() { [Listed] = Window("Listed app", Listed) }, desktop: [popup]);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Listed, IncludePopups: true));

        Rows(snap).Should().Equal(["Listed app: Listed app button", "popup: Menu item A"]);
    }

    // ---- W3 audit rows: the popup filter's other reads ---------------------------------------------

    [Theory]
    [InlineData("IsOffscreen")]
    [InlineData("BoundingRectangle")]
    public async Task W3_a_timeout_reading_a_popups_offscreen_flag_or_bounds_in_the_filter_propagates(string property)
    {
        // Closes round 4's note: U2b pinned the handle and process reads; these are the other two.
        var hung = new TimeoutException($"popup {property} read timed out (wmcp-e5)");
        var popup = _fakes.Window("Context menu", Popup, controlType: 50009, bounds: (150, 150, 120, 80),
            propertyFailures: new Dictionary<string, Exception> { [property] = hung });
        using var svc = NewService(activeListed: true, byHandle: new() { [Listed] = Window("Listed app", Listed) }, desktop: [popup]);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Listed, IncludePopups: true));

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(hung);
    }

    // ============================================================================================
    // W2 at the tool
    // ============================================================================================

    [Fact]
    public async Task Observe_foreground_whose_window_label_read_times_out_returns_the_note()
    {
        using var svc = NewService(activeListed: false,
            foregroundRoot: Window("Tool palette", Palette, firstReads: NameFailsOnce(Hung("foreground root"))));

        var text = await new Observation(svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().StartWith("acted\n\nObservation failed: ");
        text.Should().Contain("foreground root label read timed out (wmcp-e5)");
        text.Should().NotContainEquivalentOf("gone");
        text.Should().NotContain("Cursor: (", "no rows: the observation is the note");
    }
}
