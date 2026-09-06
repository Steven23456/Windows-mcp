using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10 on a live desktop: a Notepad window parked behind another window is brought forward by
/// title, by handle, and after being minimized, and <c>window(action:"close", title:…)</c> closes
/// it through the same matcher.
/// <para>
/// <c>Category=UIAutomation</c> — these change which window has the foreground, so they must never
/// run unattended (the phase-4 rule in the B roadmap's C10). They also join
/// <see cref="DesktopCollection"/>: a window coming to the front rewrites the pixels any
/// capture-comparing class is in the middle of comparing, which is exactly the cross-talk that
/// collection exists to serialise away.
/// </para>
/// <para>
/// Every test targets <see cref="NotepadFixture.Window"/> — the window this fixture opened —
/// rather than "the first window whose process is Notepad". A machine that runs this suite
/// routinely has several Notepad windows, and a title search picks an arbitrary one of them.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class WindowForegroundDesktopTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    /// <summary>The fixture's Notepad starts in front; each test parks it where it needs it.</summary>
    public WindowForegroundDesktopTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    /// <summary>
    /// Puts a window that is <b>not</b> Notepad's in front, so "bring Notepad forward" has
    /// something to do, and returns it — or null when this desktop has no other window to park
    /// behind, which is the one case a test may skip.
    /// <para>
    /// The other window is raised through the service's own ladder on purpose: a raw
    /// <c>SetForegroundWindow</c> from this process is subject to exactly the foreground lock the
    /// ladder exists to work around, so the arrangement used to fail before the assertion ran.
    /// </para>
    /// </summary>
    private async Task<WindowInfo?> ParkNotepadBehind(WindowService svc)
    {
        var other = (await svc.ListAsync(includeMinimized: false))
            .Where(w => w.Hwnd != _np.Hwnd
                        && !w.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase)
                        && w.Title.Length > 0
                        && w.State != WindowState.Minimized)
            .OrderBy(w => w.ZOrder)
            .FirstOrDefault();
        if (other is null) return null;

        var parked = await svc.BringToFrontAsync(null, other.Hwnd);
        parked.Success.Should().BeTrue(
            $"the arrangement needs '{other.Title}' in front, and bringing a window forward is the "
            + "very thing B-10 claims to do");
        await Task.Delay(300);
        (await svc.GetActiveAsync())!.Hwnd.Should().Be(other.Hwnd, "the arrangement has to have taken effect");
        return other;
    }

    private WindowInfo Fixture()
    {
        _np.Window.Should().NotBeNull(
            "the fixture launched notepad.exe and waited for the window it opened to appear");
        return _np.Window!;
    }

    [Fact]
    public async Task BringToFrontAsync_brings_a_notepad_window_forward_from_behind_another_window()
    {
        // The title path, which deliberately does NOT assume the fixture's window: with several
        // Notepad windows open, "notepad" matches whichever is frontmost, and what has to be true
        // is that the window it reports is a Notepad window and is the one now in front.
        var svc = new WindowService();
        Fixture();
        var parked = await ParkNotepadBehind(svc);
        if (parked is null) return;   // no other window on this desktop: nothing to park behind

        var result = await svc.BringToFrontAsync("notepad", null);

        result.Window.ProcessName.Should().ContainEquivalentOf("notepad",
            "the request was 'notepad' and only a Notepad window may answer it");
        result.MatchStrategy.Should().BeOneOf("substring", "fuzzy",
            "the request is 'notepad' and the title is 'Untitled - Notepad'");
        result.Score.Should().BeGreaterThanOrEqualTo(70);
        result.Success.Should().BeTrue("the ladder has three rungs and one of them has to work here");
        result.Strategy.Should().NotBeNull("a success names the rung that produced it");

        var active = await svc.GetActiveAsync();
        active!.Hwnd.Should().Be(result.Window.Hwnd, "GetActiveAsync is the independent check on Success");
        active.Hwnd.Should().NotBe(parked.Hwnd);
    }

    [Fact]
    public async Task BringToFrontAsync_by_hwnd_brings_the_fixtures_notepad_forward()
    {
        var svc = new WindowService();
        var notepad = Fixture();
        if (await ParkNotepadBehind(svc) is null) return;

        var result = await svc.BringToFrontAsync(null, notepad.Hwnd);

        result.MatchStrategy.Should().Be("hwnd");
        result.Window.Hwnd.Should().Be(notepad.Hwnd, "a handle names one window and no other");
        result.Success.Should().BeTrue();
        (await svc.GetActiveAsync())!.Hwnd.Should().Be(notepad.Hwnd);
    }

    [Fact]
    public async Task BringToFrontAsync_restores_a_minimized_notepad_and_reports_Restored()
    {
        // By handle throughout: minimizing "the first Notepad window" and then asking for
        // "notepad" by title matched a different one of them, which is the ambiguity the hwnd
        // target exists to remove.
        var svc = new WindowService();
        var notepad = Fixture();
        await svc.ExecuteAsync("minimize", null, notepad.Hwnd);
        await Task.Delay(400);
        (await svc.ListAsync(includeMinimized: true)).Single(w => w.Hwnd == notepad.Hwnd)
            .State.Should().Be(WindowState.Minimized, "the arrangement has to have taken effect");

        var result = await svc.BringToFrontAsync(null, notepad.Hwnd);

        result.Restored.Should().BeTrue("the window was minimized, so SW_RESTORE was sent");
        result.Success.Should().BeTrue();
        var after = (await svc.ListAsync()).Single(w => w.Hwnd == notepad.Hwnd);
        after.State.Should().NotBe(WindowState.Minimized);
        after.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchToWindow_over_the_tool_layer_reports_the_matched_notepad()
    {
        var svc = new WindowService();
        Fixture();
        if (await ParkNotepadBehind(svc) is null) return;
        var tools = new WindowTools(svc, new Mock<IVirtualDesktopService>().Object);

        var json = await tools.SwitchToWindow("notepad");

        json.Should().Contain("\"Success\":true").And.Contain("Notepad");
        (await svc.GetActiveAsync())!.ProcessName.Should().ContainEquivalentOf("notepad");
    }
}

