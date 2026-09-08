using FluentAssertions;
using WindowsMcp.Hosting;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// C-6 (roadmap R9): the pure <c>Path</c> merge. Every rule here is a promise made to a host that
/// launched us with a stripped block — <b>nothing it set is removed or reordered</b>, and the
/// registry only ever lands behind it — so each rule gets its own row rather than one composite
/// assertion that would still pass if the order were wrong.
/// </summary>
[Trait("Category", "Unit")]
public class PathMergeTests
{
    // ---- Merge: order --------------------------------------------------------------------

    [Fact]
    public void Merge_puts_the_hosts_entries_first_and_as_written()
        => PathMerge.Merge(@"C:\host-a;C:\host-b", @"C:\Windows\System32", @"C:\Users\x\bin")
            .Should().Be(@"C:\host-a;C:\host-b;C:\Windows\System32;C:\Users\x\bin",
                "the host's Path may be deliberate: it keeps its entries, its spelling and its order");

    [Fact]
    public void Merge_appends_the_machine_value_before_the_user_value()
        => PathMerge.Merge(null, @"C:\m1;C:\m2", @"C:\u1;C:\u2")
            .Should().Be(@"C:\m1;C:\m2;C:\u1;C:\u2", "Windows' own merge rule is machine then user");

    [Fact]
    public void Merge_of_an_empty_host_has_no_leading_separator()
    {
        var merged = PathMerge.Merge("", @"C:\Windows\System32", null);

        merged.Should().Be(@"C:\Windows\System32");
        merged.Should().NotStartWith(";", "a leading separator is an empty entry every consumer has to skip");
    }

    [Fact]
    public void Merge_with_nothing_to_append_returns_the_host_unchanged()
        => PathMerge.Merge(@"C:\host-a;C:\host-b", null, null).Should().Be(@"C:\host-a;C:\host-b");

    [Fact]
    public void Merge_of_nothing_at_all_is_empty()
        => PathMerge.Merge(null, null, null).Should().BeEmpty();

    // ---- Merge: de-duplication -------------------------------------------------------------

    [Fact]
    public void Merge_drops_a_duplicate_that_differs_only_by_case()
        => PathMerge.Merge(@"C:\Windows\System32", @"c:\windows\system32", null)
            .Should().Be(@"C:\Windows\System32", "paths are case-insensitive; the first spelling wins");

    [Fact]
    public void Merge_drops_a_duplicate_that_differs_only_by_a_trailing_backslash()
        => PathMerge.Merge(@"C:\Tools", @"C:\Tools\", null).Should().Be(@"C:\Tools");

    [Fact]
    public void Merge_drops_a_duplicate_that_differs_only_by_a_trailing_forward_slash()
        => PathMerge.Merge(@"C:\Tools", "C:\\Tools/", null)
            .Should().Be(@"C:\Tools", "a trailing separator is not a different directory, whichever slash it is");

    [Fact]
    public void Merge_keeps_the_first_spelling_even_when_it_is_the_one_with_the_separator()
        => PathMerge.Merge(@"C:\Tools\", @"C:\Tools", null).Should().Be(@"C:\Tools\",
            "nothing the host set is rewritten - only what follows it is filtered");

    [Fact]
    public void Merge_drops_a_duplicate_that_differs_only_by_surrounding_quotes()
        => PathMerge.Merge("\"C:\\Tools\"", @"C:\Tools", null).Should().Be(@"C:\Tools",
            "a quoted entry names the same directory, and the quotes are not part of it");

    [Fact]
    public void Merge_de_duplicates_within_the_registry_values_too()
        => PathMerge.Merge(null, @"C:\a;C:\a\", @"C:\A").Should().Be(@"C:\a");

    // ---- Merge: empties --------------------------------------------------------------------

    [Fact]
    public void Merge_drops_empty_and_whitespace_only_entries()
        => PathMerge.Merge(@"C:\a;; ;C:\b", "   ;C:\\c", ";").Should().Be(@"C:\a;C:\b;C:\c");

    [Fact]
    public void Merge_trims_the_whitespace_around_an_entry()
        => PathMerge.Merge(@"  C:\a  ; C:\b ", null, null).Should().Be(@"C:\a;C:\b");

    // ---- HasSystem32 -----------------------------------------------------------------------

    [Theory]
    // A known SystemRoot: the entry has to BE <SystemRoot>\System32, however it is spelled.
    [InlineData(@"C:\Windows\System32", @"C:\Windows", true)]
    [InlineData(@"c:\windows\system32", @"C:\Windows", true)]
    [InlineData(@"c:\windows\system32\", @"C:\Windows", true)]
    [InlineData("\"C:\\Windows\\System32\"", @"C:\Windows", true)]
    [InlineData(@"C:\nothing;C:\Windows\System32;C:\Tools", @"C:\Windows", true)]
    [InlineData(@"C:\Windows\System32", @"C:\Windows\", true)]           // a SystemRoot with its own separator
    [InlineData(@"C:\Windows\System32\Wbem", @"C:\Windows", false)]      // a directory UNDER it is not it
    [InlineData(@"C:\Other\System32", @"C:\Windows", false)]             // somebody else's System32
    [InlineData(@"C:\Windows", @"C:\Windows", false)]
    [InlineData(@"C:\nothing", @"C:\Windows", false)]
    [InlineData("", @"C:\Windows", false)]
    [InlineData(null, @"C:\Windows", false)]
    // No SystemRoot to compare against: any entry whose LAST segment is System32 counts.
    [InlineData(@"D:\x\System32", null, true)]
    [InlineData(@"D:\x\system32\", null, true)]
    [InlineData(@"D:\x\System32\Wbem", null, false)]
    [InlineData(@"C:\nothing", null, false)]
    [InlineData(null, null, false)]
    // An entry with no separator at all is its own last segment.
    [InlineData("System32", null, true)]
    [InlineData("system32", null, true)]
    [InlineData("bin", null, false)]
    // A whitespace-only SystemRoot is "not known", so the last-segment rule applies.
    [InlineData(@"D:\x\System32", "   ", true)]
    [InlineData(@"C:\nothing", "   ", false)]
    public void HasSystem32_recognises_the_system_directory(string? path, string? systemRoot, bool expected)
        => PathMerge.HasSystem32(path, systemRoot).Should().Be(expected);

    // ---- Split -----------------------------------------------------------------------------

    [Fact]
    public void Split_trims_unquotes_and_drops_empties_and_leaves_the_rest_as_written()
        => PathMerge.Split(" C:\\a ; \"C:\\b\" ;;  ;C:\\c\\ ")
            .Should().Equal(@"C:\a", @"C:\b", @"C:\c\");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(";; ;  ")]
    public void Split_of_nothing_is_an_empty_array(string? path)
        => PathMerge.Split(path).Should().BeEmpty();

    [Fact]
    public void Split_keeps_the_case_and_the_separators_inside_an_entry()
        => PathMerge.Split(@"C:\Program Files\Git\cmd").Should().Equal(@"C:\Program Files\Git\cmd");
}
