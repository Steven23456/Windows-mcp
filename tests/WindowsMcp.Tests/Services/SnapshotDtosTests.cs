using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-2 / A-3 / A-4 (R1): the wire contract of the snapshot DTOs. These are the shapes the tool
/// layer serialises in cycle B, so what is pinned here is what the model will see — including the
/// two additive fields on <see cref="ElementTree"/> that must stay INVISIBLE in JSON until a walk
/// is actually truncated, or every existing <c>get_state</c> response changes shape.
/// </summary>
[Trait("Category", "Unit")]
public class SnapshotDtosTests
{
    private static ElementInfo Info(string id = "el_1") =>
        new(id, "Untitled - Notepad", "Window", true, false, new Bounds(0, 0, 800, 600), null, null, null);

    // ---- R1.1 ElementInfo gains Scroll, additively -------------------------------------------

    [Fact]
    public void ElementInfo_still_constructs_with_the_pre_A3_argument_list_and_no_scroll()
    {
        // The nine-argument form is what every caller in the tree uses today; A-3 must not touch it.
        var info = new ElementInfo("el_7", "Save", "Button", true, false, new Bounds(1, 2, 3, 4), null, null, null);
        info.Scroll.Should().BeNull();
    }

    [Fact]
    public void ElementInfo_carries_scroll_when_the_element_has_a_scroll_pattern()
    {
        var scroll = new ScrollInfo(37.0, 0.0, true, false);
        var info = new ElementInfo("el_7", "Text Editor", "Document", true, false, new Bounds(1, 2, 3, 4), null, null, null, scroll);

        info.Scroll.Should().BeSameAs(scroll);
        info.Scroll!.VerticalPercent.Should().Be(37.0);
        info.Scroll.HorizontalPercent.Should().Be(0.0);
        info.Scroll.VerticallyScrollable.Should().BeTrue();
        info.Scroll.HorizontallyScrollable.Should().BeFalse();
    }

    // ---- R1.2 ElementTree's truncation fields are invisible until they matter -----------------

    [Fact]
    public void ElementTree_defaults_are_untruncated_and_no_limit()
    {
        var tree = new ElementTree(Info(), []);
        tree.Truncated.Should().BeFalse();
        tree.ElementLimit.Should().Be(0);
    }

    [Fact]
    public void ElementTree_untruncated_serialises_exactly_as_it_did_before_A4()
    {
        var json = JsonSerializer.Serialize(new ElementTree(Info(), [new ElementTree(Info("el_2"), [])]));

        json.Should().Contain("\"Root\"").And.Contain("\"Children\"");
        json.Should().NotContain("Truncated", "a tree that was not truncated must not grow a key");
        json.Should().NotContain("ElementLimit");
    }

    [Fact]
    public void ElementTree_truncated_root_serialises_both_keys_and_children_neither()
    {
        var tree = new ElementTree(Info(), [new ElementTree(Info("el_2"), [])], true, 500);

        var json = JsonSerializer.Serialize(tree);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("ElementLimit").GetInt32().Should().Be(500);

        var child = doc.RootElement.GetProperty("Children")[0];
        child.TryGetProperty("Truncated", out _).Should().BeFalse("the verdict belongs to the walk, not to every node");
        child.TryGetProperty("ElementLimit", out _).Should().BeFalse();
    }

    // ---- R1.3 request / options defaults -----------------------------------------------------

    [Fact]
    public void SnapshotRequest_defaults_to_the_whole_desktop_no_tree_and_the_server_limit()
    {
        var request = new SnapshotRequest();
        request.Scope.Should().Be(SnapshotScope.Desktop);
        request.WindowTitle.Should().BeNull();
        request.IncludeTree.Should().BeFalse();
        request.MaxElements.Should().Be(0, "0 means 'use the server default', not 'no elements'");
    }

    [Fact]
    public void UiTreeOptions_default_budget_is_500_elements()
        => UiTreeOptions.Default.MaxElements.Should().Be(500);

    // ---- R1.4 the service contract cycle B implements -----------------------------------------

    [Fact]
    public async Task SnapshotAsync_is_on_the_service_interface_and_returns_the_snapshot_result()
    {
        // Shape pin: the tool layer in cycle B mocks exactly this. Green from the stub on purpose —
        // it fails to COMPILE if the signature drifts, which is the regression worth catching.
        var expected = new SnapshotResult([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12);
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var actual = await mock.Object.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground));

        actual.Should().BeSameAs(expected);
        mock.Verify(s => s.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Foreground), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SnapshotResult_serialises_every_block_the_snapshot_tool_reports()
    {
        var window = new WindowInfo("Untitled - Notepad", 1, 4242, "notepad", WindowState.Normal,
            new Bounds(100, 100, 800, 600), 0, true, false, 0);
        var element = new SnapshotElement("el_12", "Untitled - Notepad", "Button", "Save", 612, 388,
            new Bounds(600, 380, 24, 16), "click", false, false, null, null, null, "Ctrl+S", null, null, null);
        var scrollable = new SnapshotScrollable("el_20", "Untitled - Notepad", "Document", "Text Editor",
            500, 400, new Bounds(100, 140, 800, 520), new ScrollInfo(37, 0, true, false));
        var result = new SnapshotResult([window], window, new CursorPosition(612, 388), 0,
            [element], [scrollable], null, true, 500, 57, 31);

        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);

