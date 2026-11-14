using System.Text;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// E-9 over the real HTTP host (the <see cref="HttpTransportTests.Harness"/>): the checklist's
/// "Done when" - one line of a remote config file is changed without the file crossing the wire -
/// and the two tools' schemas as a remote client sees them. Files live in a fresh temp directory and
/// are deleted afterwards.
/// </summary>
[Trait("Category", "Integration")]
public class FileEditHttpTransportTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-edit-http-" + Guid.NewGuid().ToString("N"));

    public FileEditHttpTransportTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static string ToolName(IEnumerable<McpClientTool> tools, string squashed) =>
        tools.Single(t => t.Name.Replace("_", "").Equals(squashed, StringComparison.OrdinalIgnoreCase)).Name;

    private static (JsonElement Properties, string[] Required) SchemaOf(IEnumerable<McpClientTool> tools, string squashed)
    {
        var name = ToolName(tools, squashed);
        var schema = tools.Single(t => t.Name == name).ProtocolTool.InputSchema;
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];
        return (schema.GetProperty("properties"), required);
    }

    private static string TextOf(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    /// <summary>
    /// E-9 "Done when": a remote client changes one line of a CRLF config file by sending only the
    /// two strings (with \n, as a model does). The file keeps its CRLF and every other byte, and
    /// the reply carries the counters, not the file - a marker line elsewhere in the file never
    /// appears in it.
    /// </summary>
    [Fact]
    public async Task One_line_of_a_remote_config_file_is_changed_without_the_file_crossing_the_wire()
    {
        const string Marker = "connection=SECRET-MARKER-7f3a";
        var path = Path.Combine(_tmp, "app.config");
        var before = $"[db]\r\n{Marker}\r\n[web]\r\nport=8080\r\nhost=localhost\r\n";
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(before));

        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "fileedit"), new Dictionary<string, object?>
        {
            ["path"] = path,
            ["old_string"] = "[web]\nport=8080",
            ["new_string"] = "[web]\nport=9090",
            ["confirm"] = true,
        });

        result.IsError.Should().NotBe(true, TextOf(result));
        var text = TextOf(result);
        text.Should().NotContain("SECRET-MARKER", "the file's content does not come back over the wire");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("replacements").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("firstLine").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("lineEndings").GetString().Should().Be("crlf");
        File.ReadAllBytes(path).Should().Equal(new UTF8Encoding(false).GetBytes(before.Replace("port=8080", "port=9090")));
    }

    /// <summary>
    /// The service's refusals are InvalidOperationExceptions, which ToolErrors surfaces: an
    /// ambiguous match reaches the remote client with the count and the way out, not as
    /// "An error occurred invoking 'file_edit'".
    /// </summary>
    [Fact]
    public async Task An_ambiguous_match_is_refused_over_the_wire_with_its_count_and_nothing_written()
    {
        var path = Path.Combine(_tmp, "dup.ini");
        var original = "x=1\r\nx=1\r\n"u8.ToArray();
        File.WriteAllBytes(path, original);

        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "fileedit"), new Dictionary<string, object?>
        {
            ["path"] = path,
            ["old_string"] = "x=1",
            ["new_string"] = "x=2",
            ["confirm"] = true,
        });

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("2").And.Contain("replace_all").And.NotContain("An error occurred");
        File.ReadAllBytes(path).Should().Equal(original);
        Directory.EnumerateFileSystemEntries(_tmp).Should().ContainSingle("no temp file is left beside the target");
    }

    /// <summary>The content search over the wire: one call finds which file mentions a setting.</summary>
    [Fact]
    public async Task File_search_with_content_finds_the_line_over_the_wire()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.config"), "<add key=\"Timeout\" value=\"30\" />\r\n");
        File.WriteAllText(Path.Combine(_tmp, "b.config"), "<add key=\"Other\" />\r\n");

        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var result = await client.CallToolAsync(ToolName(tools, "filesearch"), new Dictionary<string, object?>
        {
            ["root"] = _tmp,
            ["content"] = "(?i)key=\"timeout\"",
        });

        result.IsError.Should().NotBe(true, TextOf(result));
        using var doc = JsonDocument.Parse(TextOf(result));
        var match = doc.RootElement.GetProperty("matches").EnumerateArray().Should().ContainSingle().Which;
        match.GetProperty("path").GetString().Should().Be(Path.Combine(_tmp, "a.config"));
        match.GetProperty("line").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("filesSearched").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task File_edit_is_advertised_with_its_parameters_defaults_and_annotations()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var tools = await client.ListToolsAsync();

        var (properties, required) = SchemaOf(tools, "fileedit");

        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            new[] { "path", "old_string", "new_string", "replace_all", "encoding", "confirm" });
        required.Should().BeEquivalentTo(new[] { "path", "old_string", "new_string" });
        properties.GetProperty("replace_all").GetProperty("default").GetBoolean().Should().BeFalse();
        properties.GetProperty("encoding").GetProperty("default").GetString().Should().Be("auto");
        properties.GetProperty("confirm").GetProperty("default").GetBoolean().Should().BeFalse();
        properties.GetProperty("confirm").GetProperty("type").GetString().Should().Be("boolean");

        var annotations = tools.Single(t => t.Name == ToolName(tools, "fileedit")).ProtocolTool.Annotations;
        annotations.Should().NotBeNull();
        annotations!.DestructiveHint.Should().BeTrue();
        annotations.ReadOnlyHint.Should().BeFalse();
        annotations.IdempotentHint.Should().BeFalse("a second identical call no longer finds old_string");
        annotations.OpenWorldHint.Should().BeFalse();
    }

    [Fact]
    public async Task File_search_is_advertised_with_the_content_parameters_and_their_defaults()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);

        var (properties, required) = SchemaOf(await client.ListToolsAsync(), "filesearch");

        properties.EnumerateObject().Select(p => p.Name).Should().Contain(new[] { "content", "max_matches", "context_lines" });
        properties.GetProperty("max_matches").GetProperty("default").GetInt32().Should().Be(100);
        properties.GetProperty("context_lines").GetProperty("default").GetInt32().Should().Be(0);
        required.Should().BeEquivalentTo(new[] { "root" }, "content is optional: without it file_search is today's name search");
    }
}
