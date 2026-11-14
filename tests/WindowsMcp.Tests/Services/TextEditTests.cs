using System.Text;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-9: the pure half of <c>file_edit</c> (docs/design/E-9-file-edit-content-search.md, "Encoding
/// and BOM" and "Line endings"). No disk: every content refusal the note lists is thrown from
/// <see cref="TextEdit"/>, so each is pinned here in milliseconds and the service tests only have to
/// prove the file is left alone when it happens.
/// </summary>
[Trait("Category", "Unit")]
public class TextEditTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] Utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ==== Detect: a BOM decides ===================================================================

    /// <summary>
    /// "A byte-order mark decides when present, whatever encoding says." Each BOM is tried with
    /// <c>auto</c> and with a contradicting name: the BOM still wins.
    /// </summary>
    [Theory]
    [InlineData("utf8", "auto", "utf-8", 3)]
    [InlineData("utf8", "ascii", "utf-8", 3)]
    [InlineData("utf8", "latin1", "utf-8", 3)]
    [InlineData("utf16le", "auto", "utf-16", 2)]
    [InlineData("utf16le", "utf-8", "utf-16", 2)]
    [InlineData("utf16be", "auto", "utf-16-be", 2)]
    [InlineData("utf16be", "ascii", "utf-16-be", 2)]
    [InlineData("utf32le", "auto", "utf-32", 4)]
    [InlineData("utf32le", "utf-16", "utf-32", 4)]
    public void Detect_lets_the_bom_decide_whatever_encoding_says(string bom, string encoding, string expectedName, int expectedBomLength)
    {
        byte[] preamble = bom switch
        {
            "utf8" => Utf8Bom,
            "utf16le" => Utf16LeBom,
            "utf16be" => Utf16BeBom,
            _ => Utf32LeBom,
        };
        // Some content after the BOM, encoded the way the BOM says, so the bytes are a real file.
        var body = expectedName switch
        {
            "utf-8" => System.Text.Encoding.UTF8.GetBytes("key=value\r\n"),
            "utf-16" => System.Text.Encoding.Unicode.GetBytes("key=value\r\n"),
            "utf-16-be" => System.Text.Encoding.BigEndianUnicode.GetBytes("key=value\r\n"),
            _ => System.Text.Encoding.UTF32.GetBytes("key=value\r\n"),
        };

        var detected = TextEdit.Detect(Concat(preamble, body), encoding);

        detected.Name.Should().Be(expectedName);
        detected.BomLength.Should().Be(expectedBomLength);
    }

    /// <summary>
    /// "UTF-32 LE (FF FE 00 00, checked before UTF-16 LE)": the UTF-32 BOM begins with the UTF-16
    /// one, so checking UTF-16 first would call every UTF-32 file UTF-16 and decode it as garbage
    /// with NULs between the characters.
    /// </summary>
    [Fact]
    public void Detect_checks_the_utf32_bom_before_the_utf16_one()
    {
        var bytes = Concat(Utf32LeBom, System.Text.Encoding.UTF32.GetBytes("ab"));

        var detected = TextEdit.Detect(bytes, "auto");

        detected.Name.Should().Be("utf-32");
        detected.BomLength.Should().Be(4);
        TextEdit.Decode(bytes, detected).Should().Be("ab");
    }

    /// <summary>A UTF-16 LE BOM followed by a non-NUL character is UTF-16, not a short UTF-32 BOM.</summary>
    [Fact]
    public void Detect_reads_ff_fe_followed_by_text_as_utf16()
    {
        var bytes = Concat(Utf16LeBom, System.Text.Encoding.Unicode.GetBytes("ab"));

        var detected = TextEdit.Detect(bytes, "auto");

        detected.Name.Should().Be("utf-16");
        TextEdit.Decode(bytes, detected).Should().Be("ab");
    }

    // ==== Detect: no BOM, the encoding parameter decides ===========================================

    [Theory]
    [InlineData("auto", "utf-8")]
    [InlineData("utf-8", "utf-8")]
    [InlineData("utf-16", "utf-16")]
    [InlineData("ascii", "ascii")]
    [InlineData("latin1", "latin1")]
    [InlineData("AUTO", "utf-8")]        // "(any case)"
    [InlineData("UTF-8", "utf-8")]
    [InlineData("Utf-16", "utf-16")]
    [InlineData("ASCII", "ascii")]
    [InlineData("Latin1", "latin1")]
    public void Detect_without_a_bom_follows_the_encoding_parameter(string encoding, string expectedName)
    {
        var detected = TextEdit.Detect("plain"u8.ToArray(), encoding);

        detected.Name.Should().Be(expectedName);
        detected.BomLength.Should().Be(0, "there is no BOM, so none is reported and none will be written");
    }

    /// <summary>An empty file has no BOM; auto still means UTF-8 and it decodes to the empty string.</summary>
    [Fact]
    public void Detect_and_decode_an_empty_file()
    {
        var detected = TextEdit.Detect([], "auto");

        detected.Name.Should().Be("utf-8");
        detected.BomLength.Should().Be(0);
        TextEdit.Decode([], detected).Should().BeEmpty();
    }

    /// <summary>A file that is only a BOM is a BOM-ed empty text.</summary>
    [Fact]
    public void A_file_that_is_only_a_bom_decodes_to_the_empty_string()
    {
        var detected = TextEdit.Detect(Utf8Bom, "auto");

        detected.BomLength.Should().Be(3);
        TextEdit.Decode(Utf8Bom, detected).Should().BeEmpty();
    }

    /// <summary>
    /// The tool refuses an unknown encoding before the service is called; the pure function refuses
    /// it too rather than silently picking UTF-8 (which is what file_read's ResolveEncoding does).
    /// </summary>
    [Theory]
    [InlineData("ebcdic")]
    [InlineData("utf8 ")]
    [InlineData("")]
    public void Detect_refuses_an_unknown_encoding_by_name(string encoding)
    {
        var act = () => TextEdit.Detect("x"u8.ToArray(), encoding);

        act.Should().Throw<ArgumentException>().WithMessage("*encoding*");
    }

    // ---- R2 decision 5: auto does not guess UTF-16 ---------------------------------------------------

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void Detect_marks_auto_without_a_bom_as_a_guess(string encoding)
    {
        TextEdit.Detect("a=1"u8.ToArray(), encoding).Guessed.Should().BeTrue("no BOM and auto: UTF-8 was assumed, not asked for");
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-16", false)]
    [InlineData("ascii", false)]
    [InlineData("latin1", false)]
    [InlineData("auto", true)]       // a UTF-8 BOM decides: that is not a guess either
    public void Detect_is_not_a_guess_when_the_encoding_is_named_or_a_bom_decides(string encoding, bool withBom)
    {
        byte[] bytes = withBom ? [0xEF, 0xBB, 0xBF, (byte)'a'] : [(byte)'a'];

        TextEdit.Detect(bytes, encoding).Guessed.Should().BeFalse();
    }

    /// <summary>
    /// "Without a BOM and with encoding:auto, decoded text that contains U+0000 is refused ... it
    /// looks like UTF-16 without a byte-order mark". UTF-16 ASCII text is valid UTF-8 - a NUL after
    /// every character - so without this a one-character edit would shift every later character.
    /// </summary>
    [Fact]
    public void Decode_refuses_bomless_utf16_that_auto_would_read_as_utf8()
    {
        var bytes = new UnicodeEncoding(false, false).GetBytes("a=1\n");
        var detected = TextEdit.Detect(bytes, "auto");

        var act = () => TextEdit.Decode(bytes, detected);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("utf-16");
        // Second review pass, decision D: the refusal also names the way out for a UTF-8 file that
        // really contains NUL characters.
        message.Should().MatchRegex(@"encoding:\s*""?utf-8", "a UTF-8 file with real NULs is edited with encoding:\"utf-8\"");
    }

    /// <summary>"An explicit utf-8 is taken at its word": a NUL is just a character.</summary>
    [Fact]
    public void Decode_takes_an_explicit_utf8_at_its_word_even_with_a_nul()
    {
        var bytes = "a=1\0x"u8.ToArray();

        TextEdit.Decode(bytes, TextEdit.Detect(bytes, "utf-8")).Should().Be("a=1\0x");
    }

    [Fact]
    public void Decode_reads_bomless_utf16_when_it_is_asked_for()
    {
        var bytes = new UnicodeEncoding(false, false).GetBytes("a=1\n");

        TextEdit.Decode(bytes, TextEdit.Detect(bytes, "utf-16")).Should().Be("a=1\n");
    }

    /// <summary>A null name is as unknown as a misspelt one (an MCP client can send JSON null).</summary>
    [Fact]
    public void Detect_refuses_a_null_encoding()
    {
        var act = () => TextEdit.Detect("x"u8.ToArray(), null!);

        act.Should().Throw<ArgumentException>().WithMessage("*encoding*");
    }

    // ==== Decode is strict ===========================================================================

    /// <summary>
    /// "Nothing is decoded with replacement characters: an edit must never rewrite bytes it did not
    /// understand." C3 28 is an invalid two-byte UTF-8 sequence; a lenient decoder makes it U+FFFD
    /// and the write-back would replace the original byte for good.
    /// </summary>
    [Fact]
    public void Decode_refuses_invalid_utf8_naming_the_encoding_and_the_alternatives()
    {
        byte[] bytes = [(byte)'a', 0xC3, 0x28, (byte)'b'];
        var detected = TextEdit.Detect(bytes, "auto");

        var act = () => TextEdit.Decode(bytes, detected);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("utf-8", "the refusal names the encoding the bytes were not valid in");
        message.Should().Contain("latin1", "and says which encoding values exist - latin1 reads any single-byte file");
    }

    [Fact]
    public void Decode_refuses_a_byte_above_0x7F_as_ascii()
    {
        byte[] bytes = [(byte)'a', 0x80, (byte)'b'];
        var detected = TextEdit.Detect(bytes, "ascii");

        var act = () => TextEdit.Decode(bytes, detected);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("ascii");
    }

    [Fact]
    public void Decode_refuses_odd_length_utf16()
    {
        var bytes = Concat(Utf16LeBom, [0x41, 0x00, 0x42]);   // "A" then half of a code unit
        var detected = TextEdit.Detect(bytes, "auto");

        var act = () => TextEdit.Decode(bytes, detected);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("utf-16");
    }

    [Fact]
    public void Decode_refuses_a_lone_surrogate_in_utf16()
    {
        var bytes = Concat(Utf16LeBom, [0x41, 0x00, 0x00, 0xD8, 0x42, 0x00]);   // A, high surrogate alone, B
        var detected = TextEdit.Detect(bytes, "auto");

        var act = () => TextEdit.Decode(bytes, detected);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>"latin1 ... every byte is a character, so any single-byte legacy file can be edited without loss."</summary>
    [Fact]
    public void Latin1_decodes_every_byte_to_the_character_of_the_same_value()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var detected = TextEdit.Detect(bytes, "latin1");

        var text = TextEdit.Decode(bytes, detected);

        text.Should().HaveLength(256);
        text.Select(c => (int)c).Should().Equal(Enumerable.Range(0, 256));
    }

    [Fact]
    public void Decode_skips_the_bom_and_reads_valid_utf8()
    {
        var bytes = Concat(Utf8Bom, System.Text.Encoding.UTF8.GetBytes("héllo \U0001F600"));
        var detected = TextEdit.Detect(bytes, "auto");

        TextEdit.Decode(bytes, detected).Should().Be("héllo \U0001F600");
    }

    // ==== Encode: same encoding, BOM exactly when the original had one ==============================

    /// <summary>
    /// Decode then encode with nothing changed is the identity, BOM and all, for every encoding the
    /// note names - the property "bytes outside the replaced spans are never touched" rests on.
    /// </summary>
    [Theory]
    [InlineData("utf8bom", "auto")]
    [InlineData("utf8", "auto")]
    [InlineData("utf16lebom", "auto")]
    [InlineData("utf16le", "utf-16")]
    [InlineData("utf16bebom", "auto")]
    [InlineData("utf32lebom", "auto")]
    [InlineData("ascii", "ascii")]
    [InlineData("latin1", "latin1")]
    public void Encode_of_the_decoded_text_gives_back_the_original_bytes(string kind, string encoding)
    {
        const string Text = "[section]\r\nkey=value\nlast";
        byte[] bytes = kind switch
        {
            "utf8bom" => Concat(Utf8Bom, System.Text.Encoding.UTF8.GetBytes(Text + " ü")),
            "utf8" => System.Text.Encoding.UTF8.GetBytes(Text + " ü"),
            "utf16lebom" => Concat(Utf16LeBom, System.Text.Encoding.Unicode.GetBytes(Text)),
            "utf16le" => System.Text.Encoding.Unicode.GetBytes(Text),
            "utf16bebom" => Concat(Utf16BeBom, System.Text.Encoding.BigEndianUnicode.GetBytes(Text)),
            "utf32lebom" => Concat(Utf32LeBom, System.Text.Encoding.UTF32.GetBytes(Text)),
            "ascii" => System.Text.Encoding.ASCII.GetBytes(Text),
            _ => Concat(System.Text.Encoding.Latin1.GetBytes(Text), [0xE9, 0xFF, 0xA0]),
        };
        var detected = TextEdit.Detect(bytes, encoding);

        var roundTrip = TextEdit.Encode(TextEdit.Decode(bytes, detected), detected);

        roundTrip.Should().Equal(bytes);
    }

    [Fact]
    public void Encode_writes_the_bom_when_the_original_had_one()
    {
        var detected = TextEdit.Detect(Concat(Utf8Bom, "x"u8.ToArray()), "auto");

        TextEdit.Encode("y", detected).Should().Equal(0xEF, 0xBB, 0xBF, (byte)'y');
    }

    [Fact]
    public void Encode_writes_no_bom_when_the_original_had_none()
    {
        var detected = TextEdit.Detect("x"u8.ToArray(), "utf-8");

        TextEdit.Encode("y", detected).Should().Equal((byte)'y');
    }

    [Fact]
    public void Encode_writes_utf16_without_a_bom_when_the_original_had_none()
    {
        var detected = TextEdit.Detect([0x78, 0x00], "utf-16");

        TextEdit.Encode("y", detected).Should().Equal((byte)'y', 0x00);
    }

    /// <summary>
    /// "A new_string the encoding cannot represent (a non-ASCII character into an ascii file, a
    /// character above U+00FF into latin1, a lone surrogate anywhere) is refused." A lenient encoder
    /// writes '?' and the file silently says something the caller never sent.
    /// </summary>
    [Theory]
    [InlineData("ascii", "café")]
    [InlineData("latin1", "€ 10")]
    public void Encode_refuses_a_character_the_encoding_cannot_represent(string encoding, string text)
    {
        var detected = TextEdit.Detect("x"u8.ToArray(), encoding);

        var act = () => TextEdit.Encode(text, detected);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// "...a lone surrogate anywhere". The code unit travels as an int and the string is built
    /// here: a lone surrogate cannot survive an attribute argument (attribute strings are stored as
    /// UTF-8, so <c>[InlineData("a\ud800b")]</c> arrives as U+FFFD, which UTF-8 and UTF-16 can
    /// represent - the row would fail for the wrong reason, or pass for one).
    /// </summary>
    [Theory]
    [InlineData("utf-8", 0xD800)]
    [InlineData("utf-8", 0xDFFF)]
    [InlineData("utf-16", 0xDC00)]
    [InlineData("utf-16", 0xD83D)]     // the high half of an emoji with its low half missing
    [InlineData("latin1", 0xD800)]
    [InlineData("ascii", 0xDC00)]
    public void Encode_refuses_a_lone_surrogate(string encoding, int codeUnit)
    {
        var text = "a" + (char)codeUnit + "b";
        char.IsSurrogate(text[1]).Should().BeTrue("the row has to put a real lone surrogate in the string");
        var detected = TextEdit.Detect("x"u8.ToArray(), encoding);

        var act = () => TextEdit.Encode(text, detected);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>The same rule for a BOM-decided encoding: UTF-32 cannot hold a lone surrogate either.</summary>
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void Encode_refuses_a_lone_surrogate_into_a_utf32_file(int codeUnit)
    {
        var detected = TextEdit.Detect([0xFF, 0xFE, 0x00, 0x00, 0x78, 0x00, 0x00, 0x00], "auto");

        var act = () => TextEdit.Encode("a" + (char)codeUnit, detected);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>A well-formed surrogate pair is one character, not two lone halves: it encodes.</summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    public void Encode_accepts_a_well_formed_surrogate_pair(string encoding)
    {
        var detected = TextEdit.Detect("x"u8.ToArray(), encoding);

        var text = "a" + char.ConvertFromUtf32(0x1F600) + "b";
        var expected = encoding == "utf-8" ? new UTF8Encoding(false).GetBytes(text) : new UnicodeEncoding(false, false).GetBytes(text);

        TextEdit.Encode(text, detected).Should().Equal(expected);
    }

    [Fact]
    public void Latin1_encodes_a_character_up_to_u00ff()
    {
        var detected = TextEdit.Detect("x"u8.ToArray(), "latin1");

        TextEdit.Encode("caféÿ", detected).Should().Equal((byte)'c', (byte)'a', (byte)'f', 0xE9, 0xFF);
    }

    // ==== Apply: unique, missing, ambiguous, replace_all ============================================

    [Fact]
    public void Apply_replaces_a_unique_match_and_reports_one_replacement_and_its_line()
    {
        var outcome = TextEdit.Apply("alpha\nbeta\ngamma\n", "beta", "BETA", replaceAll: false);

        outcome.Text.Should().Be("alpha\nBETA\ngamma\n");
        outcome.Replacements.Should().Be(1);
        outcome.FirstLine.Should().Be(2);
        outcome.LineEndings.Should().Be("lf");
    }

    [Fact]
    public void Apply_refuses_an_old_string_that_is_not_there()
    {
        var act = () => TextEdit.Apply("alpha\nbeta\n", "delta", "x", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }

    /// <summary>
    /// "It occurs more than once and replace_all is false → saying how many times, and to add
    /// surrounding context to make it unique or pass replace_all:true."
    /// </summary>
    [Fact]
    public void Apply_refuses_an_ambiguous_match_saying_how_many_and_what_to_do()
    {
        var act = () => TextEdit.Apply("x=1\nx=1\nx=1\n", "x=1", "x=2", replaceAll: false);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("3", "the refusal says how many times old_string occurs");
        message.Should().Contain("replace_all", "and names the flag that would allow it");
        message.Should().ContainEquivalentOf("context", "and suggests adding surrounding context to make it unique");
    }

    [Fact]
    public void Apply_with_replace_all_replaces_every_occurrence_and_counts_them()
    {
        var outcome = TextEdit.Apply("a.b.c.d", ".", "-", replaceAll: true);

        outcome.Text.Should().Be("a-b-c-d");
        outcome.Replacements.Should().Be(3);
        outcome.FirstLine.Should().Be(1);
    }

    /// <summary>replace_all on a single occurrence is fine: it is a permission, not a requirement.</summary>
    [Fact]
    public void Apply_with_replace_all_accepts_a_single_occurrence()
    {
        var outcome = TextEdit.Apply("one two", "two", "2", replaceAll: true);

        outcome.Text.Should().Be("one 2");
        outcome.Replacements.Should().Be(1);
    }

    [Fact]
    public void Apply_with_replace_all_still_refuses_zero_occurrences()
    {
        var act = () => TextEdit.Apply("one two", "three", "3", replaceAll: true);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }

    /// <summary>
    /// "Occurrences are counted left to right, non-overlapping." In <c>aaa</c>, <c>aa</c> occurs
    /// once that way (twice if overlaps counted) - so it is unique and the edit goes through.
    /// </summary>
    [Fact]
    public void Apply_counts_non_overlapping_so_aa_in_aaa_is_unique()
    {
        var outcome = TextEdit.Apply("aaa", "aa", "X", replaceAll: false);

        outcome.Text.Should().Be("Xa", "the leftmost occurrence is replaced and the scan resumes after it");
        outcome.Replacements.Should().Be(1);
    }

    [Fact]
    public void Apply_counts_aa_in_aaaa_as_two_not_three()
    {
        var act = () => TextEdit.Apply("aaaa", "aa", "X", replaceAll: false);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("2").And.NotContain("3", "overlapping occurrences are not counted");
    }

    [Fact]
    public void Apply_with_replace_all_replaces_aaaa_as_two_non_overlapping_spans()
    {
        var outcome = TextEdit.Apply("aaaa", "aa", "X", replaceAll: true);

        outcome.Text.Should().Be("XX");
        outcome.Replacements.Should().Be(2);
    }

    /// <summary>"Matching is ordinal": no case folding, no culture equivalence (ß is not ss).</summary>
    [Theory]
    [InlineData("Setting=On", "setting=on")]
    [InlineData("Straße", "Strasse")]
    [InlineData("é", "é")]   // decomposed vs precomposed: canonically equal, ordinally not
    public void Apply_matches_ordinally(string text, string oldString)
    {
        var act = () => TextEdit.Apply(text, oldString, "x", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }

    [Theory]
    [InlineData("a\nb\nc\n", "a", 1)]
    [InlineData("a\nb\nc\n", "c", 3)]
    [InlineData("a\r\nb\r\nc\r\n", "c", 3)]
    [InlineData("a\nb\nc", "b\nc", 2)]          // a multi-line old_string: the line it starts on
    [InlineData("x y\nx\nx\n", "x\n", 2)]       // line 1 ends "y\n", so the first "x\n" starts line 2
    [InlineData("a\rb\rc", "c", 3)]             // R2 decision 6: a lone \r is a line break too (CR-only file)
    [InlineData("a\r\nb\nc\rd", "d", 4)]        // \r\n, \n and a lone \r each count once
    [InlineData("a\r\n\r\nb", "b", 3)]          // \r\n is one break, not two
    public void Apply_reports_the_1_based_line_of_the_first_replacement(string text, string oldString, int expected)
    {
        var outcome = TextEdit.Apply(text, oldString, "Z", replaceAll: true);

        outcome.FirstLine.Should().Be(expected);
    }

    /// <summary>R2 decision 9: a null new_string is refused, not read as a deletion.</summary>
    [Fact]
    public void Apply_refuses_a_null_new_string()
    {
        var act = () => TextEdit.Apply("abc", "b", null!, replaceAll: false);

        act.Should().Throw<ArgumentException>().WithMessage("*new_string*");
    }

    /// <summary>R2 decision 9: an empty string is how old_string is deleted.</summary>
    [Fact]
    public void Apply_with_an_empty_new_string_deletes_old_string()
    {
        var outcome = TextEdit.Apply("a=1;b=2", "b=2", "", replaceAll: false);

        outcome.Text.Should().Be("a=1;");
        outcome.Replacements.Should().Be(1);
    }

    [Fact]
    public void Apply_refuses_an_empty_old_string()
    {
        var act = () => TextEdit.Apply("abc", "", "x", replaceAll: false);

        act.Should().Throw<ArgumentException>().WithMessage("*old_string*");
    }

    [Fact]
    public void Apply_refuses_an_old_string_equal_to_the_new_one()
    {
        var act = () => TextEdit.Apply("abc", "b", "b", replaceAll: false);

        act.Should().Throw<ArgumentException>().WithMessage("*old_string*");
    }

    /// <summary>Equality is ordinal too: a case-only change is a change.</summary>
    [Fact]
    public void Apply_accepts_an_old_and_new_string_that_differ_only_in_case()
    {
        TextEdit.Apply("abc", "b", "B", replaceAll: false).Text.Should().Be("aBc");
    }

    // ==== Line endings ===============================================================================

    /// <summary>
    /// "lineEndings is crlf, lf, mixed or none (as found in the file before the edit)." A lone CR
    /// is not a line ending for this classification (only CRLF and bare LF are) - the note does not
    /// say; this is the reading that matches its two conversion rules, which only look at \r\n and
    /// bare \n.
    /// </summary>
    [Theory]
    [InlineData("a\r\nb\r\n", "crlf")]
    [InlineData("a\nb\n", "lf")]
    [InlineData("a\r\nb\nc", "mixed")]
    [InlineData("abc", "none")]
    [InlineData("", "none")]
    [InlineData("a\rb", "none")]
    [InlineData("a\r\nb\rc", "crlf")]
    [InlineData("a\nb\rc", "lf")]
    [InlineData("\nabc", "lf")]          // a bare LF as the very first character
    [InlineData("\r\nabc", "crlf")]
    public void LineEndingsOf_classifies_the_file(string text, string expected)
    {
        TextEdit.LineEndingsOf(text).Should().Be(expected);
    }

    [Fact]
    public void Apply_reports_the_files_line_endings_as_found_before_the_edit()
    {
        TextEdit.Apply("a\r\nb\r\n", "b", "c", replaceAll: false).LineEndings.Should().Be("crlf");
        TextEdit.Apply("a\r\nb\nc", "b", "x", replaceAll: false).LineEndings.Should().Be("mixed");
        TextEdit.Apply("abc", "b", "x", replaceAll: false).LineEndings.Should().Be("none");
    }

    /// <summary>
    /// Attempt 2: "the model usually sends \n; a Windows config file usually has \r\n". The LF
    /// old_string matches the CRLF text once converted, and the LF new_string goes in as CRLF, so
    /// the file's convention survives.
    /// </summary>
    [Fact]
    public void An_lf_old_string_matches_a_crlf_file_and_the_new_string_goes_in_as_crlf()
    {
        var outcome = TextEdit.Apply("[a]\r\nk=1\r\nv=2\r\n", "k=1\nv=2", "k=3\nv=4", replaceAll: false);

        outcome.Text.Should().Be("[a]\r\nk=3\r\nv=4\r\n");
        outcome.Replacements.Should().Be(1);
        outcome.FirstLine.Should().Be(2);
        outcome.LineEndings.Should().Be("crlf");
    }

    /// <summary>The other direction: CRLF from the caller against a file with no \r at all.</summary>
    [Fact]
    public void A_crlf_old_string_matches_an_lf_only_file_and_the_new_string_goes_in_as_lf()
    {
        var outcome = TextEdit.Apply("[a]\nk=1\nv=2\n", "k=1\r\nv=2", "k=3\r\nv=4", replaceAll: false);

        outcome.Text.Should().Be("[a]\nk=3\nv=4\n");
        outcome.Replacements.Should().Be(1);
    }

    /// <summary>
    /// "Only if that found nothing": attempt 1 is exact. In a mixed file where <c>p\nq</c> occurs
    /// once as written and <c>p\r\nq</c> once more, the exact match is unique and the edit goes
    /// through - attempt 2 is not run, so the converted occurrence is not counted against it.
    /// </summary>
    [Fact]
    public void An_exact_match_is_tried_first_and_the_converted_form_is_not_counted_against_it()
    {
        var outcome = TextEdit.Apply("p\nq\r\np\r\nq", "p\nq", "Z", replaceAll: false);

        outcome.Text.Should().Be("Z\r\np\r\nq");
        outcome.Replacements.Should().Be(1);
    }

    /// <summary>The converted old_string obeys the same uniqueness rule as an exact one.</summary>
    [Fact]
    public void A_converted_old_string_that_occurs_twice_is_ambiguous()
    {
        var act = () => TextEdit.Apply("a\r\nb\r\na\r\nb\r\n", "a\nb", "c", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("2").And.Contain("replace_all");
    }

    [Fact]
    public void Replace_all_with_a_converted_old_string_replaces_every_occurrence()
    {
        var outcome = TextEdit.Apply("a\r\nb\r\na\r\nb\r\n", "a\nb", "c\nd", replaceAll: true);

        outcome.Text.Should().Be("c\r\nd\r\nc\r\nd\r\n");
        outcome.Replacements.Should().Be(2);
    }

    /// <summary>
    /// "new_string is then written in the file's convention" - whichever attempt matched. A
    /// single-line old_string matches exactly in a CRLF file; a multi-line new_string still goes in
    /// as CRLF.
    /// </summary>
    [Fact]
    public void In_a_crlf_only_file_bare_lf_in_the_new_string_becomes_crlf_even_on_an_exact_match()
    {
        var outcome = TextEdit.Apply("a\r\nb\r\n", "b", "x\ny", replaceAll: false);

        outcome.Text.Should().Be("a\r\nx\r\ny\r\n");
    }

    /// <summary>Only BARE \n is converted: a \r\n the caller already sent is not doubled to \r\r\n.</summary>
    [Fact]
    public void In_a_crlf_only_file_a_crlf_already_in_the_new_string_is_kept_as_it_is()
    {
        var outcome = TextEdit.Apply("a\r\nb\r\n", "b", "x\r\ny\nz", replaceAll: false);

        outcome.Text.Should().Be("a\r\nx\r\ny\r\nz\r\n");
    }

    [Fact]
    public void In_an_lf_only_file_crlf_in_the_new_string_becomes_lf()
    {
        var outcome = TextEdit.Apply("a\nb\n", "b", "x\r\ny", replaceAll: false);

        outcome.Text.Should().Be("a\nx\ny\n");
    }

    /// <summary>
    /// R2 decision 6 REVERSES the first reading (CRLF in new_string became LF in a file with no
    /// line endings): a file with no line endings at all has no convention to follow, so new_string
    /// goes in as given.
    /// </summary>
    [Theory]
    [InlineData("2\r\n# note")]
    [InlineData("2\n# note")]
    [InlineData("2\r# note")]
    public void In_a_file_with_no_line_endings_the_new_string_is_written_as_given(string newString)
    {
        var outcome = TextEdit.Apply("key=1", "1", newString, replaceAll: false);

        outcome.Text.Should().Be("key=" + newString);
        outcome.LineEndings.Should().Be("none");
    }

    /// <summary>
    /// "In a file with mixed endings new_string is written as given" when attempt 1 matched - both
    /// an LF and a CRLF in the new string survive untouched.
    /// </summary>
    [Theory]
    [InlineData("x\ny")]
    [InlineData("x\r\ny")]
    public void In_a_mixed_file_an_exact_match_writes_the_new_string_as_given(string newString)
    {
        var outcome = TextEdit.Apply("a\r\nb\nc", "b", newString, replaceAll: false);

        outcome.Text.Should().Be("a\r\n" + newString + "\nc");
        outcome.LineEndings.Should().Be("mixed");
    }

    /// <summary>
    /// "...except that when attempt 2 was what matched, new_string gets the same conversion
    /// old_string got." The file contains \r\n, so the LF old_string was converted to CRLF to match;
    /// the LF new_string gets the same conversion.
    /// </summary>
    [Fact]
    public void In_a_mixed_file_a_converted_match_converts_the_new_string_the_same_way()
    {
        var outcome = TextEdit.Apply("a\r\nb\nc\r\nd", "c\nd", "X\nY", replaceAll: false);

        outcome.Text.Should().Be("a\r\nb\nX\r\nY");
        outcome.Replacements.Should().Be(1);
        outcome.FirstLine.Should().Be(3);
    }

    /// <summary>
    /// A file with lone CRs (classic Mac) contains a \r, so CRLF is not turned into LF; and it has no
    /// \r\n, so LF is not turned into CRLF: the new string goes in as given.
    /// </summary>
    [Fact]
    public void In_a_file_with_lone_cr_line_breaks_the_new_string_is_written_as_given()
    {
        var outcome = TextEdit.Apply("a\rb\rc", "b", "x\r\ny\nz", replaceAll: false);

        outcome.Text.Should().Be("a\rx\r\ny\nz\rc");
    }

    /// <summary>
    /// Attempt 2's conversions both have a condition: LF -> CRLF only when the file contains \r\n,
    /// CRLF -> LF only when it contains no \r. A lone-CR file meets neither, so an LF old_string is
    /// not converted and is simply not found.
    /// </summary>
    [Fact]
    public void In_a_file_with_lone_cr_line_breaks_an_lf_old_string_is_not_converted()
    {
        var act = () => TextEdit.Apply("a\rb\rc", "a\nb", "x", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }

    // ---- R2 decision 2: a match never splits a \r\n pair ------------------------------------------

    /// <summary>
    /// "An occurrence that starts with \n immediately after a \r ... is not a match. So
    /// old_string:"\nfoo" in a CRLF file is not found exactly, is found as "\r\nfoo" on the second
    /// attempt, and the result has no stray \r." Before: "a\r\r\nbar\r\nb".
    /// </summary>
    [Fact]
    public void An_lf_led_old_string_does_not_match_inside_a_crlf_pair_and_is_found_converted()
    {
        var outcome = TextEdit.Apply("a\r\nfoo\r\nb", "\nfoo", "\nbar", replaceAll: false);

        outcome.Text.Should().Be("a\r\nbar\r\nb");
        outcome.Replacements.Should().Be(1);
    }

    [Fact]
    public void Deleting_an_lf_led_line_from_a_crlf_file_leaves_no_stray_cr()
    {
        TextEdit.Apply("a\r\nfoo\r\nb", "\nfoo", "", replaceAll: false).Text.Should().Be("a\r\nb");
    }

    [Fact]
    public void Replace_all_of_lf_by_two_lfs_doubles_crlf_line_breaks_cleanly()
    {
        var outcome = TextEdit.Apply("a\r\nfoo\r\nb", "\n", "\n\n", replaceAll: true);

        outcome.Text.Should().Be("a\r\n\r\nfoo\r\n\r\nb");
        outcome.Replacements.Should().Be(2);
    }

    /// <summary>"...or ends with \r immediately before a \n, is not a match."</summary>
    [Fact]
    public void An_old_string_ending_in_cr_before_an_lf_is_not_a_match()
    {
        var act = () => TextEdit.Apply("a\r\nb", "a\r", "x", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }

    [Theory]
    [InlineData("a\r\nfoo\r\nb", "\nfoo", "\nbar", false)]
    [InlineData("a\r\nfoo\r\nb", "\nfoo", "", false)]
    [InlineData("a\r\nfoo\r\nb", "\n", "\n\n", true)]
    [InlineData("x\r\ny\r\n", "y\n", "z\n", false)]
    [InlineData("x\r\ny\r\nz", "\ny", "\nY", false)]
    public void No_edit_of_a_crlf_file_leaves_a_doubled_cr(string text, string oldString, string newString, bool replaceAll)
    {
        TextEdit.Apply(text, oldString, newString, replaceAll).Text.Should().NotContain("\r\r");
    }

    [Fact]
    public void An_lf_file_is_unaffected_by_the_crlf_pair_rule()
    {
        TextEdit.Apply("a\nfoo\nb", "\nfoo", "\nbar", replaceAll: false).Text.Should().Be("a\nbar\nb");
    }

    /// <summary>
    /// An LF file that also holds a lone \r is not "no \r at all", so neither conversion applies:
    /// new_string goes in as given.
    /// </summary>
    [Fact]
    public void In_an_lf_file_with_a_lone_cr_the_new_string_is_written_as_given()
    {
        TextEdit.Apply("a\nb\rc", "b", "x\r\ny", replaceAll: false).Text.Should().Be("a\nx\r\ny\rc");
    }

    /// <summary>An LF-led old_string at the very start of the text has no \r before it: it matches.</summary>
    [Fact]
    public void An_lf_led_old_string_at_the_start_of_the_text_is_a_match()
    {
        TextEdit.Apply("\nfoo\nb", "\nfoo", "\nbar", replaceAll: false).Text.Should().Be("\nbar\nb");
    }

    /// <summary>A \r at the very end of the text is followed by nothing, so it splits no pair: it matches.</summary>
    [Fact]
    public void An_old_string_ending_in_cr_at_the_end_of_the_text_is_a_match()
    {
        TextEdit.Apply("a\r", "a\r", "x", replaceAll: false).Text.Should().Be("x");
    }

    /// <summary>In a lone-CR file there is no \r\n pair to split: a \r not followed by \n matches as usual.</summary>
    [Fact]
    public void A_lone_cr_file_is_unaffected_by_the_crlf_pair_rule()
    {
        TextEdit.Apply("a\rfoo\rb", "\rfoo", "\rbar", replaceAll: false).Text.Should().Be("a\rbar\rb");
        TextEdit.Apply("a\rb", "a\r", "x", replaceAll: false).Text.Should().Be("xb");
    }

    /// <summary>Attempt 2 converts only the old_string's line endings, never other characters.</summary>
    [Fact]
    public void Attempt_two_does_not_match_when_the_text_differs_beyond_its_line_endings()
    {
        var act = () => TextEdit.Apply("k=1\r\nv=2\r\n", "k=1\nv=3", "x", replaceAll: false);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().ContainEquivalentOf("not found");
    }
}
