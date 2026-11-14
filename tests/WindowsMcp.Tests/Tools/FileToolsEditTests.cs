using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-9: the <c>file_edit</c> tool layer (docs/design/E-9-file-edit-content-search.md, "file_edit"
/// and "Refused before the file is opened"). Every refusal here must happen before the service is
/// asked for anything; the result is the documented JSON object, key for key.
/// </summary>
[Trait("Category", "Unit")]
public class FileToolsEditTests
{
    private const string Abs = @"C:\cfg\app.ini";

    private static FileTools MakeTools(IFileSystemService fs) =>
        new(fs, new Mock<IInputService>().Object, new Mock<IFileStreamService>().Object);

    private static readonly FileEditResult Sample = new(Abs, 1, 42, "utf-8", false, "crlf", 1834, 1851);

    private static Mock<IFileSystemService> Service()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.EditTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Sample);
        return mock;
    }

    private static void NeverEdited(Mock<IFileSystemService> mock) =>
        mock.Verify(s => s.EditTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    // ==== refused before the file is opened ==========================================================

    /// <summary>"confirm is not true - the rule every file write follows."</summary>
    [Fact]
    public async Task FileEdit_requires_confirm()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        NeverEdited(mock);
    }

    /// <summary>"path is not a plain absolute path (RequireAbsolute, as C-1)."</summary>
    [Theory]
    [InlineData("app.ini")]
    [InlineData(@"cfg\app.ini")]
    [InlineData(@"..\app.ini")]
    [InlineData(@"\app.ini")]
    [InlineData(@"\\?\C:\cfg\app.ini")]
    [InlineData(@"\\.\C:\cfg\app.ini")]
    [InlineData("//?/" + @"C:\cfg\app.ini")]
    [InlineData(@"\\?\UNC\server\share\app.ini")]
    public async Task FileEdit_refuses_a_path_that_is_not_plain_and_absolute(string path)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(path, "a=1", "a=2", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        NeverEdited(mock);
    }

    [Fact]
    public async Task FileEdit_refuses_an_empty_old_string()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "", "a=2", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("old_string");
        NeverEdited(mock);
    }

    /// <summary>"old_string equals new_string (ordinal): nothing would change."</summary>
    [Fact]
    public async Task FileEdit_refuses_an_old_string_equal_to_the_new_one()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=1", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("old_string").And.Contain("new_string");
        NeverEdited(mock);
    }

    /// <summary>Ordinal: a case-only change is a change, and goes to the service.</summary>
    [Fact]
    public async Task FileEdit_passes_a_case_only_change_through()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileEdit(Abs, "debug=true", "Debug=true", confirm: true);

        mock.Verify(s => s.EditTextAsync(Abs, "debug=true", "Debug=true", false, "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>"encoding is not one of auto, utf-8, utf-16, ascii, latin1 (any case)."</summary>
    [Theory]
    [InlineData("ebcdic")]
    [InlineData("utf8")]
    [InlineData("utf-32")]          // a name the RESULT can report (from a BOM), but not one a caller can choose
    [InlineData("")]
    [InlineData(" utf-8")]
    public async Task FileEdit_refuses_an_unknown_encoding_naming_the_choices(string encoding)
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", encoding: encoding, confirm: true);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("encoding");
        message.Should().Contain("latin1", "the refusal lists the encodings there are");
        NeverEdited(mock);
    }

    /// <summary>A JSON null for encoding is refused like any unknown name, not passed on.</summary>
    [Fact]
    public async Task FileEdit_refuses_a_null_encoding()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", encoding: null!, confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("encoding");
        NeverEdited(mock);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("ascii")]
    [InlineData("latin1")]
    [InlineData("UTF-8")]
    [InlineData("Latin1")]
    [InlineData("AUTO")]
    public async Task FileEdit_accepts_each_encoding_in_any_case(string encoding)
    {
        var mock = Service();

        await MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", encoding: encoding, confirm: true);

        mock.Verify(s => s.EditTextAsync(Abs, "a=1", "a=2", false,
            It.Is<string>(e => e.Equals(encoding, StringComparison.OrdinalIgnoreCase)), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>R2 decision 9: a null new_string is refused (empty is how to delete), before the service.</summary>
    [Fact]
    public async Task FileEdit_refuses_a_null_new_string()
    {
        var mock = Service();

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "a=1", null!, confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("new_string");
        NeverEdited(mock);
    }

    [Fact]
    public async Task FileEdit_passes_an_empty_new_string_through_as_a_deletion()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileEdit(Abs, "a=1", "", confirm: true);

        mock.Verify(s => s.EditTextAsync(Abs, "a=1", "", false, "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ==== passed through =============================================================================

    [Fact]
    public async Task FileEdit_defaults_are_unique_match_and_auto_encoding()
    {
        var mock = Service();

        await MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", confirm: true);

        mock.Verify(s => s.EditTextAsync(Abs, "a=1", "a=2", false, "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileEdit_forwards_replace_all_and_the_strings_unchanged()
    {
        var mock = Service();
        // Whitespace, a CRLF and a trailing space are content: nothing is trimmed or normalised here.
        const string Old = "  key = 1 \n";
        const string New = "  key = 2\r\n";

        await MakeTools(mock.Object).FileEdit(Abs, Old, New, replace_all: true, encoding: "latin1", confirm: true);

        mock.Verify(s => s.EditTextAsync(Abs, Old, New, true, "latin1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileEdit_forwards_the_cancellation_token()
    {
        var mock = Service();
        using var cts = new CancellationTokenSource();

        await MakeTools(mock.Object).FileEdit(Abs, "a", "b", confirm: true, ct: cts.Token);

        mock.Verify(s => s.EditTextAsync(Abs, "a", "b", false, "auto", cts.Token), Times.Once);
    }

    [Fact]
    public async Task FileEdit_passes_a_unc_path_through()
    {
        const string Unc = @"\\server\share\app.ini";
        var mock = Service();

        await MakeTools(mock.Object).FileEdit(Unc, "a", "b", confirm: true);

        mock.Verify(s => s.EditTextAsync(Unc, "a", "b", false, "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ==== the result =================================================================================

    /// <summary>
    /// "Result: {path, replacements, firstLine, encoding, bom, lineEndings, bytesBefore, bytesAfter}"
    /// - exactly those keys, camelCase, with the service's values.
    /// </summary>
    [Fact]
    public async Task FileEdit_returns_exactly_the_documented_keys()
    {
        var mock = Service();

        var json = await MakeTools(mock.Object).FileEdit(Abs, "a=1", "a=2", confirm: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal(
            "path", "replacements", "firstLine", "encoding", "bom", "lineEndings", "bytesBefore", "bytesAfter");
        root.GetProperty("path").GetString().Should().Be(Abs);
        root.GetProperty("replacements").GetInt32().Should().Be(1);
        root.GetProperty("firstLine").GetInt32().Should().Be(42);
        root.GetProperty("encoding").GetString().Should().Be("utf-8");
        root.GetProperty("bom").GetBoolean().Should().BeFalse();
        root.GetProperty("lineEndings").GetString().Should().Be("crlf");
        root.GetProperty("bytesBefore").GetInt64().Should().Be(1834);
        root.GetProperty("bytesAfter").GetInt64().Should().Be(1851);
    }

    /// <summary>A service refusal reaches the caller as it was thrown (ToolErrors surfaces its message).</summary>
    [Fact]
    public async Task FileEdit_lets_a_service_refusal_through_unchanged()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.EditTextAsync(Abs, "x", "y", false, "auto", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("'x' occurs 3 times in C:\\cfg\\app.ini"));

        var act = () => MakeTools(mock.Object).FileEdit(Abs, "x", "y", confirm: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("'x' occurs 3 times*");
    }

    // ==== the surface ================================================================================

    private static MethodInfo Method => typeof(FileTools).GetMethod(nameof(FileTools.FileEdit))!;

    /// <summary>The note's signature: file_edit(path, old_string, new_string, replace_all = false, encoding = "auto", confirm = false).</summary>
    [Fact]
    public void FileEdit_has_the_notes_parameters_in_order_with_its_defaults()
    {
        var parameters = Method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();

        parameters.Select(p => p.Name).Should().Equal("path", "old_string", "new_string", "replace_all", "encoding", "confirm");
        parameters.Single(p => p.Name == "replace_all").DefaultValue.Should().Be(false);
        parameters.Single(p => p.Name == "encoding").DefaultValue.Should().Be("auto");
        parameters.Single(p => p.Name == "confirm").DefaultValue.Should().Be(false);
        parameters.Take(3).Should().OnlyContain(p => !p.HasDefaultValue, "path and the two strings are required");
    }

    [Fact]
    public void Every_file_edit_parameter_has_its_own_description()
    {
        foreach (var p in Method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            p.GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull($"'{p.Name}' needs its own description");

        Method.GetParameters().Single(p => p.Name == "path").GetCustomAttribute<DescriptionAttribute>()!
            .Description.Should().ContainEquivalentOf("absolute");
        Method.GetParameters().Single(p => p.Name == "encoding").GetCustomAttribute<DescriptionAttribute>()!
            .Description.Should().Contain("latin1", "the encoding parameter names the values a caller may pass");
    }

    /// <summary>
    /// R2 decision 11: the description states the review's decisions - a hard-linked file is
    /// refused; permissions are kept only "where the file system supports it" (every sentence that
    /// mentions permissions says so); symlinks and junctions are refused; new_string's own
    /// description says empty deletes and null is refused.
    /// </summary>
    [Fact]
    public void FileEdit_description_states_the_review_decisions()
    {
        var description = Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchRegex(@"(?i)hard[- ]?link", "a hard-linked file is refused");
        description.Should().MatchRegex(@"(?i)symlink|symbolic link").And.MatchRegex(@"(?i)junction");
        description.Should().ContainEquivalentOf("where the file system supports");
        description.Split('.').Where(s => s.Contains("permission", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(s => s.Contains("where the file system supports", StringComparison.OrdinalIgnoreCase),
                "no sentence promises permissions unconditionally");
        var newString = Method.GetParameters().Single(p => p.Name == "new_string").GetCustomAttribute<DescriptionAttribute>()!.Description;
        newString.Should().ContainEquivalentOf("empty").And.ContainEquivalentOf("null");
    }

    /// <summary>
    /// Second review pass, decision D: when the swap fails the original is LEFT OR PUT BACK - the
    /// description no longer implies the edit is completed some other way (decision A: a removed
    /// target is not recreated).
    /// </summary>
    [Fact]
    public void FileEdit_description_says_the_original_is_left_or_put_back_when_the_swap_fails()
    {
        var description = Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchRegex(@"(?i)original[^.]*\bleft\b[^.]*put back|original[^.]*put back[^.]*\bleft\b",
            "one sentence says the original is left or put back");
        description.Should().NotMatchRegex(@"(?i)\bcomplet", "a failed swap is not completed by other means");
    }

    /// <summary>
    /// The description is the whole spec a model reads: confirm, the unique-match rule and
    /// replace_all, that encoding/BOM/line endings are kept, the size cap, and that only the two
    /// strings cross the wire (which is why the tool exists - E-9 "Done when").
    /// </summary>
    [Fact]
    public void FileEdit_description_states_the_contract()
    {
        var description = Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("confirm");
        description.Should().Contain("old_string").And.Contain("new_string");
        (description.Contains("once", StringComparison.OrdinalIgnoreCase) || description.Contains("unique", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"the unique-match rule is stated; it said: {description}");
        description.Should().Contain("replace_all");
        description.Should().ContainEquivalentOf("encoding");
        description.Should().Contain("BOM");
        description.Should().ContainEquivalentOf("line ending");
        description.Should().MatchRegex(@"16\s?MiB", "the size cap is stated");
        description.Should().ContainEquivalentOf("wire", "nothing but the two strings crosses the wire - the reason to use it over file_read + file_write");
        description.Should().ContainEquivalentOf("absolute");
    }
}
