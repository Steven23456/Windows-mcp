using System.Text;
using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-13's pure core: every sanitisation rule, with no UIA and no desktop (roadmap C10). The
/// end-to-end wiring proof lives in <see cref="UIAutomationServiceTests"/> (Category
/// UIAutomation) - this file is the fast regression net for the rules themselves.
/// </summary>
/// <remarks>
/// Two deliberate shapes here, both forced by what is under test:
/// <para>1. Every non-ASCII input is built from numeric code points via <see cref="Ch"/> /
/// <see cref="Cp"/> / <see cref="Cps"/> rather than written as a literal, so the file is pure
/// ASCII and no source-encoding or editor normalisation can quietly change the thing being
/// asserted. A row that says <c>Ch(0xD83D)</c> is unambiguously a lone high surrogate.</para>
/// <para>2. The cases are a table keyed by an ASCII id and the theories carry only the id,
/// because a lone surrogate CANNOT be passed through <c>[InlineData]</c>: custom-attribute
/// arguments are encoded as UTF-8 in metadata, so the compiler silently rewrites a lone high
/// surrogate into TWO U+FFFD chars (measured on this toolchain, 2026-09-04). A theory written
/// the obvious way would test the replacement character instead of the surrogate and would pass
/// against any implementation at all.</para>
/// </remarks>
[Trait("Category", "Unit")]
public class UiTextTests
{
    /// <summary>One UTF-16 code unit - the only way to write a LONE surrogate.</summary>
    private static string Ch(int codeUnit) => ((char)codeUnit).ToString();

