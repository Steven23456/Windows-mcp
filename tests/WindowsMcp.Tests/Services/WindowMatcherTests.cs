using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-10 / roadmap C5: the one pure resolver every title lookup goes through —
/// <c>switch_to_window</c>, <c>focus</c>, <c>window(action:…)</c>, and later B-8's window wait and
/// B-9's target. Pure over A-1's inventory, so it is <c>Category=Unit</c> and every rule
/// (precedence, ties, the fuzzy threshold, the two refusals) is asserted without a desktop.
/// <para>
/// Note what this class does NOT cover: <c>UIAutomationService.MatchWindows</c> stays strict
/// (exact-then-substring, no fuzz) so a snapshot scope cannot wander — see
/// <see cref="UIAutomationServiceTests"/>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class WindowMatcherTests
{
    private static WindowInfo Win(
        string title,
        long hwnd = 1,
        int zOrder = 0,
        WindowState state = WindowState.Normal,
        string process = "notepad")
        => new(title, hwnd, 100 + (int)hwnd, process, state,
               new Bounds(0, 0, 800, 600), zOrder, IsActive: zOrder == 0, IsBrowser: false, MonitorIndex: 0);

    /// <summary>A desktop with the shapes every rule below needs: an exact name, a superstring
    /// of it, a fuzzy-only candidate and an unrelated window.</summary>
    private static WindowInfo[] Desktop =>
    [
        Win("Untitled - Notepad", hwnd: 0x10, zOrder: 0),
        Win("Notepad", hwnd: 0x20, zOrder: 1),
        Win("Document1 - Word", hwnd: 0x30, zOrder: 2, process: "winword"),
        Win("Visual Studio Code - Insiders", hwnd: 0x40, zOrder: 3, process: "code"),
    ];

    // ---- hwnd wins over everything ----------------------------------------------------------

    [Fact]
    public void An_hwnd_selects_that_window_and_reports_strategy_hwnd()
    {
        var match = WindowMatcher.Match(Desktop, title: null, hwnd: 0x30);

        match.Window.Title.Should().Be("Document1 - Word");
        match.Strategy.Should().Be("hwnd");
        match.Score.Should().Be(100, "an explicit handle is an exact target, not a guess");
    }

    [Fact]
    public void An_hwnd_beats_a_title_that_would_have_matched_something_else()
    {
        // Precedence, stated once: a caller who knows the handle gets the handle, whatever the
        // title says. (The tool layer forwards both without choosing — WindowToolsTests.)
        var match = WindowMatcher.Match(Desktop, title: "Notepad", hwnd: 0x30);

        match.Window.Hwnd.Should().Be(0x30);
        match.Strategy.Should().Be("hwnd");
    }

    [Fact]
    public void An_hwnd_that_is_not_in_the_inventory_is_a_KeyNotFoundException_naming_it()
    {
        var act = () => WindowMatcher.Match(Desktop, title: null, hwnd: 0xDEAD);

        act.Should().Throw<KeyNotFoundException>()
            .Which.Message.Should().MatchRegex("57005|[Dd][Ee][Aa][Dd]",
                "the handle the caller sent belongs in the message (decimal or hex) — a stale "
                + "hwnd from an earlier window list is the common case");
    }

    [Fact]
    public void An_hwnd_finds_a_window_that_has_no_title_at_all()
    {
        // A handle is the only way to name a titleless window: the fuzzy rung skips them (an
        // empty title matches nothing meaningfully) and neither exact nor substring can address
        // one. include_hidden:true in the inventory is what puts them within reach.
        var inventory = new[] { Win("", hwnd: 0x50, zOrder: 0), Win("Untitled - Notepad", hwnd: 0x10, zOrder: 1) };

        var match = WindowMatcher.Match(inventory, title: null, hwnd: 0x50);

        match.Window.Hwnd.Should().Be(0x50);
        match.Window.Title.Should().BeEmpty();
        match.Strategy.Should().Be("hwnd");
        match.Score.Should().Be(100);
    }

    [Fact]
    public void A_titleless_window_is_never_a_fuzzy_candidate_and_is_not_listed_in_a_refusal()
    {
        var inventory = new[] { Win("", hwnd: 0x50, zOrder: 0), Win("Document1 - Word", hwnd: 0x30, zOrder: 1) };

        var act = () => WindowMatcher.Match(inventory, "notepad", hwnd: null);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain("'Document1 - Word'")
            .And.NotContain("''", "an empty title in the list would read as a window the caller could have named");
    }

    // ---- title: exact -> substring -> fuzzy --------------------------------------------------

    [Fact]
    public void An_exact_title_wins_over_a_window_that_merely_contains_it()
    {
        // "Notepad" is both an exact title (hwnd 0x20) and a substring of the frontmost window
        // (0x10). Exact wins even though the substring match is nearer the front.
        var match = WindowMatcher.Match(Desktop, "Notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0x20);
        match.Strategy.Should().Be("exact");
        match.Score.Should().Be(100);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("NOTEPAD")]
    [InlineData("NoTePaD")]
    public void An_exact_match_ignores_case(string title)
    {
        var match = WindowMatcher.Match(Desktop, title, hwnd: null);

        match.Window.Hwnd.Should().Be(0x20);
        match.Strategy.Should().Be("exact");
    }

    [Fact]
    public void A_substring_wins_when_nothing_matches_exactly()
    {
        var inventory = new[] { Win("Untitled - Notepad", hwnd: 0x10), Win("Document1 - Word", hwnd: 0x30, zOrder: 1) };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0x10);
        match.Strategy.Should().Be("substring");
        match.Score.Should().Be(100, "a substring hit is certain, so it is not scored down to its fuzzy value");
    }

    [Fact]
    public void A_fuzzy_match_is_the_last_resort_and_carries_its_score()
    {
        // The request is neither an exact title nor a substring of one (the words are in the
        // wrong order), so only the fuzzy rung can find it: token-set scores 100 because the
        // request's words are a subset of the title's. The other window scores 25 and is not a
        // candidate at all.
        var inventory = new[] { Win("Visual Studio Code - Insiders", hwnd: 0x40), Win("Document1 - Word", hwnd: 0x30, zOrder: 1) };

        var match = WindowMatcher.Match(inventory, "code studio visual", hwnd: null);

        match.Window.Hwnd.Should().Be(0x40);
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(100, "token-set scores a subset of the title's words 100");
    }

    [Fact]
    public void The_fuzzy_score_is_the_better_of_partial_ratio_and_token_set_ratio()
    {
        // A misspelt title: partial ratio carries this one (86), token-set cannot (55, no token
        // is shared) — so the reported score has to be the max of the two, not either alone and
        // not their average.
        var inventory = new[] { Win("Untitled - Notpad", hwnd: 0x10) };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(86, "PartialRatio is 86 and TokenSetRatio is 55 for this pair");
    }

    [Fact]
    public void A_candidate_below_the_threshold_is_not_a_match()
    {
        // 70 is the gate (upstream's). "edge" scores 50/30 against a Notepad title - close
        // enough that a missing threshold would return it, far enough that returning it is wrong.
        var inventory = new[] { Win("Untitled - Notepad", hwnd: 0x10) };

        var act = () => WindowMatcher.Match(inventory, "edge", hwnd: null);

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void A_candidate_that_scores_exactly_seventy_is_a_match()
    {
        // The gate is ">= 70", and only a pair that lands exactly ON it can tell that apart from
        // "> 70" or ">= 69". "Casculdtov" scores 70 on both partial ratio and token-set against
        // "calculator" - neither is an exact title nor a substring, so the fuzzy rung decides.
        var inventory = new[] { Win("Casculdtov", hwnd: 0x70, zOrder: 0) };

        var match = WindowMatcher.Match(inventory, "calculator", hwnd: null);

        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(70, "the threshold is inclusive: exactly 70 matches");
        match.Window.Hwnd.Should().Be(0x70);
    }

    [Fact]
    public void A_candidate_that_scores_sixty_nine_is_one_point_short_and_is_refused()
    {
        // The other side of the same boundary: 69 is "not this window", and the message says so
        // with the score and the gate, so a caller can see how close it came.
        var inventory = new[] { Win("Phoeo lixargf", hwnd: 0x80, zOrder: 0) };

        var act = () => WindowMatcher.Match(inventory, "photo library", hwnd: null);

        act.Should().Throw<KeyNotFoundException>().Which.Message
            .Should().Contain("scored 69").And.Contain("below 70");
    }

    [Fact]
    public void The_highest_fuzzy_score_wins_even_when_a_weaker_candidate_is_in_front()
    {
        // Both are over the threshold and neither is a substring: "Ntepad Editor" scores 86 and
        // is frontmost, "Notepd" scores 92 and is behind it. Score decides inside the fuzzy rung;
        // z-order only breaks a tie.
        var inventory = new[]
        {
            Win("Ntepad Editor", hwnd: 0x10, zOrder: 0),
            Win("Notepd", hwnd: 0x20, zOrder: 1),
        };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0x20);
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(92);
    }

    [Fact]
    public void A_substring_candidate_wins_before_any_fuzzy_score_is_looked_at()
    {
        // Ordering, not scoring: the fuzzy candidate scores higher (92 against "Notepd") than the
        // substring one would, and still loses, because the rungs are tried in order.
        var inventory = new[]
        {
            Win("Notepd", hwnd: 0x10, zOrder: 0),
            Win("Notepad++", hwnd: 0x20, zOrder: 1),
        };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0x20);
        match.Strategy.Should().Be("substring");
        match.Score.Should().Be(100);
    }

    // ---- ties, order, and which windows are candidates ---------------------------------------

    [Fact]
    public void A_tie_inside_one_strategy_goes_to_the_frontmost_window()
    {
        // Two identical titles: the one the user is looking at is the one they meant.
        var inventory = new[]
        {
            Win("Untitled - Notepad", hwnd: 0xAA, zOrder: 3),
            Win("Untitled - Notepad", hwnd: 0xBB, zOrder: 1),
            Win("Untitled - Notepad", hwnd: 0xCC, zOrder: 2),
        };

        var match = WindowMatcher.Match(inventory, "Untitled - Notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0xBB, "ZOrder 1 is the frontmost of the three, and 0 = topmost");
        match.Strategy.Should().Be("exact");
    }

    [Fact]
    public void A_substring_tie_goes_to_the_frontmost_window_too()
    {
        var inventory = new[]
        {
            Win("b - Notepad", hwnd: 0xAA, zOrder: 5),
            Win("a - Notepad", hwnd: 0xBB, zOrder: 4),
        };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0xBB);
        match.Strategy.Should().Be("substring");
    }

    [Fact]
    public void A_one_character_window_title_can_win_the_fuzzy_rung_when_nothing_else_matches()
    {
        // The edge thefuzz's partial_ratio brings with it (FuzzyMatchTests pins the scorer): a
        // one-character title scores 100 against any request containing that character, so a
        // desktop whose only other window is called "a" hands "notepad" to it. Pinned rather than
        // guarded because the scorers are specified as thefuzz's - if a minimum-length rule is
        // ever added to the fuzzy rung, this is the test that has to be rewritten deliberately.
        var inventory = new[] { Win("a", hwnd: 0x60, zOrder: 0), Win("Document1 - Word", hwnd: 0x30, zOrder: 1) };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.Hwnd.Should().Be(0x60);
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(100, "partial ratio compares one character against every one-character window");
    }

    [Fact]
    public void A_one_character_request_still_goes_through_substring_first()
    {
        // The other half of the same edge: 'd' is a substring of "Untitled - Notepad" and no
        // window is called "d", so the substring rung answers before anything is scored.
        var inventory = new[] { Win("Untitled - Notepad", hwnd: 0x10, zOrder: 0), Win("a", hwnd: 0x60, zOrder: 1) };

        var match = WindowMatcher.Match(inventory, "d", hwnd: null);

        match.Window.Hwnd.Should().Be(0x10);
        match.Strategy.Should().Be("substring");
    }

    [Fact]
    public void A_minimized_window_is_a_candidate()
    {
        // It has to be: bringing a minimized window forward is the main reason B-10 exists, and
        // the service asks for the inventory with includeMinimized:true.
        var inventory = new[] { Win("Untitled - Notepad", hwnd: 0x10, state: WindowState.Minimized) };

        var match = WindowMatcher.Match(inventory, "notepad", hwnd: null);

        match.Window.State.Should().Be(WindowState.Minimized);
        match.Strategy.Should().Be("substring");
    }

    // ---- the two refusals --------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Neither_a_title_nor_an_hwnd_is_an_ArgumentException(string? title)
    {
        var act = () => WindowMatcher.Match(Desktop, title, hwnd: null);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("title").And.Contain("hwnd",
                "a blank title is no target at all, and the caller is told both ways to give one");
    }

    [Fact]
    public void No_match_lists_the_open_windows_in_the_A2_wording()
    {
        var act = () => WindowMatcher.Match(Desktop, "libreoffice calc", hwnd: null);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().StartWith("No top-level window matching 'libreoffice calc'. Open windows: ",
            "same sentence UIAutomationService.MatchWindows uses - one wording for one situation");
        message.Should().Contain("'Untitled - Notepad'").And.Contain("'Document1 - Word'");
    }

    [Fact]
    public void No_match_names_the_best_candidate_and_its_score()
    {
        // A near miss is the interesting case: "which window did I nearly hit, and by how much"
        // is what turns a retry into a correct retry.
        var inventory = new[] { Win("Untitled - Notepad", hwnd: 0x10), Win("Document1 - Word", hwnd: 0x30, zOrder: 1) };

        var act = () => WindowMatcher.Match(inventory, "notebad pro edition", hwnd: null);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain("Untitled - Notepad").And.Contain("44",
            "the closest window (44: partial ratio, against 36 for the Word window) and its score, "
            + "so the caller can see how far off the request was and whether to retry");
    }

    [Fact]
    public void No_match_lists_at_most_fifteen_titles()
    {
        // The A-2 cap. A desktop with fifty windows must not turn one refusal into a wall of text
        // the model has to read on every retry.
        var inventory = Enumerable.Range(0, 40)
            .Select(i => Win($"Window number {i}", hwnd: 0x100 + i, zOrder: i))
            .ToArray();

        // A request that shares no character with any title: every candidate scores 0, so the
        // "best candidate" the message also names is the frontmost one - already inside the
        // fifteen, and the count below stays exact.
        var act = () => WindowMatcher.Match(inventory, "zzqqxxyy", hwnd: null);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        inventory.Count(w => message.Contains($"'{w.Title}'")).Should().Be(15,
            "fifteen titles, in the order the inventory reported them (frontmost first)");
        message.Should().Contain("'Window number 0'").And.Contain("'Window number 14'")
            .And.NotContain("'Window number 15'");
    }

    [Fact]
    public void An_empty_inventory_is_a_KeyNotFoundException_that_says_so()
    {
        var act = () => WindowMatcher.Match([], "notepad", hwnd: null);

        act.Should().Throw<KeyNotFoundException>()
            .Which.Message.Should().Contain("(none with a title)",
                "the A-2 wording for an empty desktop, not 'Open windows: ' trailing into nothing");
    }

    [Fact]
    public void An_empty_inventory_with_an_hwnd_is_a_KeyNotFoundException_too()
    {
        var act = () => WindowMatcher.Match([], title: null, hwnd: 0x10);

        act.Should().Throw<KeyNotFoundException>();
    }
}
