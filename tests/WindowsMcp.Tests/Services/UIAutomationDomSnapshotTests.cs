using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-5 phase 1 (R3/R4): <c>snapshot(use_dom:true)</c>, in the same three brackets A-2 uses.
/// <list type="bullet">
/// <item><see cref="UIAutomationDomSnapshotUnitTests"/> — Unit. What the flag does BEFORE any
/// browser is involved: off means the response is unchanged, on means the Pages block exists even
/// when there is nothing to put in it. Mocked inventory, no desktop.</item>
/// <item><see cref="UIAutomationDomSnapshotIntegrationTests"/> — Integration. The real services on
/// this session: the flag survives a real walk, the page finder really returns null on a
/// non-browser window (and does so quickly), and non-browser scopes really report no pages. The
/// mandatory non-mocked sibling — every Unit assertion above would stay green if DOM mode did
/// nothing at all.</item>
/// <item><see cref="UIAutomationDomSnapshotTests"/> — UIAutomation. A real Edge window on the
/// local probe page: the page document, its title/URL/scroll, the page controls, the visible text
/// and the chrome that is NOT there.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationDomSnapshotUnitTests
{
    private static Mock<IWindowService> WindowsMock(params WindowInfo[] list)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(list);
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        return mock;
    }

    private static UIAutomationService NewService(Mock<IWindowService>? windows = null)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(10, 10));
        return new UIAutomationService(input.Object, (windows ?? WindowsMock()).Object);
    }

    [Fact]
    public async Task SnapshotAsync_without_use_dom_reports_no_pages_at_all()
    {
        // Null, not empty: a caller who never asked must get the pre-A-5 response back, and the
        // JSON must not grow a key (SnapshotDtosTests pins the serialised half of this).
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Pages.Should().BeNull();
    }

    [Fact]
    public async Task SnapshotAsync_with_use_dom_and_no_browser_in_scope_reports_an_empty_page_list()
    {
        // An empty inventory walks nothing, so no page can be found - but DOM mode still ran, and
        // "[]" is how the model tells that apart from "the flag was ignored".
        using var svc = NewService(WindowsMock());

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, UseDom: true));

        snap.Pages.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task SnapshotAsync_with_use_dom_still_applies_every_argument_rule_first()
    {
        // use_dom changes what is walked, not what is legal: the A-2 validation order is untouched.
        var windows = WindowsMock();
        using var svc = NewService(windows);

        Func<Task> act = () => svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, null, UseDom: true));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*windowTitle*");
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

/// <summary>
/// A-5 phase 1 on this session's real desktop, through the real <c>WindowService</c> /
/// <c>InputService</c>. Read-only: it opens nothing and closes nothing, so it is safe headless —
/// what it cannot do is guarantee a browser is running, which is why every page-content assertion
/// lives in the UIAutomation bracket instead.
/// </summary>
[Trait("Category", "Integration")]
public class UIAutomationDomSnapshotIntegrationTests
{
    private static UIAutomationService NewService() => new(new InputService(), new WindowService());