    /// <summary>One code point, encoded as a surrogate pair when it is supplementary.</summary>
    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static string Cps(params int[] codePoints)
    {
        var sb = new StringBuilder();
        foreach (var cp in codePoints) sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    private static readonly string Pua = Ch(0xE0B0);          // powerline / codicon glyph (VS Code sidebar)
    private static readonly string Emoji = Cp(0x1F600);       // grinning face, a valid surrogate pair
    private static readonly string Replacement = Ch(0xFFFD);

    private sealed record Case(string Id, string? Input, string Expected, string Why);

    // R1 rows. Input -> Expected is the whole contract; Why names the requirement it encodes.
    private static readonly Case[] Cases =
    [
        // --- R1.1 null and empty ----------------------------------------------------------------
        new("null",                 null,                       "",                 "null becomes the empty string, never null"),
        new("empty",                "",                         "",                 "the empty string is unchanged"),
        new("whitespace-only",      "   \t\r\n ",               "",                 "a string of nothing but whitespace trims away to empty"),

        // --- R1.2 private use area, BMP ---------------------------------------------------------
        new("pua-codicon-prefix",   Pua + " Explorer",          "Explorer",         "a VS Code codicon prefix is stripped and the space it left is trimmed"),
        new("pua-lower-boundary",   "a" + Ch(0xE000) + "b",     "ab",               "U+E000 is the first private use code point"),
        new("pua-upper-boundary",   "a" + Ch(0xF8FF) + "b",     "ab",               "U+F8FF is the last private use code point"),
        new("pua-just-above-kept",  "a" + Ch(0xF900) + "b",     "a" + Ch(0xF900) + "b", "U+F900 is a CJK compatibility ideograph, one past the PUA, and stays"),
        new("pua-only",             Pua + Ch(0xE0B1),           "",                 "a name that is nothing but icon glyphs becomes empty"),
        new("pua-mid-string",       "Save" + Ch(0xE001) + " As", "Save As",         "an icon glyph in the middle is removed without touching the rest"),

        // --- R1.2 private use area, supplementary planes 15 and 16 ------------------------------
        new("pua-plane15-start",    "a" + Cp(0xF0000) + "b",    "ab",               "U+F0000 is the first supplementary private use code point"),
        new("pua-plane15-end",      "a" + Cp(0xFFFFD) + "b",    "ab",               "U+FFFFD is the last usable plane 15 private use code point"),
        new("pua-plane16-end",      "a" + Cp(0x10FFFD) + "b",   "ab",               "U+10FFFD is the last usable plane 16 private use code point"),
        new("supplementary-kept",   "a" + Cp(0x20000) + "b",    "a" + Cp(0x20000) + "b", "U+20000 is CJK extension B, a supplementary pair that is not private use"),

        // --- R1.3 surrogates --------------------------------------------------------------------
        new("lone-high-alone",      Ch(0xD83D),                 Replacement,        "a high surrogate with nothing after it becomes U+FFFD"),
        new("lone-high-then-ascii", Ch(0xD83D) + "x",           Replacement + "x",  "a high surrogate followed by a non-surrogate becomes U+FFFD"),
        new("lone-high-then-high",  Ch(0xD83D) + Ch(0xD83D),    Replacement + Replacement, "two high surrogates in a row are two lone surrogates"),
        new("lone-high-at-end",     "ok" + Ch(0xD83D),          "ok" + Replacement, "a pair truncated at the end of a title becomes U+FFFD"),
        new("lone-low-mid",         "a" + Ch(0xDC00) + "b",     "a" + Replacement + "b", "a low surrogate with no high surrogate before it becomes U+FFFD"),
        new("lone-low-at-start",    Ch(0xDE00) + "ok",          Replacement + "ok", "a leading low surrogate becomes U+FFFD"),
        new("lone-low-boundary",    "a" + Ch(0xDFFF) + "b",     "a" + Replacement + "b", "U+DFFF is the last low surrogate and is lone here"),
        new("valid-pair",           Emoji,                      Emoji,              "a valid surrogate pair is preserved intact"),
        new("emoji-in-sentence",    "hi " + Emoji + " there",   "hi " + Emoji + " there", "an emoji inside text is preserved with its neighbours"),
        new("emoji-window-title",   Emoji + " Untitled - Notepad", Emoji + " Untitled - Notepad", "the checklist done-when case: an emoji window title survives"),
        new("flag-pair",            Cp(0x1F1EC) + Cp(0x1F1E7),  Cp(0x1F1EC) + Cp(0x1F1E7), "a regional-indicator flag is two valid pairs and is preserved"),
        new("zwj-sequence",         Cp(0x1F469) + Ch(0x200D) + Cp(0x1F4BB), Cp(0x1F469) + Ch(0x200D) + Cp(0x1F4BB), "a zero-width joiner inside an emoji sequence is preserved"),
        new("variation-selector",   Ch(0x2764) + Ch(0xFE0F),    Ch(0x2764) + Ch(0xFE0F), "an emoji variation selector is preserved"),

        // --- R1.4 control characters ------------------------------------------------------------
        new("nul-dropped",          "a" + Ch(0x0000) + "b",     "ab",               "U+0000 is dropped"),
        new("c0-dropped",           "x" + Ch(0x0001) + "y",     "xy",               "a C0 control in the middle is dropped"),
        new("c0-upper-boundary",    "a" + Ch(0x001F) + "b",     "ab",               "U+001F is the last C0 control and is dropped"),
        new("tab-kept",             "a\tb",                     "a\tb",             "tab is kept as-is"),
        new("lf-kept",              "a\nb",                     "a\nb",             "LF is kept as-is"),
        new("crlf-kept",            "line1\r\nline2",           "line1\r\nline2",   "CRLF is kept as-is, both characters"),
        new("del-dropped",          "a" + Ch(0x007F) + "b",     "ab",               "U+007F DEL is dropped"),
        new("c1-lower-boundary",    "a" + Ch(0x0080) + "b",     "ab",               "U+0080 is the first C1 control and is dropped"),
        new("c1-upper-boundary",    "a" + Ch(0x009F) + "b",     "ab",               "U+009F is the last C1 control and is dropped"),
        new("nbsp-mid-kept",        "a" + Ch(0x00A0) + "b",     "a" + Ch(0x00A0) + "b", "U+00A0 is one past the C1 range, so a non-breaking space mid-string stays"),
        new("nbsp-edges-trimmed",   Ch(0x00A0) + "x" + Ch(0x00A0), "x",     "Trim() is Unicode-whitespace-aware, so a name padded with non-breaking spaces still trims"),
        new("tilde-kept",           "a~b",                      "a~b",              "U+007E is one below DEL and is ordinary text"),

        // --- R1.5 trimming, after stripping -----------------------------------------------------
        new("trim-both-ends",       "  hi\t",                   "hi",               "leading and trailing whitespace is trimmed"),
        new("trim-after-strip",     Pua + "  Explorer  ",       "Explorer",         "trimming happens after stripping, so the space the glyph left goes too"),
        new("trim-after-control",   Ch(0x0001) + " name " + Ch(0x0002), "name",     "the space a dropped control left at the edge is trimmed too"),
        new("inner-spaces-kept",    "a  b",                     "a  b",             "inner runs of spaces are not touched"),

        // --- R1.6 everything else is preserved --------------------------------------------------
        new("plain-ascii",          "Hello, world",             "Hello, world",     "ordinary text is returned unchanged"),
        new("arabic",               Cps(0x0645, 0x0631, 0x062D, 0x0628, 0x0627), Cps(0x0645, 0x0631, 0x062D, 0x0628, 0x0627), "Arabic is preserved"),
        new("hebrew",               Cps(0x05E9, 0x05DC, 0x05D5, 0x05DD), Cps(0x05E9, 0x05DC, 0x05D5, 0x05DD), "Hebrew is preserved"),
        new("cjk",                  Cps(0x65E5, 0x672C, 0x8A9E), Cps(0x65E5, 0x672C, 0x8A9E), "CJK is preserved"),
        new("combining-mark",       "e" + Ch(0x0301) + "cole",  "e" + Ch(0x0301) + "cole", "a combining acute accent is preserved, not normalised away"),
        new("rtl-mark",             "a" + Ch(0x200F) + "b",     "a" + Ch(0x200F) + "b", "a right-to-left mark is a format character, not a control, and is preserved"),

        // --- R1 mixed ---------------------------------------------------------------------------
        new("mixed-everything",
            Pua + "  Tab " + Ch(0x0001) + "bar\t" + Emoji + " " + Ch(0xD83D) + " end" + Ch(0x009F) + "  ",
            "Tab bar\t" + Emoji + " " + Replacement + " end",
            "one string exercising strip, drop, repair and trim together in that order"),
    ];

    private static readonly Dictionary<string, Case> ById = Cases.ToDictionary(c => c.Id);

    public static TheoryData<string> CaseIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var c in Cases) data.Add(c.Id);
            return data;
        }
    }

    // ---- R1 - the rules ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Sanitize_applies_the_rule_for_each_case(string id)
    {
        var c = ById[id];

        var actual = UiText.Sanitize(c.Input);

        actual.Should().Be(c.Expected, c.Why);
    }

    // Ordering: strip first, THEN trim. Were it the other way round, the codicon's trailing space
    // would survive as a leading space and every such name would render indented.
    [Fact]
    public void Sanitize_strips_private_use_before_it_trims()
    {
        var result = UiText.Sanitize(Pua + " Explorer");

        result.Should().Be("Explorer");
        result.Should().NotStartWith(" ", "the space the stripped glyph left must be trimmed, so the strip runs first");
    }

    // Repair before trim, for the same reason in the other direction: U+FFFD is not whitespace,
    // so a repaired character at the edge must still be there afterwards.
    [Fact]
    public void Sanitize_keeps_a_repaired_surrogate_at_the_edge()
    {
        UiText.Sanitize(" " + Ch(0xD83D) + " ").Should().Be(Replacement);
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Sanitize_is_idempotent(string id)
    {
        var once = UiText.Sanitize(ById[id].Input);

        UiText.Sanitize(once).Should().Be(once, "sanitising an already sanitised string must change nothing");
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Sanitize_output_has_no_private_use_and_no_lone_surrogate(string id)
    {
        var output = UiText.Sanitize(ById[id].Input);

        PrivateUseCodePoints(output).Should().BeEmpty("no private use code point may reach the model");
        HasLoneSurrogate(output).Should().BeFalse("no lone surrogate may reach the model");
    }

    [Fact]
    public void Sanitize_returns_an_equal_string_when_nothing_needs_fixing()
    {
        var clean = "Open " + Cps(0x65E5, 0x672C, 0x8A9E) + " file " + Ch(0x2014) + " done";

        UiText.Sanitize(clean).Should().Be(clean);
    }

    // The no-change fast path is a contract, not an accident: get_state sanitises the Name and
    // Value of every element in a tree that runs to hundreds of nodes, and almost all of them
    // need no change at all. Sanitize returns THE INPUT INSTANCE then - it allocates no
    // StringBuilder, and String.Trim returns `this` when there is nothing to trim. Pinned
    // because it is otherwise invisible: an implementation that copied every string
    // unconditionally passed every other test in this file (measured, bite B5).
    [Fact]
    public void Sanitize_returns_the_same_instance_when_nothing_needs_fixing()
    {
        var clean = "Open " + Cps(0x65E5, 0x672C, 0x8A9E) + " file " + Ch(0x2014) + " done";

        UiText.Sanitize(clean).Should().BeSameAs(clean,
            "a string that needs nothing must come back untouched, not copied");
    }

    // ---- R2 - what System.Text.Json actually does, and what sanitising buys -------------------

    /// <summary>
    /// MEASUREMENT, not a requirement on our code: this pins the platform behaviour that motivated
    /// A-13. Measured on .NET 10.0.11 (2026-09-04): <c>JsonSerializer.Serialize</c> does NOT throw
    /// on a lone surrogate - the default JavaScriptEncoder substitutes U+FFFD - so the checklist's
    /// open question ("throws vs. U+FFFD") answers as U+FFFD. The damage is therefore silent and
    /// lossy rather than fatal: the value the model receives differs from the value UIA reported,
    /// with nothing in the response saying so. Sanitising up front makes that substitution explicit
    /// and keeps serialise/deserialise a round trip. If a future runtime turns this back into a
    /// throw, this test fails and A-13 becomes a crash fix again.
    /// </summary>
    [Fact]
    public void Platform_JsonSerializer_replaces_a_lone_surrogate_and_does_not_throw()
    {
        var bad = Ch(0xD83D) + " bad";

        var serialize = () => JsonSerializer.Serialize(bad);
        serialize.Should().NotThrow("this is the measured .NET 10 behaviour A-13 is built on");

        var json = JsonSerializer.Serialize(bad);
        json.Should().Be("\"\\uFFFD bad\"", "the default encoder escapes the substituted replacement character");
        JsonSerializer.Deserialize<string>(json).Should().NotBe(bad, "the platform silently loses the original char");
        JsonSerializer.Deserialize<string>(json).Should().Be(Replacement + " bad");
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Sanitized_text_serialises_without_throwing_and_round_trips(string id)
    {
        var sanitized = UiText.Sanitize(ById[id].Input);

        var serialize = () => JsonSerializer.Serialize(sanitized);
        serialize.Should().NotThrow();

        var json = JsonSerializer.Serialize(sanitized);
        JsonSerializer.Deserialize<string>(json).Should().Be(sanitized,
            "a sanitised string must survive the encoder unchanged, so what the model reads is what UIA reported");
    }

    // ---- R4 - the DTO the tools actually serialise --------------------------------------------

    [Fact]
    public void ElementInfo_built_from_sanitized_text_serialises_and_round_trips()
    {
        // JsonSerializer.Serialize(info) with no options is exactly what UIAutomationTools does.
        var info = new ElementInfo(
            ElementId: "el_1",
            Name: UiText.Sanitize(Ch(0xD83D) + " bad"),
            ControlType: "Document",
            IsEnabled: true,
            IsOffscreen: false,
            Bounds: new Bounds(0, 0, 10, 10),
            Value: UiText.Sanitize(Pua + " val " + Ch(0xDC00)),
            IsChecked: null,
            IsSelected: null);

        var serialize = () => JsonSerializer.Serialize(info);
        serialize.Should().NotThrow();

        var json = JsonSerializer.Serialize(info);
        var back = JsonSerializer.Deserialize<ElementInfo>(json);

        back.Should().NotBeNull();
        back!.Name.Should().Be(Replacement + " bad", "the lone surrogate was repaired before serialisation, not by it");
        back.Value.Should().Be("val " + Replacement, "the icon glyph was stripped and the leading space it left trimmed");
        json.Should().Contain("\"Name\":\"\\uFFFD bad\"");
    }

    [Fact]
    public void ElementTree_of_sanitized_elements_serialises_without_a_private_use_glyph()
    {
        var root = new ElementTree(
            new ElementInfo("el_1", UiText.Sanitize(Pua + " Explorer"), "Pane", true, false, null, null, null, null),
            [new ElementTree(
                new ElementInfo("el_2", UiText.Sanitize("title " + Emoji), "Document", true, false, null,
                    UiText.Sanitize("body" + Ch(0xD83D)), null, null),
                [])]);

        var serialize = () => JsonSerializer.Serialize(root);

        serialize.Should().NotThrow();
        JsonSerializer.Serialize(root).Should().Contain("Explorer").And.NotContain("\\uE0B0",
            "the codicon must be gone before the encoder ever sees it");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static int[] PrivateUseCodePoints(string s) =>
        s.EnumerateRunes().Where(r => IsPrivateUse(r.Value)).Select(r => r.Value).ToArray();

    private static bool IsPrivateUse(int cp) =>
        (cp >= 0xE000 && cp <= 0xF8FF) ||        // BMP private use area
        (cp >= 0xF0000 && cp <= 0xFFFFD) ||      // supplementary private use area-A (plane 15)
        (cp >= 0x100000 && cp <= 0x10FFFD);      // supplementary private use area-B (plane 16)

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }
}
