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

    // ---- D-4: assert_element ----------------------------------------------------------------

    [Fact]
    public async Task AssertElementAsync_exists_enabled_visible_pass_on_the_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        foreach (var state in new[] { "exists", "enabled", "visible" })
        {
            var result = await svc.AssertElementAsync(id, state);
            result.Pass.Should().BeTrue($"{state}: observed {result.Observed}");
            result.State.Should().Be(state);
        }
    }

    [Fact]
    public async Task AssertElementAsync_focused_passes_on_the_focused_document_and_names_the_focus_owner_otherwise()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        await svc.FocusAsync(id);
        await Task.Delay(200);

        var onDocument = await svc.AssertElementAsync(id, "focused");
        onDocument.Pass.Should().BeTrue(onDocument.Observed);

        // A title-bar / toolbar button never holds keyboard focus while the document does. (The
        // top-level Window of a XAML app does report HasKeyboardFocus, so it is no use here.)
        var button = FindInTree(await svc.GetStateAsync(), "Button")
            ?? throw new Xunit.Sdk.XunitException("No Button within three levels of the Notepad window");
        var onButton = await svc.AssertElementAsync(button.ElementId, "focused");
        onButton.Pass.Should().BeFalse($"observed {onButton.Observed}");
        onButton.Observed.Should().StartWith("focus is on");
    }

    private static ElementInfo? FindInTree(ElementTree t, string controlType)
    {
        if (t.Root.ControlType == controlType) return t.Root;
        foreach (var c in t.Children)
            if (FindInTree(c, controlType) is { } hit) return hit;
        return null;
    }

    [Fact]
    public async Task AssertElementAsync_value_compares_exactly_and_quotes_the_actual_value()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d4-{Guid.NewGuid():N}"[..12];
        await svc.InteractAsync(id, "type", stamp);
        await Task.Delay(300);
        var text = await svc.GetTextAsync(id);   // other tests type into the same document

        var match = await svc.AssertElementAsync(id, "value", text);
        match.Pass.Should().BeTrue(match.Observed);

        var mismatch = await svc.AssertElementAsync(id, "value", "not-" + stamp);
        mismatch.Pass.Should().BeFalse();
        mismatch.Observed.Should().StartWith("value is '").And.Contain(stamp);
    }

    [Fact]
    public async Task AssertElementAsync_checked_names_the_missing_pattern_on_a_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        var result = await svc.AssertElementAsync(id, "checked");

        result.Pass.Should().BeFalse();
        result.Observed.Should().StartWith("no TogglePattern on");
    }

    [Fact]
    public async Task AssertElementAsync_reports_a_stale_element_as_no_longer_available()
    {
        using var svc = NewService();

        // A throwaway classic Win32 window (Character Map is single-process, multi-instance, and
        // present on every Windows edition) so killing it cannot disturb the shared Notepad.
        using var proc = System.Diagnostics.Process.Start("charmap.exe");
        string id;
        try
        {
            for (int i = 0; i < 40 && proc.MainWindowHandle == IntPtr.Zero; i++) { await Task.Delay(150); proc.Refresh(); }
            proc.MainWindowHandle.Should().NotBe(IntPtr.Zero, "charmap.exe must open a window");
            _np.Automation.FromHandle(proc.MainWindowHandle).SetForeground();

            // get_state roots at the foreground window; wait until that is Character Map.
            ElementTree state = await svc.GetStateAsync();
            for (int i = 0; i < 20 && !state.Root.Name.Contains("Character Map", StringComparison.OrdinalIgnoreCase); i++)
            {
                await Task.Delay(150);
                state = await svc.GetStateAsync();
            }
            state.Root.Name.Should().Contain("Character Map", "the throwaway window must be in the foreground for this test");
            id = state.Root.ElementId;
            var alive = await svc.AssertElementAsync(id, "exists");
            alive.Pass.Should().BeTrue(alive.Observed);

            proc.Kill();
            proc.WaitForExit();
            await Task.Delay(500);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* already gone */ }
            _np.BringToForeground();
        }

        // A dead Win32 window's element does not throw: UIA answers with defaults (ControlType
        // Pane, ProcessId 0), so the service's liveness probe must not rely on an exception.
        var gone = await svc.AssertElementAsync(id, "exists");
        gone.Pass.Should().BeFalse();
        gone.Observed.Should().Be("element no longer available");
        (await svc.AssertElementAsync(id, "enabled")).Observed.Should().Be("element no longer available");
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

        // Until checklist D-5 lands, FindElementAsync walks the whole desktop with unguarded
        // property reads and dies on the first stale element (a tooltip or closing menu in any
        // other process). Retry a few times so that defect does not fail an unrelated test.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var byName = await svc.FindElementAsync("Text editor", FindKind.Text);
                if (byName.Matches.Length > 0) return byName.Matches[0].ElementId;

                var any = await svc.FindElementAsync("", FindKind.Text);
                var doc = any.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
                    ?? throw new Xunit.Sdk.XunitException("No Document/Edit element found — is Notepad in the foreground?");
                return doc.ElementId;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(200);
            }
        }
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

    // D-4: the argument rules and the unknown-id rule are decided before any UIA call is made,
    // so these run headless.
    [Theory]
    [InlineData("value", null, "*requires expected*")]
    [InlineData("enabled", "x", "*only used with state=value*")]
    [InlineData("hovering", null, "*Unknown assertion state 'hovering'*")]
    public async Task AssertElementAsync_rejects_bad_arguments(string state, string? expected, string message)
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object);
        Func<Task> act = () => svc.AssertElementAsync("el_0", state, expected);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task AssertElementAsync_exists_fails_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object);

        var result = await svc.AssertElementAsync("el_404", "exists");

        result.Pass.Should().BeFalse();
        result.Observed.Should().Be("unknown element id");
    }

    [Fact]
    public async Task AssertElementAsync_other_states_throw_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object);
        Func<Task> act = () => svc.AssertElementAsync("el_404", "enabled");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData(unchecked((int)0x80040201), true)]   // UIA_E_ELEMENTNOTAVAILABLE
    [InlineData(unchecked((int)0x800706BA), true)]   // RPC_S_SERVER_UNAVAILABLE
    [InlineData(unchecked((int)0x80004005), false)]  // E_FAIL is not "gone"
    public void IsElementGone_recognises_the_destroyed_element_HRESULTs(int hresult, bool gone)
    {
        var ex = new System.Runtime.InteropServices.COMException("probe", hresult);
        UIAutomationService.IsElementGone(ex).Should().Be(gone);
        UIAutomationService.IsElementGone(new InvalidOperationException()).Should().BeFalse();
    }
}
