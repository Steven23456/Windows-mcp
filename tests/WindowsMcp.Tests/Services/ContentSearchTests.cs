using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-9: the pure half of <c>file_search</c>'s content mode (docs/design/E-9-file-edit-content-search.md,
/// "Reading" and "Result (content mode)") - the per-file line scan over a <see cref="TextReader"/>
/// and the binary sniff. No disk.
/// </summary>
[Trait("Category", "Unit")]
public class ContentSearchTests
{
    private const string Path_ = "C:/data/app.config";   // only echoed back; never opened

    private static Regex Re(string pattern, TimeSpan? timeout = null) =>
        new(pattern, RegexOptions.CultureInvariant, timeout ?? TimeSpan.FromSeconds(2));

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    // ==== matches and line numbers =================================================================

    [Fact]
    public void Scan_reports_each_matching_line_with_its_1_based_number_and_the_path()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("alpha", "needle one", "beta", "gamma needle")),
            Path_, Re("needle"), contextLines: 0, maxMatches: 100);

        scan.Matches.Select(m => m.Line).Should().Equal(2, 4);
        scan.Matches.Select(m => m.Text).Should().Equal("needle one", "gamma needle");
        scan.Matches.Should().OnlyContain(m => m.Path == Path_);
        scan.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Scan_with_no_match_returns_nothing_and_is_not_truncated()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("a", "b")), Path_, Re("zzz"), 0, 100);

        scan.Matches.Should().BeEmpty();
        scan.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// "The regex is matched per line (the line without its terminator)": with the \r stripped,
    /// <c>one$</c> matches a CRLF line, and the reported text has no trailing \r.
    /// </summary>
    [Fact]
    public void Scan_matches_and_reports_a_crlf_line_without_its_terminator()
    {
        var scan = ContentSearch.Scan(new StringReader("needle one\r\nother\r\n"), Path_, Re("one$"), 0, 100);

        scan.Matches.Should().ContainSingle();
        scan.Matches[0].Text.Should().Be("needle one");
        scan.Matches[0].Line.Should().Be(1);
    }

    /// <summary>"Case-insensitive search is (?i) in the pattern."</summary>
    [Fact]
    public void Scan_honours_an_inline_case_insensitive_flag()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("ERROR: disk", "ok")), Path_, Re("(?i)error"), 0, 100);

        scan.Matches.Should().ContainSingle().Which.Line.Should().Be(1);
    }

    // ==== context_lines ==============================================================================

    /// <summary>"before / after are present only when context_lines > 0."</summary>
    [Fact]
    public void Scan_without_context_leaves_before_and_after_null()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("a", "needle", "b")), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Before.Should().BeNull();
        scan.Matches[0].After.Should().BeNull();
    }

    [Fact]
    public void Scan_with_context_at_the_start_of_the_file_has_an_empty_before()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("needle", "l2", "l3", "l4")), Path_, Re("needle"), 2, 100);

        scan.Matches[0].Before.Should().NotBeNull().And.BeEmpty("context was asked for, so the key is there - there is just nothing before line 1");
        scan.Matches[0].After.Should().Equal("l2", "l3");
    }

    [Fact]
    public void Scan_with_context_at_the_end_of_the_file_has_an_empty_after()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("l1", "l2", "l3", "needle")), Path_, Re("needle"), 2, 100);

        scan.Matches[0].Before.Should().Equal("l2", "l3");
        scan.Matches[0].After.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>A file with no trailing newline: the last line is still a line, and still has its before.</summary>
    [Fact]
    public void Scan_reads_a_last_line_without_a_terminator()
    {
        var scan = ContentSearch.Scan(new StringReader("l1\nl2\nneedle"), Path_, Re("needle"), 1, 100);

        scan.Matches.Should().ContainSingle();
        scan.Matches[0].Line.Should().Be(3);
        scan.Matches[0].Before.Should().Equal("l2");
        scan.Matches[0].After.Should().BeEmpty();
    }

    [Fact]
    public void Scan_with_the_maximum_context_of_five_takes_five_each_side()
    {
        var lines = Enumerable.Range(1, 15).Select(i => i == 8 ? "needle" : $"l{i}").ToArray();

        var scan = ContentSearch.Scan(new StringReader(Lines(lines)), Path_, Re("needle"), 5, 100);

        scan.Matches[0].Line.Should().Be(8);
        scan.Matches[0].Before.Should().Equal("l3", "l4", "l5", "l6", "l7");
        scan.Matches[0].After.Should().Equal("l9", "l10", "l11", "l12", "l13");
    }

    [Fact]
    public void Scan_with_more_context_than_the_file_has_takes_what_there_is()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("l1", "needle", "l3")), Path_, Re("needle"), 5, 100);

        scan.Matches[0].Before.Should().Equal("l1");
        scan.Matches[0].After.Should().Equal("l3");
    }

    /// <summary>
    /// Adjacent matches each carry their own context, even where it overlaps the other match: the
    /// result is one object per matching line, and each one stands on its own.
    /// </summary>
    [Fact]
    public void Adjacent_matches_each_carry_their_own_overlapping_context()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("l1", "l2", "needle a", "needle b", "l5", "l6")),
            Path_, Re("needle"), 1, 100);

        scan.Matches.Should().HaveCount(2);
        scan.Matches[0].Line.Should().Be(3);
        scan.Matches[0].Before.Should().Equal("l2");
        scan.Matches[0].After.Should().Equal("needle b");
        scan.Matches[1].Line.Should().Be(4);
        scan.Matches[1].Before.Should().Equal("needle a");
        scan.Matches[1].After.Should().Equal("l5");
    }

    // ==== the match cap ==============================================================================

    /// <summary>
    /// "The search stops at max_matches matching lines and reports truncated:true" - stops, not
    /// reads to the end and trims: a 10 000-line log where every line matches is not read whole for
    /// a cap of 5.
    /// </summary>
    [Fact]
    public void Scan_stops_at_the_cap_mid_file_and_reports_truncated()
    {
        var reader = new CountingReader(Enumerable.Range(1, 10_000).Select(i => $"needle {i:D5}"));

        var scan = ContentSearch.Scan(reader, Path_, Re("needle"), 0, maxMatches: 5);

        scan.Matches.Should().HaveCount(5);
        scan.Matches.Select(m => m.Line).Should().Equal(1, 2, 3, 4, 5);
        scan.Truncated.Should().BeTrue();
        reader.LinesConsumed.Should().BeLessThan(5_000, "the scan stops at the cap instead of reading the rest of the file");
    }

    /// <summary>The last match before the cap still gets its full after-context.</summary>
    [Fact]
    public void The_last_match_before_the_cap_still_gets_its_after_context()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("l1", "needle", "l3", "l4", "needle again", "l6")),
            Path_, Re("needle"), 2, maxMatches: 1);

        scan.Matches.Should().ContainSingle();
        scan.Matches[0].Line.Should().Be(2);
        scan.Matches[0].After.Should().Equal("l3", "l4");
        scan.Truncated.Should().BeTrue("a second matching line was left unreported");
    }

    [Fact]
    public void Scan_under_the_cap_is_not_truncated()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("needle", "x", "needle")), Path_, Re("needle"), 0, maxMatches: 3);

        scan.Matches.Should().HaveCount(2);
        scan.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// The decision E-9 took on the note's "stops at max_matches matching lines and reports
    /// truncated:true": reaching the cap IS truncated, whether or not another match follows - the
    /// scan does not read on to find out, and the description says "there may be more".
    /// </summary>
    [Fact]
    public void Scan_exactly_at_the_cap_reports_truncated()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("needle", "x", "needle", "needle")), Path_, Re("needle"), 0, maxMatches: 3);

        scan.Matches.Should().HaveCount(3);
        scan.Truncated.Should().BeTrue("the scan stopped because it reached the cap");
    }

    /// <summary>A budget of one below the matches: the cap is reached on the second-last match line.</summary>
    [Fact]
    public void Scan_one_under_the_matches_reports_truncated_and_stops_at_the_cap()
    {
        var scan = ContentSearch.Scan(new StringReader(Lines("needle 1", "needle 2", "needle 3")), Path_, Re("needle"), 0, maxMatches: 2);

        scan.Matches.Select(m => m.Text).Should().Equal("needle 1", "needle 2");
        scan.Truncated.Should().BeTrue();
    }

    /// <summary>
    /// GREEN finding (E-9): a match already found must not make every later line of the file cost
    /// more. Measured on the first implementation: a 2,000,000-line log took 137 ms to scan with no
    /// early matches and 14 s with 999 of them (under the cap of 1,000), because each line walked
    /// every earlier match to offer it as after-context. The note's large-log case ("lines are read
    /// one at a time, so a large log does not load whole") has to stay linear. The bound is relative
    /// - the same tail with no early matches - with a floor, so a slow machine does not fail it.
    /// </summary>
    [Fact]
    public void Earlier_matches_do_not_make_each_later_line_slower()
    {
        const int Tail = 1_000_000;
        var re = Re("needle");
        string Log(int early)
        {
            var sb = new StringBuilder((early * 7) + (Tail * 8));
            for (int i = 0; i < early; i++) sb.Append("needle\n");
            for (int i = 0; i < Tail; i++) sb.Append("xxxxxxx\n");
            return sb.ToString();
        }
        var quiet = Log(0);
        var busy = Log(999);
        ContentSearch.Scan(new StringReader("needle\n"), Path_, re, 0, 1000);   // warm up

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ContentSearch.Scan(new StringReader(quiet), Path_, re, 0, 1000);
        var baseline = sw.Elapsed;
        sw.Restart();
        var scan = ContentSearch.Scan(new StringReader(busy), Path_, re, 0, 1000);
        var withMatches = sw.Elapsed;

        scan.Matches.Should().HaveCount(999);
        var bound = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(2).Ticks, baseline.Ticks * 10));
        withMatches.Should().BeLessThan(bound,
            "999 earlier matches must not multiply the cost of the {0:N0} lines after them (baseline {1:N0} ms)", Tail, baseline.TotalMilliseconds);
    }

    // ---- R2 decision 7: the output is bounded ------------------------------------------------------

    private static int Chars(IEnumerable<ContentMatch> matches) =>
        matches.Sum(m => m.Text.Length + (m.Before?.Sum(l => l.Length) ?? 0) + (m.After?.Sum(l => l.Length) ?? 0));

    /// <summary>The 400-character line every output-cap row uses: "needle NNNN" and padding.</summary>
    private static string Line400(int i) => "needle " + i.ToString("D4") + new string('x', 389);

    [Fact]
    public void The_output_cap_is_60000_characters()
    {
        ContentSearch.MaxResultChars.Should().Be(60_000);
        Line400(0).Should().HaveLength(400, "the rows below are built on exactly-400-character lines");
    }

    /// <summary>
    /// "the search stops once the matched and context lines returned total 60,000 characters, with
    /// truncated:true. 1,000 matches x 11 lines x 400 characters would be 4.4 MB." The bound pinned:
    /// at most the cap plus one match's worth - the match that crossed it finishes with its context
    /// (1 + 2 x context_lines lines of at most 403 characters).
    /// </summary>
    [Fact]
    public void Scan_stops_once_the_returned_text_reaches_the_output_cap()
    {
        var lines = Enumerable.Range(0, 1000).Select(Line400).ToArray();

        var scan = ContentSearch.Scan(new StringReader(Lines(lines)), Path_, Re("needle"), 5, maxMatches: 1000);

        Chars(scan.Matches).Should().BeLessThanOrEqualTo(ContentSearch.MaxResultChars + (1 + 2 * 5) * (ContentSearch.MaxLineChars + 3));
        scan.Truncated.Should().BeTrue("the scan stopped at the output cap with matching lines left");
        scan.Matches.Count.Should().BeLessThan(1000);
    }

    /// <summary>maxChars is the budget left in the whole search; a later file gets only what is left.</summary>
    [Fact]
    public void Scan_honours_a_smaller_remaining_output_budget()
    {
        var lines = Enumerable.Range(0, 50).Select(Line400).ToArray();

        var scan = ContentSearch.Scan(new StringReader(Lines(lines)), Path_, Re("needle"), 0, 1000, default, maxChars: 1000);

        Chars(scan.Matches).Should().BeLessThanOrEqualTo(1000 + ContentSearch.MaxLineChars + 3);
        scan.Truncated.Should().BeTrue();
        scan.Matches.Count.Should().BeLessThan(50);
    }

    /// <summary>
    /// "When the budget is spent only the newest match finishes its context": with dense matches
    /// and context 5, three matches are still owed after-context when the budget (3,000) runs out on
    /// line 3. Only the newest gets its five lines; the older two stop where they are. Letting all
    /// three finish would return 12 more lines instead of 5 (8,400 characters against 5,600) - over
    /// the one-match bound the cap promises (3,000 + 11 x 403 = 7,433).
    /// </summary>
    [Fact]
    public void When_the_output_budget_is_spent_only_the_newest_match_finishes_its_context()
    {
        var lines = Enumerable.Range(0, 50).Select(Line400).ToArray();

        var scan = ContentSearch.Scan(new StringReader(Lines(lines)), Path_, Re("needle"), 5, 1000, default, maxChars: 3000);

        scan.Matches.Should().HaveCount(3);
        scan.Matches[^1].After.Should().HaveCount(5, "the match that crossed the budget finishes its context");
        scan.Matches[0].After!.Length.Should().BeLessThan(5, "an older match stops collecting once the budget is spent");
        Chars(scan.Matches).Should().BeLessThanOrEqualTo(3000 + (1 + 2 * 5) * (ContentSearch.MaxLineChars + 3));
        scan.Truncated.Should().BeTrue();
    }

    /// <summary>A later file that arrives with no budget left returns nothing, reads nothing, and says truncated.</summary>
    [Fact]
    public void Scan_with_no_output_budget_left_reads_nothing()
    {
        var reader = new CountingReader(Enumerable.Range(0, 10).Select(Line400));

        var scan = ContentSearch.Scan(reader, Path_, Re("needle"), 0, 1000, default, maxChars: 0);

        scan.Matches.Should().BeEmpty();
        scan.Truncated.Should().BeTrue();
        reader.LinesConsumed.Should().Be(0);
    }

    [Fact]
    public void A_result_under_the_output_cap_is_unaffected()
    {
        var lines = Enumerable.Range(0, 10).Select(Line400).ToArray();

        var scan = ContentSearch.Scan(new StringReader(Lines(lines)), Path_, Re("needle"), 2, 1000);

        scan.Matches.Should().HaveCount(10);
        scan.Truncated.Should().BeFalse();
    }

    // ==== clipping ===================================================================================

    /// <summary>"text, and each context line, is clipped at 400 characters with ..."</summary>
    [Fact]
    public void A_long_matching_line_is_clipped_at_400_characters_with_an_ellipsis()
    {
        var line = "needle" + new string('x', 994);   // 1000 characters

        var scan = ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Text.Should().Be(line[..400] + "...");
    }

    [Fact]
    public void A_line_of_exactly_400_characters_is_not_clipped()
    {
        var line = "needle" + new string('x', 394);

        var scan = ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Text.Should().Be(line);
    }

    [Fact]
    public void A_line_of_401_characters_is_clipped_to_400_and_the_ellipsis()
    {
        var line = "needle" + new string('x', 395);

        var scan = ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Text.Should().Be(line[..400] + "...");
    }

    /// <summary>
    /// The clip never cuts a surrogate pair in half: a lone high surrogate before the "..." would be
    /// an invalid string, which the JSON writer turns into U+FFFD (or refuses). The pair straddling
    /// character 400 is dropped whole.
    /// </summary>
    [Fact]
    public void The_clip_does_not_split_a_surrogate_pair()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var line = "needle" + new string('x', 393) + emoji + new string('y', 50);   // the pair is at 399-400
        char.IsHighSurrogate(line[399]).Should().BeTrue("the row puts the high half at the last kept index");

        var scan = ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Text.Should().Be(line[..399] + "...");
    }

    /// <summary>A pair that ends exactly at character 400 is kept whole.</summary>
    [Fact]
    public void The_clip_keeps_a_surrogate_pair_that_ends_at_the_limit()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var line = "needle" + new string('x', 392) + emoji + new string('y', 50);   // the pair is at 398-399

        var scan = ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re("needle"), 0, 100);

        scan.Matches[0].Text.Should().Be(line[..400] + "...");
    }

    [Fact]
    public void Context_lines_are_clipped_too()
    {
        var longLine = new string('b', 500);
        var scan = ContentSearch.Scan(new StringReader(Lines(longLine, "needle", longLine)), Path_, Re("needle"), 1, 100);

        scan.Matches[0].Before.Should().Equal(longLine[..400] + "...");
        scan.Matches[0].After.Should().Equal(longLine[..400] + "...");
    }

    // ==== the regex timeout and cancellation =======================================================

    /// <summary>
    /// "a timeout is an InvalidOperationException naming the pattern and the file ("too slow on ...")
    /// - a pathological pattern must not pin a core." The regex here carries a 50 ms timeout so the
    /// test is fast; the service builds it with the real 2 s.
    /// </summary>
    [Fact]
    public void A_regex_timeout_is_refused_naming_the_pattern_and_the_file()
    {
        const string Pattern = "(a+)+$";
        var line = new string('a', 40) + "b";

        var act = () => ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re(Pattern, TimeSpan.FromMilliseconds(50)), 0, 100);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(Pattern).And.Contain(Path_);
        message.Should().ContainEquivalentOf("too slow");
    }

    /// <summary>
    /// A long pattern is still named - at least its first 80 characters - so the caller can tell
    /// which of their patterns was too slow without the whole of it filling the error.
    /// </summary>
    [Fact]
    public void A_long_pattern_that_times_out_is_named_by_its_start()
    {
        var pattern = "(a+)+(?#" + new string('c', 120) + ")$";   // (?#...) is an inline comment: same catastrophe, long text
        var line = new string('a', 40) + "b";

        var act = () => ContentSearch.Scan(new StringReader(line + "\n"), Path_, Re(pattern, TimeSpan.FromMilliseconds(50)), 0, 100);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(pattern[..80]).And.Contain(Path_);
    }

    [Fact]
    public void The_production_match_timeout_is_two_seconds()
    {
        ContentSearch.MatchTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// "Cancellation is observed ... every 1,000 lines." The token is cancelled while line 10 is
    /// read; the scan must give up within the next thousand or so, not run to line 5 000.
    /// </summary>
    [Fact]
    public void Scan_observes_cancellation_within_about_a_thousand_lines()
    {
        using var cts = new CancellationTokenSource();
        var reader = new CountingReader(Enumerable.Range(1, 5_000).Select(i => $"line {i:D5}"), onLine: n =>
        {
            if (n == 10) cts.Cancel();
        });

        var act = () => ContentSearch.Scan(reader, Path_, Re("never-matches"), 0, 100, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        reader.LinesConsumed.Should().BeLessThan(5_000, "the token is checked every 1,000 lines, so the scan stops long before the end");
    }

    // ==== LooksBinary ================================================================================

    [Fact]
    public void A_nul_in_the_first_8_KiB_without_a_bom_is_binary()
    {
        var bytes = Encoding.ASCII.GetBytes("MZ\0\0header needle");

        ContentSearch.LooksBinary(bytes).Should().BeTrue();
    }

    [Fact]
    public void A_nul_at_the_last_byte_of_the_8_KiB_probe_is_binary()
    {
        var bytes = Enumerable.Repeat((byte)'a', 10_000).ToArray();
        bytes[ContentSearch.BinaryProbeBytes - 1] = 0;

        ContentSearch.LooksBinary(bytes).Should().BeTrue();
    }

    /// <summary>"a NUL byte in its first 8 KiB": a NUL past the probe does not make a file binary.</summary>
    [Fact]
    public void A_nul_just_past_the_first_8_KiB_is_text()
    {
        var bytes = Enumerable.Repeat((byte)'a', 10_000).ToArray();
        bytes[ContentSearch.BinaryProbeBytes] = 0;

        ContentSearch.LooksBinary(bytes).Should().BeFalse();
    }

    [Fact]
    public void The_probe_is_8_KiB()
    {
        ContentSearch.BinaryProbeBytes.Should().Be(8192);
    }

    /// <summary>UTF-16 text is full of NULs; its BOM is what says it is text.</summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xFE, 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })]
    public void A_unicode_bom_with_nuls_after_it_is_text(byte[] bom)
    {
        var body = bom.Length == 4
            ? Encoding.UTF32.GetBytes("needle")
            : bom[0] == 0xFF ? Encoding.Unicode.GetBytes("needle") : Encoding.BigEndianUnicode.GetBytes("needle");

        ContentSearch.LooksBinary(bom.Concat(body).ToArray()).Should().BeFalse();
    }

    /// <summary>
    /// Only a whole BOM counts. The start of one (EF BB, a lone FF or FE) followed by a NUL is no BOM
    /// at all, so the file is binary.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0x00 })]
    [InlineData(new byte[] { 0xEF, 0x00, 0xBF })]
    [InlineData(new byte[] { 0xFF, 0x00 })]
    [InlineData(new byte[] { 0xFE, 0x00 })]
    [InlineData(new byte[] { 0x00, 0xFE, 0xFF })]
    public void A_partial_or_misplaced_bom_followed_by_a_nul_is_binary(byte[] bytes)
    {
        ContentSearch.LooksBinary(bytes).Should().BeTrue();
    }

    /// <summary>"binary: no BOM and a NUL byte" - any BOM, the UTF-8 one included, means text.</summary>
    [Fact]
    public void A_utf8_bom_followed_by_a_nul_is_text()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'a', 0x00, (byte)'b'];

        ContentSearch.LooksBinary(bytes).Should().BeFalse();
    }

    [Fact]
    public void An_empty_file_is_text()
    {
        ContentSearch.LooksBinary([]).Should().BeFalse();
    }

    [Fact]
    public void Plain_text_and_high_bytes_without_nul_are_text()
    {
        ContentSearch.LooksBinary([(byte)'a', 0xE9, 0xFF, (byte)'\n']).Should().BeFalse();
    }

    [Fact]
    public void The_line_clip_and_size_cap_constants_are_the_notes()
    {
        ContentSearch.MaxLineChars.Should().Be(400);
        ContentSearch.MaxFileBytes.Should().Be(16L * 1024 * 1024);
    }

    // ==== helpers ====================================================================================

    /// <summary>
    /// A reader over generated lines that counts how many line terminators it has handed out,
    /// whichever Read overload the scan uses, and can act when a given line is consumed.
    /// </summary>
    private sealed class CountingReader : TextReader
    {
        private readonly StringReader _inner;
        private readonly Action<int>? _onLine;

        public CountingReader(IEnumerable<string> lines, Action<int>? onLine = null)
        {
            _inner = new StringReader(string.Join("\n", lines) + "\n");
            _onLine = onLine;
        }

        public int LinesConsumed { get; private set; }

        private void Saw(int newlines)
        {
            for (int i = 0; i < newlines; i++)
            {
                LinesConsumed++;
                _onLine?.Invoke(LinesConsumed);
            }
        }

        public override int Peek() => _inner.Peek();

        public override int Read()
        {
            int c = _inner.Read();
            if (c == '\n') Saw(1);
            return c;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            int n = _inner.Read(buffer, index, count);
            Saw(buffer.AsSpan(index, n).Count('\n'));
            return n;
        }

        public override int Read(Span<char> buffer)
        {
            int n = _inner.Read(buffer);
            Saw(buffer[..n].Count('\n'));
            return n;
        }

        public override string? ReadLine()
        {
            var line = _inner.ReadLine();
            if (line is not null) Saw(1);
            return line;
        }

        public override string ReadToEnd()
        {
            var rest = _inner.ReadToEnd();
            Saw(rest.AsSpan().Count('\n'));
            return rest;
        }
    }
}
