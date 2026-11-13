using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// PR #43 review: an observation walks a text-only tooltip (a desktop-level popup of the observed
/// process) but used to return none of its text - a ToolTip or Text node is never an interactive
/// row, and the renderer prints only rows. <c>SnapshotResult.PopupText</c> carries it.
/// <list type="bullet">
/// <item>Z1: set only for an observation (<c>IncludePopups</c>) that has at least one line; null
/// otherwise, and then omitted from JSON - a plain snapshot is byte-identical.</item>
/// <item>Z2: for each DESKTOP-LEVEL popup (not the target window, not an owned window), every walked
/// node that is not an interactive row and has a non-empty name gives <c>"&lt;popup label&gt;: &lt;name&gt;"</c>,
/// in walk order, a name once per popup, clipped to 200 characters then "...", the list capped at 50
/// lines, the last of them <c>"... (more popup text not shown)"</c> when the cap cuts it.</item>
/// <item>Z4 (the reviewer's regression): through <c>Observation</c> over the real service.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationSnapshotPopupTextTests : IDisposable
{
    private const long Host = 0xAB10;
    private const long Owned = 0xAC01;
    private const int ButtonType = 50000;
    private const int TextType = 50020;
    private const int ToolTipType = 50022;
    private const int MenuType = 50009;
    private const int MenuItemType = 50011;
    private const int PaneType = 50033;
    private const int WindowType = 50032;

    internal const string Marker = "... (more popup text not shown)";

    private readonly FakeUiWindows _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private AutomationElement Node(string name, int controlType, (int X, int Y, int W, int H)? bounds = null) =>
        _fakes.Window(name, 0, controlType: controlType, bounds: bounds ?? (155, 155, 150, 20));

    private AutomationElement Popup(string name, long handle, int controlType, params AutomationElement[] children) =>
        _fakes.Window(name, handle, controlType: controlType, bounds: (150, 150, 200, 120), children: children);

    private AutomationElement Tooltip(string name, long handle, params string[] texts) =>
        Popup(name, handle, ToolTipType, texts.Select(t => Node(t, TextType)).ToArray());

    private AutomationElement Menu(long handle, params string[] items) =>
        Popup("", handle, MenuType, items.Select(i => Node(i, MenuItemType)).ToArray());

    private UIAutomationService NewService(AutomationElement? host = null, AutomationElement[]? desktop = null,
        AutomationElement? owned = null)
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([UiTree.SnapshotFixtures.Window("Host", hwnd: Host, pid: 4242, isActive: true)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var svc = new UIAutomationService(input.Object, windows.Object);
        host ??= _fakes.Window("Host button", Host);
        svc.ElementFromHandle = h => h == Host ? host : h == Owned ? owned : null;
        svc.OwnedWindowsOf = h => h == Host && owned is not null ? [(nint)Owned] : [];
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => desktop ?? [];
        return svc;
    }

    private static SnapshotRequest Observation => new(SnapshotScope.Window, WindowHwnd: Host, IncludePopups: true);

    // ---- Z1: only an observation, only when there is text --------------------------------------------

    [Fact]
    public void Z1_PopupText_defaults_to_null_and_is_omitted_from_json_until_set()
    {
        var plain = new SnapshotResult([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 12);

        plain.PopupText.Should().BeNull();
        JsonSerializer.Serialize(plain).Should().NotContain("PopupText", "snapshot's JSON is byte-identical to today");
        JsonSerializer.Serialize(plain with { PopupText = ["popup: Tip"] }).Should().Contain("\"PopupText\":[\"popup: Tip\"]");
    }

    [Fact]
    public async Task Z1_a_plain_snapshot_never_has_popup_text()
    {
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, "Saves the document")]);

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: Host));

        snap.PopupText.Should().BeNull("plain snapshots never walk popups");
    }

    [Fact]
    public async Task Z1_an_observation_with_no_popup_or_only_a_menu_has_no_popup_text()
    {
        using var none = NewService();
        using var menu = NewService(desktop: [Menu(0xAB41, "Open", "Save")]);

        (await none.SnapshotAsync(Observation)).PopupText.Should().BeNull();
        var withMenu = await menu.SnapshotAsync(Observation);
        withMenu.PopupText.Should().BeNull("a menu's items are rows already; an unnamed menu has no text of its own");
        withMenu.Interactive.Select(e => e.Name).Should().Contain(["Open", "Save"]);
    }

    // ---- Z2: content ------------------------------------------------------------------------------

    [Fact]
    public async Task Z2_a_text_only_tooltips_text_is_listed_under_its_label()
    {
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, "Saves the document")]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["popup: Saves the document"]);
    }

    [Fact]
    public async Task Z2_a_tooltip_whose_text_child_repeats_its_own_name_gives_one_line()
    {
        using var svc = NewService(desktop: [Tooltip("Save (Ctrl+S)", 0xAB40, "Save (Ctrl+S)")]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Save (Ctrl+S): Save (Ctrl+S)"], "within one popup a name is listed once");
    }

    [Fact]
    public async Task Z2_interactive_nodes_are_rows_not_popup_text()
    {
        var dialog = Popup("", 0xAB42, PaneType, Node("Delete this file?", TextType), Node("OK", ButtonType));
        using var svc = NewService(desktop: [dialog]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["popup: Delete this file?"]);
        snap.Interactive.Select(e => e.Name).Should().Contain("OK");
    }

    private AutomationElement HostWithLabel() => _fakes.Window("Host", Host, controlType: WindowType, bounds: (100, 100, 400, 300),
        children: [Node("Host label", TextType, (110, 110, 100, 20)), Node("Host button", ButtonType, (110, 140, 100, 20))]);

    private AutomationElement OwnedDialog(string title, params AutomationElement[] children) =>
        _fakes.Window(title, Owned, controlType: WindowType, bounds: (450, 150, 300, 200), children: children);

    [Fact]
    public async Task Z6_owned_windows_count_too_owned_first_but_never_the_target_window()
    {
        // Re-pinned from Z2_only_desktop_level_popups_count_not_the_target_window_or_an_owned_window:
        // a real tooltip, and an owned dialog's static text, are owned windows.
        var owned = OwnedDialog("Owned dialog", Node("Owned label", TextType, (460, 160, 100, 20)));
        using var svc = NewService(HostWithLabel(), [Tooltip("", 0xAB40, "Tip")], owned);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Owned dialog: Owned label", "popup: Tip"],
            "the owned window's text (under its label) first, then the desktop popup's; the host's own label is not listed");
    }

    [Fact]
    public async Task Z6_the_foreground_fallback_root_is_a_target_too_and_contributes_nothing()
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([UiTree.SnapshotFixtures.Window("Host", hwnd: Host, pid: 4242, isActive: false)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        using var svc = new UIAutomationService(input.Object, windows.Object);
        var palette = _fakes.Window("Tool palette", 0xAD00, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: [Node("Palette label", TextType, (110, 110, 100, 20))]);
        svc.ForegroundRoot = () => palette;
        svc.ElementFromHandle = _ => null;
        svc.OwnedWindowsOf = _ => [];
        svc.RootWindowOf = h => h;
        svc.DesktopWindows = () => [Tooltip("", 0xAB40, "Tip")];

        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true));

        snap.PopupText.Should().Equal(["popup: Tip"]);
    }

    [Fact]
    public async Task Z6_the_50_line_cap_is_across_owned_windows_and_desktop_popups()
    {
        var ownedTexts = Enumerable.Range(1, 30).Select(i => $"owned {i:00}").ToArray();
        var tipTexts = Enumerable.Range(1, 30).Select(i => $"tip {i:00}").ToArray();
        var owned = OwnedDialog("Owned dialog", ownedTexts.Select(t => Node(t, TextType, (460, 160, 100, 20))).ToArray());
        using var svc = NewService(HostWithLabel(), [Tooltip("", 0xAB40, tipTexts)], owned);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().HaveCount(50);
        snap.PopupText![..30].Should().Equal(ownedTexts.Select(t => "Owned dialog: " + t));
        snap.PopupText[30..49].Should().Equal(tipTexts.Take(19).Select(t => "popup: " + t));
        snap.PopupText[49].Should().Be(Marker);
    }

    // ---- Z7: the popup's root names itself only when nothing else in it did -------------------------

    [Fact]
    public async Task Z7_lines_follow_walk_order_and_a_named_root_with_text_inside_adds_no_line_of_its_own()
    {
        // Re-pinned from Z2_lines_follow_walk_order_across_popups (it expected "Second: Second").
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, "First tip"), Tooltip("Second", 0xAB43, "Second tip")]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["popup: First tip", "Second: Second tip"]);
    }

    [Fact]
    public async Task Z7_a_named_tooltip_with_no_named_children_gives_its_own_name()
    {
        // The shape UIA gives a comctl32 tooltip: a ToolTip whose Name is the text, no children.
        using var svc = NewService(desktop: [Popup("Saves the document", 0xAB40, ToolTipType)]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Saves the document: Saves the document"]);
    }

    [Fact]
    public async Task Z7_an_owned_dialog_gives_its_static_text_not_its_title_again()
    {
        var owned = OwnedDialog("Save As",
            Node("Do you want to save changes to Untitled?", TextType, (460, 160, 250, 20)),
            Node("Save", ButtonType, (460, 200, 60, 20)));
        using var svc = NewService(HostWithLabel(), owned: owned);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Save As: Do you want to save changes to Untitled?"]);
    }

    [Fact]
    public async Task Z7_a_named_container_inside_a_popup_still_gives_a_line()
    {
        var find = Popup("Find", 0xAB44, PaneType,
            _fakes.Window("Options", 0, controlType: PaneType, bounds: (155, 155, 150, 60),
                children: [Node("Use defaults", TextType, (160, 160, 100, 20))]));
        using var svc = NewService(desktop: [find]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Find: Options", "Find: Use defaults"], "a named non-root container is text; the root's own name is not, here");
    }

    // ---- PR #43 review: the LABEL of a popup-text line is folded and clipped too ---------------------
    // Label: line breaks -> single spaces, then at most 80 characters followed by "...". Name: its
    // 200-character rule. So no entry has a line break and none is longer than 80+3+2+200+3.

    private const int MaxEntryLength = 80 + 3 + 2 + 200 + 3;

    private static void EveryEntryIsOneBoundedLine(SnapshotResult snap)
    {
        snap.PopupText.Should().NotBeNull();
        snap.PopupText!.Should().OnlyContain(e => !e.Contains('\n') && !e.Contains('\r'), "no entry spans lines");
        snap.PopupText.Should().OnlyContain(e => e.Length <= MaxEntryLength, $"no entry is longer than {MaxEntryLength} characters");
    }

    [Theory]
    [InlineData("Line one\nLine two")]
    [InlineData("Line one\r\nLine two")]
    public async Task Label_a_named_tooltip_with_a_multi_line_name_gives_one_line_label_and_name_alike(string name)
    {
        using var svc = NewService(desktop: [Popup(name, 0xAB40, ToolTipType)]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["Line one Line two: Line one Line two"]);
        EveryEntryIsOneBoundedLine(snap);
    }

    [Fact]
    public async Task Label_a_3000_character_tooltip_is_clipped_at_80_in_the_label_and_200_in_the_name()
    {
        var huge = new string('x', 3000);
        using var svc = NewService(desktop: [Popup(huge, 0xAB40, ToolTipType)]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal([new string('x', 80) + "...: " + new string('x', 200) + "..."]);
        EveryEntryIsOneBoundedLine(snap);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    [InlineData(120, true)]
    public async Task Label_a_label_of_80_characters_is_kept_and_a_longer_one_is_clipped(int length, bool clipped)
    {
        // A long-labelled popup with a short Text child: the root's own name gives no line (Z7).
        var label = new string('L', length);
        using var svc = NewService(desktop: [Tooltip(label, 0xAB40, "Tip")]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal([(clipped ? new string('L', 80) + "..." : label) + ": Tip"]);
    }

    [Fact]
    public async Task Label_an_owned_windows_long_label_is_clipped_in_its_popup_text_too()
    {
        var title = new string('T', 120);
        var owned = OwnedDialog(title, Node("Owned label", TextType, (460, 160, 100, 20)));
        using var svc = NewService(HostWithLabel(), owned: owned);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal([new string('T', 80) + "...: Owned label"]);
    }

    [Fact]
    public async Task Label_guard_the_rows_window_label_is_unchanged_only_the_popup_text_line_is_clipped()
    {
        var label = new string('L', 120);
        var popup = Popup(label, 0xAB40, PaneType, Node("Tip", TextType), Node("OK", ButtonType, (155, 180, 50, 20)));
        using var svc = NewService(desktop: [popup]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.Interactive.Should().ContainSingle(e => e.Name == "OK").Which.Window.Should().Be(label,
            "the snapshot's rows keep the label as it is; only PopupText folds and clips it");
        snap.PopupText.Should().Equal([new string('L', 80) + "...: Tip"]);
    }

    // ---- Z8: a popup with no text contributes nothing --------------------------------------------

    [Fact]
    public async Task Z8_an_owned_dialog_or_a_popup_with_only_controls_contributes_nothing_and_popup_text_stays_null()
    {
        var owned = OwnedDialog("", Node("OK", ButtonType, (460, 160, 60, 20)));
        var buttons = Popup("", 0xAB45, PaneType, Node("Cancel", ButtonType));
        using var svc = NewService(HostWithLabel(), [buttons, Menu(0xAB41, "Open")], owned);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().BeNull();
    }

    [Fact]
    public async Task Z2_a_long_name_is_clipped_to_200_characters()
    {
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, new string('x', 300))]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["popup: " + new string('x', 200) + "..."]);
    }

    [Fact]
    public async Task Z2_the_list_is_capped_at_50_lines_the_last_saying_more_was_not_shown()
    {
        // My reading of "capped at 50 lines; when the cap cuts it, the last line is the marker":
        // 50 lines in all, 49 of text and the marker.
        var texts = Enumerable.Range(1, 60).Select(i => $"line {i:00}").ToArray();
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, texts)]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().HaveCount(50);
        snap.PopupText![..49].Should().Equal(texts.Take(49).Select(t => "popup: " + t));
        snap.PopupText[49].Should().Be(Marker);
    }

    [Fact]
    public async Task Z2_exactly_50_lines_are_not_cut_and_carry_no_marker()
    {
        var texts = Enumerable.Range(1, 50).Select(i => $"line {i:00}").ToArray();
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, texts)]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(texts.Select(t => "popup: " + t));
    }

    [Fact]
    public async Task Z2_a_multi_line_name_is_still_one_line()
    {
        // Confirmed: newlines collapse to spaces.
        using var svc = NewService(desktop: [Tooltip("", 0xAB40, "Line one\nLine two")]);

        var snap = await svc.SnapshotAsync(Observation);

        snap.PopupText.Should().Equal(["popup: Line one Line two"]);
    }
}

