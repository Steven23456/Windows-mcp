using System.Text;
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
// Serialised with every other class that opens a Notepad window, moves the pointer or reads the
// desktop (see DesktopCollection): two NotepadFixture instances launching at the same time each
// see the other's new window in their inventory diff and pick the wrong one.
[Collection(DesktopCollection.Name)]
public class UIAutomationServiceTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    // xUnit builds this class once per test, so this hands the desktop back to Notepad before
    // EVERY test. It matters more since D-5: get_state and the default find scope both root at the
    // FOREGROUND window, so a test that runs while another app holds the foreground silently
    // inspects that app instead of Notepad — under a full parallel suite run these tests were
    // resolving VS Code's document and asserting against it.
    public UIAutomationServiceTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    // A-2 (R2): the service now takes the window inventory too. Real WindowService here — these
    // tests already need the live desktop, and a mock would hide a wiring break in the snapshot's
    // header and root list.
    private static UIAutomationService NewService() => new(new InputService(), new WindowService());

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

    // ---- D-5 / D-6 / D-7: the find path, live ------------------------------------------------

    // The regression this whole item was filed for: on a busy desktop one element that dies
    // between the walk and the property read used to fail the entire call.
    [Theory]
    [InlineData(FindScope.Foreground)]
    [InlineData(FindScope.Desktop)]
    public async Task FindElementAsync_any_survives_a_busy_desktop(FindScope scope)
    {
        using var svc = NewService();
        for (int i = 0; i < 10; i++)
        {
            var result = await svc.FindElementAsync("", FindKind.Any, scope);
            result.Matches.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task FindElementAsync_window_scope_targets_that_window_by_substring()
    {
        using var svc = NewService();

        var scoped = await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");

        scoped.Matches.Should().NotBeEmpty("'Notepad' must match 'Untitled - Notepad' by substring");
    }

    [Fact]
    public async Task FindElementAsync_unmatched_window_names_the_open_windows()
    {
        using var svc = NewService();
        Func<Task> act = () => svc.FindElementAsync("", FindKind.Any, FindScope.Window, "zzqxv-no-such-window");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).WithMessage("*Open windows:*");
    }

    // D-6: kind=interactive used to be Button|CheckBox|Hyperlink|MenuItem, so the editor — an Edit
    // on classic Notepad, a Document on the modern one — was invisible to it. Foreground scope, so
    // the assertion is about Notepad rather than whichever window the desktop walk reached first.
    [Fact]
    public async Task FindElementAsync_interactive_finds_the_editor()
    {
        using var svc = NewService();

        var result = await svc.FindElementAsync("", FindKind.Interactive);

        result.Matches.Should().Contain(m => m.ControlType == "Edit" || m.ControlType == "Document");
    }

    // Regression: the kind filter is pushed into the UIA condition, which only covers DESCENDANTS.
    // The walk's own roots have to be filtered client-side, or every window Pane counts as a match
    // for every kind and fills the 20-result cap before any real content is reached.
    [Fact]
    public async Task FindElementAsync_interactive_never_returns_a_window_root_pane()
    {
        using var svc = NewService();

        var result = await svc.FindElementAsync("", FindKind.Interactive, FindScope.Desktop);

        result.Matches.Should().NotContain(m => m.ControlType == "Pane" || m.ControlType == "Window");
    }

    // D-7: off-screen elements used to crowd out on-screen ones inside the 20-result cap.
    [Fact]
    public async Task FindElementAsync_drops_offscreen_results_by_default()
    {
        using var svc = NewService();

        var visible = await svc.FindElementAsync("", FindKind.Text, FindScope.Desktop);
        var all = await svc.FindElementAsync("", FindKind.Text, FindScope.Desktop, null, includeOffscreen: true);

        visible.Matches.Should().OnlyContain(
            m => !m.IsOffscreen || m.ControlType == "Edit",
            "only the documented Edit exception may be off-screen");
        all.Matches.Length.Should().BeGreaterThanOrEqualTo(visible.Matches.Length);
    }

    [Fact]
    public async Task WaitForAsync_returns_text_that_appears_after_the_first_poll()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d5-{Guid.NewGuid():N}"[..10];

        var waiting = svc.WaitForAsync(stamp, timeoutMs: 15000, intervalMs: 250, FindKind.Text);
        await Task.Delay(500);
        await svc.InteractAsync(id, "type", stamp);

        (await waiting).Should().NotBeNull("the text appeared while the wait was polling");
    }

    // ---- A-13: unicode hygiene, end to end ---------------------------------------------------

    // A codicon-style private use glyph and an emoji, written as code points so this file stays
    // ASCII and nothing can normalise them on the way to the compiler.
    private static readonly string PuaGlyph = ((char)0xE0B0).ToString();   // U+E0B0, powerline/VS Code
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600); // grinning face, a valid pair

    // These two are the ONLY proof that the UIA read sites are wired to UiText.Sanitize: an
    // AutomationElement cannot be faked or mocked (sealed, COM-backed), so nothing headless can
    // observe TryGetName / TryGetValue / GetTextAsync. They type the glyph and the emoji into the
    // editor with InputService.TypeAsync (SendInput KEYEVENTF_UNICODE - the only way a PUA glyph
    // and the two halves of a surrogate pair get into a control) and read them back through the
    // paths the model uses.
    //
    // Modern (XAML) Notepad's editor is a Document whose text may surface as ElementInfo.Value
    // (classic Notepad's Edit carries a ValuePattern) or only through the TextPattern that
    // GetTextAsync falls back on, and the same string can also land on an element's Name. The
    // first test is therefore tolerant of WHICH carrier holds it: it searches every element from
    // both get_state and find_element, and every Name and Value on them.

    [Fact]
    public async Task Element_name_and_value_carry_no_private_use_glyph_and_keep_the_emoji()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var marker = $"a13-{Guid.NewGuid():N}"[..10];

        await svc.InteractAsync(id, "focus", null);
        await new InputService().TypeAsync($"{PuaGlyph}left-{marker} {Emoji} right");
        await WaitForTypedTextAsync(svc, id, marker);

        var carriers = await TextCarriersContainingAsync(svc, marker);

        carriers.Should().NotBeEmpty("the typed text must surface on some element's Name or Value");
        carriers.Should().OnlyContain(t => PrivateUseCodePoints(t).Length == 0,
            "A-13 strips U+E000-U+F8FF in TryGetName and TryGetValue before the model sees the name");
        carriers.Should().Contain(t => t.Contains(Emoji),
            "sanitising must not break a valid surrogate pair - the emoji has to survive intact");
    }

    [Fact]
    public async Task GetTextAsync_strips_private_use_glyphs_and_keeps_the_emoji()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var marker = $"a13-{Guid.NewGuid():N}"[..10];

        await svc.InteractAsync(id, "focus", null);
        await new InputService().TypeAsync($"{PuaGlyph}left-{marker} {Emoji} right");
        await WaitForTypedTextAsync(svc, id, marker);

        var text = await svc.GetTextAsync(id);

        text.Should().Contain(marker, "get_text must read back what was typed into the editor");
        PrivateUseCodePoints(text).Should().BeEmpty(
            "A-13 sanitises the text GetTextAsync returns, so no icon-font glyph reaches the model");
        text.Should().Contain(Emoji, "the emoji must survive intact");
    }

    /// <summary>
    /// SendInput returns before the control has processed the keystrokes, and on a loaded box a
    /// fixed delay raced the read (a run saw the text cut off right after the marker). Poll until
    /// the LAST word of the typed string is visible, so the whole string, emoji included, is there.
    /// </summary>
    private static async Task WaitForTypedTextAsync(UIAutomationService svc, string id, string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            string text;
            try { text = await svc.GetTextAsync(id); } catch { continue; }
            if (text.Contains(marker) && text.Contains("right")) return;
        }
        // Fall through: the assertions below say precisely what is missing.
    }

    /// <summary>
    /// Every string the model could read the typed text from: the Name and Value of every element
    /// in the get_state tree plus every find_element match in the Notepad window.
    /// </summary>
    private static async Task<string[]> TextCarriersContainingAsync(UIAutomationService svc, string marker)
    {
        var infos = new List<ElementInfo>();
        Collect(await svc.GetStateAsync(), infos);
        infos.AddRange((await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad")).Matches);

        return infos
            .SelectMany(i => new[] { i.Name, i.Value ?? "" })
            .Where(t => t.Contains(marker))
            .ToArray();

        static void Collect(ElementTree node, List<ElementInfo> into)
        {
            into.Add(node.Root);
            foreach (var child in node.Children) Collect(child, into);
        }
    }

    private static int[] PrivateUseCodePoints(string s) =>
        s.EnumerateRunes()
            .Where(r => (r.Value >= 0xE000 && r.Value <= 0xF8FF)          // BMP private use area
                     || (r.Value >= 0xF0000 && r.Value <= 0xFFFFD)        // plane 15
                     || (r.Value >= 0x100000 && r.Value <= 0x10FFFD))     // plane 16
            .Select(r => r.Value)
            .ToArray();

    private static async Task<string> FindNotepadDocumentIdAsync(UIAutomationService svc)
    {
        // Resolve the editor inside the NOTEPAD window explicitly (D-5 scope=window) rather than
        // trusting the foreground: if focus slipped, a foreground-scoped search returns another
        // app's Document and the test then asserts against the wrong window.
        var inWindow = await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        var doc = inWindow.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element in the Notepad window");
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
        var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
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
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.AssertElementAsync("el_0", state, expected);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task AssertElementAsync_exists_fails_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);

        var result = await svc.AssertElementAsync("el_404", "exists");

        result.Pass.Should().BeFalse();
        result.Observed.Should().Be("unknown element id");
    }

    [Fact]
    public async Task AssertElementAsync_other_states_throw_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
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

    // ---- D-5: the wait_for retry loop, exercised with a fake poll (no UIA, no desktop) ---------

    private static readonly ElementInfo Hit =
        new("el_1", "Ready", "Text", true, false, new Bounds(0, 0, 10, 10), null, null, null);

    // THE D-5 headline: before this, the first transient UIA failure ended the wait — the one
    // thing a wait exists to absorb.
    [Fact]
    public async Task PollAsync_keeps_polling_after_a_poll_throws()
    {
        var attempts = 0;
        var result = await UIAutomationService.PollAsync(_ =>
        {
            attempts++;
            if (attempts < 3) throw new System.Runtime.InteropServices.COMException("stale", unchecked((int)0x80040201));
            return Task.FromResult<ElementInfo?>(Hit);
        }, timeoutMs: 5000, intervalMs: 1, CancellationToken.None);

        result.Should().BeSameAs(Hit);
        attempts.Should().Be(3);
    }

    // "Never managed to look" must not be reported as "looked and did not find it".
    [Fact]
    public async Task PollAsync_throws_when_every_poll_failed()
    {
        Func<Task> act = () => UIAutomationService.PollAsync(
            _ => throw new InvalidOperationException("provider exploded"),
            timeoutMs: 60, intervalMs: 1, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TimeoutException>();
        thrown.WithMessage("*provider exploded*");
        thrown.And.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task PollAsync_returns_null_when_clean_polls_find_nothing()
    {
        var result = await UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(null), timeoutMs: 60, intervalMs: 1, CancellationToken.None);

        result.Should().BeNull();
    }

    // timeout_ms:0 means "check now", not "do nothing" — the old loop never polled at all.
    [Fact]
    public async Task PollAsync_polls_at_least_once_with_a_zero_timeout()
    {
        var attempts = 0;
        var result = await UIAutomationService.PollAsync(
            _ => { attempts++; return Task.FromResult<ElementInfo?>(null); },
            timeoutMs: 0, intervalMs: 500, CancellationToken.None);

        attempts.Should().Be(1);
        result.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_does_not_sleep_when_the_first_poll_hits()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(Hit), timeoutMs: 10000, intervalMs: 5000, CancellationToken.None);
        sw.Stop();

        result.Should().BeSameAs(Hit);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PollAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Func<Task> act = () => UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(null), timeoutMs: 5000, intervalMs: 1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- D-5: argument rules, decided before any UIA call, so they run headless ---------------

    [Fact]
    public async Task FindElementAsync_rejects_window_scope_without_a_title()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.FindElementAsync("x", FindKind.Any, FindScope.Window);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires windowTitle*");
    }

    [Fact]
    public async Task FindElementAsync_rejects_a_window_title_with_another_scope()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.FindElementAsync("x", FindKind.Any, FindScope.Desktop, "Notepad");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
    }

    // ---- D-6: the interactive set is pinned, so a later edit is a visible diff ----------------

    [Fact]
    public void InteractiveControlTypes_matches_the_upstream_set_plus_Document()
    {
        UIAutomationService.InteractiveControlTypes.Should().BeEquivalentTo(new[]
        {
            ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
            ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
            ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
            ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
            ControlType.Document,
        });

        // The four types kind=interactive used to be limited to — the regression D-6 fixes.
        UIAutomationService.InteractiveControlTypes.Should()
            .Contain(ControlType.Edit).And.Contain(ControlType.ComboBox).And.Contain(ControlType.ListItem)
            .And.Contain(ControlType.TabItem).And.Contain(ControlType.RadioButton)
            .And.Contain(ControlType.Slider).And.Contain(ControlType.TreeItem);
    }
}
