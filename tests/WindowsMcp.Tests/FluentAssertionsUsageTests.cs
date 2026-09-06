using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace WindowsMcp.Tests;

/// <summary>
/// A meta test over the test sources themselves: it looks for the one FluentAssertions misuse
/// this suite has produced five times, and would keep producing.
/// <para>
/// <c>collection.Should().Equal("a", "the reason it must be a")</c> compiles — <c>Equal</c> has a
/// <c>params T[]</c> overload, so the reason is silently read as a SECOND EXPECTED ELEMENT and the
/// assertion asks for a two-item collection. It fails with "expected 2 items, found 1", which
/// reads like a production bug, and if the collection ever did hold two items it would pass for
/// the wrong reason. The because-string overload needs the expected items as ONE argument:
/// <c>Should().Equal(new[] { "a" }, "the reason it must be a")</c>.
/// </para>
/// <para>
/// The detector is a heuristic (nothing in the type system separates a reason from an element),
/// so it fires only on the shape that is nearly always the slip: an all-string-literal argument
/// list whose LAST literal reads like a sentence — four or more words, and one of the words that
/// only ever appears in a reason. <see cref="The_detector_still_recognises_the_slip"/> and
/// <see cref="The_detector_leaves_a_multi_word_expected_value_alone"/> keep it honest in both
/// directions.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class FluentAssertionsUsageTests
{
    /// <summary>A C# string literal, optionally interpolated or verbatim; escapes respected.</summary>
    private const string StringLiteral = @"(?:\$@?|@\$?)?""(?:[^""\\\r\n]|\\.)*""";

    /// <summary>A call whose whole argument list is string literals — the params overload's shape.</summary>
    private static readonly Regex ParamsFormCall = new(
        @"\.(?:Equal|ContainInOrder|ContainInConsecutiveOrder)\(\s*" + StringLiteral +
        @"(?:\s*,\s*" + StringLiteral + @")+\s*\)", RegexOptions.Compiled);

    private static readonly Regex OneLiteral = new(StringLiteral, RegexOptions.Compiled);

    /// <summary>Words that turn up in a reason and not in a control name, a rendered line or a path.</summary>
    private static readonly Regex BecauseCue = new(
        @"\b(because|must|should|would|could|cannot|never|always|otherwise|not|so|instead of|rather than|the caller|which is)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every params-form call in <paramref name="source"/> whose last literal reads like a reason.</summary>
    private static List<string> Suspects(string source, string label)
    {
        var found = new List<string>();
        foreach (Match call in ParamsFormCall.Matches(source))
        {
            var literals = OneLiteral.Matches(call.Value);
            var inner = literals[^1].Value.TrimStart('$', '@').Trim('"');
            var words = inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4 || !BecauseCue.IsMatch(inner)) continue;

            int line = source.Take(call.Index).Count(c => c == '\n') + 1;
            found.Add($"{label}({line}): {call.Value.ReplaceLineEndings(" ")}");
        }
        return found;
    }

    private static string TestsRoot()
    {
        // The same walk ToolInventoryTests and ServerInfoTests use - no fragile ../../.. count.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test reads the test sources, so it must run from inside the repo");
        return Path.Combine(dir!.FullName, "tests");
    }

    private static string[] TestSources() =>
        Directory.EnumerateFiles(TestsRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && Path.GetFileName(p) != "FluentAssertionsUsageTests.cs")   // its own samples are deliberate
            .ToArray();

    [Fact]
    public void The_detector_still_recognises_the_slip()
    {
        // The exact line that shipped five times, most recently in InputServiceKeyHoldTests: a
        // detector that stopped matching it would make the sweep below vacuously green.
        const string sample = """
            sink.Log.Should().Equal($"down:{key}", "the sink resolves the name; the service must not rewrite it");
            """;

        Suspects(sample, "sample").Should().ContainSingle().Which.Should().Contain("down:");
    }

    [Fact]
    public void The_detector_leaves_a_multi_word_expected_value_alone()
    {
        // Rendered lines and window titles are long, spaced and legitimate LAST elements. Flagging
        // them would make this test noise, and noise gets suppressed.
        const string sample = """
            lines.Should().Equal("  window \"Notes\"", "  window \"Other Browser\": no page document found under this window");
            argv.Should().Equal("/c", "echo", "a quoted b");
            names.Should().Equal("Calculator", "Microsoft Edge");
            """;

        Suspects(sample, "sample").Should().BeEmpty();
    }

    [Fact]
    public void No_test_passes_a_reason_where_an_expected_element_is_read()
    {
        var sources = TestSources();
        sources.Length.Should().BeGreaterThan(50, "the sweep must actually have found the test tree");

        var texts = sources.Select(File.ReadAllText).ToArray();
        var suspects = sources.Zip(texts, (path, text) => Suspects(text, Path.GetFileName(path)))
            .SelectMany(s => s)
            .ToList();
        var callsScanned = texts.Sum(text => ParamsFormCall.Matches(text).Count);

        callsScanned.Should().BeGreaterThan(20,
            "the params-form pattern must still match the calls this suite really makes");
        suspects.Should().BeEmpty(
            "Equal/ContainInOrder read a trailing string as another EXPECTED element, not as a reason; "
            + "pass the expectation as one argument instead - Should().Equal(new[] { x }, \"the reason\"). "
            + "Offenders: " + string.Join(" | ", suspects));
    }
}
