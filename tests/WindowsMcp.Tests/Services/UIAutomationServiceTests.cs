using FlaUI.Core.Definitions;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "UIAutomation")]
public class UIAutomationServiceTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    public UIAutomationServiceTests(NotepadFixture np) => _np = np;

    private static UIAutomationService NewService() => new(new InputService());

    [Fact]
    public async Task GetStateAsync_returns_tree_with_notepad_root()
    {
        using var svc = NewService();
        var state = await svc.GetStateAsync();
        state.Root.Name.Should().NotBeNullOrEmpty();
        state.Children.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FindElementAsync_finds_notepad_text_area()
    {
        using var svc = NewService();
        var matches = await svc.FindElementAsync("", FindKind.Text);
        matches.Matches.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Concurrency_50_parallel_calls_no_deadlock()
    {
        using var svc = NewService();
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => svc.GetStateAsync()).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Root.Should().NotBeNull());
    }

    // ---- D-2: interact_element ---------------------------------------------------------------

    [Fact]
    public async Task InteractAsync_type_enters_text_into_the_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d2-{Guid.NewGuid():N}"[..12];

        var result = await svc.InteractAsync(id, "type", stamp);

        // Modern (XAML) Notepad has no writable ValuePattern on its editor, so this usually proves
        // the keyboard fallback; classic Notepad takes the ValuePattern path. Both must land the text.
        result.Method.Should().BeOneOf("ValuePattern", "Keyboard");
        await Task.Delay(300);   // let injected keystrokes reach the control
        (await svc.GetTextAsync(id)).Should().Contain(stamp);
    }

    [Fact]
    public async Task InteractAsync_focus_gives_the_document_keyboard_focus()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        var result = await svc.InteractAsync(id, "focus", null);

        result.Method.Should().Be("Focus");
        var focused = _np.Automation.FocusedElement();
        (focused.ControlType is ControlType.Document or ControlType.Edit)
            .Should().BeTrue($"the focused element was {focused.ControlType} '{focused.Name}'");
    }

    [Fact]
    public async Task InteractAsync_click_falls_back_to_a_physical_click_on_a_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        // A Document supports none of Invoke / SelectionItem / Toggle, so this must be the fallback.
        var result = await svc.InteractAsync(id, "click", null);

        result.Method.Should().Be("PhysicalClick");
        result.Detail.Should().MatchRegex(@"^\(-?\d+,-?\d+\)$");
    }

    [Fact]
    public async Task InteractAsync_toggle_names_the_unsupported_pattern_and_the_control()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        Func<Task> act = () => svc.InteractAsync(id, "toggle", null);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("TogglePattern not supported on *");
    }

    /// <summary>
    /// The service-issued id of Notepad's editor. get_state is rooted at the foreground window
    /// three levels deep, which reaches classic Notepad's Document; modern (XAML) Notepad nests its
    /// RichEditBox deeper, so fall back to a name search, then to any Document/Edit.
    /// </summary>
    private static async Task<string> FindNotepadDocumentIdAsync(UIAutomationService svc)
    {
        static ElementInfo? Dfs(ElementTree t)
        {
            if (t.Root.ControlType is "Document" or "Edit") return t.Root;
            foreach (var c in t.Children)
                if (Dfs(c) is { } hit) return hit;
            return null;
        }

        var state = await svc.GetStateAsync();
        if (Dfs(state) is { } inTree) return inTree.ElementId;

        var byName = await svc.FindElementAsync("Text editor", FindKind.Text);
        if (byName.Matches.Length > 0) return byName.Matches[0].ElementId;

        var any = await svc.FindElementAsync("", FindKind.Text);
        var doc = any.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element found — is Notepad in the foreground?");
        return doc.ElementId;
    }
}

// Separate class so it doesn't need the NotepadFixture — Dispose tears down before any
// UIA call, so this test does not need a live desktop session and is Unit-trait safe.
[Trait("Category", "Unit")]
public class UIAutomationServiceUnitTests
{
    [Fact]
    public async Task GetStateAsync_throws_after_dispose()
    {
        var svc = new UIAutomationService(new Mock<IInputService>().Object);
        svc.Dispose();
        Func<Task> act = () => svc.GetStateAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