        foreach (var key in new[] { "Windows", "ActiveWindow", "Cursor", "CursorMonitorIndex", "Interactive",
                                    "Scrollable", "Tree", "Truncated", "ElementLimit", "ElementCount", "CaptureMs" })
            doc.RootElement.TryGetProperty(key, out _).Should().BeTrue($"the snapshot JSON must carry '{key}'");

        doc.RootElement.GetProperty("Interactive")[0].GetProperty("CenterX").GetInt32().Should().Be(612);
        doc.RootElement.GetProperty("Scrollable")[0].GetProperty("Scroll").GetProperty("VerticalPercent").GetDouble().Should().Be(37);
        doc.RootElement.GetProperty("ElementCount").GetInt32().Should().Be(57);
        doc.RootElement.GetProperty("CaptureMs").GetInt64().Should().Be(31);
    }

    // ---- A-5 phase 1 (R1): the DOM request flag, the page, and the invisible Pages block -------
    // Same rule A-14 pins for the timings: browser DOM mode is opt-in, so a caller who never asks
    // for it must see EXACTLY the response it saw before A-5 shipped - no extra key, no null block.

    private static SnapshotResult Snapshot(SnapshotPage[]? pages) =>
        new([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12, null, pages);

    private static SnapshotPage ProbePage(string? note = null) => new(
        Window: "A5 Probe Page", DocumentId: "el_7", Title: "A5 Probe Page",
        Url: "http://127.0.0.1:9999/a5", Scroll: new ScrollInfo(12, 0, true, false),
        Text: ["Probe heading", "First paragraph of body text."], Note: note);

    [Fact]
    public void SnapshotRequest_does_not_use_the_dom_by_default()
    {
        // Every pre-A-5 construction in the tree is positional and must keep compiling AND keep
        // meaning "walk the whole window".
        new SnapshotRequest().UseDom.Should().BeFalse();
        new SnapshotRequest(SnapshotScope.Window, "Notepad", true, 25).UseDom.Should().BeFalse();
    }

    [Fact]
    public void SnapshotRequest_carries_use_dom_as_its_last_argument()
        => new SnapshotRequest(SnapshotScope.Foreground, null, false, 0, true).UseDom.Should().BeTrue();

    [Fact]
    public void SnapshotPage_carries_the_document_id_title_url_scroll_and_text()
    {
        var page = ProbePage();

        page.Window.Should().Be("A5 Probe Page");
        page.DocumentId.Should().Be("el_7", "the page document's own element id is what scroll/get_element take");
        page.Title.Should().Be("A5 Probe Page");
        page.Url.Should().Be("http://127.0.0.1:9999/a5");
        page.Scroll!.VerticalPercent.Should().Be(12);
        page.Scroll.VerticallyScrollable.Should().BeTrue();
        page.Text.Should().Equal("Probe heading", "First paragraph of body text.");
        page.Note.Should().BeNull("a page that was found needs no explanation");
    }

    [Fact]
    public void SnapshotPage_without_a_document_is_all_nulls_and_a_note()
    {
        var page = new SnapshotPage("Some Browser", null, null, null, null, [], "no page document found under this window");

        page.DocumentId.Should().BeNull();
        page.Title.Should().BeNull();
        page.Url.Should().BeNull();
        page.Scroll.Should().BeNull();
        page.Text.Should().BeEmpty();
        page.Note.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SnapshotResult_pages_default_to_null_so_every_pre_A5_construction_still_compiles()
    {
        new SnapshotResult([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12)
            .Pages.Should().BeNull();
        new SnapshotResult([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12,
            [new StageTiming("walk", 1)]).Pages.Should().BeNull("Pages sits AFTER Stages");
    }

    [Fact]
    public void SnapshotResult_without_pages_serialises_exactly_as_it_did_before_A5()
    {
        var json = JsonSerializer.Serialize(Snapshot(null));

        json.Should().NotContain("Pages", "a snapshot that did not ask for the DOM grows no key");
    }

    [Fact]
    public void SnapshotResult_with_pages_serialises_them_in_order()
    {
        var second = ProbePage() with { Window = "Other Browser", DocumentId = null, Title = null, Url = null, Scroll = null, Text = [], Note = "no page document found under this window; walked the whole window instead" };

        var json = JsonSerializer.Serialize(Snapshot([ProbePage(), second]));

        using var doc = JsonDocument.Parse(json);
        var pages = doc.RootElement.GetProperty("Pages");
        pages.GetArrayLength().Should().Be(2);
        pages[0].GetProperty("DocumentId").GetString().Should().Be("el_7");
        pages[0].GetProperty("Title").GetString().Should().Be("A5 Probe Page");
        pages[0].GetProperty("Url").GetString().Should().Be("http://127.0.0.1:9999/a5");
        pages[0].GetProperty("Scroll").GetProperty("VerticalPercent").GetDouble().Should().Be(12);
        pages[0].GetProperty("Text")[0].GetString().Should().Be("Probe heading");
        pages[1].GetProperty("Window").GetString().Should().Be("Other Browser");
        pages[1].GetProperty("Note").GetString().Should().Contain("no page document");
    }

    [Fact]
    public void SnapshotResult_with_no_browser_window_still_writes_an_empty_pages_array()
    {
        // Empty is not the same answer as absent: [] says "DOM mode ran and found no browser",
        // absent says "nobody asked". The model must be able to tell those apart.
        var json = JsonSerializer.Serialize(Snapshot([]));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Pages").ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetProperty("Pages").GetArrayLength().Should().Be(0);
    }
}
