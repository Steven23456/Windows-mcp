using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

// MCP9005: the SDK marks the Sampling feature obsolete (SEP-2577); see
// src/WindowsMcp/Tools/ISamplingClient.cs. These tests never sample - the seam just has to exist.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): <c>scrape(source:"dom")</c> against a REAL Edge window on the A-5 probe page,
/// through the real <c>UIAutomationService</c> and the real <c>WindowService</c>. The mocked
/// siblings in <see cref="WebToolsTests"/> prove the tool's arithmetic; only this one proves that
/// what Chromium's accessibility tree hands back is a page a model can read.
/// <para>
/// <c>Category=UIAutomation</c>: it needs the interactive desktop, it opens a browser window, and
/// Chromium builds its accessibility tree lazily on the first UIA query. Never run headless.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(EdgeCollection.Name)]
public class WebToolsDomDesktopTests
{
    private const string TopHint = "Reached top of the page; scroll down to see more.";

    private readonly EdgeFixture _edge;

    public WebToolsDomDesktopTests(EdgeFixture edge) => _edge = edge;

    private static WebTools Tools(IUIAutomationService uia, IWindowService windows)
        => new(new Mock<IWebService>().Object, uia, windows);

    /// <summary>The real UIA service, over the real window service — only the cursor is faked.</summary>
    private static UIAutomationService RealUia(IWindowService windows)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new CursorPosition(10, 10));
        return new UIAutomationService(input.Object, windows);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Scrape_dom_of_a_named_window_returns_the_pages_visible_text_and_its_scroll_hint()
    {
        if (!_edge.Available) return;   // no Edge on this machine: nothing to assert

        var windows = new WindowService();
        using var uia = RealUia(windows);
        var tools = Tools(uia, windows);

        var json = await tools.ScrapeAsync(
            url: null, query: null, source: "dom", summarize: false, max_chars: 100000,
            window: _edge.WindowTitle, sampling: new NeverSampling(), ct: CancellationToken.None);

        var result = Parse(json);
        result.GetProperty("Source").GetString().Should().Be("dom");
        result.GetProperty("Title").GetString().Should().Be(EdgeFixture.PageTitle);
        result.GetProperty("Url").GetString().Should().StartWith(_edge.BaseUrl);

        var content = result.GetProperty("Content").GetString()!;
        content.Should().Contain("Probe heading").And.Contain("First paragraph");
        content.Should().NotContain("Last paragraph",
            "it is below a 3000px spacer, off-screen, and the DOM walk drops off-screen text (D-7)");
        content.Should().EndWith(TopHint, "the page has not been scrolled, so there is more below");
        result.GetProperty("Chars").GetInt32().Should().Be(content.Length);
        result.GetProperty("Truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("Summarized").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Scrape_dom_without_a_window_picks_the_browser_that_is_in_front()
    {
        if (!_edge.Available) return;

        var windows = new WindowService();
        await windows.BringToFrontAsync(null, _edge.Hwnd);
        using var uia = RealUia(windows);

        var json = await Tools(uia, windows).ScrapeAsync(
            url: null, query: null, source: "dom", summarize: false, max_chars: 100000,
            window: null, sampling: new NeverSampling(), ct: CancellationToken.None);

        var result = Parse(json);
        result.GetProperty("Source").GetString().Should().Be("dom");
        result.GetProperty("Title").GetString().Should().Be(EdgeFixture.PageTitle,
            "the frontmost browser window is the tab the agent means by 'the open page'");
        result.GetProperty("Content").GetString().Should().Contain("Probe heading");
    }

    /// <summary>A client with no sampling capability: these tests never ask for a summary.</summary>
    private sealed class NeverSampling : ISamplingClient
    {
        public bool Supported => false;

        public Task<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken ct)
            => throw new InvalidOperationException("summarize was never requested by these tests");
    }
}
#pragma warning restore MCP9005