/// <summary>
/// B-10's acceptance line for <c>window(action:"close")</c>. Its own class with its own Notepad,
/// because it destroys the window: a shared <c>IClassFixture</c> instance would leave every other
/// test in the class looking for a Notepad that is gone.
/// <para>
/// The test BAILS when the fixture's file landed in a Notepad window that already existed
/// (<see cref="NotepadFixture.ReusedExistingWindow"/>). The modern Notepad is one process hosting
/// every window: <c>notepad.exe file</c> launched while a Notepad window is open adds a TAB to
/// that window instead of creating one, and <c>window(action:"close")</c> posts WM_CLOSE to the
/// whole window — which would take the other tabs, belonging to whoever opened them, with it.
/// There is no window-close semantics to assert in that state, so the run is skipped rather than
/// made destructive. Close Notepad before the desktop bracket if you want this test to execute.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class WindowCloseDesktopTests
{
    [Fact]
    public async Task Window_close_by_a_partial_title_closes_the_matched_window()
    {
        // window(action:"close", title:"<fragment>") closes the window whose title contains the
        // fragment — the whole point of routing the acting actions through the matcher. The
        // fixture opens a uniquely named file so the fragment can only match this window: several
        // windows called "Untitled - Notepad" is the normal state of a machine that runs this
        // suite, and closing an arbitrary one of them would be a destructive flake.
        var marker = "wmcp-close-" + Guid.NewGuid().ToString("N")[..8];
        var file = Path.Combine(Path.GetTempPath(), marker + ".txt");
        File.WriteAllText(file, "B-10: this window is closed by the test that opened it.");
        try
        {
            using var np = new NotepadFixture(file);
            np.Window.Should().NotBeNull(
                "the fixture opened Notepad on the file and waited for the window - or the "
                + "pre-existing window whose tab now holds it - to appear");
            if (np.ReusedExistingWindow) return;   // shared window: see the class comment
            var opened = np.Window!;
            opened.Title.Should().Contain(marker, "Notepad titles the window after the file it opened");
            var svc = new WindowService();

            var result = await svc.ExecuteAsync("close", marker);

            result.Success.Should().BeTrue();
            result.Hwnd.Should().Be(opened.Hwnd, "the fragment names exactly one window: the one just opened");
            result.Title.Should().Be(opened.Title, "the response names the window that was closed, not the request");
            result.MatchStrategy.Should().BeOneOf("substring", "fuzzy");

            await Task.Delay(700);
            (await svc.ListAsync(includeMinimized: true)).Should().NotContain(w => w.Hwnd == opened.Hwnd,
                "WM_CLOSE was posted to the matched window and it is gone");
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
