using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// PR #43 Z5/Z6 against a REAL tooltip: a comctl32 tracking tooltip OWNED by a window of this process
/// (<see cref="OwnedTrackingTooltip"/>), shown with no input and no activation. Observing the owner by
/// handle with <c>IncludePopups</c> lifts the tooltip out as an owned window, and its text reaches
/// <c>SnapshotResult.PopupText</c>. Real <see cref="WindowService"/>, real UIA, the real default owner
/// relation; the input mock is strict except the header's cursor read.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class ObservePopupTextIntegrationTests : IDisposable
{
    private readonly OwnedTrackingTooltip _tip = new();
    private readonly UIAutomationService _svc;

    public ObservePopupTextIntegrationTests()
    {
        var input = new Mock<IInputService>(MockBehavior.Strict);
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(5, 5));
        _svc = new UIAutomationService(input.Object, new WindowService());
    }

    public void Dispose()
    {
        _svc.Dispose();
        _tip.Dispose();
    }

    private void TooltipIsUp()
    {
        _tip.ToolAdded.Should().BeTrue("precondition: TTM_ADDTOOL accepted the tracking tool");
        _tip.TooltipVisible.Should().BeTrue("precondition: TTM_TRACKACTIVATE showed the tooltip without input");
    }

    [Fact]
    public void Harness_UIA_exposes_the_owned_tooltip_as_a_ToolTip_whose_name_is_its_text()
    {
        // What UI Automation itself reports for the tooltip window, independently of the service.
        TooltipIsUp();
        using var automation = new UIA3Automation();

        var element = automation.FromHandle(_tip.Tooltip);

        element.Properties.ControlType.ValueOrDefault.ToString().Should().Be("ToolTip");
        element.Properties.Name.ValueOrDefault.Should().Be(_tip.TipText);
        _svc.OwnedWindowsOf(_tip.Host).Should().Contain(_tip.Tooltip, "the tooltip is owned by the host and visible");
    }

    [Fact]
    public async Task Observing_the_owner_by_handle_with_popups_returns_the_owned_tooltips_text()
    {
        TooltipIsUp();

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_tip.Host, IncludePopups: true));

        snap.PopupText.Should().NotBeNull().And.Contain(line => line.Contains(_tip.TipText, StringComparison.Ordinal),
            "Z6: an owned window walked as a popup contributes its text - the real-world tooltip is an owned window");
        snap.Interactive.Select(e => e.Name).Should().Contain(_tip.HostButton);
    }

    [Fact]
    public async Task Without_popups_the_snapshot_has_no_popup_text()
    {
        TooltipIsUp();

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: (long)_tip.Host));

        snap.PopupText.Should().BeNull();
    }
}
