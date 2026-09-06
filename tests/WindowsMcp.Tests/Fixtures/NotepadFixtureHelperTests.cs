using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// The parts of <see cref="NotepadFixture"/> that decide WHAT it closes and WHAT it deletes,
/// tested without a desktop and without Notepad. These are the decisions that go wrong
/// destructively - closing a window that belongs to someone else, invoking "Save" instead of
/// "Don't save", deleting a tab-state file the user's own session needs - and none of them can be
/// observed from the <c>UIAutomation</c> self-test, which can only report the outcome after the
/// fact. Everything here is <c>Category=Unit</c>: it runs headless, in milliseconds, on every run.
/// </summary>
[Trait("Category", "Unit")]
public class NotepadFixtureHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wmcp-tabstate-" + Guid.NewGuid().ToString("N"));

    public NotepadFixtureHelperTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "x");

    // ---- TabStateFiles: the "before" snapshot dispose has to restore ---------------------------

    [Fact]
    public void TabStateFiles_on_a_missing_directory_is_empty_not_an_error()
    {
        // Classic Notepad, or a machine that has never opened the modern one: the fixture must
        // still construct and still dispose.
        var missing = Path.Combine(_dir, "no-such-folder");

        NotepadFixture.TabStateFiles(missing).Should().BeEmpty(
            "a machine with no TabState folder has no tabs to leak, which is not an error");
    }

    [Fact]
    public void TabStateFiles_returns_bare_names_of_every_file_in_the_folder()
    {
        Touch("11111111-1111-1111-1111-111111111111.bin");
        Touch("22222222-2222-2222-2222-222222222222.0.bin");
        Touch("notes.txt");   // whatever else lives there is part of the set that must come back

        var files = NotepadFixture.TabStateFiles(_dir);

        files.Should().BeEquivalentTo(
            new[] { "11111111-1111-1111-1111-111111111111.bin",
                    "22222222-2222-2222-2222-222222222222.0.bin",
                    "notes.txt" },
            "the set is compared by NAME, so it must hold names and not full paths");
    }

    [Fact]
    public void TabStateFiles_compares_names_case_insensitively()
    {
        Touch("AAAA1111.bin");

        NotepadFixture.TabStateFiles(_dir).Contains("aaaa1111.BIN").Should().BeTrue(
            "NTFS is case-insensitive, so a case flip must not look like a new file to delete");
    }

    // ---- NewTabStateFiles: exactly what dispose is allowed to delete ---------------------------

    [Fact]
    public void NewTabStateFiles_names_only_what_appeared()
    {
        var before = NotepadFixture.TabStateFiles(_dir);
        Touch("bbbb.bin");
        Touch("aaaa.bin");

        var added = NotepadFixture.NewTabStateFiles(before, NotepadFixture.TabStateFiles(_dir));

        added.Should().Equal("aaaa.bin", "bbbb.bin");
    }

    [Fact]
    public void NewTabStateFiles_is_empty_when_the_folder_came_back_unchanged()
    {
        Touch("kept.bin");
        var before = NotepadFixture.TabStateFiles(_dir);

        NotepadFixture.NewTabStateFiles(before, NotepadFixture.TabStateFiles(_dir)).Should().BeEmpty(
            "nothing appeared, so dispose must delete nothing at all");
    }

    [Fact]
    public void NewTabStateFiles_ignores_files_that_disappeared()
    {
        Touch("was-here.bin");
        var before = NotepadFixture.TabStateFiles(_dir);
        File.Delete(Path.Combine(_dir, "was-here.bin"));

        NotepadFixture.NewTabStateFiles(before, NotepadFixture.TabStateFiles(_dir)).Should().BeEmpty(
            "the sweep only deletes; a file the user closed themselves is not the fixture's to restore");
    }

    [Fact]
    public void NewTabStateFiles_treats_a_case_flipped_name_as_the_same_file()
    {
        Touch("KEPT.BIN");
        var before = NotepadFixture.TabStateFiles(_dir);

        var after = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kept.bin" };

        NotepadFixture.NewTabStateFiles(before, after).Should().BeEmpty(
            "deleting the user's own tab because Windows reported a different case would be the "
            + "worst possible failure of this sweep");
    }

    // ---- the save prompt: "Don't save" and nothing else ---------------------------------------

    [Theory]
    [InlineData("Don't save")]                 // U+0027
    [InlineData("Don\u2019t save")]            // U+2019, the spelling XAML dialogs usually carry
    [InlineData("Don\u02bct save")]            // U+02BC, seen in some localised resources
    [InlineData("DON'T SAVE")]
    [InlineData("Don't Save")]
    [InlineData("Do not save")]
    [InlineData("  Don\u2019t save  ")]
    public void IsDiscardButtonName_accepts_every_spelling_of_the_discard_button(string name)
        => NotepadFixture.IsDiscardButtonName(name).Should().BeTrue();

    [Theory]
    [InlineData("Save")]
    [InlineData("Save as")]
    [InlineData("Cancel")]
    [InlineData("Close")]
    [InlineData("Save changes to Untitled?")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsDiscardButtonName_rejects_everything_else_on_the_prompt(string? name)
        => NotepadFixture.IsDiscardButtonName(name).Should().BeFalse(
            "invoking 'Save' or 'Cancel' would write the test's rubbish to disk or hang the dispose");

    [Fact]
    public void DiscardButtonNames_are_all_names_the_predicate_also_accepts()
    {
        // The fixture asks UIA for these EXACT names first and falls back to the predicate sweep;
        // if the two disagreed, the fast path could invoke something the predicate would reject.
        NotepadFixture.DiscardButtonNames.Should().NotBeEmpty()
            .And.OnlyContain(n => NotepadFixture.IsDiscardButtonName(n));
        NotepadFixture.DiscardButtonNames.Should().Contain(n => n.Contains('\''))
            .And.Contain(n => n.Contains('\u2019'),
                "the live dialog spells the apostrophe differently between builds, so both go to UIA");
    }

    // ---- the tab close button, only ever used inside one tab's subtree -------------------------

    [Theory]
    [InlineData("Close tab", true)]
    [InlineData("Close all tabs", true)]
    [InlineData("Close", true)]
    [InlineData("CLOSE TAB", true)]
    [InlineData("Save", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCloseTabButtonName_matches_a_close_button_and_nothing_else(string? name, bool expected)
        => NotepadFixture.IsCloseTabButtonName(name).Should().Be(expected);

    // ---- which window (or tab) the launch produced ---------------------------------------------

    private const string File1 = @"C:\Temp\wmcp-close-a1b2c3d4.txt";

    [Theory]
    [InlineData("wmcp-close-a1b2c3d4.txt - Notepad", true)]     // saved
    [InlineData("*wmcp-close-a1b2c3d4.txt - Notepad", true)]    // dirty
    [InlineData("wmcp-close-a1b2c3d4 - Notepad", true)]         // modern Notepad drops the extension
    [InlineData("*wmcp-close-a1b2c3d4 - Notepad", true)]
    [InlineData("WMCP-CLOSE-A1B2C3D4.TXT - Notepad", true)]
    [InlineData("Untitled - Notepad", false)]
    [InlineData("wmcp-close-99999999.txt - Notepad", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TitleNamesFile_recognises_the_title_notepad_gives_our_file(string? title, bool expected)
        => NotepadFixture.TitleNamesFile(title, File1).Should().Be(expected);

    [Fact]
    public void TitleNamesFile_is_false_when_no_file_was_opened()
        => NotepadFixture.TitleNamesFile("Untitled - Notepad", null).Should().BeFalse(
            "the no-file fixture has no title to search for and must never claim a window by name");

    private static WindowInfo Win(string title, long hwnd, int zOrder = 0, int pid = 4242) =>
        new(title, hwnd, pid, "notepad", WindowState.Normal,
            new Bounds(0, 0, 800, 600), zOrder, false, false, 0);

    [Fact]
    public void SelectOpenedWindow_with_no_file_takes_the_window_that_is_new()
    {
        var before = new HashSet<long> { 100 };
        var now = new[] { Win("Untitled - Notepad", 100), Win("Untitled - Notepad", 200) };

        NotepadFixture.SelectOpenedWindow(before, now, null)!.Hwnd.Should().Be(200);
    }

    [Fact]
    public void SelectOpenedWindow_with_no_file_never_claims_a_pre_existing_window()
    {
        // Two windows called "Untitled - Notepad" cannot be told apart, so a fixture that opened
        // no file and produced no new window owns NOTHING and must close nothing.
        var before = new HashSet<long> { 100, 200 };
        var now = new[] { Win("Untitled - Notepad", 100), Win("Untitled - Notepad", 200) };

        NotepadFixture.SelectOpenedWindow(before, now, null).Should().BeNull();
    }

    [Fact]
    public void SelectOpenedWindow_with_no_file_prefers_the_frontmost_new_window()
    {
        var before = new HashSet<long>();
        var now = new[] { Win("Untitled - Notepad", 300, zOrder: 5), Win("Untitled - Notepad", 400, zOrder: 1) };

        NotepadFixture.SelectOpenedWindow(before, now, null)!.Hwnd.Should().Be(400,
            "z-order 1 is in front of z-order 5, and the launch just brought our window forward");
    }

    [Fact]
    public void SelectOpenedWindow_with_a_file_takes_the_new_window_titled_after_it()
    {
        var before = new HashSet<long> { 100 };
        var now = new[]
        {
            Win("Untitled - Notepad", 100),
            Win("Untitled - Notepad", 500),                        // someone else's new window
            Win("wmcp-close-a1b2c3d4.txt - Notepad", 600),
        };

        NotepadFixture.SelectOpenedWindow(before, now, File1)!.Hwnd.Should().Be(600,
            "the title is the only thing that distinguishes OUR window from another new one");
    }

    [Fact]
    public void SelectOpenedWindow_with_a_file_finds_the_tab_opened_in_a_pre_existing_window()
    {
        // The modern Notepad case that broke WindowCloseDesktopTests: no new hwnd EVER appears,
        // the file becomes a tab in the window that was already there and its title changes.
        var before = new HashSet<long> { 100 };
        var now = new[] { Win("*wmcp-close-a1b2c3d4.txt - Notepad", 100) };

        var picked = NotepadFixture.SelectOpenedWindow(before, now, File1);

        picked.Should().NotBeNull("the fixture must find its tab or it can never clean it up");
        picked!.Hwnd.Should().Be(100);
    }

    [Fact]
    public void SelectOpenedWindow_with_a_file_prefers_a_new_window_over_a_retitled_old_one()
    {
        var before = new HashSet<long> { 100 };
        var now = new[]
        {
            Win("wmcp-close-a1b2c3d4.txt - Notepad", 100, zOrder: 0),   // stale title on an old window
            Win("wmcp-close-a1b2c3d4.txt - Notepad", 700, zOrder: 3),
        };

        NotepadFixture.SelectOpenedWindow(before, now, File1)!.Hwnd.Should().Be(700,
            "a window of our own is safe to close outright; a shared one is not");
    }

    [Fact]
    public void SelectOpenedWindow_with_a_file_returns_null_while_no_title_names_it()
    {
        // Deliberately strict: the caller polls, and claiming an unrelated new window would make
        // dispose close somebody else's Notepad.
        var before = new HashSet<long> { 100 };
        var now = new[] { Win("Untitled - Notepad", 100), Win("Untitled - Notepad", 800) };

        NotepadFixture.SelectOpenedWindow(before, now, File1).Should().BeNull();
    }

    [Fact]
    public void SelectOpenedWindow_on_an_empty_desktop_is_null()
        => NotepadFixture.SelectOpenedWindow(new HashSet<long>(), [], File1).Should().BeNull();

    // ---- when the process may be killed --------------------------------------------------------

    [Fact]
    public void MayTerminateNotepad_only_when_every_condition_holds()
        => NotepadFixture.MayTerminateNotepad(
            soleOwner: true, lastFixtureAlive: true, ourWindowGone: true,
            anyNotepadWindowRemains: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, true, false)]   // Notepad was already running: not ours to kill
    [InlineData(true, false, true, false)]   // another fixture is still using the process
    [InlineData(true, true, false, false)]   // our own window is still on screen
    [InlineData(true, true, true, true)]     // some other Notepad window appeared meanwhile
    public void MayTerminateNotepad_refuses_when_any_condition_fails(
        bool soleOwner, bool last, bool gone, bool remains)
        => NotepadFixture.MayTerminateNotepad(soleOwner, last, gone, remains).Should().BeFalse(
            "killing notepad.exe destroys unsaved work in windows this fixture never opened");
}
