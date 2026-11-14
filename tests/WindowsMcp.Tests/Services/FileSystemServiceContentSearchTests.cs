using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-9: <see cref="FileSystemService.SearchContentAsync"/> over a real tree in a fresh temp directory
/// (docs/design/E-9-file-edit-content-search.md, "file_search content mode" / "Reading"). The line
/// scan itself is pinned in <see cref="ContentSearchTests"/>; what is proved here is the walk around
/// it - which files are read, which are skipped and counted, that links are not followed, that one
/// bad file or directory does not end the search, and that the cap and the token hold across files.
/// Nothing outside <c>_tmp</c> is touched.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemServiceContentSearchTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-grep-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public FileSystemServiceContentSearchTests()
    {
        _root = Path.Combine(_tmp, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            RemoveLinks(_tmp);   // the loop junction and the one to "outside", as links
            Directory.Delete(_tmp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: Dispose must not throw
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Removes every directory link under <paramref name="dir"/> as a link (non-recursive delete),
    /// never descending into one. The framework's recursive delete trips on a junction and the
    /// directory would be left behind.
    /// </summary>
    private static void RemoveLinks(string dir)
    {
        foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) sub.Delete();
            else RemoveLinks(sub.FullName);
        }
    }

    private static FileSystemService Svc() => new();

    private string File_(string relative, string content) => File_(relative, new UTF8Encoding(false).GetBytes(content));

    private string File_(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ContentSearchRequest Request(string content, string? pattern = null, long? minSize = null,
        DateTime? modifiedSince = null, int maxMatches = 100, int contextLines = 0) =>
        new(_root, content, pattern, minSize, modifiedSince, maxMatches, contextLines);

    // ==== what is found ==============================================================================

    [Fact]
    public async Task SearchContentAsync_finds_matching_lines_across_the_tree()
    {
        var a = File_("a.txt", "alpha\nneedle one\ngamma\nneedle two\n");
        var b = File_(Path.Combine("sub", "b.log"), "needle three\n");
        File_(Path.Combine("sub", "c.txt"), "nothing here\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().HaveCount(3);
        result.Matches.Where(m => m.Path == a).Select(m => (m.Line, m.Text))
            .Should().Equal((2, "needle one"), (4, "needle two"));
        result.Matches.Where(m => m.Path == b).Select(m => (m.Line, m.Text))
            .Should().Equal((1, "needle three"));
        result.FilesSearched.Should().Be(3);
        result.FilesSkipped.Should().Be(0);
        result.Truncated.Should().BeFalse();
        result.MaxMatches.Should().Be(100);
    }

    /// <summary>"Matches are in enumeration order, then line order": one file's matches are together, in line order.</summary>
    [Fact]
    public async Task SearchContentAsync_groups_each_files_matches_in_line_order()
    {
        File_("a.txt", "needle\nx\nneedle\nneedle\n");
        File_("b.txt", "needle\nneedle\n");
        File_(Path.Combine("s", "c.txt"), "x\nneedle\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        var paths = result.Matches.Select(m => m.Path).ToArray();
        var runs = paths.Where((p, i) => i == 0 || paths[i - 1] != p).ToArray();
        runs.Should().OnlyHaveUniqueItems("a file's matches are reported together, not interleaved with another file's");
        foreach (var group in result.Matches.GroupBy(m => m.Path))
            group.Select(m => m.Line).Should().BeInAscendingOrder();
    }

    /// <summary>"The regex is matched per line (the line without its terminator)".</summary>
    [Fact]
    public async Task SearchContentAsync_matches_crlf_lines_without_their_terminator()
    {
        File_("win.ini", "[s]\r\nneedle one\r\nlast\r\n");

        var result = await Svc().SearchContentAsync(Request("one$"));

        result.Matches.Should().ContainSingle().Which.Text.Should().Be("needle one");
    }

    /// <summary>
    /// "CultureInvariant": under tr-TR, I and i are not a case pair (I↔ı, İ↔i), so a culture-sensitive
    /// <c>(?i)FILE</c> misses <c>file</c> - on a Turkish-locale server every case-insensitive search
    /// with an i in it would quietly find less.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_is_culture_invariant_under_a_turkish_culture()
    {
        File_("t.txt", "config file path\n");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var result = await Svc().SearchContentAsync(Request("(?i)FILE"));

            result.Matches.Should().ContainSingle("(?i) in the pattern is a culture-invariant case-insensitive match");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task SearchContentAsync_returns_context_when_asked()
    {
        File_("ctx.txt", "l1\nl2\nneedle\nl4\nl5\n");

        var result = await Svc().SearchContentAsync(Request("needle", contextLines: 1));

        var match = result.Matches.Should().ContainSingle().Which;
        match.Before.Should().Equal("l2");
        match.After.Should().Equal("l4");
    }

    [Fact]
    public async Task SearchContentAsync_without_context_leaves_before_and_after_null()
    {
        File_("ctx.txt", "l1\nneedle\nl3\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches[0].Before.Should().BeNull();
        result.Matches[0].After.Should().BeNull();
    }

    // ==== which files are read: pattern, min_size, modified_since ===================================

    /// <summary>
    /// "With content, the existing pattern (name glob), min_size and modified_since still choose
    /// which files are read." A file the filters leave out is neither searched nor skipped.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_reads_only_files_the_name_glob_chooses()
    {
        var a = File_("a.txt", "needle\n");
        File_("b.log", "needle\n");
        var c = File_(Path.Combine("sub", "c.txt"), "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle", pattern: "*.txt"));

        result.Matches.Select(m => m.Path).Should().BeEquivalentTo(new[] { a, c });
        result.FilesSearched.Should().Be(2);
        result.FilesSkipped.Should().Be(0, "a file the glob leaves out is not a skipped file");
    }

    [Fact]
    public async Task SearchContentAsync_reads_only_files_of_at_least_min_size()
    {
        File_("small.txt", "needle\n");
        var big = File_("big.txt", "needle\n" + new string('x', 200) + "\n");

        var result = await Svc().SearchContentAsync(Request("needle", minSize: 100));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(big);
        result.FilesSearched.Should().Be(1);
        result.FilesSkipped.Should().Be(0);
    }

    [Fact]
    public async Task SearchContentAsync_reads_only_files_modified_since()
    {
        var old = File_("old.txt", "needle\n");
        File.SetLastWriteTimeUtc(old, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = File_("fresh.txt", "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle", modifiedSince: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(fresh);
        result.FilesSearched.Should().Be(1);
    }

    // ==== what is skipped and counted ================================================================

    /// <summary>"A file is skipped (counted in filesSkipped) when ... binary: no BOM and a NUL byte in its first 8 KiB."</summary>
    [Fact]
    public async Task SearchContentAsync_skips_and_counts_a_binary_file()
    {
        File_("tool.exe", Encoding.ASCII.GetBytes("MZ\0\0 needle in a binary\n"));
        var text = File_("notes.txt", "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(text);
        result.FilesSkipped.Should().Be(1);
        result.FilesSearched.Should().Be(1, "a skipped file is not counted as searched");
    }

    [Fact]
    public async Task SearchContentAsync_reads_a_file_whose_only_nul_is_past_the_first_8_KiB()
    {
        var bytes = new byte[ContentSearch.BinaryProbeBytes + 100];
        Array.Fill(bytes, (byte)'x');
        "needle\n"u8.CopyTo(bytes);
        bytes[ContentSearch.BinaryProbeBytes + 10] = 0;
        var path = File_("late-nul.txt", bytes);

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(path);
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>"Text is decoded by BOM (UTF-8, UTF-16 LE/BE, UTF-32 LE)": UTF-16 is full of NULs and is still text.</summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    public async Task SearchContentAsync_decodes_a_file_by_its_bom(string kind)
    {
        Encoding enc = kind switch
        {
            "utf-8" => new UTF8Encoding(true),
            "utf-16le" => new UnicodeEncoding(false, true),
            "utf-16be" => new UnicodeEncoding(true, true),
            _ => new UTF32Encoding(false, true),
        };
        var path = File_("bom.txt", enc.GetPreamble().Concat(enc.GetBytes("first\r\nthe needle é\r\n")).ToArray());

        var result = await Svc().SearchContentAsync(Request("needle"));

        var match = result.Matches.Should().ContainSingle().Which;
        match.Path.Should().Be(path);
        match.Line.Should().Be(2);
        match.Text.Should().Be("the needle é");
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>"else UTF-8 - leniently: this only reads." An invalid byte on one line does not skip the file.</summary>
    [Fact]
    public async Task SearchContentAsync_reads_invalid_utf8_leniently()
    {
        var path = File_("legacy.ini", "caf"u8.ToArray().Concat(new byte[] { 0xE9, (byte)'\n' }).Concat("needle\n"u8.ToArray()).ToArray());

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Should().Match<ContentMatch>(m => m.Path == path && m.Line == 2);
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>"larger than 16 MiB" is skipped and counted - a text file, so only the size check can skip it.</summary>
    [Fact]
    public async Task SearchContentAsync_skips_and_counts_a_file_over_16_MiB()
    {
        File_("huge.log", BigText(ContentSearch.MaxFileBytes + 1));
        var small = File_("small.log", "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(small);
        result.FilesSkipped.Should().Be(1);
    }

    [Fact]
    public async Task SearchContentAsync_reads_a_file_of_exactly_16_MiB()
    {
        var path = File_("exact.log", BigText(ContentSearch.MaxFileBytes));

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(path);
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>A text file of exactly <paramref name="length"/> bytes: "needle" on line 1, then lines of x.</summary>
    private static byte[] BigText(long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        for (long i = 99; i < length; i += 100) bytes[i] = (byte)'\n';
        "needle\n"u8.CopyTo(bytes);
        return bytes;
    }

    /// <summary>"A file is skipped (counted in filesSkipped) when it cannot be opened".</summary>
    [Fact]
    public async Task SearchContentAsync_skips_and_counts_a_file_it_cannot_open()
    {
        var locked = File_("locked.txt", "needle\n");
        var open = File_("open.txt", "needle\n");
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(open);
        result.FilesSkipped.Should().Be(1);
    }

    /// <summary>"Files are enumerated recursively with inaccessible directories skipped" - one denied directory does not end the search.</summary>
    [Fact]
    public async Task SearchContentAsync_carries_on_past_a_directory_it_cannot_list()
    {
        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File_(Path.Combine("denied", "inside.txt"), "needle\n");
        var open = File_("open.txt", "needle\n");
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(denied);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            var result = await Svc().SearchContentAsync(Request("needle"));

            result.Matches.Should().ContainSingle().Which.Path.Should().Be(open);
        }
        finally
        {
            var restore = info.GetAccessControl();
            restore.RemoveAccessRuleSpecific(rule);
            info.SetAccessControl(restore);
        }
    }

    // ==== links are not followed =====================================================================

    /// <summary>"reparse points not followed": a junction to a tree outside the root is not searched.</summary>
    [JunctionFact]
    public async Task SearchContentAsync_does_not_follow_a_junction_out_of_the_root()
    {
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "needle outside\n");
        JunctionFactAttribute.TryCreateJunction(Path.Combine(_root, "link"), outside, out var error).Should().BeTrue(error);
        var inside = File_("inside.txt", "needle inside\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(inside);
    }

    /// <summary>"a junction loop must not hang a search": a junction to the root itself is not descended.</summary>
    [JunctionFact]
    public async Task SearchContentAsync_does_not_loop_through_a_junction_to_its_own_root()
    {
        var inside = File_("inside.txt", "needle\n");
        JunctionFactAttribute.TryCreateJunction(Path.Combine(_root, "loop"), _root, out var error).Should().BeTrue(error);

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle("the file is found once, not once per trip round the loop")
            .Which.Path.Should().Be(inside);
        result.FilesSearched.Should().Be(1);
    }

    // ==== the cap, cancellation and the regex timeout across files ==================================

    [Fact]
    public async Task SearchContentAsync_stops_at_max_matches_across_files()
    {
        File_("a.txt", "needle\nneedle\n");
        File_("b.txt", "needle\nneedle\n");
        File_("c.txt", "needle\nneedle\n");

        var result = await Svc().SearchContentAsync(Request("needle", maxMatches: 3));

        result.Matches.Should().HaveCount(3);
        result.Truncated.Should().BeTrue();
        result.MaxMatches.Should().Be(3);
    }

    /// <summary>E-9's decision: reaching the cap is truncated, even when no match is left unreported.</summary>
    [Fact]
    public async Task SearchContentAsync_exactly_at_the_cap_across_files_reports_truncated()
    {
        File_("a.txt", "needle\n");
        File_("b.txt", "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle", maxMatches: 2));

        result.Matches.Should().HaveCount(2);
        result.Truncated.Should().BeTrue("the search stopped at the cap; it does not read on to see whether more exist");
    }

    [Fact]
    public async Task SearchContentAsync_under_the_cap_across_files_is_not_truncated()
    {
        File_("a.txt", "needle\n");
        File_("b.txt", "needle\n");

        var result = await Svc().SearchContentAsync(Request("needle", maxMatches: 3));

        result.Matches.Should().HaveCount(2);
        result.Truncated.Should().BeFalse();
        result.FilesSearched.Should().Be(2);
    }

    /// <summary>
    /// The cap is one budget for the whole search, not per file: with a cap of 3 and a first file of
    /// 2 matches, the second file contributes exactly 1.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_gives_each_later_file_only_what_is_left_of_the_cap()
    {
        File_("a.txt", "needle\nneedle\n");
        File_(Path.Combine("sub", "b.txt"), "needle\nneedle\nneedle\n");

        var result = await Svc().SearchContentAsync(Request("needle", maxMatches: 3));

        result.Matches.Should().HaveCount(3);
        result.Truncated.Should().BeTrue();
    }

    /// <summary>
    /// R2 decision 7: "the search stops once the matched and context lines returned total 60,000
    /// characters" - one budget for the whole search. Three files of 100 x 400 characters are 40,000
    /// each, under the cap one by one and 120,000 together.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_shares_the_output_cap_across_files()
    {
        var line = "needle " + new string('x', 393);
        for (int f = 0; f < 3; f++)
            File_($"f{f}.log", string.Join("\n", Enumerable.Repeat(line, 100)) + "\n");

        var result = await Svc().SearchContentAsync(Request("needle", maxMatches: 1000));

        var chars = result.Matches.Sum(m => m.Text.Length);
        chars.Should().BeLessThanOrEqualTo(ContentSearch.MaxResultChars + ContentSearch.MaxLineChars + 3);
        result.Truncated.Should().BeTrue();
        result.Matches.Length.Should().BeLessThan(300);
    }

    /// <summary>
    /// R2 decision 4 REVERSES the silent drop: "a file symlink is skipped and counted" - not
    /// followed (its target is outside the root and must not be read), and not invisible either.
    /// </summary>
    [FileSymlinkFact]
    public async Task SearchContentAsync_skips_and_counts_a_file_symlink()
    {
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target.txt");
        File.WriteAllText(target, "needle outside\n");
        File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), target);
        var real = File_("real.txt", "needle real\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(real);
        result.FilesSkipped.Should().Be(1, "the link is counted as skipped");
        result.FilesSearched.Should().Be(1);
    }

    /// <summary>
    /// R2 decision 4: "a cloud file that is not on disk (Offline, RecallOnOpen or RecallOnDataAccess)
    /// is skipped and counted, so a search never downloads files." Offline is the one of the three a
    /// test can set on an ordinary NTFS file; its content must not be matched.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_skips_and_counts_an_offline_file_without_reading_it()
    {
        var cloud = File_("cloud.txt", "needle cloud\n");
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
        (File.GetAttributes(cloud) & FileAttributes.Offline).Should().NotBe(0, "precondition: NTFS keeps the Offline bit");
        var disk = File_("disk.txt", "needle disk\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(disk);
        result.FilesSkipped.Should().Be(1);
        result.FilesSearched.Should().Be(1);
    }

    /// <summary>
    /// R2 decision 11: "hidden and system files are read" - the walk skips nothing by attribute
    /// except what it counts (links, cloud files).
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_reads_hidden_and_system_files()
    {
        var hidden = File_("hidden.cfg", "needle hidden\n");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = File_("system.cfg", "needle system\n");
        File.SetAttributes(system, FileAttributes.System);

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Select(m => m.Path).Should().BeEquivalentTo(new[] { hidden, system });
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>
    /// Second review pass, decision C (from the GREEN round 2 finding): in a content search
    /// "pattern is a file-name glob. One containing \ or / is refused (ArgumentException naming
    /// pattern: put the folder in root)" - before any file is opened. Before: the walk matched the
    /// pattern against each file name, found nothing, and answered "no matches".
    /// </summary>
    [Theory]
    [InlineData("sub", "*.txt")]       // joined with the platform separator
    [InlineData("sub/*.txt", null)]
    [InlineData("../*.txt", null)]
    public async Task SearchContentAsync_refuses_a_pattern_with_a_directory_in_it(string first, string? second)
    {
        var pattern = second is null ? first : Path.Combine(first, second);
        File_(Path.Combine("sub", "a.txt"), "needle\n");
        var opened = new List<string>();
        var svc = new FileSystemService { StageHook = (stage, file) => { if (stage == FileSystemService.Stage.SearchFile) opened.Add(file); } };

        var act = () => svc.SearchContentAsync(Request("needle", pattern: pattern));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("pattern");
        opened.Should().BeEmpty("the refusal comes before the walk");
    }

    /// <summary>Decision C: "The name search is unchanged" - it still reads the directory part of the pattern.</summary>
    [Fact]
    public async Task The_name_search_still_takes_a_pattern_with_a_directory_in_it()
    {
        var inSub = File_(Path.Combine("sub", "a.txt"), "needle\n");
        File_("b.txt", "needle\n");

        var hits = await Svc().SearchAsync(_root, Path.Combine("sub", "*.txt"), null, null, false);

        hits.Select(h => h.Path).Should().Equal(inSub);
    }

    /// <summary>
    /// Decision B keeps links out of the walk: a junction is not entered, while an ordinary sibling
    /// folder is. (B's other half - entering a non-link reparse folder such as a OneDrive folder -
    /// needs a cloud or dedup fixture and is not pinned.)
    /// </summary>
    [JunctionFact]
    public async Task A_junction_is_not_entered_but_its_sibling_folder_is()
    {
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "needle outside\n");
        JunctionFactAttribute.TryCreateJunction(Path.Combine(_root, "link"), outside, out var error).Should().BeTrue(error);
        var plain = File_(Path.Combine("plain", "inside.txt"), "needle inside\n");

        var result = await Svc().SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Path.Should().Be(plain);
    }

    // ==== PR #47 finding B: the token is observed for every entry, matching or not ====================

    /// <summary>200 .txt files in four folders, none matching '*.config'; optionally one match last.</summary>
    private void TreeWithNoMatches(bool withLateMatch = false)
    {
        for (int d = 0; d < 4; d++)
            for (int f = 0; f < 50; f++)
                File_(Path.Combine($"f{d}", $"n{f:D3}.txt"), "needle\n");
        if (withLateMatch)
            File_(Path.Combine("f3", "zz-last.config"), "needle\n");
    }

    /// <summary>
    /// B1: the token was checked only in the loop body, which runs only for files the name glob
    /// matched - a walk over thousands of non-matching entries could not be cancelled. Cancelled at
    /// the 20th entry the search must end in OperationCanceledException, and the walk must not go
    /// on: at most one more entry after the cancel. Today it walks all 204 entries and returns an
    /// empty result.
    /// </summary>
    [Fact]
    public async Task A_walk_over_entries_the_glob_excludes_observes_cancellation()
    {
        TreeWithNoMatches();
        using var cts = new CancellationTokenSource();
        int seen = 0, seenAfterCancel = 0;
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage != FileSystemService.Stage.SearchEntry) return;
                if (cts.IsCancellationRequested) seenAfterCancel++;
                if (++seen == 20) cts.Cancel();
            },
        };

        var act = () => svc.SearchContentAsync(Request("needle", pattern: "*.config"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        seenAfterCancel.Should().BeLessThanOrEqualTo(1, "the walk stops at the next entry once the token is cancelled");
    }

    /// <summary>B2: the one matching file comes after the cancel point, so it is never opened.</summary>
    [Fact]
    public async Task A_match_after_the_cancel_point_is_never_opened()
    {
        TreeWithNoMatches(withLateMatch: true);
        using var cts = new CancellationTokenSource();
        int seen = 0;
        var opened = new List<string>();
        var svc = new FileSystemService
        {
            StageHook = (stage, arg) =>
            {
                if (stage == FileSystemService.Stage.SearchFile) opened.Add(arg);
                if (stage == FileSystemService.Stage.SearchEntry && ++seen == 3) cts.Cancel();
            },
        };

        var act = () => svc.SearchContentAsync(Request("needle", pattern: "*.config"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        opened.Should().BeEmpty("the walk stopped before it reached the matching file");
    }

    /// <summary>B3 (guard): without a cancel the same tree is an empty, unremarkable result.</summary>
    [Fact]
    public async Task A_walk_over_entries_the_glob_excludes_returns_an_empty_result()
    {
        TreeWithNoMatches();

        var result = await Svc().SearchContentAsync(Request("needle", pattern: "*.config"));

        result.Matches.Should().BeEmpty();
        result.FilesSearched.Should().Be(0);
        result.FilesSkipped.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    // ==== PR #47 finding C: the per-file cap holds for the file that is actually opened ===============

    /// <summary>
    /// C1: the size test used the length the WALK saw; the stream that was then opened was unbounded.
    /// A matching file under the cap at enumeration, grown past it in the SearchFile hook (the moment
    /// before the open) with lines that match: skipped and counted, never searched, no matches. Today
    /// it is read whole.
    /// </summary>
    [Fact]
    public async Task A_file_grown_past_the_cap_after_the_walk_saw_it_is_skipped_not_searched()
    {
        var path = File_("grow.log", "needle 0\n");
        var svc = new FileSystemService
        {
            StageHook = (stage, arg) =>
            {
                if (stage != FileSystemService.Stage.SearchFile) return;
                var tail = new byte[ContentSearch.MaxFileBytes];
                Array.Fill(tail, (byte)'x');
                for (long i = 99; i < tail.Length; i += 100) tail[i] = (byte)'\n';
                "needle grown\n"u8.CopyTo(tail);
                using var append = new FileStream(arg, FileMode.Append, FileAccess.Write);
                append.Write(tail);
            },
        };

        var result = await svc.SearchContentAsync(Request("needle"));

        new FileInfo(path).Length.Should().BeGreaterThan(ContentSearch.MaxFileBytes, "precondition: the hook grew it past the cap");
        result.Matches.Should().BeEmpty("a file over the cap at the open is not read");
        result.FilesSkipped.Should().Be(1);
        result.FilesSearched.Should().Be(0);
    }

    /// <summary>C2 (guard): grown in the hook but still under the cap - searched, and the appended lines are found.</summary>
    [Fact]
    public async Task A_file_grown_under_the_cap_after_the_walk_saw_it_is_searched_with_the_new_lines()
    {
        var path = File_("grow.log", "nothing\n");
        var svc = new FileSystemService
        {
            StageHook = (stage, arg) =>
            {
                if (stage == FileSystemService.Stage.SearchFile) File.AppendAllText(arg, "needle appended\n");
            },
        };

        var result = await svc.SearchContentAsync(Request("needle"));

        result.Matches.Should().ContainSingle().Which.Should().Match<ContentMatch>(m => m.Path == path && m.Line == 2);
        result.FilesSearched.Should().Be(1);
        result.FilesSkipped.Should().Be(0);
    }

    /// <summary>C3 (guard): grown in the hook to EXACTLY the cap - not over it, so it is searched.</summary>
    [Fact]
    public async Task A_file_grown_to_exactly_the_cap_after_the_walk_saw_it_is_searched()
    {
        var path = File_("edge.log", "needle\n");
        var svc = new FileSystemService
        {
            StageHook = (stage, arg) =>
            {
                if (stage == FileSystemService.Stage.SearchFile) File.WriteAllBytes(arg, BigText(ContentSearch.MaxFileBytes));
            },
        };

        var result = await svc.SearchContentAsync(Request("needle"));

        new FileInfo(path).Length.Should().Be(ContentSearch.MaxFileBytes);
        result.Matches.Should().ContainSingle().Which.Path.Should().Be(path);
        result.FilesSearched.Should().Be(1);
    }

    [Fact]

    public async Task SearchContentAsync_with_a_cancelled_token_does_nothing()
    {
        File_("a.txt", "needle\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Svc().SearchContentAsync(Request("needle"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// "Cancellation is observed per file": the token is cancelled as the first file is reached; the
    /// walk stops there instead of opening the other nine.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_observes_cancellation_before_the_next_file()
    {
        for (int i = 0; i < 10; i++) File_($"f{i}.txt", "needle\n");
        using var cts = new CancellationTokenSource();
        var reached = new List<string>();
        var svc = new FileSystemService
        {
            StageHook = (stage, path) =>
            {
                if (stage != FileSystemService.Stage.SearchFile) return;
                reached.Add(path);
                cts.Cancel();
            },
        };

        var act = () => svc.SearchContentAsync(Request("needle"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        reached.Should().ContainSingle("the token is checked before each file, so the second file is never reached");
    }

    /// <summary>
    /// "a 2-second match timeout per line; a timeout is an InvalidOperationException naming the
    /// pattern and the file". The service's timeout is a seam here (50 ms) so the test is fast.
    /// </summary>
    [Fact]
    public async Task A_regex_too_slow_on_a_line_is_refused_naming_the_pattern_and_the_file()
    {
        const string Pattern = "(a+)+$";
        var path = File_("slow.txt", new string('a', 40) + "b\n");
        var svc = new FileSystemService { ContentMatchTimeout = TimeSpan.FromMilliseconds(50) };

        var search = svc.SearchContentAsync(Request(Pattern));

        // Without the timeout this line takes 2^40 steps: the search must give up, not pin a core.
        (await Task.WhenAny(search, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(search,
            "the regex is built with the match timeout, so a catastrophic pattern stops");
        var act = () => search;
        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain(Pattern).And.Contain(path);
    }

    /// <summary>
    /// The tool validates the regex first, but the service is an interface other callers reach: it
    /// refuses an invalid pattern itself, naming content, before any file is opened.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_refuses_an_invalid_regex_naming_content_before_opening_anything()
    {
        File_("a.txt", "needle\n");
        var opened = new List<string>();
        var svc = new FileSystemService { StageHook = (stage, path) => { if (stage == FileSystemService.Stage.SearchFile) opened.Add(path); } };

        var act = () => svc.SearchContentAsync(Request("(unclosed"));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("content");
        opened.Should().BeEmpty();
    }

    [Fact]
    public void The_service_match_timeout_defaults_to_two_seconds()
    {
        new FileSystemService().ContentMatchTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// A root that is not there is a refusal a caller can read (an IOException naming it), not an
    /// empty result that reads as "nothing matched". Not in the note: this pins today's SearchAsync
    /// behaviour (DirectoryNotFoundException from the enumeration) for the content mode too.
    /// </summary>
    [Fact]
    public async Task SearchContentAsync_refuses_a_root_that_does_not_exist()
    {
        var missing = Path.Combine(_tmp, "no-such-root");

        var act = () => Svc().SearchContentAsync(new ContentSearchRequest(missing, "needle"));

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain(missing);
    }
}
