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
    // request, the entry it must resolve to, the score max(PartialRatio, TokenSetRatio) gives
    [InlineData("edge", "Microsoft Edge", 100)]
    [InlineData("code", "Visual Studio Code", 100)]
    [InlineData("vs code", "Visual Studio Code", 73)]
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
}
