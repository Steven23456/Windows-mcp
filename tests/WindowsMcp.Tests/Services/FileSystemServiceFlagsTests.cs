using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R2: the flags the service grew, against the real file system in a temp directory. The
/// sibling <see cref="FileSystemServiceTests"/> keeps the pre-C-1 behaviour; everything here is
/// either a new parameter or a refusal that did not exist before (copy/move over an existing
/// target, a recursive delete). Nothing outside <c>_tmp</c> is touched.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemServiceFlagsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-fs-" + Guid.NewGuid().ToString("N"));
    private static FileSystemService Svc() => new();

    public FileSystemServiceFlagsTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()
    {
        ForceDelete(_tmp);
        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string name, string content = "x")
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- ReadLinesAsync ----------------------------------------------------------------------

    [Fact]
    public async Task ReadLinesAsync_windows_a_crlf_file_by_line_not_by_byte()
    {
        var path = File_("crlf.txt", "l1\r\nl2\r\nl3\r\nl4\r\nl5\r\n");

        var window = await Svc().ReadLinesAsync(path, 1_048_576, "utf-8", 2, 2);

        window.TotalLines.Should().Be(5, "the trailing newline is not a sixth line");
        window.Offset.Should().Be(2);
        window.Returned.Should().Be(2);
        window.Content.Should().Be("l2\nl3", "the window joins with \\n, whatever the file used");
        window.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ReadLinesAsync_still_bounds_the_file_by_max_bytes()
    {
        // max_bytes bounds the FILE, not the window: a 2 KB file is refused even for one line.
        var path = File_("big.txt", string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}")));

        var act = () => Svc().ReadLinesAsync(path, 100, "utf-8", 1, 1);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds*");
    }

    // ---- WriteTextAsync: append / createParents ------------------------------------------------

    [Fact]
    public async Task WriteTextAsync_with_append_keeps_what_was_there_and_adds_to_it()
    {
        var path = Path.Combine(_tmp, "log.txt");
        var svc = Svc();

        await svc.WriteTextAsync(path, "first\n", "utf-8", append: true, createParents: true);
        await svc.WriteTextAsync(path, "second\n", "utf-8", append: true, createParents: true);

        (await File.ReadAllTextAsync(path)).Should().Be("first\nsecond\n");
    }

    [Fact]
    public async Task WriteTextAsync_without_append_replaces_the_file()
    {
        var path = File_("replace.txt", "old content");

        await Svc().WriteTextAsync(path, "new", "utf-8", append: false, createParents: true);

        (await File.ReadAllTextAsync(path)).Should().Be("new");
    }

    [Fact]
    public async Task WriteTextAsync_with_create_parents_creates_the_missing_directory()
    {
        var path = Path.Combine(_tmp, "a", "b", "c.txt");

        await Svc().WriteTextAsync(path, "deep", "utf-8", append: false, createParents: true);

        (await File.ReadAllTextAsync(path)).Should().Be("deep");
    }

    [Fact]
    public async Task WriteTextAsync_without_create_parents_refuses_a_missing_directory_by_name()
    {
        var path = Path.Combine(_tmp, "nope", "c.txt");

        var act = () => Svc().WriteTextAsync(path, "deep", "utf-8", append: false, createParents: false);

        (await act.Should().ThrowAsync<DirectoryNotFoundException>()).Which.Message
            .Should().Contain("create_parents", "the refusal has to name the flag that would allow it");
        File.Exists(path).Should().BeFalse();
    }

    // ---- CopyAsync ---------------------------------------------------------------------------

    [Fact]
    public async Task CopyAsync_refuses_an_existing_destination_unless_overwrite()
    {
        var src = File_("src.txt", "source");
        var dst = File_("dst.txt", "destination");

        var act = () => Svc().CopyAsync(src, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("overwrite");
        (await File.ReadAllTextAsync(dst)).Should().Be("destination", "the refusal must not have copied anything");
    }

    [Fact]
    public async Task CopyAsync_with_overwrite_replaces_the_destination()
    {
        var src = File_("src2.txt", "source");
        var dst = File_("dst2.txt", "destination");

        await Svc().CopyAsync(src, dst, overwrite: true);

        (await File.ReadAllTextAsync(dst)).Should().Be("source");
    }

    [Fact]
    public async Task CopyAsync_of_a_directory_copies_the_tree()
    {
        // File.Copy throws on a directory, which is what C-1 fixes.
        var src = Dir("tree");
        File_(Path.Combine("tree", "top.txt"), "top");
        File_(Path.Combine("tree", "sub", "deep.txt"), "deep");
        var dst = Path.Combine(_tmp, "tree-copy");

        await Svc().CopyAsync(src, dst, overwrite: false);

        (await File.ReadAllTextAsync(Path.Combine(dst, "top.txt"))).Should().Be("top");
        (await File.ReadAllTextAsync(Path.Combine(dst, "sub", "deep.txt"))).Should().Be("deep");
        Directory.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    /// <summary>
    /// PR #25 review finding: overwrite:true means REPLACE, on copy exactly as it already does on
    /// move (<see cref="MoveAsync_of_a_directory_with_overwrite_replaces_an_existing_directory"/>).
    /// A tree copied over an existing directory must leave the source's entries and nothing else -
    /// a file that existed only in the destination surviving the copy is a silent merge, and the
    /// caller reading the result would attribute that stale file to the source.
    /// </summary>
    [Fact]
    public async Task CopyAsync_of_a_directory_with_overwrite_replaces_an_existing_directory()
    {
        var src = Dir("crep");
        File_(Path.Combine("crep", "new.txt"), "new");
        var dst = Dir("crep-dst");
        File_(Path.Combine("crep-dst", "stale.txt"), "stale");

        await Svc().CopyAsync(src, dst, overwrite: true);

        (await File.ReadAllTextAsync(Path.Combine(dst, "new.txt"))).Should().Be("new");
        File.Exists(Path.Combine(dst, "stale.txt")).Should()
            .BeFalse("the destination was replaced, not merged into");
        Directory.EnumerateFileSystemEntries(dst, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dst, p))
            .Should().BeEquivalentTo(new[] { "new.txt" }, "exactly the source's tree is left behind");
        Directory.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    [Fact]
    public async Task CopyAsync_of_a_directory_with_overwrite_replaces_an_existing_file()
    {
        // Directory.CreateDirectory cannot create over a file, so overwrite:true has to clear the
        // file first rather than let an IOException out - MoveAsync already does exactly this.
        var src = Dir("cfile");
        File_(Path.Combine("cfile", "inside.txt"), "inside");
        var dst = File_("cfile-dst", "a file standing where the directory should go");

        await Svc().CopyAsync(src, dst, overwrite: true);

        Directory.Exists(dst).Should().BeTrue("the file gave way to the directory");
        (await File.ReadAllTextAsync(Path.Combine(dst, "inside.txt"))).Should().Be("inside");
        Directory.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    [Fact]
    public async Task CopyAsync_of_a_file_with_overwrite_replaces_an_existing_directory()
    {
        // The mirror image: File.Copy cannot write over a directory, so overwrite:true has to
        // remove the directory first. Refusing here while MoveAsync accepts it is an inconsistency
        // no caller can predict from the tool description.
        var src = File_("cf-src.txt", "source");
        var dst = Dir("cf-dst");
        File_(Path.Combine("cf-dst", "stale.txt"), "stale");

        await Svc().CopyAsync(src, dst, overwrite: true);

        File.Exists(dst).Should().BeTrue("the directory gave way to the file");
        Directory.Exists(dst).Should().BeFalse("nothing of the old destination tree is left");
        (await File.ReadAllTextAsync(dst)).Should().Be("source");
        File.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    // ---- MoveAsync ---------------------------------------------------------------------------

    [Fact]
    public async Task MoveAsync_refuses_an_existing_destination_unless_overwrite()
    {
        var src = File_("m-src.txt", "source");
        var dst = File_("m-dst.txt", "destination");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("overwrite");
        File.Exists(src).Should().BeTrue("the refusal must not have moved anything");
        (await File.ReadAllTextAsync(dst)).Should().Be("destination");
    }

    [Fact]
    public async Task MoveAsync_with_overwrite_replaces_the_destination_and_removes_the_source()
    {
        var src = File_("m2-src.txt", "source");
        var dst = File_("m2-dst.txt", "destination");

        await Svc().MoveAsync(src, dst, overwrite: true);

        (await File.ReadAllTextAsync(dst)).Should().Be("source");
        File.Exists(src).Should().BeFalse();
    }

    /// <summary>
    /// A writable directory on a volume that is NOT the temp directory's, or null when this box
    /// has only one (Directory.Move / File.Move refuse a cross-volume move outright, so the
    /// fallback cannot be proven on a single-drive box). The caller deletes it.
    /// </summary>
    private string? OtherVolumeLanding()
    {
        var other = DriveInfo.GetDrives().FirstOrDefault(d =>
            d.IsReady && d.DriveType == DriveType.Fixed &&
            !string.Equals(d.RootDirectory.FullName, Path.GetPathRoot(_tmp), StringComparison.OrdinalIgnoreCase));
        if (other is null) return null;   // one volume on this box: nothing to move across

        var landing = Path.Combine(other.RootDirectory.FullName, "wmcp-xvol-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(landing); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return null; }   // not writable here
        return landing;
    }

    [Fact]
    public async Task MoveAsync_across_volumes_falls_back_to_copy_then_delete()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;

        try
        {
            var src = File_("xvol.txt", "carried across");
            var dst = Path.Combine(landing, "xvol.txt");

            await Svc().MoveAsync(src, dst, overwrite: false);

            (await File.ReadAllTextAsync(dst)).Should().Be("carried across");
            File.Exists(src).Should().BeFalse("a move removes the source even when it had to be a copy");
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task MoveAsync_of_a_directory_moves_the_whole_tree()
    {
        // The design's "a move of a directory" path: Directory.Move, not File.Move, and the
        // children have to arrive with it.
        var src = Dir("mtree");
        File_(Path.Combine("mtree", "top.txt"), "top");
        File_(Path.Combine("mtree", "sub", "deep.txt"), "deep");
        var dst = Path.Combine(_tmp, "mtree-moved");

        await Svc().MoveAsync(src, dst, overwrite: false);

        (await File.ReadAllTextAsync(Path.Combine(dst, "top.txt"))).Should().Be("top");
        (await File.ReadAllTextAsync(Path.Combine(dst, "sub", "deep.txt"))).Should().Be("deep");
        Directory.Exists(src).Should().BeFalse("a move leaves nothing behind");
    }

    [Fact]
    public async Task MoveAsync_of_a_directory_with_overwrite_replaces_an_existing_directory()
    {
        // Directory.Move cannot replace an existing target, so overwrite:true has to clear it
        // first - and clear it COMPLETELY: a leftover from the old tree would be a silent merge.
        var src = Dir("mrep");
        File_(Path.Combine("mrep", "new.txt"), "new");
        var dst = Dir("mrep-dst");
        File_(Path.Combine("mrep-dst", "stale.txt"), "stale");

        await Svc().MoveAsync(src, dst, overwrite: true);

        (await File.ReadAllTextAsync(Path.Combine(dst, "new.txt"))).Should().Be("new");
        File.Exists(Path.Combine(dst, "stale.txt")).Should().BeFalse("the destination was replaced, not merged into");
        Directory.Exists(src).Should().BeFalse();
    }

    [Fact]
    public async Task MoveAsync_of_a_directory_with_overwrite_replaces_an_existing_file()
    {
        var src = Dir("mfile");
        File_(Path.Combine("mfile", "inside.txt"), "inside");
        var dst = File_("mfile-dst", "a file standing where the directory should go");

        await Svc().MoveAsync(src, dst, overwrite: true);

        Directory.Exists(dst).Should().BeTrue("the file gave way to the directory");
        (await File.ReadAllTextAsync(Path.Combine(dst, "inside.txt"))).Should().Be("inside");
    }

    [Fact]
    public async Task MoveAsync_of_a_directory_across_volumes_copies_then_deletes()
    {
        // Directory.Move throws IOException across volumes - THIS is the case the copy-then-delete
        // fallback exists for (a cross-volume FILE move is handled by File.Move itself).
        var landing = OtherVolumeLanding();
        if (landing is null) return;

        try
        {
            var src = Dir("xvoldir");
            File_(Path.Combine("xvoldir", "top.txt"), "top");
            File_(Path.Combine("xvoldir", "sub", "deep.txt"), "deep");
            var dst = Path.Combine(landing, "xvoldir");

            await Svc().MoveAsync(src, dst, overwrite: false);

            (await File.ReadAllTextAsync(Path.Combine(dst, "top.txt"))).Should().Be("top");
            (await File.ReadAllTextAsync(Path.Combine(dst, "sub", "deep.txt"))).Should().Be("deep");
            Directory.Exists(src).Should().BeFalse("the source tree is deleted once the copy landed");
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    // ---- DeleteAsync -------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_refuses_a_non_empty_directory_unless_recursive()
    {
        var dir = Dir("full");
        File_(Path.Combine("full", "child.txt"), "child");

        var act = () => Svc().DeleteAsync(dir, recursive: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("recursive");
        Directory.Exists(dir).Should().BeTrue("the tree is still there");
    }

    [Fact]
    public async Task DeleteAsync_removes_a_non_empty_directory_with_recursive()
    {
        var dir = Dir("full2");
        File_(Path.Combine("full2", "child.txt"), "child");

        await Svc().DeleteAsync(dir, recursive: true);

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_removes_an_empty_directory_without_recursive()
    {
        var dir = Dir("empty");

        await Svc().DeleteAsync(dir, recursive: false);

        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_removes_a_file_without_recursive()
    {
        var file = File_("gone.txt");

        await Svc().DeleteAsync(file, recursive: false);

        File.Exists(file).Should().BeFalse();
    }

    // ---- ListAsync ---------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_returns_the_entry_fields_for_a_file_and_a_directory()
    {
        // Built segment by segment: the listing reports FileSystemInfo.FullName, which is
        // separator-normalised, so an expectation written with a forward slash would compare a
        // path Windows never produces (see ListAsync_reports_the_path_windows_normalised_it_to).
        var file = File_(Path.Combine("entries", "one.txt"), "1234567890");
        var sub = Dir(Path.Combine("entries", "child"));
        var root = Path.Combine(_tmp, "entries");

        var entries = await Svc().ListAsync(root, null, recursive: false, includeHidden: false);

        var one = entries.Should().ContainSingle(e => e.Name == "one.txt").Subject;
        one.Path.Should().Be(file);
        one.IsDirectory.Should().BeFalse();
        one.Size.Should().Be(10);
        one.Hidden.Should().BeFalse();
        one.Modified.Should().BeCloseTo(File.GetLastWriteTimeUtc(file), TimeSpan.FromSeconds(2));

        var child = entries.Should().ContainSingle(e => e.Name == "child").Subject;
        child.Path.Should().Be(sub);
        child.IsDirectory.Should().BeTrue();
        child.Size.Should().Be(0, "a directory has no size of its own");
    }

    /// <summary>
    /// C-1 deviation, pinned: the entry's Path is <c>FileSystemInfo.FullName</c>, so whatever
    /// separators the caller passed in, what comes back is the Windows form. A caller comparing
    /// the listing against a path it built with '/' has to normalise, and this is the test that
    /// says so out loud.
    /// </summary>
    [Fact]
    public async Task ListAsync_reports_the_path_windows_normalised_it_to()
    {
        File_(Path.Combine("norm", "n.txt"));
        var rootWithForwardSlashes = _tmp + "/norm";

        var entries = await Svc().ListAsync(rootWithForwardSlashes, null, recursive: false, includeHidden: false);

        var entry = entries.Should().ContainSingle().Subject;
        entry.Path.Should().Be(Path.Combine(_tmp, "norm", "n.txt"))
            .And.NotContain("/", "FullName reports the Windows separator whatever the caller wrote");
    }

    [Fact]
    public async Task ListAsync_applies_the_glob_to_names_case_insensitively()
    {
        File_("glob/a.txt");
        File_("glob/b.TXT");
        File_("glob/c.log");
        Dir(Path.Combine("glob", "d.txt"));   // the pattern matches directories too

        var entries = await Svc().ListAsync(Path.Combine(_tmp, "glob"), "*.txt", recursive: false, includeHidden: false);

        entries.Select(e => e.Name).Should().BeEquivalentTo(new[] { "a.txt", "b.TXT", "d.txt" });
    }

    [Fact]
    public async Task ListAsync_without_recursive_stays_in_the_directory()
    {
        File_("rec/top.txt");
        File_("rec/sub/deep.txt");

        var entries = await Svc().ListAsync(Path.Combine(_tmp, "rec"), null, recursive: false, includeHidden: false);

        entries.Select(e => e.Name).Should().BeEquivalentTo(new[] { "top.txt", "sub" });
    }

    [Fact]
    public async Task ListAsync_with_recursive_descends()
    {
        File_("rec2/top.txt");
        File_("rec2/sub/deep.txt");

        var entries = await Svc().ListAsync(Path.Combine(_tmp, "rec2"), "*.txt", recursive: true, includeHidden: false);

        entries.Select(e => e.Name).Should().BeEquivalentTo(new[] { "top.txt", "deep.txt" });
    }

    [Fact]
    public async Task ListAsync_skips_hidden_and_system_entries_unless_asked()
    {
        var root = Dir("attrs");
        File_("attrs/plain.txt");
        var hidden = File_("attrs/hidden.txt");
        var system = File_("attrs/system.txt");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);

        var without = await Svc().ListAsync(root, null, recursive: false, includeHidden: false);
        var with = await Svc().ListAsync(root, null, recursive: false, includeHidden: true);

        without.Select(e => e.Name).Should().BeEquivalentTo(new[] { "plain.txt" },
            "hidden AND system are skipped - that is what keeps $RECYCLE.BIN out of a root listing");
        with.Select(e => e.Name).Should().BeEquivalentTo(new[] { "plain.txt", "hidden.txt", "system.txt" });
        with.Single(e => e.Name == "hidden.txt").Hidden.Should().BeTrue();
        with.Single(e => e.Name == "plain.txt").Hidden.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_does_not_descend_into_a_hidden_directory()
    {
        var root = Dir("skipdir");
        File_("skipdir/visible.txt");
        var hiddenDir = Dir(Path.Combine("skipdir", "secret"));
        File_("skipdir/secret/inside.txt");
        File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);

        var entries = await Svc().ListAsync(root, null, recursive: true, includeHidden: false);

        entries.Select(e => e.Name).Should().BeEquivalentTo(new[] { "visible.txt" },
            "a skipped directory is not descended into either, or its children leak into the listing");
    }

    // ---- Copy/Move into the source's own subtree (PR #25 review finding) ----------------------

    /// <summary>
    /// The directory a copy is pointed at inside its own source. The name is long on purpose:
    /// until the containment check exists, <c>CopyDirectory</c> creates it, the lazy
    /// <c>EnumerateDirectories</c> of the source then picks it up and recurses into
    /// sub\sub\sub... until the path limit stops it. This box has LongPathsEnabled=1, so a
    /// three-letter name would bottom out thousands of levels down; ~126 characters per level
    /// bottoms out in a couple of hundred, which is fast and leaves a chain
    /// <see cref="ForceDelete"/> can still remove.
    /// </summary>
    private const string SubtreeName =
        "sub-a-copy-must-never-descend-into-" +
        "and-this-name-is-long-so-the-runaway-bottoms-out-in-a-few-hundred-levels-not-a-few-thousand";

    /// <summary>
    /// PR #25 review finding, the runaway: a directory copied into a destination *inside itself*
    /// has no fixed point - every file copied in becomes another file to copy. The service has to
    /// refuse before it creates anything, naming both paths.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_destination_inside_the_source_tree()
    {
        var src = Dir("runaway");
        File_(Path.Combine("runaway", "one.txt"), "one");
        File_(Path.Combine("runaway", "two.txt"), "two");
        var dst = Path.Combine(src, SubtreeName);

        var act = () => Svc().CopyAsync(src, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a copy into the source's own subtree can never terminate"))
            .Which.Message.Should().Contain(src).And.Contain(dst,
                "the refusal names both paths so the caller can see the containment");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(src, p))
            .Should().BeEquivalentTo(new[] { "one.txt", "two.txt" }, "the source is left exactly as it was");
    }

    /// <summary>
    /// The degenerate case of the same finding: source and destination are the same directory.
    /// Refused whatever <c>overwrite</c> says and whatever trailing separator the caller wrote -
    /// and the refusal must not offer <c>overwrite:true</c> as the way through, because it is not.
    /// </summary>
    [Theory]
    [InlineData(false, "", "")]
    [InlineData(true, "", "")]
    [InlineData(false, "\\", "")]
    [InlineData(true, "", "\\")]
    public async Task CopyAsync_refuses_a_destination_that_is_the_source(bool overwrite, string srcSuffix, string dstSuffix)
    {
        var name = $"same-{overwrite}-{srcSuffix.Length}{dstSuffix.Length}";
        var dir = Dir(name);
        File_(Path.Combine(name, "keep.txt"), "keep");

        var act = () => Svc().CopyAsync(dir + srcSuffix, dir + dstSuffix, overwrite);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a directory cannot be copied onto itself")).Which.Message;
        message.Should().Contain(dir, "the refusal names the path it is talking about");
        message.Should().NotContain("overwrite:true",
            "the refusal holds regardless of overwrite, so offering that flag as the remedy would be false");
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p))
            .Should().BeEquivalentTo(new[] { "keep.txt" }, "nothing was created or removed");
        (await File.ReadAllTextAsync(Path.Combine(dir, "keep.txt"))).Should().Be("keep");
    }

    /// <summary>
    /// The same containment, with the destination already there and <c>overwrite:true</c>. The
    /// existing check in <c>ClearDestination</c> only catches a destination that *contains* the
    /// source; this is the other direction, and clearing the destination here deletes a subtree
    /// of the very thing being copied before the runaway even starts.
    /// </summary>
    [Fact]
    public async Task CopyAsync_with_overwrite_refuses_an_existing_destination_inside_the_source_tree()
    {
        var src = Dir("runaway-existing");
        File_(Path.Combine("runaway-existing", "one.txt"), "one");
        var dst = Path.Combine(src, SubtreeName);
        Directory.CreateDirectory(dst);
        await File.WriteAllTextAsync(Path.Combine(dst, "already-there.txt"), "already there");

        var act = () => Svc().CopyAsync(src, dst, overwrite: true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain(dst);
        (await File.ReadAllTextAsync(Path.Combine(dst, "already-there.txt"))).Should()
            .Be("already there", "a refusal removes nothing - clearing this destination deletes part of the source");
        Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(src, p))
            .Should().BeEquivalentTo(new[] { "one.txt", SubtreeName, Path.Combine(SubtreeName, "already-there.txt") });
    }

    /// <summary>
    /// A move into the source's own subtree is the same refusal with the same exception type.
    /// Windows refuses it too, but with an <see cref="IOException"/> a caller cannot tell apart
    /// from a disk error, so the service has to refuse it first.
    /// </summary>
    [Fact]
    public async Task MoveAsync_refuses_a_destination_inside_the_source_tree()
    {
        var src = Dir("mv-into-self");
        File_(Path.Combine("mv-into-self", "one.txt"), "one");
        var dst = Path.Combine(src, "sub");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a directory cannot be moved inside itself"))
            .Which.Message.Should().Contain(src).And.Contain(dst);
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        (await File.ReadAllTextAsync(Path.Combine(src, "one.txt"))).Should().Be("one", "nothing moved");
    }

    /// <summary>
    /// The guard on the guard: containment is about path *segments*, not string prefixes.
    /// C:\tmp\ab -> C:\tmp\abc shares a prefix and contains nothing, so it must still be copied.
    /// A check written as a bare StartsWith would refuse this.
    /// </summary>
    [Fact]
    public async Task CopyAsync_allows_a_file_whose_destination_merely_shares_a_prefix()
    {
        var src = File_("ab", "prefix source");
        var dst = Path.Combine(_tmp, "abc");

        await Svc().CopyAsync(src, dst, overwrite: false);

        (await File.ReadAllTextAsync(dst)).Should().Be("prefix source");
        File.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    /// <summary>The same look-alike between two directories, where the containment check bites.</summary>
    [Fact]
    public async Task CopyAsync_allows_a_directory_whose_destination_merely_shares_a_prefix()
    {
        var src = Dir("pre");
        File_(Path.Combine("pre", "inside.txt"), "inside");
        var dst = Path.Combine(_tmp, "prefix");

        await Svc().CopyAsync(src, dst, overwrite: false);

        (await File.ReadAllTextAsync(Path.Combine(dst, "inside.txt"))).Should().Be("inside");
        Directory.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    /// <summary>
    /// The runaway cases can leave a chain of nested directories tens of thousands of characters
    /// deep, which a plain recursive delete cannot always remove (PathTooLongException). Falls
    /// back to the extended-length form, then to a robocopy mirror from an empty directory, which
    /// is long-path native. Nothing outside the caller's temp directory is touched.
    /// </summary>
    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); return; } catch { /* too deep for the plain form */ }
        try { Directory.Delete(@"\\?\" + Path.GetFullPath(path), true); return; } catch { /* still too deep */ }

        var empty = Path.Combine(Path.GetTempPath(), "wmcp-fs-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(empty);
            using var robocopy = Process.Start(new ProcessStartInfo("robocopy",
                $"\"{empty}\" \"{path}\" /MIR /NJH /NJS /NP /NFL /NDL /R:0 /W:0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            robocopy?.WaitForExit(120_000);
            Directory.Delete(path, true);
        }
        catch { /* best effort: Dispose must not throw; a leftover under %TEMP% is reported instead */ }
        finally { try { Directory.Delete(empty, true); } catch { /* best effort */ } }
    }
}
