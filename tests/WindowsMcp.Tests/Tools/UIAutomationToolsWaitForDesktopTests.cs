using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-6 (R64-R70) on a live desktop: the five conditions driven end to end through the REAL
/// service against the Notepad fixture. The mocked rows above prove the tool asks for the right
/// thing and the pure rows prove the verdicts; only these prove a wait actually resolves against
/// a real window (CLAUDE.md's "a mocked collaborator is not evidence").
/// <para>
/// UIAutomation-category and in <see cref="DesktopCollection"/> (roadmap C10): the class needs the
/// interactive desktop, opens a Notepad window, brings it forward and clicks inside it. Every
/// wait is scoped to that window by title, never to "whatever has focus".
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class UIAutomationToolsWaitForDesktopTests : IClassFixture<NotepadFixture>, IDisposable
{
    private readonly UIAutomationService _uia;
    private readonly InputService _input;
    private readonly UIAutomationTools _tools;

    public UIAutomationToolsWaitForDesktopTests(NotepadFixture np)
    {
        np.BringToForeground();
        _input = new InputService();
        _uia = new UIAutomationService(_input, new WindowService());
        _tools = new UIAutomationTools(_uia);
    }

    public void Dispose()
    {
        _uia.Dispose();
        GC.SuppressFinalize(this);
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static bool Satisfied(JsonElement json) => json.GetProperty("Satisfied").GetBoolean();
    private static string Detail(JsonElement json) => json.GetProperty("Detail").GetString()!;

    /// <summary>The editor inside the NOTEPAD window (D-5 scope=window), never the foreground's.</summary>
    private async Task<ElementInfo> EditorAsync()
    {
        var found = await _uia.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        return found.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element in the Notepad window");
    }

    [Fact]
    public async Task Active_window_resolves_for_the_window_that_was_just_brought_forward()
    {
        // The item's "Done when": wait_for(condition:"active_window", text:"Notepad") after a
        // launch/focus. No tree walk is involved - this is A-1's inventory only.
        var json = Json(await _tools.WaitFor("Notepad", timeout_ms: 5000, interval_ms: 200, condition: "active_window"));

        Satisfied(json).Should().BeTrue();
        Detail(json).Should().StartWith("active window is").And.ContainAny("(exact)", "(substring)", "(fuzzy)");
        json.GetProperty("Attempts").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Text_exists_finds_a_string_that_is_really_on_screen()
    {
        // Self-adapting on purpose: the name is read out of a live snapshot of the same window, so
        // the row cannot rot when Windows renames a menu.
        var snapshot = await _uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, "Notepad"));
        var name = snapshot.Interactive.Select(e => e.Name).FirstOrDefault(n => n.Trim().Length >= 4)
            ?? throw new Xunit.Sdk.XunitException("The Notepad window exposed no named interactive element");

        var json = Json(await _tools.WaitFor(name.Trim(), timeout_ms: 8000, interval_ms: 250,
            scope: "window", window: "Notepad", condition: "text_exists"));

        Satisfied(json).Should().BeTrue();
        Detail(json).Should().ContainEquivalentOf(name.Trim());
    }

    [Fact]
    public async Task Element_enabled_resolves_for_the_editor()
    {
        var editor = await EditorAsync();
        if (editor.Name.Trim().Length < 3) return;   // an unnamed editor: nothing to wait on by name

        var json = Json(await _tools.WaitFor(editor.Name.Trim(), timeout_ms: 8000, interval_ms: 250,
            scope: "window", window: "Notepad", condition: "element_enabled"));

        Satisfied(json).Should().BeTrue();
        Detail(json).Should().ContainEquivalentOf("enabled");
        json.GetProperty("Element").GetProperty("ElementId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Focused_element_resolves_after_the_editor_is_clicked()
    {
        var editor = await EditorAsync();
        if (editor.Name.Trim().Length < 3) return;
        var bounds = editor.Bounds!;
        await _input.ClickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        var json = Json(await _tools.WaitFor(editor.Name.Trim(), timeout_ms: 8000, interval_ms: 250,
            scope: "window", window: "Notepad", condition: "focused_element"));

        Satisfied(json).Should().BeTrue();
        json.GetProperty("Element").GetProperty("Name").GetString().Should().ContainEquivalentOf(editor.Name.Trim());
    }

    [Fact]
    public async Task A_condition_that_never_holds_times_out_into_a_result_rather_than_an_error()
    {
        var missing = "wmcp-b6-never-appears-" + Guid.NewGuid().ToString("N");

        var json = Json(await _tools.WaitFor(missing, timeout_ms: 600, interval_ms: 100,
            scope: "window", window: "Notepad", condition: "text_exists"));

        Satisfied(json).Should().BeFalse();
        json.GetProperty("Attempts").GetInt32().Should().BeGreaterThanOrEqualTo(2, "600 ms at 100 ms polls repeatedly");
        json.GetProperty("ElapsedMs").GetInt64().Should().BeGreaterThanOrEqualTo(600);
        Detail(json).Should().Contain(missing);
    }
}

/// <summary>
/// B-6 with A-5's DOM mode on a real Chromium window: the whole reason <c>use_dom</c> is a
/// parameter of <c>wait_for</c> — a page's words live in the page document, not in the window's
/// controls, so a browser wait that does not walk the DOM never sees them.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(EdgeCollection.Name)]
public class UIAutomationToolsWaitForDomTests
{
    private readonly EdgeFixture _edge;

    public UIAutomationToolsWaitForDomTests(EdgeFixture edge) => _edge = edge;

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Text_exists_with_use_dom_finds_a_heading_in_the_page()
    {
        if (!_edge.Available) return;   // no Edge on this machine: nothing to assert
        using var uia = new UIAutomationService(new InputService(), new WindowService());
        var tools = new UIAutomationTools(uia);

        var json = Json(await tools.WaitFor("Probe heading", timeout_ms: 15000, interval_ms: 500,
            scope: "window", window: _edge.WindowTitle, condition: "text_exists", use_dom: true));

        json.GetProperty("Satisfied").GetBoolean().Should().BeTrue();
        json.GetProperty("Detail").GetString().Should().ContainEquivalentOf("page");
    }

    [Fact]
    public async Task The_same_wait_without_use_dom_does_not_see_the_page_text()
    {
        // What use_dom buys, stated as a difference: the heading is a Text node inside the page,
        // and a snapshot without DOM mode carries the window's controls, not the page's prose.
        if (!_edge.Available) return;
        using var uia = new UIAutomationService(new InputService(), new WindowService());
        var tools = new UIAutomationTools(uia);

        var json = Json(await tools.WaitFor("Probe heading", timeout_ms: 800, interval_ms: 200,
            scope: "window", window: _edge.WindowTitle, condition: "text_exists"));

        json.GetProperty("Satisfied").GetBoolean().Should().BeFalse();
    }
}
