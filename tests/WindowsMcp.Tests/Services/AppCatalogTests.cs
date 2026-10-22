using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8 / roadmap C7: the pure half of the app catalog — how two sources become one list and how a
/// requested name resolves against it. Everything an agent's <c>launch("calc")</c> depends on is
/// decided here, with no Start Menu, no package manager and no clock, so a change to the matching
/// rules fails here first and not on somebody's desktop.
/// <para>
/// Every fuzzy score in the tables below was read out of the real <see cref="FuzzyMatch"/>
/// (B-10's scorers, roadmap C6) before this file existed: they are the numbers the shared scorer
/// actually produces, not numbers invented to fit an implementation.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AppCatalogTests
{
    private const string StartMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";

    private static AppEntry Shortcut(string name)
        => new(name, "shortcut", Path.Combine(StartMenu, name + ".lnk"), StartMenu);

    private static AppEntry Packaged(string name, string family = "Contoso.Test_8wekyb3d8bbwe")
        => new(name, "packaged", family + "!App", "package:" + family);

    /// <summary>A plausible Start Menu: three packaged apps and three shortcuts.</summary>
    private static readonly AppEntry[] CatalogA =
    [
        Packaged("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe"),
        Shortcut("Command Prompt"),
        Shortcut("Microsoft Edge"),
        Packaged("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe"),
        Shortcut("Visual Studio Code"),
        Packaged("Windows Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe"),
    ];

    // ---- Merge: two sources, one list --------------------------------------------------------

    [Fact]
    public void Merge_puts_both_sources_in_one_list_ordered_by_name()
    {
        var merged = AppCatalog.Merge(
            [Shortcut("charlie"), Shortcut("Alpha")],
            [Packaged("beta"), Packaged("delta")]);

        merged.Select(e => e.Name).Should().Equal(new[] { "Alpha", "beta", "charlie", "delta" },
            "one list, ordered by name ignoring case - the model reads it and a resolve scans it");
    }

    [Fact]
    public void Merge_lets_a_shortcut_win_over_a_packaged_entry_with_the_same_name()
    {
        // The .lnk is the user's own Start Menu entry; the package is the OS's idea of the app.
        var merged = AppCatalog.Merge(
            [Shortcut("Notepad")],
            [Packaged("Notepad", "Microsoft.WindowsNotepad_8wekyb3d8bbwe"), Packaged("Calculator")]);

        merged.Should().HaveCount(2, "the duplicate name collapses to one entry");
        var notepad = merged.Single(e => e.Name == "Notepad");
        notepad.Kind.Should().Be("shortcut");
        notepad.Target.Should().EndWith("Notepad.lnk", "launching it must open the shortcut, not the AUMID");
    }

    [Fact]
    public void Merge_dedupes_names_ignoring_case()
    {
        var merged = AppCatalog.Merge([Shortcut("Notepad")], [Packaged("NOTEPAD")]);

        merged.Should().ContainSingle().Which.Name.Should().Be("Notepad",
            "the surviving entry keeps the winning source's own spelling");
    }

    [Fact]
    public void Merge_keeps_the_first_of_two_shortcuts_that_share_a_name()
    {
        // Both Start Menu folders can hold "Microsoft Edge.lnk"; the scan order decides.
        var first = new AppEntry("Microsoft Edge", "shortcut", @"C:\ProgramData\a\Microsoft Edge.lnk", @"C:\ProgramData\a");
        var second = new AppEntry("Microsoft Edge", "shortcut", @"C:\Users\b\Microsoft Edge.lnk", @"C:\Users\b");

        var merged = AppCatalog.Merge([first, second], []);

        merged.Should().ContainSingle().Which.Target.Should().Be(first.Target);
    }

    [Fact]
    public void Merge_keeps_a_packaged_app_no_shortcut_shadows()
    {
        var merged = AppCatalog.Merge([Shortcut("Microsoft Edge")], [Packaged("Calculator")]);

        merged.Select(e => e.Name).Should().Equal("Calculator", "Microsoft Edge");
        merged.Single(e => e.Name == "Calculator").Kind.Should().Be("packaged");
    }

    [Fact]
    public void Merge_of_two_empty_sources_is_an_empty_list()
    {
        AppCatalog.Merge([], []).Should().BeEmpty("no apps is an empty catalog, not null and not a throw");
    }

    // ---- Match: exact ------------------------------------------------------------------------

    [Theory]
    [InlineData("Calculator")]
    [InlineData("calculator")]
    [InlineData("CALCULATOR")]
    public void Match_takes_an_exact_name_ignoring_case(string request)
    {
        var match = AppCatalog.Match(CatalogA, request);

        match.Entry.Name.Should().Be("Calculator");
        match.Strategy.Should().Be("exact");
        match.Score.Should().Be(100);
    }

    [Fact]
    public void Match_returns_the_whole_entry_not_just_its_name()
    {
        // The launcher needs Kind and Target: an AUMID goes to the activation manager, a .lnk to
        // ShellExecute. A match that carried only a name could not make that decision.
        var match = AppCatalog.Match(CatalogA, "Calculator");

        match.Entry.Kind.Should().Be("packaged");
        match.Entry.Target.Should().Be("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        match.Entry.Source.Should().Be("package:Microsoft.WindowsCalculator_8wekyb3d8bbwe");
    }

    [Fact]
    public void Match_prefers_an_exact_name_over_a_name_it_is_a_prefix_of()
    {
        var catalog = new[] { Shortcut("Notes"), Shortcut("Note") };

        var match = AppCatalog.Match(catalog, "note");

        match.Entry.Name.Should().Be("Note");
        match.Strategy.Should().Be("exact");
    }

    // ---- Match: prefix -----------------------------------------------------------------------

    [Theory]
    [InlineData("calc")]
    [InlineData("CALC")]
    [InlineData("calcul")]
    public void Match_takes_a_name_the_request_is_a_prefix_of(string request)
    {
        var match = AppCatalog.Match(CatalogA, request);

        match.Entry.Name.Should().Be("Calculator");
        match.Strategy.Should().Be("prefix", "'calc' is the start of 'Calculator' - that is not a guess");
        match.Score.Should().Be(100);
    }

    [Fact]
    public void Match_prefix_picks_the_shortest_name_it_starts()
    {
        var catalog = new[] { Shortcut("Notepad++"), Shortcut("Notepad"), Shortcut("Notes") };

        var match = AppCatalog.Match(catalog, "note");

        match.Entry.Name.Should().Be("Notes", "the shortest name the request starts is the least-guessing one");
        match.Strategy.Should().Be("prefix");
    }

    [Fact]
    public void Match_prefix_runs_before_fuzzy_even_when_fuzzy_would_pick_another_entry()
    {
        // Both score 100 fuzzy ("code" is a substring of each), and the fuzzy tie-break (shortest
        // name) would take "Visual Studio Code". The prefix stage has to win first.
        var catalog = new[] { Shortcut("Codewriter Pro Edition"), Shortcut("Visual Studio Code") };

        var match = AppCatalog.Match(catalog, "code");

        match.Entry.Name.Should().Be("Codewriter Pro Edition");
        match.Strategy.Should().Be("prefix");
        match.Score.Should().Be(100);
    }

    // ---- Match: fuzzy ------------------------------------------------------------------------

    [Theory]
    // request, the entry it must resolve to, and the score: max(PartialRatio, TokenSetRatio, 95
    // when FuzzyMatch.AbbreviationCovers the name). "vs code" was 73 on the two scorers alone; it
    // is 95 because "vs" is the initials of "Visual Studio" (fix/app-catalog-matching).
    [InlineData("edge", "Microsoft Edge", 100)]
    [InlineData("code", "Visual Studio Code", 100)]
    [InlineData("vs code", "Visual Studio Code", 95)]
    [InlineData("visual code", "Visual Studio Code", 100)]
    [InlineData("terminal", "Windows Terminal", 100)]
    public void Match_falls_back_to_fuzzy_and_reports_the_score(string request, string expected, int score)
    {
        var match = AppCatalog.Match(CatalogA, request);

        match.Entry.Name.Should().Be(expected);
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(score, "the score is FuzzyMatch's, and the model is shown how sure the match is");
    }

    [Fact]
    public void Match_fuzzy_takes_the_highest_score_not_the_first_candidate()
    {
        // "Notepad" comes first and scores 50 against "edge"; "Microsoft Edge" scores 100. An
        // implementation that stopped at the first candidate over the floor would open Notepad.
        var catalog = new[] { Shortcut("Notepad"), Shortcut("Notepad++"), Shortcut("Microsoft Edge") };

        AppCatalog.Match(catalog, "edge").Entry.Name.Should().Be("Microsoft Edge");
    }

    [Fact]
    public void Match_fuzzy_ties_go_to_the_shortest_name()
    {
        var catalog = new[] { Shortcut("Alpha xyz app"), Shortcut("Beta xyz app") };

        var match = AppCatalog.Match(catalog, "xyz app");

        match.Score.Should().Be(100, "the request is a substring of both");
        match.Entry.Name.Should().Be("Beta xyz app", "twelve characters beats thirteen");
    }

    [Theory]
    [InlineData("abcdefghij", "abcdefgxyz", 70, true)]                // exactly the floor: a match
    [InlineData("abcdefghijklmnop", "abcdefghijkvwxyz", 69, false)]   // one below: not a match
    public void Match_takes_a_score_of_seventy_and_refuses_sixty_nine(
        string request, string name, int score, bool matches)
    {
        // The floor is shared with WindowMatcher (roadmap C6) and is the whole difference between
        // "launch opened the wrong app" and "launch said it did not know the name".
        AppCatalog.FuzzyThreshold.Should().Be(70);
        var catalog = new[] { Shortcut(name) };

        if (matches)
        {
            var match = AppCatalog.Match(catalog, request);
            match.Score.Should().Be(score);
            match.Strategy.Should().Be("fuzzy");
        }
        else
        {
            var act = () => AppCatalog.Match(catalog, request);
            act.Should().Throw<KeyNotFoundException>().WithMessage($"*{request}*");
        }
    }

    // ---- Match: nothing at all ---------------------------------------------------------------

    [Fact]
    public void Match_refuses_a_name_nothing_scores_seventy_for()
    {
        // "cmd" scores 67 against both "Command Prompt" and "Visual Studio Code" - close, and
        // still not close enough. Opening the wrong app is worse than saying no.
        var act = () => AppCatalog.Match(CatalogA, "cmd");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*cmd*",
            "the model needs to see the name it sent");
    }

    /// <summary>Eight entries scoring 67, 67, 33, 33, 33, 33, 0, 0 against "cmd".</summary>
    private static readonly AppEntry[] CatalogM =
    [
        Shortcut("Visual Studio Code"), Shortcut("Command Prompt"),
        Shortcut("Calculator"), Shortcut("Notepad"), Shortcut("Microsoft Edge"),
        Shortcut("Windows Terminal"), Shortcut("Alpha xyz app"), Shortcut("Beta xyz app"),
    ];

    [Fact]
    public void Match_lists_the_five_nearest_names_with_their_scores_when_nothing_matches()
    {
        var act = () => AppCatalog.Match(CatalogM, "cmd");

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain("cmd");
        message.Should().Contain("Command Prompt").And.Contain("Visual Studio Code",
            "the two nearest at 67 are exactly what the model should try next");
        message.Should().Contain("67", "a name without its score is not actionable");
        message.Should().NotContain("Alpha xyz app").And.NotContain("Beta xyz app",
            "the two entries that score 0 cannot be among the five nearest");
        CatalogM.Count(e => message.Contains(e.Name, StringComparison.Ordinal)).Should().Be(5,
            "five nearest means five, not the whole catalog");
    }

    [Fact]
    public void Match_on_an_empty_catalog_still_names_the_request()
    {
        var act = () => AppCatalog.Match([], "calc");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*calc*",
            "an empty catalog is a miss with nothing to suggest, not a crash");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Match_refuses_a_blank_name(string? request)
    {
        var act = () => AppCatalog.Match(CatalogA, request!);

        act.Should().Throw<ArgumentException>().WithMessage("*name*",
            "a blank request is a caller bug, not a fuzzy match against everything");
    }

    // ---- Match: which of the two scorers carried the match --------------------------------------

    [Fact]
    public void Match_takes_a_name_only_the_token_set_scorer_reaches_the_floor_for()
    {
        // "command shell": PartialRatio 62, TokenSetRatio exactly 70. The match exists only
        // because the rule is max(PartialRatio, TokenSetRatio) - a PartialRatio-only matcher
        // would tell the model this machine has nothing like it.
        var catalog = new[] { Shortcut("Command Prompt") };

        var match = AppCatalog.Match(catalog, "command shell");

        match.Entry.Name.Should().Be("Command Prompt");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(70, "the token-set score is what got it over the floor, on the nose");
    }

    [Fact]
    public void Match_takes_a_name_only_the_partial_scorer_reaches_the_floor_for()
    {
        // The mirror image: "alcul" is a window of "Calculator" (PartialRatio 100) but shares no
        // whole token with it (TokenSetRatio 67). Dropping either scorer breaks half of launch().
        var catalog = new[] { Shortcut("Calculator") };

        var match = AppCatalog.Match(catalog, "alcul");

        match.Entry.Name.Should().Be("Calculator");
        match.Score.Should().Be(100, "the higher of the two scorers is the score, not the average or the last one");
    }

    // ---- Match: the tie-breaks are total, so two runs give the same answer ------------------------

    [Fact]
    public void Match_prefix_ties_of_equal_length_go_to_the_first_name_alphabetically()
    {
        // Two Start Menu entries of the same length that the request starts: without a second
        // tie-break the answer would depend on the order the folders happened to be scanned in,
        // and launch("note") would open a different app on a different day.
        var catalog = new[] { Shortcut("NoteBar"), Shortcut("NoteAxe") };

        var match = AppCatalog.Match(catalog, "note");

        match.Entry.Name.Should().Be("NoteAxe");
        match.Strategy.Should().Be("prefix");
    }

    [Fact]
    public void Match_fuzzy_ties_of_equal_score_and_equal_length_go_to_the_first_name_alphabetically()
    {
        var catalog = new[] { Shortcut("Delta xyz app"), Shortcut("Alpha xyz app") };

        var match = AppCatalog.Match(catalog, "xyz app");

        match.Score.Should().Be(100, "the request is a substring of both");
        match.Entry.Name.Should().Be("Alpha xyz app", "same score, same length: the name decides, so the answer is stable");
    }

    // ---- Match: initials (fix/app-catalog-matching) ---------------------------------------------
    //
    // The fuzzy stage's score is max(PartialRatio, TokenSetRatio, 95 - unread when
    // FuzzyMatch.AbbreviationUnread(request, name) >= 0); the strategy stays "fuzzy", and the exact
    // and prefix stages are unchanged and still run first. A top-score tie in which some entry got
    // its score only from the abbreviation is refused (ArgumentException) rather than broken by
    // name length. FuzzyMatchTests pins the reading rule itself; these pin what it does to a resolve.

    /// <summary>The pair that broke on a real box: both share only "code" with "vs code" and score
    /// the same 73, so the old tie-break (shortest name) opened the QR scanner.</summary>
    private static readonly AppEntry[] CatalogQr =
    [
        Packaged("QR Code Scanner", "Contoso.QrCodeScanner_8wekyb3d8bbwe"),
        Shortcut("Visual Studio Code"),
    ];

    [Theory]
    [InlineData("vs code")]
    [InlineData("VS CODE")]
    [InlineData("Vs Code")]
    public void Match_reads_vs_as_the_initials_of_Visual_Studio_not_a_tie_with_QR_Code_Scanner(string request)
    {
        var match = AppCatalog.Match(CatalogQr, request);

        match.Entry.Name.Should().Be("Visual Studio Code",
            "'vs' is the initials of 'Visual Studio'; QR Code Scanner shares only 'code' with the request");
        match.Strategy.Should().Be("fuzzy", "an abbreviation is a fuzzy-stage score, not a new strategy");
        match.Score.Should().Be(95, "a covering abbreviation scores 95 - above the 73 both names get from the two scorers");
    }

    [Fact]
    public void Match_finds_an_app_by_the_initials_of_its_whole_name()
    {
        // "vsc" scores 67 against Visual Studio Code and 67 against QR Code Scanner on the two
        // scorers - a miss today. Its letters are the name's initials.
        var match = AppCatalog.Match(CatalogQr, "vsc");

        match.Entry.Name.Should().Be("Visual Studio Code");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(95);
    }

    [Fact]
    public void Match_finds_Windows_Terminal_by_wt_and_not_WordPad()
    {
        // Both score 50 on the two scorers, under the floor - a miss today. "wt" is the initials
        // of "Windows Terminal"; "WordPad" is one word and has no two-word initials.
        var catalog = new[] { Packaged("Windows Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe"), Shortcut("WordPad") };

        var match = AppCatalog.Match(catalog, "wt");

        match.Entry.Name.Should().Be("Windows Terminal");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(95);
    }

    [Fact]
    public void Match_does_not_skip_a_leading_word_to_read_initials()
    {
        // Round 2 (strict rule): an abbreviation is anchored at the name's first token. Round 1
        // skipped "Microsoft" and scored this 95; now "vs" cannot read "microsoft", so the name
        // stands on the two scorers' 73, exactly as on main.
        var match = AppCatalog.Match([Shortcut("Microsoft Visual Studio Code")], "vs code");

        match.Entry.Name.Should().Be("Microsoft Visual Studio Code");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(73, "the abbreviation does not apply; max(PartialRatio 71, TokenSetRatio 73) is the score");
    }

    [Theory]
    [InlineData("VS Code", "exact")]
    [InlineData("VS Code Portable", "prefix")]
    public void Match_exact_and_prefix_still_run_before_an_abbreviation(string other, string strategy)
    {
        // Guard: the initials rule is part of the fuzzy stage. A name the request IS, or starts,
        // is not a guess and must not lose to "Visual Studio Code" at 95.
        var catalog = new[] { Shortcut("Visual Studio Code"), Shortcut(other) };

        var match = AppCatalog.Match(catalog, "vs code");

        match.Entry.Name.Should().Be(other);
        match.Strategy.Should().Be(strategy);
        match.Score.Should().Be(100);
    }

    [Fact]
    public void Match_an_abbreviation_at_95_loses_to_a_name_a_scorer_gives_100()
    {
        // Guard: 95 is a score inside max(), not a stage that preempts the scorers. "term" is the
        // initials of "Tiny Easy Remote Manager" (95) and a substring of "Windows Terminal" (100).
        var catalog = new[] { Shortcut("Tiny Easy Remote Manager"), Packaged("Windows Terminal") };

        var match = AppCatalog.Match(catalog, "term");

        match.Entry.Name.Should().Be("Windows Terminal");
        match.Score.Should().Be(100);
        match.Strategy.Should().Be("fuzzy");
    }

    [Fact]
    public void Match_ms_edge_keeps_its_scorer_score_because_ms_is_not_initials()
    {
        // Guard: "ms" is neither a prefix of "Microsoft" nor the initials "me", so the resolve
        // stands on the two scorers' 73 - an implementation that loosened the rule would say 95.
        var match = AppCatalog.Match([Shortcut("Microsoft Edge")], "ms edge");

        match.Entry.Name.Should().Be("Microsoft Edge");
        match.Score.Should().Be(73);
    }

    [Fact]
    public void Match_a_request_with_no_tokens_is_still_a_miss()
    {
        // Guard: "---" passes the blank-name check but has no tokens. "Every token is covered" is
        // vacuously true of none; read that way, every entry scores 95 and launch("---") opens
        // Notepad (the shortest name). It must stay the miss it is on the two scorers.
        var act = () => AppCatalog.Match(CatalogA, "---");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*---*");
    }

    // ---- Match: the strict abbreviation rule on real Start Menu names (round 2) -----------------
    //
    // Every row below is a name review-agent found on a real catalog, where the round-1 rule
    // turned a refusal into a confident launch of the wrong app, or picked between two apps by
    // name length. Scores were checked against a reference walk of the rule and the scorers.

    private static AppEntry[] Catalog(params string[] names) => names.Select(n => Shortcut(n)).ToArray();

    [Fact]
    public void Match_refuses_wt_when_it_abbreviates_Windows_Terminal_and_Windows_Tools_equally()
    {
        // Both read "wt" completely (95); WordPad does not (50). Round 1 broke the tie by length and
        // opened Windows Tools. A tie that only the abbreviation created is a question for the
        // model, not a coin toss - and it is an ArgumentException (the request is ambiguous), NOT a
        // KeyNotFoundException: the service must not refresh the catalog looking for a better one.
        var act = () => AppCatalog.Match(Catalog("Windows Terminal", "Windows Tools", "WordPad"), "wt");

        var message = act.Should().ThrowExactly<ArgumentException>().Which.Message;
        message.Should().Contain("'wt'", "the model needs to see the request it sent");
        message.Should().Contain("'Windows Terminal' (95)").And.Contain("'Windows Tools' (95)",
            "every tied entry is named with its score, so the model can pick one and ask again");
        message.Should().NotContain("WordPad", "only the tied entries are candidates");
        message.Should().NotContain("(Parameter",
            "launch's parameter is app_name: an ArgumentException parameter suffix naming 'name' would mislead the model");
    }

    [Fact]
    public void Match_refuses_a_tie_between_a_scorer_95_and_an_abbreviation_95()
    {
        // The mixed case: "VZ Codebase Explorer" is one character off the request (PartialRatio and
        // TokenSetRatio 95); "Visual Studio Codebase Explorer" reaches 95 only by reading "vs" as
        // its initials (its scorers give 92). One of the two tied entries got there through the
        // abbreviation, so the tie is refused instead of going to the shorter name.
        // (Constructed - no natural Start Menu pair with a scorer at exactly 95 was found.)
        var act = () => AppCatalog.Match(
            Catalog("Visual Studio Codebase Explorer", "VZ Codebase Explorer"), "vs codebase explorer");

        var message = act.Should().ThrowExactly<ArgumentException>().Which.Message;
        message.Should().Contain("'vs codebase explorer'")
            .And.Contain("'Visual Studio Codebase Explorer' (95)")
            .And.Contain("'VZ Codebase Explorer' (95)");
    }

    [Fact]
    public void Match_an_abbreviation_that_only_equals_the_scorers_does_not_make_a_tie_ambiguous()
    {
        // The boundary of "reaches the top score ONLY through the abbreviation (its scorers are
        // lower)". "vs code" reads this long name with 22 tokens unread: 95 - 22 = 73, exactly what
        // its scorers give anyway. So both tied entries are scorer entries, and the tie keeps the
        // shortest-name rule from main - no refusal. (Constructed; real names are not this long.)
        var longName = "Visual Studio Code " + string.Join(' ', Enumerable.Repeat("x", 22));
        var match = AppCatalog.Match(Catalog("QR Code Scanner", longName), "vs code");

        match.Entry.Name.Should().Be("QR Code Scanner", "a scorer-only tie goes to the shorter name, as before");
        match.Score.Should().Be(73);
    }

    [Fact]
    public void Match_vs_code_takes_Visual_Studio_Code_over_its_Insiders_build_without_refusing()
    {
        // "vs code" reads both, but Insiders leaves one token unread (94): no tie, no refusal.
        var match = AppCatalog.Match(
            Catalog("Visual Studio Code", "Visual Studio Code Insiders", "QR Code Scanner"), "vs code");

        match.Entry.Name.Should().Be("Visual Studio Code");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(95);
    }

    [Fact]
    public void Match_scores_an_abbreviation_95_less_one_per_unread_trailing_token()
    {
        var match = AppCatalog.Match(Catalog("Visual Studio Code Insiders", "QR Code Scanner"), "vs code");

        match.Entry.Name.Should().Be("Visual Studio Code Insiders");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(94, "one trailing token ('insiders') is left unread: 95 - 1");
    }

    [Theory]
    // (request, the catalog, the entry it must resolve to, its score)
    [InlineData("mps", new[] { "Maps", "VLC media player skinned" }, "Maps", 86)]                     // round 1: VLC (initials of tokens 2-4)
    [InlineData("dcp", new[] { "dhcp", "Developer Command Prompt for VS" }, "dhcp", 86)]               // round 1: the prompt (initials stop short)
    [InlineData("sm", new[] { "Server Manager", "SQL Server Management Studio" }, "Server Manager", 95)]
    [InlineData("mt", new[] { "Movies & TV", "Microsoft To Do" }, "Movies & TV", 95)]
    public void Match_resolves_real_start_menu_names_the_strict_rule_reads(
        string request, string[] names, string expected, int score)
    {
        var match = AppCatalog.Match(Catalog(names), request);

        match.Entry.Name.Should().Be(expected);
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(score);
    }

    [Theory]
    [InlineData("7z", new[] { "7-Zip File Manager", "7-Zip Help" })]   // initials must reach the last token: a miss, as on main
    [InlineData("uy", new[] { "Uninstall YubiKey Manager", "Notepad" })] // round 1: Uninstall YubiKey Manager
    [InlineData("16", new[] { "Python 3.13 (64-bit)" })]                // round 1: Python ([1]3 [6]4)
    public void Match_refuses_a_name_the_strict_rule_does_not_read(string request, string[] names)
    {
        var act = () => AppCatalog.Match(Catalog(names), request);

        act.Should().ThrowExactly<KeyNotFoundException>().WithMessage($"*'{request}'*",
            "nothing reads the request and no scorer reaches 70: opening an uninstaller is worse than saying no");
    }
}
