using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-1 (R2): the whole window inventory decided on hand-written probes. The enumerator only fills
/// <see cref="WindowProbe"/>s from Win32; every judgement — what is chrome, what is a ghost, what
/// z-order number a window gets, which monitor it is on, what its title reads as — is made here,
/// so it is provable with no desktop attached (roadmap C10). The live end is
/// <c>WindowServiceTests</c> (Integration).
/// </summary>
[Trait("Category", "Unit")]
public class WindowFilterTests
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_EX_TOPMOST = 0x00000008;

    /// <summary>A plain, visible, titled application window — the case every rule below deviates from.</summary>
    private static WindowProbe Probe(
        long hwnd = 1,
        bool visible = true,
        uint exStyle = 0,
        bool cloaked = false,
        Bounds? bounds = null,
        string? title = "Untitled - Notepad",
        string className = "Notepad",
        bool minimized = false,
        bool maximized = false,
        int pid = 4242,
        string process = "notepad")
        => new(hwnd, visible, exStyle, cloaked, bounds ?? new Bounds(100, 100, 800, 600),
               title, className, minimized, maximized, pid, process);

    /// <summary>Two 1920x1080 monitors side by side; the seam is x = 1920.</summary>
    private static MonitorInfo[] SideBySide =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, 0, 1920, 1080, false),
    ];

    // ---- Keep: the baseline -----------------------------------------------------------------

    [Fact]
    public void Keep_keeps_a_plain_visible_titled_window()
    {
        WindowFilter.Keep(Probe(), includeMinimized: true, includeHidden: false).Should().BeTrue();
        WindowFilter.Keep(Probe(), includeMinimized: false, includeHidden: false).Should().BeTrue(
            "the flags only ever remove windows, they never rescue one the rules dropped");
    }

    [Fact]
    public void Keep_keeps_a_window_with_an_unrelated_extended_style()
    {
        // WS_EX_TOPMOST is not WS_EX_TOOLWINDOW: an always-on-top app window stays in the list.
        WindowFilter.Keep(Probe(exStyle: WS_EX_TOPMOST), includeMinimized: true, includeHidden: false)
            .Should().BeTrue();
    }

    // ---- Keep: one row per rule -------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Keep_drops_an_invisible_window_whatever_the_flags(bool includeMinimized, bool includeHidden)
    {
        // include_hidden is about EMPTY TITLES, not about IsWindowVisible: a window the user
        // cannot see is never inventory, or the list fills up with every background window.
        WindowFilter.Keep(Probe(visible: false), includeMinimized, includeHidden).Should().BeFalse();
    }

    [Fact]
    public void Keep_drops_a_tool_window()
    {
        WindowFilter.Keep(Probe(exStyle: WS_EX_TOOLWINDOW), includeMinimized: true, includeHidden: true)
            .Should().BeFalse("WS_EX_TOOLWINDOW means palette/overlay chrome, not a task the user switches to");
    }

    [Fact]
    public void Keep_keeps_a_tool_window_that_also_forces_a_taskbar_button()
    {
        // WS_EX_APPWINDOW overrides WS_EX_TOOLWINDOW: Windows shows it on the taskbar, so do we.
        WindowFilter.Keep(Probe(exStyle: WS_EX_TOOLWINDOW | WS_EX_APPWINDOW), includeMinimized: true, includeHidden: false)
            .Should().BeTrue();
        WindowFilter.Keep(Probe(exStyle: WS_EX_APPWINDOW), includeMinimized: true, includeHidden: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Keep_drops_a_cloaked_window()
    {
        // DWM-cloaked = a suspended UWP ghost or a window on another virtual desktop: visible to
        // EnumWindows, invisible to the user.
        WindowFilter.Keep(Probe(cloaked: true), includeMinimized: true, includeHidden: true)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(0, 0)]
    [InlineData(-10, 600)]
    [InlineData(800, -10)]
    public void Keep_drops_a_zero_or_negative_area_window(int width, int height)
    {
        WindowFilter.Keep(Probe(bounds: new Bounds(100, 100, width, height)), includeMinimized: true, includeHidden: true)
            .Should().BeFalse("a window with no area cannot be clicked and cannot be seen");
    }

    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("IME")]
    [InlineData("MSCTFIME UI")]
    public void Keep_drops_shell_chrome_by_class_name(string className)
    {
        // The taskbar, the desktop and the IME windows are always visible and always titled;
        // only the class name tells them apart from an application window.
        WindowFilter.Keep(Probe(className: className, title: "Program Manager"), includeMinimized: true, includeHidden: true)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("shell_traywnd")]   // ordinal, not case-insensitive
    [InlineData("PROGMAN")]
    [InlineData("WorkerW2")]        // exact, not prefix
    [InlineData("IMEX")]
    [InlineData("XIME")]
    [InlineData("Notepad")]
    public void Keep_keeps_a_class_name_that_is_not_exactly_shell_chrome(string className)
    {
        WindowFilter.Keep(Probe(className: className), includeMinimized: true, includeHidden: false)
            .Should().BeTrue("the chrome match is ordinal and exact, so an app must not lose its window to a near-miss");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Keep_drops_an_untitled_window_unless_hidden_are_asked_for(string? title)
    {
        WindowFilter.Keep(Probe(title: title), includeMinimized: true, includeHidden: false).Should().BeFalse();
        WindowFilter.Keep(Probe(title: title), includeMinimized: true, includeHidden: true).Should().BeTrue(
            "include_hidden is exactly the switch that lets untitled windows through");
    }

    [Fact]
    public void Keep_judges_the_title_after_sanitising_it()
    {
        // A title made only of Private Use glyphs (an icon-font caption) sanitises to "", so it is
        // an untitled window — judging the RAW title would leak an unreadable row to the model.
        var probe = Probe(title: "");

        WindowFilter.Keep(probe, includeMinimized: true, includeHidden: false).Should().BeFalse();
        WindowFilter.Keep(probe, includeMinimized: true, includeHidden: true).Should().BeTrue();
    }

    [Fact]
    public void Keep_drops_a_minimized_window_unless_minimized_are_asked_for()
    {
        // A real minimized window is parked at -32000,-32000 with a positive-area rect, so it
        // survives the area rule and only the flag decides.
        var probe = Probe(minimized: true, bounds: new Bounds(-32000, -32000, 160, 28));

        WindowFilter.Keep(probe, includeMinimized: true, includeHidden: false).Should().BeTrue();
        WindowFilter.Keep(probe, includeMinimized: false, includeHidden: false).Should().BeFalse();
    }

    [Fact]
    public void Keep_applies_every_rule_the_flags_do_not_disable()
    {
        // include_hidden forgives the empty title and nothing else; include_minimized=false still
        // drops a minimized window that include_hidden let through on its title.
        var untitledMinimized = Probe(title: "", minimized: true, bounds: new Bounds(-32000, -32000, 160, 28));

        WindowFilter.Keep(untitledMinimized, includeMinimized: true, includeHidden: true).Should().BeTrue();
        WindowFilter.Keep(untitledMinimized, includeMinimized: false, includeHidden: true).Should().BeFalse();
        WindowFilter.Keep(Probe(title: "", cloaked: true), includeMinimized: true, includeHidden: true).Should().BeFalse();
    }

    // ---- StateOf ----------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, WindowState.Normal)]
    [InlineData(false, true, WindowState.Maximized)]
    [InlineData(true, false, WindowState.Minimized)]
    [InlineData(true, true, WindowState.Minimized)]   // IsIconic wins: WS_MAXIMIZE survives minimizing
    public void StateOf_reports_the_state_minimized_first(bool minimized, bool maximized, WindowState expected)
    {
        WindowFilter.StateOf(Probe(minimized: minimized, maximized: maximized)).Should().Be(expected);
    }

    // ---- IsBrowser --------------------------------------------------------------------------

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("vivaldi")]
    [InlineData("chrome.exe")]
    [InlineData("MSEDGE.EXE")]
    [InlineData("Firefox")]
    [InlineData("Brave.Exe")]
    public void IsBrowser_recognises_the_browser_set_with_or_without_the_extension(string processName)
    {
        WindowFilter.IsBrowser(processName).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("explorer")]
    [InlineData("chromedriver")]     // exact match on the stem, not a prefix
    [InlineData("chrome_proxy")]
    [InlineData("operator")]
    [InlineData("firefox.exe.bak")]
    public void IsBrowser_rejects_everything_else(string processName)
    {
        // "" is what the enumerator records when the process lookup throws (an exited process),
        // and it must never be mistaken for a browser.
        WindowFilter.IsBrowser(processName).Should().BeFalse();
    }

    [Fact]
    public void BrowserProcesses_is_the_case_insensitive_set_A_5_reuses()
    {
        // The set itself is the contract A-5 inherits (roadmap A-1: "a static readonly set A-5
        // reuses"), so both its contents and its comparer are pinned here, not just IsBrowser.
        WindowFilter.BrowserProcesses.Should().BeEquivalentTo(
            new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" });
        WindowFilter.BrowserProcesses.Contains("CHROME").Should().BeTrue(
            "a raw Win32 process name arrives in whatever case the image was registered with");
    }

    // ---- Build ------------------------------------------------------------------------------

    [Fact]
    public void Build_on_an_empty_desktop_returns_an_empty_array()
    {
        WindowFilter.Build([], foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false)
            .Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Build_keeps_the_order_and_renumbers_z_order_over_the_survivors()
    {
        // EnumWindows order IS z-order; a dropped window must close the gap, not leave a hole,
        // because ZOrder is what the model reads as "depth" (0 = topmost).
        WindowProbe[] probes =
        [
            Probe(hwnd: 10, title: "Top"),
            Probe(hwnd: 11, title: "Palette", exStyle: WS_EX_TOOLWINDOW),
            Probe(hwnd: 12, title: "Middle"),
            Probe(hwnd: 13, title: "Ghost", cloaked: true),
            Probe(hwnd: 14, title: "Bottom"),
        ];

        var list = WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false);

        list.Select(w => w.Title).Should().Equal("Top", "Middle", "Bottom");
        list.Select(w => w.ZOrder).Should().Equal(0, 1, 2);
        list.Select(w => w.Hwnd).Should().Equal(10L, 12L, 14L);
    }

    [Fact]
    public void Build_sanitises_the_title_and_keeps_real_text_intact()
    {
        WindowProbe[] probes =
        [
            Probe(hwnd: 10, title: " Explorer"),                       // codicon prefix
            Probe(hwnd: 11, title: "  Notes \U0001F600  "),                  // emoji pair + padding
            Probe(hwnd: 12, title: "Rapport – café 中文"), // en dash, accents, CJK
        ];

        var list = WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false);

        list.Select(w => w.Title).Should().Equal(
            "Explorer",
            "Notes \U0001F600",
            "Rapport – café 中文");
    }

    [Fact]
    public void Build_flags_exactly_the_foreground_window_as_active()
    {
        WindowProbe[] probes = [Probe(hwnd: 10), Probe(hwnd: 11), Probe(hwnd: 12)];

        var list = WindowFilter.Build(probes, foregroundHwnd: 11, SideBySide, includeMinimized: true, includeHidden: false);

        list.Should().HaveCount(3);
        list.Single(w => w.IsActive).Hwnd.Should().Be(11);
        list.Where(w => !w.IsActive).Select(w => w.Hwnd).Should().Equal(10L, 12L);
    }

    [Fact]
    public void Build_flags_nothing_active_when_the_foreground_window_is_not_in_the_list()
    {
        // The foreground window can be the desktop, a tool window, or a window the flags dropped.
        WindowProbe[] probes = [Probe(hwnd: 10), Probe(hwnd: 11, exStyle: WS_EX_TOOLWINDOW)];

        WindowFilter.Build(probes, foregroundHwnd: 11, SideBySide, includeMinimized: true, includeHidden: false)
            .Should().OnlyContain(w => !w.IsActive);
        WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false)
            .Should().OnlyContain(w => !w.IsActive, "no foreground window means no active window");
    }

    [Fact]
    public void Build_flags_browsers_by_process_name()
    {
        WindowProbe[] probes =
        [
            Probe(hwnd: 10, title: "Docs - Google Chrome", process: "chrome"),
            Probe(hwnd: 11, title: "Untitled - Notepad", process: "notepad"),
            Probe(hwnd: 12, title: "Parity - Edge", process: "MSEDGE.EXE"),
        ];

        WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false)
            .Select(w => w.IsBrowser).Should().Equal(true, false, true);
    }

    [Fact]
    public void Build_reports_the_monitor_the_window_centre_is_on()
    {
        WindowProbe[] probes =
        [
            Probe(hwnd: 10, bounds: new Bounds(0, 0, 800, 600)),        // centre (400, 300)
            Probe(hwnd: 11, bounds: new Bounds(2000, 100, 800, 600)),   // centre (2400, 400)
        ];

        WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false)
            .Select(w => w.MonitorIndex).Should().Equal(0, 1);
    }

    [Fact]
    public void Build_uses_the_centre_not_the_origin_for_the_monitor()
    {
        // Origin x=1820 is on monitor 0, centre x=1920 is the first pixel of monitor 1. A window
        // dragged across the seam belongs to the monitor showing most of it.
        WindowProbe[] probes = [Probe(hwnd: 10, bounds: new Bounds(1820, 100, 200, 600))];

        WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false)
            .Single().MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void Build_reports_minus_one_for_a_window_on_no_monitor()
    {
        // The parked rect of a minimized window, and an empty inventory.
        WindowProbe[] probes = [Probe(hwnd: 10, minimized: true, bounds: new Bounds(-32000, -32000, 160, 28))];

        var parked = WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: false);
        parked.Single().MonitorIndex.Should().Be(-1);
        parked.Single().State.Should().Be(WindowState.Minimized);

        WindowFilter.Build([Probe(hwnd: 11)], foregroundHwnd: 0, [], includeMinimized: true, includeHidden: false)
            .Single().MonitorIndex.Should().Be(-1, "with no monitors nothing is on one");
    }

    [Fact]
    public void Build_forwards_both_flags_to_the_filter_and_renumbers_what_is_left()
    {
        WindowProbe[] probes =
        [
            Probe(hwnd: 10, title: "Top"),
            Probe(hwnd: 11, title: "Tucked away", minimized: true, bounds: new Bounds(-32000, -32000, 160, 28)),
            Probe(hwnd: 12, title: ""),
            Probe(hwnd: 13, title: "Bottom"),
        ];

        var lean = WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: false, includeHidden: false);
        lean.Select(w => w.Hwnd).Should().Equal(10L, 13L);
        lean.Select(w => w.ZOrder).Should().Equal(0, 1);

        var wide = WindowFilter.Build(probes, foregroundHwnd: 0, SideBySide, includeMinimized: true, includeHidden: true);
        wide.Select(w => w.Hwnd).Should().Equal(10L, 11L, 12L, 13L);
        wide.Select(w => w.ZOrder).Should().Equal(0, 1, 2, 3);
        wide[2].Title.Should().BeEmpty("an untitled window kept by include_hidden reports an empty title, not null");
    }

    [Fact]
    public void Build_carries_every_probed_fact_through_unchanged()
    {
        var bounds = new Bounds(300, 40, 1024, 768);
        WindowProbe[] probes = [Probe(hwnd: 0x1234, bounds: bounds, pid: 9001, process: "notepad", maximized: true)];

        var info = WindowFilter.Build(probes, foregroundHwnd: 0x1234, SideBySide, includeMinimized: true, includeHidden: false)
            .Single();

        info.Should().BeEquivalentTo(new WindowInfo(
            Title: "Untitled - Notepad",
            Hwnd: 0x1234,
            Pid: 9001,
            ProcessName: "notepad",
            State: WindowState.Maximized,
            Bounds: bounds,
            ZOrder: 0,
            IsActive: true,
            IsBrowser: false,
            MonitorIndex: 0,
            DesktopId: null));
    }

    [Fact]
    public void Build_leaves_DesktopId_null_for_the_enumerator_to_fill()
    {
        WindowProbe[] probes = [Probe(hwnd: 10), Probe(hwnd: 11, process: "chrome")];

        WindowFilter.Build(probes, foregroundHwnd: 10, SideBySide, includeMinimized: true, includeHidden: false)
            .Should().OnlyContain(w => w.DesktopId == null,
                "A-12 left the pure filter alone: a WindowProbe carries no desktop id, and the id "
                + "comes from a COM call WindowService makes after the filter has chosen the windows");
    }

    // ---- ActiveOf ---------------------------------------------------------------------------

    private static WindowInfo Info(long hwnd, bool active) =>
        new($"w{hwnd}", hwnd, 1, "app", WindowState.Normal, new Bounds(0, 0, 10, 10), (int)hwnd, active, false, 0);

    [Fact]
    public void ActiveOf_picks_the_flagged_window_not_the_first()
    {
        // On a quiet desktop the foreground window is also the topmost one, so a live test cannot
        // tell FirstOrDefault() from FirstOrDefault(IsActive). This list can.
        var windows = new[] { Info(1, false), Info(2, false), Info(3, true), Info(4, false) };

        WindowFilter.ActiveOf(windows).Should().BeSameAs(windows[2]);
    }

    [Fact]
    public void ActiveOf_is_null_when_nothing_is_flagged()
        => WindowFilter.ActiveOf(new[] { Info(1, false), Info(2, false) }).Should().BeNull();

    [Fact]
    public void ActiveOf_is_null_on_an_empty_inventory()
        => WindowFilter.ActiveOf(Array.Empty<WindowInfo>()).Should().BeNull();
}
