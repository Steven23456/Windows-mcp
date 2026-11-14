using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-9: <c>file_search</c>'s content mode at the tool layer (docs/design/E-9-file-edit-content-search.md,
/// "file_search content mode"). With <c>content</c> the tool calls
/// <see cref="IFileSystemService.SearchContentAsync"/> and returns the documented object; without it
/// the call and the reply are byte-for-byte today's. The refusals run before anything is enumerated.
/// </summary>
[Trait("Category", "Unit")]
public class FileToolsContentSearchTests
{
    private const string Root = @"C:\data";

    private static FileTools MakeTools(IFileSystemService fs) =>
        new(fs, new Mock<IInputService>().Object, new Mock<IFileStreamService>().Object);

    private static readonly ContentSearchResult Empty = new([], 0, 0, false, 100);

    private static Mock<IFileSystemService> Service(ContentSearchResult? result = null)
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.SearchContentAsync(It.IsAny<ContentSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result ?? Empty);
        mock.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
                It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileSearchHit>());
        return mock;
    }

    private static void NothingEnumerated(Mock<IFileSystemService> mock)
    {
        mock.Verify(s => s.SearchContentAsync(It.IsAny<ContentSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==== with content ===============================================================================

    [Fact]
    public async Task FileSearch_with_content_calls_the_content_search_with_every_filter()
    {
        var mock = Service();
        const string Iso = "2026-01-15T10:00:00Z";
        var since = DateTime.Parse(Iso, null, System.Globalization.DateTimeStyles.RoundtripKind);

        await MakeTools(mock.Object).FileSearch(Root, "*.config", min_size: 10, modified_since: Iso,
            content: "connectionString", max_matches: 25, context_lines: 2);

        mock.Verify(s => s.SearchContentAsync(
            new ContentSearchRequest(Root, "connectionString", "*.config", 10, since, 25, 2),
            It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never,
            "content mode does not also run the name search");
    }

    [Fact]
    public async Task FileSearch_with_content_defaults_to_a_hundred_matches_and_no_context()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileSearch(Root, content: "needle");

        mock.Verify(s => s.SearchContentAsync(new ContentSearchRequest(Root, "needle", null, null, null, 100, 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The regex is the caller's: leading and trailing spaces are part of it and are not trimmed.</summary>
    [Fact]
    public async Task FileSearch_passes_the_content_pattern_as_given()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileSearch(Root, content: " key = ");

        mock.Verify(s => s.SearchContentAsync(It.Is<ContentSearchRequest>(r => r.Content == " key = "),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileSearch_with_content_forwards_the_cancellation_token()
    {
        var mock = Service();
        using var cts = new CancellationTokenSource();

        await MakeTools(mock.Object).FileSearch(Root, content: "needle", ct: cts.Token);

        mock.Verify(s => s.SearchContentAsync(It.IsAny<ContentSearchRequest>(), cts.Token), Times.Once);
    }

    /// <summary>
    /// "Result (content mode): {matches:[{path,line,text,before,after}], filesSearched, filesSkipped,
    /// truncated, maxMatches}" - camelCase, unlike the PascalCase rows of the name search.
    /// </summary>
    [Fact]
    public async Task FileSearch_with_content_returns_the_documented_object()
    {
        var result = new ContentSearchResult(
            [new ContentMatch(@"C:\data\web.config", 12, "<add key=\"x\" />", ["<appSettings>"], ["</appSettings>"])],
            FilesSearched: 37, FilesSkipped: 2, Truncated: true, MaxMatches: 1);
        var mock = Service(result);

        var json = await MakeTools(mock.Object).FileSearch(Root, content: "key", max_matches: 1, context_lines: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("matches", "filesSearched", "filesSkipped", "truncated", "maxMatches");
        root.GetProperty("filesSearched").GetInt32().Should().Be(37);
        root.GetProperty("filesSkipped").GetInt32().Should().Be(2);
        root.GetProperty("truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("maxMatches").GetInt32().Should().Be(1);
        var match = root.GetProperty("matches").EnumerateArray().Single();
        match.EnumerateObject().Select(p => p.Name).Should().Equal("path", "line", "text", "before", "after");
        match.GetProperty("path").GetString().Should().Be(@"C:\data\web.config");
        match.GetProperty("line").GetInt32().Should().Be(12);
        match.GetProperty("text").GetString().Should().Be("<add key=\"x\" />");
        match.GetProperty("before").EnumerateArray().Select(e => e.GetString()).Should().Equal("<appSettings>");
        match.GetProperty("after").EnumerateArray().Select(e => e.GetString()).Should().Equal("</appSettings>");
    }

    /// <summary>"before / after are present only when context_lines > 0" - absent, not null.</summary>
    [Fact]
    public async Task FileSearch_with_content_omits_before_and_after_without_context()
    {
        var result = new ContentSearchResult([new ContentMatch(@"C:\data\a.txt", 3, "needle")], 1, 0, false, 100);
        var mock = Service(result);

        var json = await MakeTools(mock.Object).FileSearch(Root, content: "needle");

        using var doc = JsonDocument.Parse(json);
        var match = doc.RootElement.GetProperty("matches")[0];
        match.EnumerateObject().Select(p => p.Name).Should().Equal("path", "line", "text");
    }

    [Fact]
    public async Task FileSearch_with_content_and_no_match_returns_an_empty_matches_array()
    {
        var mock = Service(new ContentSearchResult([], 5, 0, false, 100));

        var json = await MakeTools(mock.Object).FileSearch(Root, content: "nothing");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("matches").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("filesSearched").GetInt32().Should().Be(5);
    }

    // ==== without content: today's behaviour =========================================================

    /// <summary>"null/blank = today's behaviour, unchanged": the same call, and the reply is the serialised hit array.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FileSearch_without_content_is_todays_name_search_byte_for_byte(string? content)
    {
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        FileSearchHit[] hits = [new(@"C:\data\a.txt", 42, when), new(@"C:\data\b.txt", 7, when)];
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.SearchAsync(Root, "*.txt", 5, null, true, It.IsAny<CancellationToken>())).ReturnsAsync(hits);

        var json = await MakeTools(mock.Object).FileSearch(Root, "*.txt", min_size: 5, find_duplicates: true, content: content);

        json.Should().Be(JsonSerializer.Serialize(hits), "the reply without content is exactly what it was before E-9");
        mock.Verify(s => s.SearchAsync(Root, "*.txt", 5, null, true, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.SearchContentAsync(It.IsAny<ContentSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==== refused before anything is enumerated =====================================================

    /// <summary>"an invalid regex (naming content and quoting the parser's message)".</summary>
    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData("a{2,1}")]
    [InlineData(@"\")]
    public async Task FileSearch_refuses_an_invalid_regex_naming_content_and_quoting_the_parser(string content)
    {
        var mock = Service();
        string parser;
        try { _ = new Regex(content); parser = ""; }
        catch (ArgumentException ex) { parser = ex.Message; }
        parser.Should().NotBeEmpty("the row has to be an invalid pattern");

        var act = () => MakeTools(mock.Object).FileSearch(Root, content: content);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("content");
        message.Should().Contain(parser, "the parser's own message says what is wrong and where");
        NothingEnumerated(mock);
    }

    /// <summary>"max_matches ... 1-1000", "checked always, as settle_ms is" - with or without content.</summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1001, null)]
    [InlineData(0, "needle")]
    [InlineData(1001, "needle")]
    public async Task FileSearch_refuses_max_matches_outside_1_to_1000(int maxMatches, string? content)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileSearch(Root, content: content, max_matches: maxMatches);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("max_matches");
        NothingEnumerated(mock);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(6, null)]
    [InlineData(-1, "needle")]
    [InlineData(6, "needle")]
    public async Task FileSearch_refuses_context_lines_outside_0_to_5(int contextLines, string? content)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileSearch(Root, content: content, context_lines: contextLines);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("context_lines");
        NothingEnumerated(mock);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1000, 5)]
    public async Task FileSearch_accepts_the_ends_of_both_ranges(int maxMatches, int contextLines)
    {
        var mock = Service();

        await MakeTools(mock.Object).FileSearch(Root, content: "needle", max_matches: maxMatches, context_lines: contextLines);

        mock.Verify(s => s.SearchContentAsync(
            It.Is<ContentSearchRequest>(r => r.MaxMatches == maxMatches && r.ContextLines == contextLines),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Second review pass, decision C: with content, pattern is a file-name glob; one with \ or / is
    /// refused naming pattern, before the service is asked for anything.
    /// </summary>
    [Theory]
    [InlineData(@"sub\*.txt")]
    [InlineData("sub/*.txt")]
    [InlineData(@"..\*.txt")]
    public async Task FileSearch_with_content_refuses_a_pattern_with_a_directory_in_it(string pattern)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileSearch(Root, pattern, content: "needle");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("pattern");
        NothingEnumerated(mock);
    }

    /// <summary>Decision C: "The name search is unchanged" - the same pattern without content still goes to SearchAsync.</summary>
    [Theory]
    [InlineData(@"sub\*.txt")]
    [InlineData("sub/*.txt")]
    public async Task FileSearch_without_content_still_passes_a_pattern_with_a_directory_in_it(string pattern)
    {
        var mock = Service();

        await MakeTools(mock.Object).FileSearch(Root, pattern);

        mock.Verify(s => s.SearchAsync(Root, pattern, null, null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileSearch_refuses_content_with_find_duplicates()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileSearch(Root, find_duplicates: true, content: "needle");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("content").And.Contain("find_duplicates");
        NothingEnumerated(mock);
    }

    /// <summary>Blank content is "not given", so it does not collide with find_duplicates.</summary>
    [Fact]
    public async Task FileSearch_with_blank_content_and_find_duplicates_is_the_duplicate_search()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileSearch(Root, find_duplicates: true, content: " ");

        mock.Verify(s => s.SearchAsync(Root, null, null, null, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("data")]
    [InlineData(@"\\?\C:\data")]
    public async Task FileSearch_with_content_still_refuses_a_root_that_is_not_plain_and_absolute(string root)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileSearch(root, content: "needle");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("root");
        NothingEnumerated(mock);
    }

    // ==== the surface ================================================================================

    private static MethodInfo Method => typeof(FileTools).GetMethod(nameof(FileTools.FileSearch))!;

    /// <summary>"file_search gains three parameters, appended after find_duplicates" - existing positional callers keep working.</summary>
    [Fact]
    public void FileSearch_appends_the_three_parameters_after_find_duplicates()
    {
        var parameters = Method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();

        parameters.Select(p => p.Name).Should().Equal(
            "root", "pattern", "min_size", "modified_since", "find_duplicates", "content", "max_matches", "context_lines");
        parameters.Single(p => p.Name == "content").DefaultValue.Should().BeNull();
        parameters.Single(p => p.Name == "max_matches").DefaultValue.Should().Be(100);
        parameters.Single(p => p.Name == "context_lines").DefaultValue.Should().Be(0);
    }

    [Fact]
    public void The_three_new_parameters_have_their_own_descriptions()
    {
        foreach (var name in new[] { "content", "max_matches", "context_lines" })
            Method.GetParameters().Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()
                .Should().NotBeNull($"'{name}' needs its own description");
    }

    /// <summary>
    /// R2 decision 11: hidden and system files are read; links and cloud-only (offline) files are
    /// skipped and counted; the 60,000-character output cap.
    /// </summary>
    [Fact]
    public void FileSearch_description_states_the_review_decisions()
    {
        var description = Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchRegex(@"(?i)hidden[^.]*system|system[^.]*hidden", "hidden and system files are read");
        description.Should().MatchRegex(@"(?i)symlink|symbolic link");
        description.Should().MatchRegex(@"(?i)cloud|offline", "cloud-only files are skipped, never downloaded");
        description.Should().MatchRegex(@"60[,. ]?000", "the output cap");
    }

    /// <summary>Second review pass, decision C: the description says pattern is a file-name glob in a content search.</summary>
    [Fact]
    public void FileSearch_description_says_pattern_is_a_file_name_glob_in_a_content_search()
    {
        Method.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().MatchRegex(@"(?i)file[- ]?name glob");
    }

    /// <summary>The content mode, (?i), the caps and the skips are documented where the model reads them.</summary>
    [Fact]
    public void FileSearch_description_documents_the_content_mode()
    {
        var description = Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("content");
        description.Should().ContainEquivalentOf("regex");
        description.Should().Contain("(?i)", "case-insensitive search is spelled in the pattern");
        description.Should().Contain("max_matches").And.Contain("context_lines");
        description.Should().ContainEquivalentOf("binary", "binary files are skipped");
        description.Should().MatchRegex(@"16\s?MiB", "files over the size cap are skipped");
        description.Should().ContainEquivalentOf("truncated");
        description.Should().Contain("find_duplicates", "the existing parameters are still described");
    }
}
