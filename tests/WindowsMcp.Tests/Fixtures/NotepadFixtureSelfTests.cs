using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// The fixture testing itself: after <see cref="NotepadFixture.Dispose"/> the desktop and the disk
/// must look exactly as they did before the constructor ran.
/// <para>
/// This exists because the leak was invisible to every other test. A day of desktop runs left
/// twelve dirty Notepad windows on screen, and because the modern Notepad PERSISTS every open tab
/// under <c>%LOCALAPPDATA%\Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState\TabState</c>
/// and restores them all on its next start, killing the process did not clear them - they came
/// back the moment the next fixture launched Notepad. Nothing failed; the mess simply accumulated.
/// So the check is: record the tab-state file names, run a fixture that DIRTIES its tab (a dirty
/// tab is the case that puts up "Save changes to ...?" and refuses to close), dispose, and prove
/// the window, the tab, the session file and - when the fixture owned the process - the process
/// itself are all gone.
/// </para>
/// <para>
/// <c>Category=UIAutomation</c> and <see cref="DesktopCollection"/>: it launches Notepad,
/// takes the foreground and TYPES, so it must never run unattended.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class NotepadFixtureSelfTests
{
    /// <summary>The text typed into the tab, and the fragment no window may still show afterwards.</summary>
    private const string Probe = "wmcp-leak-probe";

    /// <summary>Poll until <paramref name="done"/> or the timeout: Notepad writes its session state
    /// and exits asynchronously, so a bare assertion right after Dispose is a race.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await done()) return true;
            await Task.Delay(250);
        }
        return await done();
    }

    private static string[] NewTabStateSince(IReadOnlySet<string> before)
        => NotepadFixture.NewTabStateFiles(before, NotepadFixture.TabStateFiles(NotepadFixture.TabStateDirectory));

    [Fact]
    public async Task Fixture_disposes_a_dirty_tab_leaving_no_window_no_session_file_and_no_process()
    {
        var marker = Probe + "-" + Guid.NewGuid().ToString("N")[..8];
        var file = Path.Combine(Path.GetTempPath(), marker + ".txt");
        File.WriteAllText(file, "NotepadFixture self-test: opened, dirtied, and disposed by the test.");
        var tabStateBefore = NotepadFixture.TabStateFiles(NotepadFixture.TabStateDirectory);
        var svc = new WindowService();

        long hwnd;
        bool soleOwner;
        bool shared;
        try
        {
            using (var np = new NotepadFixture(file))
            {
                np.Window.Should().NotBeNull(
                    "the fixture opened Notepad on the file and must identify either the window it "
                    + "created or the pre-existing window its tab landed in");
                np.Title.Should().Contain(marker,
                    "the window or tab is titled after the file, which is how dispose finds it again");
                hwnd = np.Hwnd;
                soleOwner = np.SoleOwner;
                // Modern Notepad may have opened the file as a tab in a window that already
                // existed. Then the WINDOW survives on purpose - it is not the fixture's to close -
                // and what must disappear is the tab, i.e. the title that names our file.
                shared = np.ReusedExistingWindow;

                // Dirty the tab: an untouched tab closes silently, and the case that leaked is the
                // one that puts up the save prompt. Type only once the fixture's own window really
                // holds the foreground - a synthetic keystroke lands wherever the focus is.
                np.BringToForeground();
                await Task.Delay(400);
                var active = await svc.GetActiveAsync();
                active.Should().NotBeNull("something must have the foreground on an interactive desktop");
                active!.Hwnd.Should().Be(hwnd,
                    "the probe types, so the fixture's own window has to be in front first - typing "
                    + "into whatever else had focus is exactly what must never happen");

                await new InputService().TypeAsync(Probe);
                await Task.Delay(700);

                (await svc.ListAsync(includeMinimized: true)).Should().Contain(w => w.Hwnd == hwnd,
                    "arrangement check: the fixture's window is on the desktop before Dispose runs");
            }

            // ---- Dispose has run: nothing of the fixture's may be left -------------------------

            var tabStateRestored = await WaitUntilAsync(
                () => Task.FromResult(NewTabStateSince(tabStateBefore).Length == 0),
                TimeSpan.FromSeconds(8));
            tabStateRestored.Should().BeTrue(
                "the TabState folder must hold exactly the files it held before the constructor "
                + "ran; anything left there is a tab Notepad will restore on its next start "
                + $"(left behind: {string.Join(", ", NewTabStateSince(tabStateBefore))})");

            var windowsGone = await WaitUntilAsync(async () =>
            {
                var listed = await svc.ListAsync(includeMinimized: true);
                var titleGone = !listed.Any(
                    w => w.Title.Contains(Probe, StringComparison.OrdinalIgnoreCase)
                         || w.Title.Contains(marker, StringComparison.OrdinalIgnoreCase));
                return titleGone && (shared || listed.All(w => w.Hwnd != hwnd));
            }, TimeSpan.FromSeconds(8));
            windowsGone.Should().BeTrue(
                shared
                    ? $"the tab the fixture opened in the pre-existing window {hwnd} must be closed "
                      + $"by Dispose - no window may still be titled after '{marker}' or show the "
                      + "probe text - while the window itself, which is not the fixture's, stays"
                    : $"the window the fixture opened (hwnd {hwnd}, '{marker}') and any window "
                      + "still showing the probe text must be closed by Dispose, save prompt and all");

            if (soleOwner)
            {
                var processGone = await WaitUntilAsync(
                    () => Task.FromResult(Process.GetProcessesByName("notepad").Length == 0),
                    TimeSpan.FromSeconds(3));
                processGone.Should().BeTrue(
                    "no Notepad was running before this fixture launched one, so the windowless "
                    + "notepad.exe that survives its last window is the fixture's to terminate");
            }
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The no-file constructor is the one every other UIAutomation class uses
    /// (<c>IClassFixture&lt;NotepadFixture&gt;</c>), so it gets the same leak check - minus the
    /// typing, because an "Untitled - Notepad" that was never touched is the shape those classes
    /// actually leave behind.
    /// </summary>
    [Fact]
    public async Task Fixture_with_no_file_leaves_no_window_and_no_session_file()
    {
        var tabStateBefore = NotepadFixture.TabStateFiles(NotepadFixture.TabStateDirectory);
        var svc = new WindowService();

        long hwnd;
        using (var np = new NotepadFixture())
        {
            np.Window.Should().NotBeNull(
                "the default constructor must produce a window of its own; if this fails, a Notepad "
                + "window was already open and the launch became a tab in it, which the no-file "
                + "fixture cannot identify");
            hwnd = np.Hwnd;
            (await svc.ListAsync(includeMinimized: true)).Should().Contain(w => w.Hwnd == hwnd);
        }

        var gone = await WaitUntilAsync(async () =>
            (await svc.ListAsync(includeMinimized: true)).All(w => w.Hwnd != hwnd),
            TimeSpan.FromSeconds(8));
        gone.Should().BeTrue($"Dispose closes the window it opened (hwnd {hwnd})");

        NewTabStateSince(tabStateBefore).Should().BeEmpty(
            "an untouched Untitled tab still writes session state, and it must not survive the fixture");
    }
}
