using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-12 phase 1 (R4): <c>WindowInfo.DesktopId</c> stops being reserved. The pure
/// <c>WindowFilter</c> still leaves it null (<see cref="WindowFilterTests"/>); the enumerator is
/// what fills it, from the optional <see cref="IVirtualDesktopService"/>.
/// <para>
/// <c>Category=Integration</c>, not Unit: filling the field happens around the real
/// <c>EnumWindows</c> walk, so these run against the real enumeration with a mocked desktop
/// service (deterministic ids, exact call verification) and once with both services real. Same
/// headless bracket as <see cref="WindowServiceTests"/> — a window station, no foreground app,
/// no input, no capture.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceDesktopIdTests
{
    private static readonly Regex GuidD = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);

    private static Mock<IVirtualDesktopService> Answering(Func<long, string?> answer)
    {
        var mock = new Mock<IVirtualDesktopService>();
        mock.Setup(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long hwnd, CancellationToken _) => answer(hwnd));
        return mock;
    }

    [Fact]
    public async Task ListAsync_fills_DesktopId_from_the_desktop_service_window_by_window()
    {
        var desktops = Answering(hwnd => "desktop-of-" + hwnd);

        var list = await new WindowService(desktops.Object).ListAsync();

        list.Should().NotBeEmpty("this session has windows open (see WindowServiceTests' non-vacuity guard)");
        foreach (var w in list)
        {
            w.DesktopId.Should().Be("desktop-of-" + w.Hwnd,
                "every listed window carries the id the service gave for that window's handle");
            desktops.Verify(d => d.GetWindowDesktopIdAsync(w.Hwnd, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task ListAsync_asks_only_about_the_windows_it_returns()
    {
        // Filter first, then ask: one COM round-trip per *listed* window, not per enumerated
        // one (EnumWindows yields hundreds of invisible ones).
        var desktops = Answering(_ => "id");

        var list = await new WindowService(desktops.Object).ListAsync();

        desktops.Verify(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Exactly(list.Length));
    }

    [Fact]
    public async Task ListAsync_leaves_DesktopId_null_when_the_service_has_no_answer()
    {
        var desktops = Answering(_ => null);

        var list = await new WindowService(desktops.Object).ListAsync();

        list.Should().NotBeEmpty();
        list.Should().OnlyContain(w => w.DesktopId == null, "an unknown desktop stays null, it is not invented");
    }

    [Fact]
    public async Task ListAsync_leaves_DesktopId_null_when_no_desktop_service_was_injected()
    {
        // The 39 existing `new WindowService()` call sites (and any host that has not registered
        // the service) keep working, with the field simply unfilled.
        var list = await new WindowService().ListAsync();

        list.Should().NotBeEmpty();
        list.Should().OnlyContain(w => w.DesktopId == null);
    }

    [Fact]
    public async Task ListAsync_forwards_its_cancellation_token_to_the_desktop_service()
    {
        using var cts = new CancellationTokenSource();
        var desktops = Answering(_ => "id");

        var list = await new WindowService(desktops.Object).ListAsync(ct: cts.Token);

        list.Should().NotBeEmpty();
        desktops.Verify(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), cts.Token), Times.Exactly(list.Length),
            "the caller's token is the one that reaches the desktop service, on every window");
        desktops.Verify(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), CancellationToken.None), Times.Never);
    }

    [Fact]
    public async Task ListAsync_still_returns_the_windows_when_the_desktop_service_throws()
    {
        // Resolved ambiguity: DesktopId is one optional field. A virtual-desktop hiccup must not
        // cost the caller the whole window inventory (IVirtualDesktopService's own contract is
        // "null, never throw", so this is the backstop for a contract violation).
        var desktops = new Mock<IVirtualDesktopService>();
        desktops.Setup(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("COM said no"));

        var act = () => new WindowService(desktops.Object).ListAsync();

        await act.Should().NotThrowAsync();
        var list = await act();
        list.Should().NotBeEmpty().And.OnlyContain(w => w.DesktopId == null);
    }

    [Fact]
    public async Task ListAsync_propagates_a_cancellation_raised_by_the_desktop_service()
    {
        // The swallow-everything backstop above has one exception: a cancelled call is the caller
        // walking away, and must not come back as a successful list with unfilled ids.
        var desktops = new Mock<IVirtualDesktopService>();
        desktops.Setup(d => d.GetWindowDesktopIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => new WindowService(desktops.Object).ListAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetActiveAsync_carries_the_DesktopId_too()
    {
        var desktops = Answering(hwnd => "desktop-of-" + hwnd);

        var active = await new WindowService(desktops.Object).GetActiveAsync();

        if (active is null)
            return;   // no foreground window in this run
        active.DesktopId.Should().Be("desktop-of-" + active.Hwnd,
            "'active' is the inventory's own entry, so it carries the same fields");
    }

    // ---- both collaborators real --------------------------------------------------------------

    [Fact]
    public async Task ListAsync_with_the_real_services_tags_every_window_with_a_real_desktop_id()
    {
        var service = new VirtualDesktopService(new RegistryService());
        var windows = await new WindowService(service).ListAsync();
        var probe = windows.FirstOrDefault(w => w.State != WindowState.Minimized);
        if (probe is null || await service.GetWindowDesktopIdAsync(probe.Hwnd) is null)
            return;   // no interactive desktop, or no IVirtualDesktopManager on this build

        // The manager answers for only a few of the hundreds of top-level windows (the rest refuse
        // or report GUID_NULL), so "every showing window is tagged" would flake on the desktop's
        // composition. What holds: at least one is, and every id present is well-formed.
        windows.Should().Contain(w => w.DesktopId != null, "the probe window above was tagged, so the list carries ids");
        windows.Where(w => w.DesktopId != null).Should()
            .OnlyContain(w => GuidD.IsMatch(w.DesktopId!), "the id is a lower-case dashed GUID");

        var desktops = await service.ListAsync();
        if (desktops.Length == 0)
            return;   // this build does not list desktop ids in the registry
        windows.Where(w => w.DesktopId != null).Select(w => w.DesktopId!).Distinct()
            .Should().BeSubsetOf(desktops.Select(d => d.Id),
                "a window's desktop id is one of the ids the desktop inventory reports");
    }
}