/// <summary>
/// PR #43 Z4, the reviewer's regression at the tool: <c>Observation</c> (what every action tool
/// returns) over the REAL service, the windows given by fakes as in <c>ObserveWindowTimeoutTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObservePopupTextTests : IDisposable
{
    private readonly FakeUiWindows _fakes = new();
    private readonly UIAutomationService _svc;

    public ObservePopupTextTests()
    {
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([UiTree.SnapshotFixtures.Window("Some app", hwnd: 0xAB00, isActive: true)]);
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        _svc = new UIAutomationService(input.Object, windows.Object);
        _svc.ElementFromHandle = h => _fakes.Window("Some app button", h);
        _svc.OwnedWindowsOf = _ => [];
        _svc.RootWindowOf = h => h;
    }

    public void Dispose()
    {
        _svc.Dispose();
        _fakes.Dispose();
    }

    [Fact]
    public async Task Observe_foreground_with_a_text_only_tooltip_open_returns_the_tooltips_text()
    {
        _svc.DesktopWindows = () =>
        [
            _fakes.Window("", 0xAB40, controlType: 50022, bounds: (150, 150, 200, 40),
                children: [_fakes.Window("Saves the document", 0, controlType: 50020, bounds: (155, 155, 150, 20))]),
        ];

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().StartWith("acted\n\nObserved");
        text.Should().EndWith("\n\nPopup text (not controls, no ids):\npopup: Saves the document");
    }

    [Fact]
    public async Task Observe_foreground_with_a_multi_line_tooltip_puts_its_entry_on_exactly_one_line()
    {
        _svc.DesktopWindows = () => [_fakes.Window("Line one\nLine two", 0xAB40, controlType: 50022, bounds: (150, 150, 200, 40))];

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().EndWith("\n\nPopup text (not controls, no ids):\nLine one Line two: Line one Line two",
            "the section's entry is one line, label and name alike");
    }

    [Fact]
    public async Task Observe_foreground_with_a_menu_open_adds_no_popup_text_section()
    {
        _svc.DesktopWindows = () =>
        [
            _fakes.Window("", 0xAB41, controlType: 50009, bounds: (150, 150, 200, 80),
                children: [_fakes.Window("Open", 0, controlType: 50011, bounds: (155, 155, 150, 20))]),
        ];

        var text = await new Observation(_svc).AroundAsync("foreground", 0, null, CancellationToken.None, () => Task.FromResult("acted"));

        text.Should().Contain("Open", "the menu item is a row");
        text.Should().NotContain("Popup text");
    }
}