    [Fact]
    public async Task SnapshotAsync_desktop_with_use_dom_walks_this_session_and_reports_a_page_list()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, UseDom: true));

        snap.Pages.Should().NotBeNull("use_dom always answers with a list, empty or not");
        snap.ElementCount.Should().BeGreaterThan(0, "DOM mode must not turn the whole walk off");

        var browsers = snap.Windows
            .Where(w => w.IsBrowser && w.State != WindowState.Minimized)
            .Select(w => w.Title).ToArray();
        snap.Pages!.Should().OnlyContain(p => browsers.Contains(p.Window),
            "a page is reported for a BROWSER window and for nothing else");
        snap.Pages.Select(p => p.Window).Should().OnlyHaveUniqueItems("one page per browser window");
        snap.Pages.Should().OnlyContain(p => p.Note == null || p.DocumentId == null,
            "a page either has a document or says why it has none");
        snap.Pages.Should().OnlyContain(p => p.Text != null, "Text is empty, never null");

        // Walk order, not sorted and not grouped by outcome: the Nth page belongs to the Nth
        // browser window the walk reached, so a page and its window line up by position. (A window
        // the walk had to skip drops out entirely, so the indices need only ASCEND, not be dense.)
        snap.Pages.Select(p => Array.IndexOf(browsers, p.Window)).Should()
            .BeInAscendingOrder("pages come back in the order their windows were walked");
    }

    [Fact]
    public async Task SnapshotAsync_desktop_without_use_dom_reports_no_pages_on_the_real_desktop()
    {
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));

        snap.Pages.Should().BeNull();
        snap.ElementCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SnapshotAsync_window_scope_on_a_non_browser_window_reports_no_pages_but_still_walks_it()
    {
        var target = await FirstNonBrowserWindowAsync();
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, target.Title, UseDom: true));

        snap.Pages.Should().NotBeNull().And.BeEmpty("use_dom over a non-browser window finds no page to report");
        snap.ElementCount.Should().BeGreaterThan(0, "the window is still walked exactly as it was without the flag");
    }

    [Fact]
    public async Task FindPageDocument_on_a_window_with_no_web_content_gives_up_quickly_and_says_no()
    {
        // The finder RETRIES, because Chromium builds its UIA tree on the first query and the
        // first find can come back empty on a page that is there. The retry budget must stay small
        // enough that a desktop full of non-browser windows does not pay seconds for it.
        using var automation = new UIA3Automation();
        var target = await PagelessWindowAsync(automation);
        if (target is null) return;   // every window on this session hosts web content: nothing to assert

        var sw = Stopwatch.StartNew();
        var document = UIAutomationService.FindPageDocument(target.Value.Root);
        sw.Stop();

        document.Should().BeNull(
            "the window provably has no RootWebArea under it, so the finder must not settle for any Document");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the retry bound is a handful of short pauses, not a timeout");
    }

    [Fact]
    public async Task FindPageDocument_pauses_between_attempts_and_never_after_the_last_one()
    {
        // The retry is what makes DOM mode work on a browser that has only just been asked for its
        // accessibility tree, and the "not after the last" half is what stops a desktop full of
        // non-browser windows paying a pause per window for a page that will never appear.
        using var automation = new UIA3Automation();
        var target = await PagelessWindowAsync(automation);
        if (target is null) return;
        var root = target.Value.Root;

        var single = Time(() => UIAutomationService.FindPageDocument(root, attempts: 1, pauseMs: 5000));
        var three = Time(() => UIAutomationService.FindPageDocument(root, attempts: 3, pauseMs: 250));

        single.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "one attempt sleeps zero times: the pause is BETWEEN attempts, not after the last");
        three.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
            "three attempts sleep twice - a finder that never retries would come back instantly");
    }

    private static TimeSpan Time(Action act)
    {
        var sw = Stopwatch.StartNew();
        act();
        return sw.Elapsed;
    }

    [Fact]
    public async Task FindPageDocument_finds_the_document_in_an_electron_window_but_dom_mode_still_leaves_it_alone()
    {
        // The fact this pins (found the hard way: FirstNonBrowserWindow picked the Claude desktop
        // app and the "no document here" assertion failed with FrameworkId:Chrome,
        // AutomationId:RootWebArea): an Electron / WebView2 app is Chromium inside, so
        // FindPageDocument DOES find a page under it. What keeps DOM mode off is the A-1
        // inventory's IsBrowser - the process name - and nothing else. Get that gate wrong and
        // every Electron app on the desktop starts reporting "pages".
        using var automation = new UIA3Automation();
        var target = await EmbeddedWebWindowAsync(automation);
        if (target is null) return;   // no Electron/WebView2 window on this session
        var (window, root) = target.Value;

        var document = UIAutomationService.FindPageDocument(root);

        document.Should().NotBeNull("an Electron shell hosts a real Chromium RootWebArea");
        document!.Properties.AutomationId.ValueOrDefault.Should().Be("RootWebArea");
        FrameworkOf(document).Should().Be("Chrome",
            "the document is Chromium's, even though the window around it is a plain Win32 shell");
        window.IsBrowser.Should().BeFalse("the process is not one of the browsers A-1 knows");

        using var svc = NewService();
        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, window.Title, UseDom: true));

        snap.Pages.Should().NotBeNull().And.BeEmpty(
            "IsBrowser gates DOM mode, not the presence of a document - the window is walked whole");
        snap.ElementCount.Should().BeGreaterThan(0, "and it really was walked");
    }

    /// <summary>
    /// A window these tests may walk: visible, titled, not a browser - and not one of this test
    /// process's own. WindowServiceExecuteTests creates a real top-level window and destroys it a
    /// moment later, and a walk that picked it would fail with "no top-level window matching"
    /// through no fault of the code under test.
    /// </summary>
    private static bool IsWalkable(WindowInfo w)
        => !w.IsBrowser && w.State != WindowState.Minimized && w.Hwnd != 0 && w.Title.Length > 0
           && w.Pid != Environment.ProcessId;

    /// <summary>Any visible, titled, non-browser window; the caller does not care what is inside it.</summary>
    private static async Task<WindowInfo> FirstNonBrowserWindowAsync()
    {
        var windows = await new WindowService().ListAsync();
        var target = windows.FirstOrDefault(w => IsWalkable(w));
        target.Should().NotBeNull("this session must have at least one visible non-browser window to walk");
        return target!;
    }

    /// <summary>
    /// A window that provably hosts no web content: an independent AutomationId-only query finds
    /// no <c>RootWebArea</c> anywhere under it. The window's OWN FrameworkId says nothing about
    /// this — an Electron shell's top-level window is a plain "Win32" window and only the document
    /// inside it is "Chrome" — so the subtree has to be searched, cheapest (Win32) trees first.
    /// Null when this session has no such window.
    /// </summary>
    private static async Task<(WindowInfo Window, AutomationElement Root)?> PagelessWindowAsync(UIA3Automation automation)
        => await FirstWindowWithWebAreaAsync(automation, hasWebArea: false);

    /// <summary>
    /// The opposite: a NON-browser window that is Chromium inside — an Electron or WebView2 app.
    /// Null when this session has none.
    /// </summary>
    private static async Task<(WindowInfo Window, AutomationElement Root)?> EmbeddedWebWindowAsync(UIA3Automation automation)
        => await FirstWindowWithWebAreaAsync(automation, hasWebArea: true);

    private static async Task<(WindowInfo Window, AutomationElement Root)?> FirstWindowWithWebAreaAsync(
        UIA3Automation automation, bool hasWebArea)
    {
        foreach (var (window, root) in await CandidatesAsync(automation))
        {
            try
            {
                var found = root.FindFirstDescendant(root.ConditionFactory.ByAutomationId("RootWebArea")) is not null;
                if (found == hasWebArea) return (window, root);
            }
            catch { /* the window went away mid-search: try the next one */ }
        }
        return null;
    }

    /// <summary>Visible, titled, non-browser windows with a UIA root, cheapest framework first.</summary>
    private static async Task<List<(WindowInfo Window, AutomationElement Root)>> CandidatesAsync(UIA3Automation automation)
    {
        var windows = await new WindowService().ListAsync();
        var candidates = new List<(WindowInfo, AutomationElement)>();
        foreach (var w in windows.Where(IsWalkable))
        {
            AutomationElement? root;
            try { root = automation.FromHandle((nint)w.Hwnd); }
            catch { continue; }
            if (root is not null) candidates.Add((w, root));
        }
        return candidates
            .OrderByDescending(c => FrameworkOf(c.Item2).Equals("Win32", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The UIA FrameworkId of a window's root ("Win32", "WPF", "XAML", "Chrome", …).</summary>
    private static string FrameworkOf(AutomationElement root)
    {
        try { return root.Properties.FrameworkId.ValueOrDefault ?? ""; }
        catch { return ""; }
    }
}

/// <summary>
/// A-5 phase 1 (R4) against a real Chromium window on the local probe page. Everything here needs
/// the interactive desktop: Chromium exposes the page's accessibility tree only once something
/// queries it, and the whole point of the item is what that tree contains.
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(EdgeCollection.Name)]
public class UIAutomationDomSnapshotTests
{
    private readonly EdgeFixture _edge;

    public UIAutomationDomSnapshotTests(EdgeFixture edge) => _edge = edge;

    private static UIAutomationService NewService() => new(new InputService(), new WindowService());

    private SnapshotRequest PageRequest(bool useDom = true, bool includeTree = false) =>
        new(SnapshotScope.Window, _edge.WindowTitle, includeTree, 0, useDom);

    /// <summary>The interactive rows that belong to the browser window under test.</summary>
    private SnapshotElement[] PageElements(SnapshotResult snap) =>
        snap.Interactive.Where(e => e.Window == _edge.WindowTitle).ToArray();

    // ---- (a) the page itself ------------------------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_use_dom_reports_the_page_title_url_and_scroll()
    {
        if (!_edge.Available) return;   // no Edge on this machine: nothing to assert
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(PageRequest());

        var page = snap.Pages.Should().ContainSingle().Subject;
        page.Window.Should().Be(_edge.WindowTitle);
        page.Title.Should().Be(EdgeFixture.PageTitle, "the document's Name is the page <title>");
        page.Url.Should().StartWith(_edge.BaseUrl, "the document's value is the page URL");
        page.Url.Should().EndWith("/a5");
        page.Note.Should().BeNull("the page was found, so there is nothing to explain");
        page.DocumentId.Should().NotBeNullOrWhiteSpace();
        page.Scroll.Should().NotBeNull("a page taller than the window exposes a scroll pattern");
        page.Scroll!.VerticallyScrollable.Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_page_document_id_resolves_to_the_page_document()
    {
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(PageRequest());
        var page = snap.Pages.Should().ContainSingle().Subject;
        var info = await svc.GetElementAsync(page.DocumentId!);

        info.ControlType.Should().Be("Document", "DocumentId is the id of the RootWebArea element");
        info.Name.Should().Be(EdgeFixture.PageTitle);
    }

    [Fact]
    public async Task Snapshot_lists_the_page_document_as_scrollable_but_never_as_interactive()
    {
        // Correction 1: a Document is "fill" in the desktop classifier, but a web PAGE is not a
        // control - it is where the model scrolls, not where it types.
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(PageRequest());
        var page = snap.Pages.Should().ContainSingle().Subject;

        snap.Scrollable.Select(s => s.ElementId).Should().Contain(page.DocumentId!,
            "the page is the scroll target, with its percentages");
        PageElements(snap).Should().NotContain(e => e.ControlType == "Document",
            "the page document is never an interactive row");
    }

    // ---- (b) the page's controls --------------------------------------------------------------

    [Fact]
    public async Task Snapshot_use_dom_lists_every_control_on_the_probe_page_with_its_action()
    {
        if (!_edge.Available) return;
        using var svc = NewService();

        var elements = PageElements(await svc.SnapshotAsync(PageRequest()));

        var link = elements.Should().ContainSingle(e => e.ControlType == "Hyperlink" && e.Name == "A link to one").Subject;
        link.Action.Should().Be("click");

        elements.Should().ContainSingle(e => e.ControlType == "Button" && e.Name == "Press me")
            .Which.Action.Should().Be("click");

        var search = elements.Should().ContainSingle(e => e.ControlType == "Edit" && e.Name == "Search").Subject;
        search.Action.Should().Be("fill");
        search.Value.Should().Be("prefilled", "an Edit reports what is in it");

        var tick = elements.Should().ContainSingle(e => e.ControlType == "CheckBox" && e.Name == "Tick").Subject;
        tick.Action.Should().Be("toggle");
        tick.Toggle.Should().Be("On", "the probe page ships the box checked");

        var picker = elements.Should().ContainSingle(e => e.ControlType == "ComboBox").Subject;
        picker.Action.Should().Be("select");
        picker.Value.Should().Be("Alpha", "the first option is selected");

        elements.Where(e => e.ControlType == "ListItem").Select(e => e.Name)
            .Should().Contain("Item one", "ListItem stays interactive - parity with upstream's set")
            .And.Contain("Item two");
    }

    [Fact]
    public async Task Snapshot_use_dom_leaves_the_browser_chrome_out()
    {
        // The whole point of DOM mode: the walk starts at the page document, so the toolbar, the
        // tab strip and the address bar are not even visited. --app mode has no address bar, so
        // the check that survives is the element COUNT: the page alone cannot be bigger than the
        // window that contains it.
        if (!_edge.Available) return;
        using var svc = NewService();

        var withDom = await svc.SnapshotAsync(PageRequest());
        var whole = await svc.SnapshotAsync(PageRequest(useDom: false));

        whole.Pages.Should().BeNull("the same call without the flag is a pre-A-5 response");
        PageElements(withDom).Length.Should().BeLessThanOrEqualTo(PageElements(whole).Length,
            "walking the page can only ever see a subset of the window");

        var wholeNames = PageElements(whole).Select(e => e.ControlType + "/" + e.Name).ToHashSet();
        PageElements(withDom).Select(e => e.ControlType + "/" + e.Name).Should().OnlyContain(
            n => wholeNames.Contains(n), "nothing the page contains is invented by DOM mode");
        PageElements(whole).Should().Contain(e => e.ControlType == "Hyperlink" && e.Name == "A link to one",
            "and nothing from the page is lost when the whole window is walked");
    }

    // ---- (c) the page's visible text ---------------------------------------------------------

    [Fact]
    public async Task Snapshot_use_dom_collects_the_visible_page_text_in_document_order()
    {
        if (!_edge.Available) return;
        using var svc = NewService();

        var page = (await svc.SnapshotAsync(PageRequest())).Pages.Should().ContainSingle().Subject;
        var text = page.Text;

        text.Should().Contain("Probe heading");
        text.Should().Contain("First paragraph of body text.");
        text.Should().Contain("inline span text");
        text.Should().Contain(t => t.StartsWith("tall spacer", StringComparison.Ordinal));

        text.Should().NotContain("Last paragraph.",
            "text below the fold is off-screen, and the snapshot only reports what is visible (D-7)");

        var heading = Array.IndexOf(text, "Probe heading");
        var paragraph = Array.IndexOf(text, "First paragraph of body text.");
        heading.Should().BeLessThan(paragraph, "the page text is in document order, not sorted");
    }

    [Fact]
    public async Task Snapshot_use_dom_text_does_not_repeat_the_controls_labels()
    {
        // Correction 2: some Chromium builds expose a link's label as a Text child of the link.
        // It is already on the interactive row; repeating it as page text is pure token cost.
        if (!_edge.Available) return;
        using var svc = NewService();

        var page = (await svc.SnapshotAsync(PageRequest())).Pages.Should().ContainSingle().Subject;

        page.Text.Should().NotContain("A link to one");
        page.Text.Should().NotContain("Press me");
        page.Text.Should().NotContain(t => string.IsNullOrWhiteSpace(t), "a text node with nothing to say is dropped");
    }

    // ---- (d) the other scopes, and the budget -------------------------------------------------

    [Fact]
    public async Task SnapshotAsync_foreground_scope_uses_the_dom_when_the_front_window_is_a_browser()
    {
        // The rule is the window's IsBrowser flag from the A-1 inventory, whatever picked the
        // window - scope:foreground must not quietly fall back to walking the chrome.
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, null, false, 0, true));

        if (snap.ActiveWindow?.Title != _edge.WindowTitle) return;   // something else took focus
        var page = snap.Pages.Should().ContainSingle().Subject;
        page.Window.Should().Be(_edge.WindowTitle);
        page.Title.Should().Be(EdgeFixture.PageTitle);
    }

    [Fact]
    public async Task SnapshotAsync_use_dom_reports_the_page_it_got_even_when_the_budget_ran_out()
    {
        // The budget is shared across windows and stops the walk mid-page. The page is still
        // reported - with whatever text was collected - so the model is not told "no page" for a
        // page that is there.
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, _edge.WindowTitle, false, 5, true));

        snap.Truncated.Should().BeTrue("five elements cannot hold this page");
        snap.ElementCount.Should().Be(5);
        var page = snap.Pages.Should().ContainSingle().Subject;
        page.DocumentId.Should().NotBeNullOrWhiteSpace("the document is entry 0 of the walk, so it is always in");
        page.Title.Should().Be(EdgeFixture.PageTitle);
        page.Note.Should().BeNull("a truncated page is still a page that was found");
    }

    // ---- (d) the tree and the rendered text ---------------------------------------------------

    [Fact]
    public async Task Snapshot_use_dom_with_include_tree_roots_the_window_at_the_page_document()
    {
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(PageRequest(includeTree: true));

        snap.Tree.Should().NotBeNull();
        var windowTree = snap.Tree!.Children.Should().ContainSingle().Subject;
        windowTree.Root.ControlType.Should().Be("Document", "the walk started at the page, so the tree does too");
        windowTree.Root.Name.Should().Be(EdgeFixture.PageTitle);
        windowTree.Root.ElementId.Should().Be(snap.Pages!.Single().DocumentId);
    }

    [Fact]
    public async Task Snapshot_use_dom_text_form_carries_the_pages_block()
    {
        if (!_edge.Available) return;
        using var svc = NewService();

        var snap = await svc.SnapshotAsync(PageRequest());
        var text = SnapshotRenderer.Render(snap);
        var page = snap.Pages!.Single();

        text.Should().Contain("Pages (1):");
        text.Should().Contain($"  {page.DocumentId} \"{EdgeFixture.PageTitle}\" {page.Url}");
        text.Should().Contain("\n    Probe heading");
    }
}
