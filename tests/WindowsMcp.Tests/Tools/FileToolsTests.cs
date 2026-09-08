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

    /// <summary>C-1 R4-5: the listing DTO a mocked service hands back when the rows do not matter.</summary>
    private static FileListing EmptyListing => new([], Truncated: false, MaxEntries: 1000);

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
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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
        mock.Setup(s => s.ListAsync(@"C:\tmp", "*.txt", true, true, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileListing(
                [
                    new FileEntry(@"C:\tmp\a.txt", "a.txt", false, 42, modified, false),
                    new FileEntry(@"C:\tmp\sub", "sub", true, 0, modified, true),
                ],
                Truncated: false, MaxEntries: 1000));
        var tools = MakeTools(fs: mock.Object);

        var root = Parse(await tools.FileManage(
            "list", @"C:\tmp", pattern: "*.txt", recursive: true, include_hidden: true));

        var entries = root.GetProperty("Entries");
        entries.GetArrayLength().Should().Be(2);
        var first = entries[0];
        first.GetProperty("Path").GetString().Should().Be(@"C:\tmp\a.txt");
        first.GetProperty("Name").GetString().Should().Be("a.txt");
        first.GetProperty("IsDirectory").GetBoolean().Should().BeFalse();
        first.GetProperty("Size").GetInt64().Should().Be(42);
        first.GetProperty("Hidden").GetBoolean().Should().BeFalse();
        entries[1].GetProperty("IsDirectory").GetBoolean().Should().BeTrue();
        entries[1].GetProperty("Hidden").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.ListAsync(@"C:\tmp", "*.txt", true, true, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FileManage_list_defaults_to_no_pattern_no_recursion_and_no_hidden()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp");

        mock.Verify(s => s.ListAsync(@"C:\tmp", null, false, false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
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

    // ---- C-1 R4-2: the device-path forms are refused at the tool ------------------------------

    /// <summary>
    /// R4-2: <c>\\?\</c> skips the Win32 path parser (no <c>..</c> resolution, no length check)
    /// and <c>\\.\</c> addresses DEVICES, not files. <c>Path.IsPathFullyQualified</c> says yes to
    /// both, so the absolute-path rail lets them through today - and the service's containment
    /// check compares them as plain strings, which is how <c>\\?\C:\x</c> copies over <c>C:\x</c>.
    /// A model has no reason to send either form, so the tool refuses them by parameter name
    /// before the service is asked for anything.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\C:\tmp\file.txt")]
    [InlineData(@"\\.\C:\tmp\file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt")]
    public async Task FileRead_refuses_a_device_path_before_touching_the_service(string path)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileRead(path);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.ReadLinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(@"\\?\C:\tmp\file.txt")]
    [InlineData(@"\\.\C:\tmp\file.txt")]
    public async Task FileWrite_refuses_a_device_path_before_touching_the_service(string path)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileWrite(path, "hello", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.WriteTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(@"\\?\C:\tmp\dir")]
    [InlineData(@"\\.\C:\tmp\dir")]
    public async Task FileManage_refuses_a_device_path_src_before_touching_the_service(string src)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", src);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("src");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(@"\\?\C:\tmp\copy.txt")]
    [InlineData(@"\\.\C:\tmp\copy.txt")]
    public async Task FileManage_refuses_a_device_path_dst_before_touching_the_service(string dst)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("copy", Abs, dst);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("dst");
        mock.Verify(s => s.CopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(@"\\?\C:\data")]
    [InlineData(@"\\.\C:\data")]
    public async Task FileSearch_refuses_a_device_path_root_before_touching_the_service(string root)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileSearch(root);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("root");
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- C-1 R4-5: the listing's cap -----------------------------------------------------------

    /// <summary>
    /// R4-5: a recursive listing of C:\Windows was 160 000 entries and 42 MB in one response. The
    /// cap is a tool-level default so no existing call has to change to become survivable.
    /// </summary>
    [Fact]
    public async Task FileManage_list_caps_at_a_thousand_entries_by_default()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp");

        mock.Verify(s => s.ListAsync(@"C:\tmp", null, false, false, 1000, It.IsAny<CancellationToken>()), Times.Once,
            "1000 rows is a readable answer; the caller raises it deliberately");
    }

    [Fact]
    public async Task FileManage_list_forwards_max_entries()
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp", max_entries: 50);

        mock.Verify(s => s.ListAsync(@"C:\tmp", null, false, false, 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public async Task FileManage_list_refuses_a_max_entries_outside_the_range(int maxEntries)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", @"C:\tmp", max_entries: maxEntries);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("max_entries", "the refusal names the parameter the caller has to fix");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100_000)]
    public async Task FileManage_list_accepts_the_ends_of_the_max_entries_range(int maxEntries)
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp", max_entries: maxEntries);

        mock.Verify(s => s.ListAsync(@"C:\tmp", null, false, false, maxEntries, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// R4-5: the reply carries the flag that says the listing stopped early. A caller that cannot
    /// see Truncated reads a capped listing as a complete one and concludes the files are not there.
    /// </summary>
    [Fact]
    public async Task FileManage_list_returns_the_truncated_flag_and_the_cap_that_applied()
    {
        var modified = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileListing(
                [new FileEntry(@"C:\tmp\a.txt", "a.txt", false, 1, modified, false)],
                Truncated: true, MaxEntries: 1));
        var tools = MakeTools(fs: mock.Object);

        var root = Parse(await tools.FileManage("list", @"C:\tmp", max_entries: 1));

        root.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("MaxEntries").GetInt32().Should().Be(1);
        root.GetProperty("Entries").GetArrayLength().Should().Be(1);
    }

    // ---- C-1 R4-8: replies that tell the truth --------------------------------------------------

    /// <summary>
    /// R4-8: "deleted 'C:\...'" for a path that was never there tells the caller a lie it may act
    /// on (it stops looking for the file it meant to remove). Nothing is created or deleted here -
    /// the path is a name under %TEMP% that does not exist.
    /// </summary>
    [Fact]
    public async Task FileManage_delete_of_a_path_that_is_not_there_says_nothing_was_deleted()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wmcp-absent-" + Guid.NewGuid().ToString("N"), "gone.txt");
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var reply = await tools.FileManage("delete", missing, confirm: true);

        reply.Should().ContainEquivalentOf("nothing").And.Contain(missing);
        reply.Should().NotStartWith("deleted", "nothing was deleted, so the reply must not say it was");
    }

    /// <summary>
    /// R4-8/R4-5: the description is the only place a model learns that overwrite:true DELETES what
    /// is at the destination, and that a listing is capped. "refuse an existing destination unless
    /// overwrite:true" reads like a merge to anyone who has not read the source.
    /// </summary>
    [Fact]
    public void FileManage_describes_what_overwrite_does_and_the_listing_cap()
    {
        var manage = typeof(FileTools).GetMethod(nameof(FileTools.FileManage))!;
        var description = manage.GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchEquivalentOf("*overwrite:true*replace*",
            "the flag's own sentence has to say the destination is replaced");
        description.Should().ContainEquivalentOf("max_entries");
        manage.GetParameters().Single(p => p.Name == "max_entries")
            .GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull(
                "'max_entries' needs its own description");
    }

    // ==== C-1 round 4b ============================================================================

    // ---- R4b-4: every spelling of the device prefix ---------------------------------------------

    /// <summary>A path an MCP client might send meaning "beside the file I am looking at".</summary>
    private const string RelativeToTheServersWorkingDirectory = @"sub\file.txt";

    /// <summary>
    /// R4b-4: Windows turns <c>/</c> into <c>\</c> before it looks at a path, so all four of these
    /// prefixes ARE the <c>\\?\</c> and <c>\\.\</c> device forms — <c>Path.GetFullPath</c>
    /// normalises every one of them to <c>\\?\C:\tmp\file.txt</c> or <c>\\.\C:\tmp\file.txt</c> —
    /// while the tool's refusal only recognises the backslash spelling. Each is fully qualified, so
    /// the absolute-path rule lets them straight through to the service, whose containment checks
    /// the prefix then bypasses.
    /// </summary>
    [Theory]
    [InlineData("//?/")]
    [InlineData("//./")]
    [InlineData(@"\\?/")]
    [InlineData(@"/\?\")]
    public async Task FileRead_refuses_every_spelling_of_the_device_prefix(string prefix)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileRead(prefix + @"C:\tmp\file.txt");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(s => s.ReadLinesAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("//?/")]
    [InlineData("//./")]
    [InlineData(@"\\?/")]
    [InlineData(@"/\?\")]
    public async Task FileWrite_refuses_every_spelling_of_the_device_prefix(string prefix)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileWrite(prefix + @"C:\tmp\file.txt", "hello", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.WriteTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("//?/")]
    [InlineData("//./")]
    [InlineData(@"\\?/")]
    [InlineData(@"/\?\")]
    public async Task FileManage_refuses_every_spelling_of_the_device_prefix_in_src(string prefix)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", prefix + @"C:\tmp\dir");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("src");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("//?/")]
    [InlineData("//./")]
    [InlineData(@"\\?/")]
    [InlineData(@"/\?\")]
    public async Task FileManage_refuses_every_spelling_of_the_device_prefix_in_dst(string prefix)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("copy", Abs, prefix + @"C:\tmp\copy.txt");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("dst");
        mock.Verify(s => s.CopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("//?/")]
    [InlineData("//./")]
    [InlineData(@"\\?/")]
    [InlineData(@"/\?\")]
    public async Task FileSearch_refuses_every_spelling_of_the_device_prefix_in_root(string prefix)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileSearch(prefix + @"C:\data");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("root");
        mock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<long?>(),
            It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- R4b-4: the four read-only file tools go through RequireAbsolute too ---------------------

    /// <summary>
    /// R4b-4 closes the follow-up C-1 left open: <c>file_hash</c>, <c>file_info</c>,
    /// <c>file_streams</c> and <c>archive</c> took a relative path, which resolves against the
    /// server's working directory — whatever the MCP host set, and nothing the caller can see. A
    /// hash of the wrong file is worse than a refusal.
    /// </summary>
    [Theory]
    [InlineData("file.txt")]
    [InlineData(RelativeToTheServersWorkingDirectory)]
    [InlineData(@"..\file.txt")]
    [InlineData(@"\file.txt")]      // rooted but not fully qualified: no drive
    [InlineData(@"\\?\C:\tmp\file.txt")]
    [InlineData("//?/" + @"C:\tmp\file.txt")]
    public async Task FileHash_refuses_a_path_that_is_not_a_plain_absolute_one(string path)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileHash(path);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.HashFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData(RelativeToTheServersWorkingDirectory)]
    [InlineData(@"..\file.txt")]
    [InlineData(@"\file.txt")]
    [InlineData(@"\\?\C:\tmp\file.txt")]
    [InlineData("//?/" + @"C:\tmp\file.txt")]
    public async Task FileInfo_refuses_a_path_that_is_not_a_plain_absolute_one(string path)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileInfo(path);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        mock.Verify(s => s.GetInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("file.txt")]
    [InlineData(RelativeToTheServersWorkingDirectory)]
    [InlineData(@"..\file.txt")]
    [InlineData(@"\file.txt")]
    [InlineData(@"\\?\C:\tmp\file.txt")]
    [InlineData("//?/" + @"C:\tmp\file.txt")]
    public async Task FileStreams_refuses_a_path_that_is_not_a_plain_absolute_one(string path)
    {
        var streams = new Mock<IFileStreamService>();
        var tools = MakeTools(streams: streams.Object);

        var act = () => tools.FileStreams(path);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("path");
        streams.Verify(s => s.GetStreamsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("dir")]
    [InlineData(@"..\dir")]
    [InlineData(@"\\?\C:\tmp\dir")]
    [InlineData("//?/" + @"C:\tmp\dir")]
    public async Task Archive_refuses_a_src_that_is_not_a_plain_absolute_one(string src)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.Archive("zip", src, @"C:\tmp\out.zip");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("src");
        mock.Verify(s => s.ZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("out.zip")]
    [InlineData(@"..\out.zip")]
    [InlineData(@"\\?\C:\tmp\out.zip")]
    [InlineData("//?/" + @"C:\tmp\out.zip")]
    public async Task Archive_refuses_a_dst_that_is_not_a_plain_absolute_one(string dst)
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.Archive("zip", @"C:\tmp\dir", dst);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("dst");
        mock.Verify(s => s.ZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// R4b-4, the other half: the rule is "plain and absolute", not "refuse things". A normal
    /// absolute path still reaches the service on all four tools — otherwise a check that refused
    /// everything would pass every test above.
    /// </summary>
    [Fact]
    public async Task The_four_read_only_file_tools_still_pass_an_absolute_path_through()
    {
        var when = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        var fs = new Mock<IFileSystemService>();
        fs.Setup(s => s.HashFileAsync(Abs, "sha256", It.IsAny<CancellationToken>())).ReturnsAsync("deadbeef");
        fs.Setup(s => s.GetInfoAsync(Abs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileInfoDto(Abs, 12, when, when, when, "Archive", false));
        var streams = new Mock<IFileStreamService>();
        streams.Setup(s => s.GetStreamsAsync(Abs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileStreamsDto(Abs, null, []));
        var tools = MakeTools(fs: fs.Object, streams: streams.Object);

        (await tools.FileHash(Abs)).Should().Be("deadbeef");
        Parse(await tools.FileInfo(Abs)).GetProperty("Path").GetString().Should().Be(Abs);
        Parse(await tools.FileStreams(Abs)).GetProperty("Path").GetString().Should().Be(Abs);
        (await tools.Archive("zip", @"C:\tmp\dir", @"C:\tmp\out.zip")).Should().Contain(@"C:\tmp\out.zip");

        fs.Verify(s => s.HashFileAsync(Abs, "sha256", It.IsAny<CancellationToken>()), Times.Once);
        fs.Verify(s => s.GetInfoAsync(Abs, It.IsAny<CancellationToken>()), Times.Once);
        streams.Verify(s => s.GetStreamsAsync(Abs, It.IsAny<CancellationToken>()), Times.Once);
        fs.Verify(s => s.ZipAsync(@"C:\tmp\dir", @"C:\tmp\out.zip", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The other half of <c>archive</c>: <c>unzip</c> is not <c>zip</c>. Both actions take the same
    /// two paths in the same order and both are absolute-checked the same way, so a switch that
    /// fell into the wrong arm would still answer and still look right — while creating an archive
    /// where the caller asked for their files back, or the other way round.
    /// </summary>
    [Fact]
    public async Task Archive_unzip_extracts_through_the_service_and_never_zips()
    {
        var fs = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: fs.Object);

        var reply = await tools.Archive("UnZip", @"C:\tmp\in.zip", @"C:\tmp\out-dir");

        reply.Should().Contain("unzipped", "the reply says which of the two actions ran")
            .And.Contain(@"C:\tmp\in.zip").And.Contain(@"C:\tmp\out-dir");
        fs.Verify(s => s.UnzipAsync(@"C:\tmp\in.zip", @"C:\tmp\out-dir", It.IsAny<CancellationToken>()), Times.Once,
            "the action is matched case-insensitively, and the paths go through in the order they were given");
        fs.Verify(s => s.ZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "zipping when asked to unzip would overwrite the caller's archive with an archive of it");
    }

    /// <summary>
    /// An action the tool does not have is a refusal that names what was sent and the two that
    /// exist — not a silent no-op, and not the masked "An error occurred invoking archive". Both
    /// paths here are already good, so the action is the only thing left to be wrong; a trailing
    /// space is the ordinary way it is wrong.
    /// </summary>
    [Theory]
    [InlineData("extract")]
    [InlineData("")]
    [InlineData("zip ")]
    public async Task Archive_refuses_an_action_it_does_not_have(string action)
    {
        var fs = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: fs.Object);

        var act = () => tools.Archive(action, @"C:\tmp\dir", @"C:\tmp\out.zip");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain($"'{action}'", "the caller has to see what they sent, quoted so a space shows");
        message.Should().Contain("zip|unzip", "and the two actions there are instead");
        fs.Verify(s => s.ZipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unknown action does nothing at all - it does not fall back to the first arm");
        fs.Verify(s => s.UnzipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- R4b-2 / R4b-7: the two sentences the description owes the caller ------------------------

    /// <summary>
    /// R4b-2 and R4b-7 both end "and the description says so", because both are things a caller
    /// only finds out afterwards: a move across volumes is a copy, so the junctions and symlinks
    /// inside the tree do not arrive with it; and a file copy or move creates a missing destination
    /// parent, so a caller does not need to create it first (which would turn their copy into "the
    /// destination already exists").
    /// </summary>
    [Fact]
    public void FileManage_describes_the_links_a_cross_volume_move_drops_and_the_parent_it_creates()
    {
        var description = typeof(FileTools).GetMethod(nameof(FileTools.FileManage))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().ContainEquivalentOf("across volumes");
        // Not a wildcard match: "junctions or symlinks" and "removes the link only" are already in
        // there, so any pattern that only wants the WORD passes without the sentence being said.
        var saysLinksAreDropped =
            description.Contains("not carried", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not copied", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("dropped", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("left behind", StringComparison.OrdinalIgnoreCase);
        saysLinksAreDropped.Should().BeTrue(
            "the cross-volume move is a copy, so links inside the tree do not arrive with it - the description "
            + "has to say so (\"not carried\", \"not copied\", \"dropped\" or \"left behind\"); a link that "
            + "quietly does not arrive is found out long after the move");
        description.Should().MatchEquivalentOf("*parent*creat*",
            "a missing destination parent is created, and the description is where that is promised");
    }

    // ---- R4b-7: the max_entries refusal says what 0 is not ---------------------------------------

    /// <summary>
    /// R4b-7: <c>0</c> is the one wrong value a caller arrives at by reasoning — every other
    /// bounded parameter in this server reads 0 as "all" (<c>process limit</c>,
    /// <c>file_read limit_lines</c>). "must be between 1 and 100000" tells them the bound they
    /// broke, not the assumption they made.
    /// </summary>
    [Fact]
    public async Task FileManage_list_refusing_max_entries_zero_says_that_zero_is_not_all()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", @"C:\tmp", max_entries: 0);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("max_entries");
        message.Should().ContainEquivalentOf("all",
            "elsewhere 0 means 'no limit'; here it does not, and the refusal is where the caller finds that out");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==== C-1 round 4c ============================================================================

    // ---- R4c-6: a list pattern is a name glob, not a path ----------------------------------------

    /// <summary>
    /// R4c-6: the pattern is matched against an entry's NAME, so one with a path separator in it
    /// cannot match anything — the caller gets an empty listing that reads as "the directory is
    /// empty" and no hint that <c>recursive:true</c> is how you descend. The refusal names the
    /// parameter and quotes what was sent, like every other bad-input answer in this tool.
    /// </summary>
    [Theory]
    [InlineData(@"sub\*.txt")]
    [InlineData("sub/*.txt")]
    [InlineData(@"*\*.log")]
    public async Task FileManage_list_refuses_a_pattern_with_a_path_separator(string pattern)
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        var act = () => tools.FileManage("list", @"C:\tmp", pattern: pattern);

        var message = (await act.Should().ThrowAsync<ArgumentException>(
                "an empty listing is indistinguishable from an empty directory - a pattern that CANNOT match "
                + "is a mistake to report, not a result to return")).Which.Message;
        message.Should().Contain("pattern", "the refusal names the parameter as the caller spells it");
        message.Should().Contain(pattern, "and quotes what they sent, so they can see what was wrong with it");
        mock.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.VerifyNoOtherCalls();
    }

    /// <summary>R4c-6: the glob a pattern IS still goes through untouched.</summary>
    [Theory]
    [InlineData("*.txt")]
    [InlineData("report-??.log")]
    [InlineData("*")]
    public async Task FileManage_list_forwards_an_ordinary_name_glob(string pattern)
    {
        var mock = new Mock<IFileSystemService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyListing);
        var tools = MakeTools(fs: mock.Object);

        await tools.FileManage("list", @"C:\tmp", pattern: pattern);

        mock.Verify(s => s.ListAsync(@"C:\tmp", pattern, false, false, 1000, It.IsAny<CancellationToken>()), Times.Once,
            "refusing a separator must not narrow what a name glob may contain");
    }

    // ---- R4c-2: the description does not promise a put-back it cannot always make -----------------

    /// <summary>
    /// R4c-2's other half. "(a failure or a cancel puts it back)" reads as a guarantee, and it is
    /// not one: when the partial destination cannot be removed — a file still open, a directory
    /// that is some process's working directory — the previous content stays under a
    /// <c>&lt;dst&gt;.replaced.&lt;guid&gt;</c> name beside the destination. A caller who was
    /// promised the put-back never goes looking for it, and the sibling reads as junk to delete.
    /// </summary>
    [Fact]
    public void FileManage_describes_the_put_back_as_best_effort_and_where_the_previous_content_goes()
    {
        var description = typeof(FileTools).GetMethod(nameof(FileTools.FileManage))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        var saysBestEffort =
            description.Contains("best effort", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("best-effort", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("if it can", StringComparison.OrdinalIgnoreCase);
        saysBestEffort.Should().BeTrue(
            "the put-back is attempted, not guaranteed - the description is where a caller learns which. "
            + $"It said: {description}");
        description.Should().ContainEquivalentOf(".replaced.",
            "the aside's name is the only way a caller finds content the put-back could not restore");
    }

    // ---- R4c-7: the four read-only file tools say their paths are absolute -------------------------

    /// <summary>
    /// R4c-7: R4b-4 put <c>file_hash</c>, <c>file_info</c>, <c>file_streams</c> and <c>archive</c>
    /// behind <c>RequireAbsolute</c> too, so a relative path is now REFUSED rather than resolved
    /// against the server's working directory. A description that still reads "File path to hash"
    /// is an invitation to send the one thing that cannot work — the other file tools all say
    /// "(absolute)".
    /// </summary>
    [Theory]
    [InlineData(nameof(FileTools.FileHash), "path")]
    [InlineData(nameof(FileTools.FileInfo), "path")]
    [InlineData(nameof(FileTools.FileStreams), "path")]
    [InlineData(nameof(FileTools.Archive), "src")]
    [InlineData(nameof(FileTools.Archive), "dst")]
    public void The_read_only_file_tools_say_their_paths_are_absolute(string method, string parameter)
    {
        var attribute = typeof(FileTools).GetMethod(method)!
            .GetParameters().Single(p => p.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>();

        attribute.Should().NotBeNull($"'{parameter}' on {method} needs its own description");
        attribute!.Description.Should().ContainEquivalentOf("absolute",
            $"{method}('{parameter}') refuses a relative path; the description is the only place a caller "
            + "learns that before they send one");
    }
}
