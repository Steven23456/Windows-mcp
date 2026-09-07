using System.Diagnostics;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// C-1 R4-4/R4-8: what <c>file_manage(delete)</c> REPLIES, through the real
/// <see cref="FileSystemService"/> rather than a mock. The mocked sibling in
/// <see cref="FileToolsTests"/> can only prove the tool composes a sentence; whether the sentence
/// is true of the file system - the link was removed and its target was not, nothing was there to
/// delete - needs the real thing. Everything happens under one temp directory.
/// </summary>
[Trait("Category", "Integration")]
public class FileToolsDeleteReplyTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-del-" + Guid.NewGuid().ToString("N"));

    public FileToolsDeleteReplyTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        // Removing a junction is not synchronous enough for the parent's RemoveDirectory that
        // follows it: the first attempt can fail with "directory is not empty" against a
        // directory whose children have all gone. Retry rather than leave a stray %TEMP% entry.
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
    /// R4-4: a junction is a link, not the directory it points at. Deleting it removes the link
    /// and needs no <c>recursive:true</c> - today the emptiness check enumerates through the link,
    /// so the caller is told to pass the flag that would take the TARGET's tree with it.
    /// </summary>
    [Fact]
    public async Task Delete_of_a_junction_removes_the_link_and_says_so()
    {
        var target = Path.Combine(_tmp, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "inside.txt"), "inside");
        var link = Path.Combine(_tmp, "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
               })!)
        {
            mklink.WaitForExit(30_000);
            mklink.ExitCode.Should().Be(0, "the junction is what this test is about");
        }

        var reply = await Tools().FileManage("delete", link, confirm: true);

        reply.Should().ContainEquivalentOf("removed link",
            "'deleted' would read as though the target's tree went too");
        Directory.Exists(link).Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(target, "inside.txt"))).Should().Be("inside",
            "the link's target is not the delete target");
    }

    /// <summary>R4-8: nothing was there, so the reply must not claim a deletion.</summary>
    [Fact]
    public async Task Delete_of_a_path_that_is_not_there_says_nothing_was_deleted()
    {
        var missing = Path.Combine(_tmp, "never-existed.txt");

        var reply = await Tools().FileManage("delete", missing, confirm: true);

        reply.Should().ContainEquivalentOf("nothing").And.Contain(missing);
        reply.Should().NotStartWith("deleted");
    }

    /// <summary>The other half of the same reply: a real file really is deleted, and says so.</summary>
    [Fact]
    public async Task Delete_of_a_file_that_is_there_still_says_deleted()
    {
        var file = Path.Combine(_tmp, "real.txt");
        await File.WriteAllTextAsync(file, "content");

        var reply = await Tools().FileManage("delete", file, confirm: true);

        reply.Should().ContainEquivalentOf("deleted").And.Contain(file);
        File.Exists(file).Should().BeFalse();
    }

    // ---- C-1 round 4b ---------------------------------------------------------------------------

    /// <summary>
    /// R4b-7: the parent directory is missing too, so <c>File.Delete</c> answers
    /// DirectoryNotFoundException instead of shrugging the way it does for a missing file. Nothing
    /// is there either way — a delete of what is not there is a no-op, and the reply says so
    /// rather than reporting a fault the caller cannot act on.
    /// </summary>
    [Fact]
    public async Task Delete_of_a_path_whose_parent_is_missing_says_nothing_was_deleted()
    {
        var missing = Path.Combine(_tmp, "never-existed-dir", "gone.txt");

        var reply = await Tools().FileManage("delete", missing, confirm: true);

        reply.Should().ContainEquivalentOf("nothing").And.Contain(missing);
        reply.Should().NotStartWith("deleted", "nothing was deleted, so the reply must not say it was");
    }

    /// <summary>
    /// R4b-3 through the tool: a tree holding both obstacles at once. The delete succeeds, the
    /// junction's target is untouched, and the reply is the plain "deleted" — not "removed link"
    /// (the tree was the target, not a link) and not an exception.
    /// </summary>
    [Fact]
    public async Task Delete_recursive_of_a_tree_holding_a_junction_and_a_read_only_file_says_deleted()
    {
        var target = Path.Combine(_tmp, "keep-target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "inside.txt"), "inside");
        var tree = Path.Combine(_tmp, "tree");
        Directory.CreateDirectory(tree);
        var readOnly = Path.Combine(tree, "ro.txt");
        await File.WriteAllTextAsync(readOnly, "ro");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var link = Path.Combine(tree, "link");
        Mklink(link, target);

        try
        {
            var reply = await Tools().FileManage("delete", tree, confirm: true, recursive: true);

            reply.Should().ContainEquivalentOf("deleted").And.Contain(tree);
            reply.Should().NotContainEquivalentOf("removed link",
                "the caller named the tree, not the junction inside it");
            Directory.Exists(tree).Should().BeFalse("the whole tree goes");
            (await File.ReadAllTextAsync(Path.Combine(target, "inside.txt"))).Should().Be("inside",
                "the junction's target is not part of the tree the caller asked to delete");
        }
        finally
        {
            try { if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal); } catch { /* best effort */ }
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { /* best effort */ }
        }
    }

    // ---- C-1 round 4c ---------------------------------------------------------------------------

    /// <summary>
    /// R4c-10: a FILE symlink. The tool's link check is <c>Directory.Exists(src) &amp;&amp;
    /// ReparsePoint</c>, and <c>Directory.Exists</c> is false for one — so the reply is the plain
    /// "deleted", which reads as though the file the link pointed at went with it. What was
    /// deleted is the link; the target is still there, and that difference is the whole reason the
    /// junction reply says "removed link" already.
    /// </summary>
    [Fact]
    public async Task Delete_of_a_file_symlink_removes_the_link_and_says_so()
    {
        var target = Path.Combine(_tmp, "symlink-target.txt");
        await File.WriteAllTextAsync(target, "content");
        var link = Path.Combine(_tmp, "symlink.txt");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // no Developer Mode / no SeCreateSymbolicLink privilege: a file symlink cannot be made here
        }

        var reply = await Tools().FileManage("delete", link, confirm: true);

        reply.Should().ContainEquivalentOf("removed link",
            "'deleted' reads as though the file at the other end went too");
        reply.Should().Contain(link, "and the reply names what the caller asked about");
        File.Exists(link).Should().BeFalse("the link is gone");
        (await File.ReadAllTextAsync(target)).Should().Be("content",
            "the link's target is not the delete target");
    }

    /// <summary>A directory junction — no elevation needed, unlike a symlink.</summary>
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
