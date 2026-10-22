using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tests.Services;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// S-11 (R5 with R7) end to end: the REAL <see cref="ControlIndicator"/> the container builds,
/// behind the REAL call-tool filter, over the REAL MCP wire. <see cref="ControlIndicatorFilterTests"/>
/// prove the filter against a mock indicator and <see cref="ControlIndicatorTests"/> prove the
/// indicator against direct calls; neither would notice the filter resolving a different instance
/// from the one that draws, a <c>WM_HOTKEY</c> takeover that never reaches a result, or the host's
/// disposal leaving the windows and the combination behind.
/// <para>
/// The harness runs with the indicator off (D1); this class turns it back on through
/// <c>configureServices</c> with <see cref="ControlIndicatorTests.TestChord"/> (Ctrl+Alt+Shift+F13),
/// never the user's real combination. It injects no input - the click goes to a mock
/// <see cref="IInputService"/> and the takeover is a posted <c>WM_HOTKEY</c> - but it draws a ring
/// and a banner, so it is Integration in <see cref="DesktopCollection"/>, like
/// <see cref="ControlIndicatorTests"/>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public class ControlIndicatorPipelineTests
{
    private static readonly Dictionary<string, object?> WaitArgs = new() { ["seconds"] = 0.01 };

    [Fact]
    public async Task A_driving_call_shows_the_real_indicator_and_a_posted_takeover_reaches_the_next_result_once()
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClickResult(10, 20, MouseButton.Left, 1));
        var server = await HttpTransportTests.Harness.StartAsync(configureServices: services =>
        {
            services.AddSingleton(new ControlIndicatorOptions(true, ControlIndicatorTests.TestChord, TimeSpan.FromSeconds(30)));
            services.AddSingleton(input.Object);
        });
        IntPtr ring, banner;
        try
        {
            await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
            var indicator = server.App.Services.GetRequiredService<IControlIndicator>()
                .Should().BeOfType<ControlIndicator>("the container's own indicator, built from the registered options").Subject;

            var waited = await client.CallToolAsync("wait", WaitArgs);
            waited.IsError.Should().NotBe(true);
            indicator.IsVisible.Should().BeFalse("wait injects nothing");
            indicator.RingHandle.Should().Be(0, "nothing is created before the first driving call");

            var clicked = await client.CallToolAsync("click", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 20 });

            clicked.IsError.Should().NotBe(true);
            input.Verify(i => i.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()), Times.Once);
            (await ControlIndicatorTests.WithinAsync(2000, () => indicator.IsVisible)).Should()
                .BeTrue("the filter began an action on the container's indicator, and the 30 s linger keeps it up after the click");
            (await ControlIndicatorTests.WithinAsync(2000, () => !ControlIndicatorTests.ChordIsFree())).Should()
                .BeTrue("while it is up the takeover combination is the indicator's");

            ControlIndicatorTests.PostTakeover(indicator.BannerHandle).Should().BeTrue();
            (await ControlIndicatorTests.WithinAsync(2000, () => !indicator.IsVisible)).Should().BeTrue("the takeover hides it at once");

            var next = await client.CallToolAsync("wait", WaitArgs);
            var after = await client.CallToolAsync("wait", WaitArgs);

            next.IsError.Should().NotBe(true);
            next.Content.Should().HaveCount(2, "the tool's own answer, then the note");
            var note = next.Content[^1].Should().BeOfType<TextContentBlock>().Subject.Text;
            note.Should().ContainEquivalentOf("took over", "the agent is told the user took the controls back");
            note.Should().Contain("Ctrl+Alt+Shift+F13", "and with the configured combination");
            after.Content.Should().ContainSingle("the note is delivered once");

            // The next driving call shows it again - and holds the combination again, so the
            // host's disposal below has something real to give back.
            await client.CallToolAsync("click", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 20 });
            (await ControlIndicatorTests.WithinAsync(2000, () => indicator.IsVisible)).Should().BeTrue("the next driving call shows it again");
            (await ControlIndicatorTests.WithinAsync(2000, () => !ControlIndicatorTests.ChordIsFree())).Should().BeTrue();
            ring = new IntPtr(indicator.RingHandle);
            banner = new IntPtr(indicator.BannerHandle);
        }
        finally
        {
            await server.DisposeAsync();
        }

        (await ControlIndicatorTests.WithinAsync(2000, () => !ControlIndicatorTests.IsWindow(ring) && !ControlIndicatorTests.IsWindow(banner)))
            .Should().BeTrue("disposing the host disposes the singleton, which destroys both windows");
        (await ControlIndicatorTests.WithinAsync(2000, ControlIndicatorTests.ChordIsFree)).Should()
            .BeTrue("and gives the combination back");
    }
}
