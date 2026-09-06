using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// C-3 R4, the half that needs a desktop: a real window that answers WM_CLOSE. Everything else
/// about the graceful kill is proven headless in <c>ProcessServiceKillTests</c>; what only a live
/// Notepad can show is that the process leaves ON ITS OWN inside the grace window, so
/// <c>forced:false</c> is reachable at all.
/// <para>
/// In <see cref="DesktopCollection"/> because it opens a Notepad window. It runs only when this
/// fixture is the SOLE owner of Notepad: modern Notepad hosts every window in one process, so a
/// graceful kill of that pid closes every Notepad window on the desktop — somebody else's tabs
/// included. Anything less than sole ownership is bailed out of, not worked around.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class ProcessToolsKillDesktopTests
{
    private static ProcessTools Tools() => new(
        new ProcessService(new WmiService()),
        new Mock<IServiceControlService>().Object,
        new Mock<ITaskSchedulerService>().Object,
        new Mock<IEventLogService>().Object);

    [Fact]
    public async Task Graceful_kill_closes_an_unmodified_notepad_without_forcing_it()
    {
        using var notepad = new NotepadFixture();
        if (notepad.Window is null) return;                 // no window was identified: nothing to close
        if (!notepad.SoleOwner || notepad.ReusedExistingWindow) return;   // shared process: see the class comment

        int pid = notepad.Window.Pid;

        var json = await Tools().Process(
            "kill", pid: pid, confirm: true, graceful: true, grace_ms: 10_000);

        using var doc = JsonDocument.Parse(json);
        var killed = doc.RootElement.GetProperty("killed");
        killed.GetArrayLength().Should().Be(1);
        var row = killed[0];
        row.GetProperty("pid").GetInt32().Should().Be(pid);
        row.GetProperty("graceful").GetBoolean().Should().BeTrue();
        row.GetProperty("exitedGracefully").GetBoolean().Should().BeTrue(
            "an unmodified Notepad has nothing to save, so WM_CLOSE is enough");
        row.GetProperty("forced").GetBoolean().Should().BeFalse(
            "the point of the graceful path is that TerminateProcess is never reached");
        row.GetProperty("waitedMs").GetInt32().Should().BeGreaterThan(0,
            "the wait is measured, not assumed");

        var stillThere = () => System.Diagnostics.Process.GetProcessById(pid);
        stillThere.Should().Throw<ArgumentException>("the process really did exit");
    }
}
