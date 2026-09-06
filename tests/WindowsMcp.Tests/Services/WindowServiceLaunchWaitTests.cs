using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8's window wait wired to the <b>real</b> A-1 inventory. <see cref="LaunchWaitTests"/> drives
/// the loop over a fake list and would stay green if <c>WindowService.LaunchAsync</c> never
/// polled anything at all; this is the class that fails when the wait is not connected to
/// <c>ListAsync</c>, or when a timeout becomes an exception instead of
/// <c>WindowDetected:false</c>.
/// <para>
/// <c>Category=Integration</c>: the activator is a fake, so <b>no application is started</b> —
/// the only real thing here is the window enumeration and a window this process owns.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceLaunchWaitTests
{
    private sealed class FixedPidActivator(int pid) : IAppActivator
    {
        public int ActivatePackaged(string aumid) => pid;
        public int StartShortcutOrPath(string target) => pid;
    }

    private static Mock<IAppCatalogService> Catalog(string name)
    {
        var entry = new AppEntry(name, "packaged", "Contoso.Test_8wekyb3d8bbwe!App", "package:Contoso.Test_8wekyb3d8bbwe");
        var mock = new Mock<IAppCatalogService>();
        mock.Setup(c => c.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppMatch(entry, 100, "exact"));
        return mock;
    }

    [Fact]
    public async Task LaunchAsync_reports_the_window_of_the_process_the_activation_returned()
    {
        // The activator hands back this process's own pid and this process owns a real top-level
        // window, so the PID rung of the wait has something true to find - through the real
        // EnumWindows, not a hand-written list.
        var marker = "wmcp-launchwait-" + Guid.NewGuid().ToString("N")[..8];
        using var window = new OwnedWindow(marker + " test window");
        var service = new WindowService(null, null, Catalog(marker).Object, new FixedPidActivator(Environment.ProcessId));

        var result = await service.LaunchAsync(marker, waitForWindow: true, timeoutMs: 10_000);

        result.WindowDetected.Should().BeTrue();
        result.Hwnd.Should().NotBeNull();
        result.Title.Should().NotBeNull();
        result.Pid.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task LaunchAsync_reports_a_timeout_as_windowDetected_false_and_still_gives_the_pid()
    {
        // "Sent, window not detected" is an outcome the agent acts on with the pid, never an
        // exception (roadmap C11). A pid nothing owns and a name nothing is titled.
        var name = "wmcp-no-such-app-" + Guid.NewGuid().ToString("N");
        var service = new WindowService(null, null, Catalog(name).Object, new FixedPidActivator(int.MaxValue - 1));

        var result = await service.LaunchAsync(name, waitForWindow: true, timeoutMs: 600);

        result.WindowDetected.Should().BeFalse();
        result.Hwnd.Should().BeNull();
        result.Title.Should().BeNull();
        result.Pid.Should().Be(int.MaxValue - 1, "the pid is what the agent can still act on");
        result.MatchedName.Should().Be(name);
    }
}
