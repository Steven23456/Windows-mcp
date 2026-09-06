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

[Trait("Category", "Unit")]
public class FileToolsTests
{
    private const string Abs = @"C:\tmp\file.txt";

    private static FileTools MakeTools(
        IFileSystemService? fs = null,
        IInputService? input = null,
        IFileStreamService? streams = null)
    {
        return new FileTools(
            fs      ?? new Mock<IFileSystemService>().Object,
            input   ?? new Mock<IInputService>().Object,
            streams ?? new Mock<IFileStreamService>().Object);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task FileWrite_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileWrite(Abs, "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.WriteTextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FileManage_delete_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileManage("delete", Abs, confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileSearch_passes_args_to_service()
    {
        var mock = new Mock<IFileSystemService>();
        var isoDate = "2024-01-15T10:00:00Z";
        var expectedDate = DateTime.Parse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind);

        mock.Setup(s => s.SearchAsync(
                @"C:\data", "*.txt", null, It.Is<DateTime?>(d => d.HasValue && d.Value == expectedDate), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileSearchHit>());

        var tools = MakeTools(fs: mock.Object);
        var result = await tools.FileSearch(@"C:\data", "*.txt", modified_since: isoDate);

        result.Should().NotBeNull();
        mock.VerifyAll();
    }

    // ---- C-1 R1 (roadmap R1): absolute paths only, refused in the tool ------------------------

    /// <summary>
    /// A relative path from a model is a guess about a working directory it cannot see. Each of
    /// these must be refused BEFORE the service is asked to do anything, naming the parameter.
    /// </summary>
    [Theory]
    [InlineData("file.txt")]
    [InlineData(@"sub\file.txt")]
    [InlineData(@"..\file.txt")]
    [InlineData(@"\file.txt")]      // rooted but not fully qualified: no drive
    public async Task FileRead_refuses_a_relative_path_before_touching_the_service(string path)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileRead(path);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("path").And.ContainEquivalentOf("absolute");
        mock.Verify(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.ReadLinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileWrite_refuses_a_relative_path_before_touching_the_service()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileWrite("file.txt", "hello", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("path").And.ContainEquivalentOf("absolute");
        mock.Verify(s => s.WriteTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileManage_refuses_a_relative_src_before_touching_the_service()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", "sub");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("src").And.ContainEquivalentOf("absolute");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileManage_refuses_a_relative_dst_before_touching_the_service()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("copy", Abs, "copy.txt");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("dst").And.ContainEquivalentOf("absolute");
        mock.Verify(s => s.CopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileSearch_refuses_a_relative_root_before_touching_the_service()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileSearch("data");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("root").And.ContainEquivalentOf("absolute");
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_unc_path_is_absolute_and_is_passed_through()
    {
        const string unc = @"\\server\share\file.txt";
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ReadTextAsync(unc, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("contents");
        var tools = MakeTools(fs: mock.Object);

        var text = await tools.FileRead(unc);

        text.Should().Be("contents", "a UNC path is fully qualified - only relative paths are refused");
        mock.Verify(s => s.ReadTextAsync(unc, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- C-1: file_read's two result shapes ---------------------------------------------------

    [Fact]
    public async Task FileRead_without_a_window_returns_the_plain_text_it_always_did()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ReadTextAsync(Abs, 4096, "utf-8", It.IsAny<CancellationToken>()))
            .ReturnsAsync("line one\nline two");
        var tools = MakeTools(fs: mock.Object);

        var result = await tools.FileRead(Abs, max_bytes: 4096, encoding: "utf-8");

        result.Should().Be("line one\nline two", "an un-windowed read must not become JSON for today's callers");
        mock.Verify(s => s.ReadLinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileRead_with_offset_and_limit_returns_the_window_as_json()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ReadLinesAsync(Abs, 1048576, "auto", 100, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextWindow(500, 100, 20, true, "windowed body"));
        var tools = MakeTools(fs: mock.Object);

        var root = Parse(await tools.FileRead(Abs, offset_lines: 100, limit_lines: 20));

        root.GetProperty("path").GetString().Should().Be(Abs);
        root.GetProperty("totalLines").GetInt32().Should().Be(500);
        root.GetProperty("offset").GetInt32().Should().Be(100);
        root.GetProperty("returned").GetInt32().Should().Be(20);
        root.GetProperty("truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("content").GetString().Should().Be("windowed body");
        mock.Verify(s => s.ReadLinesAsync(Abs, 1048576, "auto", 100, 20, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileRead_with_only_a_limit_is_still_windowed()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ReadLinesAsync(Abs, 1048576, "auto", 0, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TextWindow(9, 1, 5, true, "head"));
        var tools = MakeTools(fs: mock.Object);

        var root = Parse(await tools.FileRead(Abs, limit_lines: 5));

        root.GetProperty("returned").GetInt32().Should().Be(5);
        mock.Verify(s => s.ReadLinesAsync(Abs, 1048576, "auto", 0, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-1, 0, "offset_lines")]
    [InlineData(0, -1, "limit_lines")]
    public async Task FileRead_refuses_a_negative_window_by_name(int offset, int limit, string parameter)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileRead(Abs, offset_lines: offset, limit_lines: limit);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(parameter);
        mock.Verify(s => s.ReadLinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- C-1: file_write's two flags -----------------------------------------------------------

    [Fact]
    public async Task FileWrite_defaults_are_overwrite_and_create_parents()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        await tools.FileWrite(Abs, "hello", confirm: true);

        mock.Verify(s => s.WriteTextAsync(Abs, "hello", "utf-8", false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileWrite_forwards_append_and_create_parents_and_says_it_appended()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var result = await tools.FileWrite(Abs, "more", confirm: true, append: true, create_parents: false);

        mock.Verify(s => s.WriteTextAsync(Abs, "more", "utf-8", true, false, It.IsAny<CancellationToken>()), Times.Once);
        result.Should().ContainEquivalentOf("append",
            "the reply has to say the content was added, not that the file now holds only it");
    }

    // ---- C-1 R2/R3: file_manage's flags --------------------------------------------------------

    [Fact]
    public async Task FileManage_copy_defaults_to_refusing_an_existing_destination()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("copy", Abs, @"C:\tmp\copy.txt");

        mock.Verify(s => s.CopyAsync(Abs, @"C:\tmp\copy.txt", false, It.IsAny<CancellationToken>()), Times.Once,
            "overwrite defaults to false - the tool layer owns the safer default");
    }

    [Fact]
    public async Task FileManage_copy_forwards_overwrite()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("copy", Abs, @"C:\tmp\copy.txt", overwrite: true);

        mock.Verify(s => s.CopyAsync(Abs, @"C:\tmp\copy.txt", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileManage_move_forwards_overwrite_and_defaults_to_false()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("move", Abs, @"C:\tmp\moved.txt");
        await tools.FileManage("move", Abs, @"C:\tmp\moved.txt", overwrite: true);

        mock.Verify(s => s.MoveAsync(Abs, @"C:\tmp\moved.txt", false, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(s => s.MoveAsync(Abs, @"C:\tmp\moved.txt", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileManage_delete_defaults_to_non_recursive_and_forwards_recursive()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("delete", Abs, confirm: true);
        await tools.FileManage("delete", Abs, confirm: true, recursive: true);

        mock.Verify(s => s.DeleteAsync(Abs, false, It.IsAny<CancellationToken>()), Times.Once,
            "confirm acknowledged a delete, never a whole tree");
        mock.Verify(s => s.DeleteAsync(Abs, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileManage_list_forwards_the_listing_flags_and_returns_the_entry_dtos()
    {
        var modified = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(@"C:\tmp", "*.txt", true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new FileEntry(@"C:\tmp\a.txt", "a.txt", false, 42, modified, false),
                new FileEntry(@"C:\tmp\sub", "sub", true, 0, modified, true),
            });
        var tools = MakeTools(fs: mock.Object);

        var root = Parse(await tools.FileManage(
            "list", @"C:\tmp", pattern: "*.txt", recursive: true, include_hidden: true));

        root.GetArrayLength().Should().Be(2);
        var first = root[0];
        first.GetProperty("Path").GetString().Should().Be(@"C:\tmp\a.txt");
        first.GetProperty("Name").GetString().Should().Be("a.txt");
        first.GetProperty("IsDirectory").GetBoolean().Should().BeFalse();
        first.GetProperty("Size").GetInt64().Should().Be(42);
        first.GetProperty("Hidden").GetBoolean().Should().BeFalse();
        root[1].GetProperty("IsDirectory").GetBoolean().Should().BeTrue();
        root[1].GetProperty("Hidden").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.ListAsync(@"C:\tmp", "*.txt", true, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileManage_list_defaults_to_no_pattern_no_recursion_and_no_hidden()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileEntry>());
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp");

        mock.Verify(s => s.ListAsync(@"C:\tmp", null, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- The refusals the descriptions advertise -----------------------------------------------

    /// <summary>
    /// The action menu lives in this message and nowhere else: a model that mistypes an action has
    /// only the error text to recover from.
    /// </summary>
    [Theory]
    [InlineData("bogus")]
    [InlineData("remove")]
    public async Task FileManage_refuses_an_unknown_action_naming_the_four(string action)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage(action, Abs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("copy").And.Contain("move").And.Contain("delete").And.Contain("list");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task FileManage_copy_and_move_require_a_destination(string action)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage(action, Abs, dst: null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("dst").And.Contain(action);
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileSearch_refuses_a_malformed_modified_since_naming_the_parameter()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileSearch(@"C:\data", modified_since: "last tuesday");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("modified_since").And.Contain("last tuesday");
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- C-1: the description is the only thing that makes a parameter usable ------------------

    [Fact]
    public void The_file_tools_describe_their_new_parameters()
    {
        var read = typeof(FileTools).GetMethod(nameof(FileTools.FileRead))!;
        var write = typeof(FileTools).GetMethod(nameof(FileTools.FileWrite))!;
        var manage = typeof(FileTools).GetMethod(nameof(FileTools.FileManage))!;

        read.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().Contain("offset_lines").And.Contain("limit_lines");
        write.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().Contain("append").And.Contain("create_parents");
        manage.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().Contain("overwrite").And.Contain("recursive").And.Contain("pattern");

        foreach (var (method, names) in new (MethodInfo, string[])[]
                 {
                     (read, ["offset_lines", "limit_lines"]),
                     (write, ["append", "create_parents"]),
                     (manage, ["overwrite", "recursive", "pattern", "include_hidden"]),
                 })
            foreach (var name in names)
                method.GetParameters().Single(p => p.Name == name)
                    .GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull(
                        $"'{name}' on {method.Name} needs its own description");
    }
}
