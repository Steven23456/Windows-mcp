using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// C-1 R4-5: what <c>file_manage(list)</c> actually returns, through the real
/// <see cref="FileSystemService"/> rather than a mock. The mocked siblings in
/// <see cref="FileToolsTests"/> serialise a hand-written <see cref="FileListing"/>, so they would
/// stay green if the tool and the service disagreed about the cap or if a real walk produced
/// different key names from the ones the tool's description advertises. One temp directory,
/// removed in <see cref="Dispose"/>.
/// </summary>
[Trait("Category", "Integration")]
public class FileToolsListingIntegrationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-list-" + Guid.NewGuid().ToString("N"));

    public FileToolsListingIntegrationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        // Removing a junction is not synchronous enough for the parent's RemoveDirectory that
        // follows it: the first attempt can fail against a directory whose children have all gone.
        for (var attempt = 0; attempt < 5 && Directory.Exists(_tmp); attempt++)
        {
            try { Directory.Delete(_tmp, recursive: true); }
            catch { Thread.Sleep(50); }
        }
        GC.SuppressFinalize(this);
    }

    private static FileTools Tools() => new(
        new FileSystemService(),
        new Mock<IInputService>().Object,
        new Mock<IFileStreamService>().Object);

    /// <summary>
    /// The cap the tool passes and the flag the service sets have to meet in the middle: three
    /// files, <c>max_entries:2</c>, and the reply says two entries and Truncated.
    /// </summary>
    [Fact]
    public async Task List_over_the_real_file_system_caps_and_says_it_truncated()
    {
        for (var i = 0; i < 3; i++) await File.WriteAllTextAsync(Path.Combine(_tmp, $"f{i}.txt"), "x");

        var root = JsonSerializer.Deserialize<JsonElement>(
            await Tools().FileManage("list", _tmp, max_entries: 2));

        root.GetProperty("Entries").GetArrayLength().Should().Be(2, "the walk stopped at the cap");
        root.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("MaxEntries").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// The same call under the cap, with the six per-entry keys the description promises measured
    /// against a file whose size and kind the test set itself.
    /// </summary>
    [Fact]
    public async Task List_over_the_real_file_system_returns_the_advertised_entry_keys()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmp, "a.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(_tmp, "sub"));

        var root = JsonSerializer.Deserialize<JsonElement>(await Tools().FileManage("list", _tmp));

        root.GetProperty("Truncated").GetBoolean().Should().BeFalse("two entries is under the 1000 default");
        root.GetProperty("MaxEntries").GetInt32().Should().Be(1000);
        var entries = root.GetProperty("Entries").EnumerateArray().ToArray();
        entries.Should().HaveCount(2);
        var file = entries.Should().ContainSingle(e => e.GetProperty("Name").GetString() == "a.txt").Subject;
        file.GetProperty("Path").GetString().Should().Be(Path.Combine(_tmp, "a.txt"));
        file.GetProperty("IsDirectory").GetBoolean().Should().BeFalse();
        file.GetProperty("Size").GetInt64().Should().Be(5, "the size is the file's, not a placeholder");
        file.GetProperty("Hidden").GetBoolean().Should().BeFalse();
        file.GetProperty("Modified").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        var dir = entries.Should().ContainSingle(e => e.GetProperty("Name").GetString() == "sub").Subject;
        dir.GetProperty("IsDirectory").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// C-1 R4b-6 through the tool (GREEN): a junction is an entry carrying <c>IsLink:true</c> and
    /// the walk stops at it. The mocked siblings serialise rows a hand-written
    /// <see cref="WindowsMcp.Abstractions.Models.FileListing"/> already contains, so only a real
    /// link under a real recursive walk can show that the reply carries the flag and that what is
    /// behind the link stays out of the listing.
    /// </summary>
    [Fact]
    public async Task List_over_the_real_file_system_marks_a_link_and_does_not_walk_into_it()
    {
        var target = Path.Combine(_tmp, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "behind-the-link.txt"), "behind");
        var root = Path.Combine(_tmp, "root");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "plain.txt"), "plain");
        var link = Path.Combine(root, "link");
        Mklink(link, target);

        try
        {
            var entries = JsonSerializer.Deserialize<JsonElement>(
                    await Tools().FileManage("list", root, recursive: true))
                .GetProperty("Entries").EnumerateArray().ToArray();

            entries.Should().ContainSingle(e => e.GetProperty("Name").GetString() == "link").Subject
                .GetProperty("IsLink").GetBoolean().Should().BeTrue(
                    "nothing else in the row tells a caller a junction apart from a directory");
            entries.Should().ContainSingle(e => e.GetProperty("Name").GetString() == "plain.txt").Subject
                .GetProperty("IsLink").GetBoolean().Should().BeFalse("a plain file is not a link");
            entries.Should().NotContain(e => e.GetProperty("Name").GetString() == "behind-the-link.txt",
                "the link is listed, never descended into - what is behind it was not the directory the caller listed");
        }
        finally { try { Directory.Delete(link); } catch { /* best effort */ } }
    }

    /// <summary>
    /// C-1 R4d-4 where the caller stands: <c>list</c> of a FILE. The service's sentence has to
    /// reach the client as it is — the tool must neither swallow it into an empty listing (which
    /// would read as "that directory is empty") nor reshape it. Paired with the service-level
    /// <c>FileSystemServiceRound4Tests.ListAsync_of_a_file_says_it_is_not_a_directory</c>: that one
    /// pins the sentence, this one pins that <c>file_manage</c> lets it out.
    /// </summary>
    [Fact]
    public async Task List_of_a_file_fails_with_the_services_sentence()
    {
        var file = Path.Combine(_tmp, "not-a-directory.txt");
        await File.WriteAllTextAsync(file, "x");

        var act = () => Tools().FileManage("list", file);

        var message = (await act.Should().ThrowAsync<IOException>(
                "an empty listing would tell the caller the opposite of what happened")).Which.Message;
        message.Should().Contain(file).And.ContainEquivalentOf("not a directory");
    }

    /// <summary>A directory junction - no elevation needed, unlike a symlink.</summary>
    private static void Mklink(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit(30_000);
        mklink.ExitCode.Should().Be(0, "the junction is what this test is about");
    }
}
