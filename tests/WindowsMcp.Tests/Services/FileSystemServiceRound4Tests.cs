using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// Serialises the two classes that reason about whole DRIVES. <see cref="FileSystemServiceRound4Tests"/>
/// creates and removes a <c>subst</c> drive; <see cref="FileSystemServiceFlagsTests"/> picks a
/// second volume out of <c>DriveInfo.GetDrives()</c> for its cross-volume move. Run in parallel,
/// the second could pick the first's drive and have it disappear underneath it.
/// </summary>
[CollectionDefinition(Name)]
public class FileSystemVolumesCollection
{
    public const string Name = "FileSystemVolumes";
}

/// <summary>
/// C-1 round 4 (the review-agent's findings), against the real file system in a temp directory:
/// R4-1 verify-before-mutating and the aside-and-restore, R4-2 canonical paths, R4-3 roots,
/// R4-4 junctions, R4-5 the bounded listing, R4-6 no orphaned temp file. The sibling
/// <see cref="FileSystemServiceFlagsTests"/> keeps rounds 1-3.
/// <para>
/// Everything is created under <c>_tmp</c>; the <c>subst</c> drive points INTO <c>_tmp</c> so
/// that a test which fails RED by wiping "a volume root" wipes only this directory, and the
/// junctions point at directories under <c>_tmp</c> too. <see cref="Dispose"/> removes the subst
/// drive and the junctions before deleting the tree.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(FileSystemVolumesCollection.Name)]
public class FileSystemServiceRound4Tests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-fs4-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private readonly List<string> _readOnly = [];
    private string? _substLetter;   // "Y:" while a drive is substituted
    private readonly List<string> _extraSubstLetters = [];   // R4f-1 needs a SECOND letter onto one directory

    private static FileSystemService Svc() => new();

    public FileSystemServiceRound4Tests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var path in _readOnly)
            try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { /* best effort */ }
        // Links go BEFORE the subst drive: R4b-5 points one at the subst root, and once the drive
        // is gone that link is dangling — Directory.Exists says false and nothing would remove it.
        foreach (var link in _junctions)
            try { Directory.Delete(link); } catch { /* best effort: gone already, or never created */ }
        if (_substLetter is not null)
            RunCmd($"subst {_substLetter} /D");
        foreach (var letter in _extraSubstLetters)
            RunCmd($"subst {letter} /D");
        ForceDelete(_tmp);
        GC.SuppressFinalize(this);
    }

    // ---- helpers -----------------------------------------------------------------------------

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

    /// <summary>Every entry under <paramref name="root"/>, relative — the set an assertion compares.</summary>
    private static string[] EntriesUnder(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    /// <summary>
    /// The <c>&lt;dst&gt;.replaced.&lt;guid&gt;</c> siblings R4-1 moves an existing destination to.
    /// None may survive a successful call, and none may survive a failed one either — a failure
    /// puts the aside BACK.
    /// </summary>
    private static string[] AsidesOf(string dst) =>
        Directory.EnumerateFileSystemEntries(
            Path.GetDirectoryName(Path.GetFullPath(dst))!,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dst))) + ".replaced.*").ToArray();

    /// <summary>Any leftover <c>&lt;name&gt;.tmp.&lt;guid&gt;</c> from the write path (R4-6).</summary>
    private static string[] TempFilesIn(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*.tmp.*").ToArray();

    private static (int ExitCode, string Output) RunCmd(string command)
    {
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, output);
    }

    /// <summary>A directory junction (no elevation needed, unlike a symlink), removed in Dispose.</summary>
    private string Junction(string linkName, string target)
    {
        var link = Path.Combine(_tmp, linkName);
        var (exit, output) = RunCmd($"mklink /J \"{link}\" \"{target}\"");
        exit.Should().Be(0, "mklink /J has to create the junction this test is about: {0}", output);
        _junctions.Add(link);
        return link;
    }

    /// <summary>
    /// A drive root of our own: <c>subst</c> maps a free letter onto a directory under
    /// <c>_tmp</c>, so "the root of a volume" is a path this test may legitimately point a copy
    /// at — and a RED run that clears it clears only our own directory. Null when every letter is
    /// taken, in which case the caller returns (xunit 2.x has no runtime skip; the reason is in
    /// the comment at each call site).
    /// </summary>
    private string? SubstRoot(string targetDirectory)
    {
        foreach (var letter in "YXWVUTSRQPNMKJIHGF".Select(c => c + ":"))
        {
            if (Directory.Exists(letter + "\\")) continue;
            var (exit, _) = RunCmd($"subst {letter} \"{targetDirectory}\"");
            if (exit != 0) continue;
            _substLetter = letter;
            var root = letter + "\\";
            // A drive that did not actually appear would fail these tests as a "refusal that did
            // not happen", which is the wrong story: say so here instead.
            Directory.Exists(root).Should().BeTrue("subst {0} has to produce a usable drive root", letter);
            return root;
        }
        return null;   // no free drive letter on this box: nothing to test a root against
    }

    /// <summary>
    /// A SECOND <c>subst</c> letter, for the one test that needs two spellings of the same
    /// directory that are both drive letters (R4f-1). <see cref="SubstRoot"/> keeps room for one
    /// letter only — the field <see cref="Dispose"/> removes — so these are remembered separately
    /// and removed alongside it. Null when this box has no further free letter, in which case the
    /// caller returns (the first letter is covered by R4d-6's canary; a second one is not
    /// something a box can be required to have).
    /// </summary>
    private string? ExtraSubstRoot(string targetDirectory)
    {
        foreach (var letter in "YXWVUTSRQPNMKJIHGF".Select(c => c + ":"))
        {
            if (Directory.Exists(letter + "\\")) continue;   // taken - including by SubstRoot's own letter
            var (exit, _) = RunCmd($"subst {letter} \"{targetDirectory}\"");
            if (exit != 0) continue;
            _extraSubstLetters.Add(letter);
            var root = letter + "\\";
            Directory.Exists(root).Should().BeTrue("subst {0} has to produce a usable drive root", letter);
            return root;
        }
        return null;   // one free letter and no more: nothing to move BETWEEN two letters
    }

    /// <summary>
    /// A writable directory on another volume, or null on a single-volume box (mirrors the
    /// sibling class's helper — <c>Directory.Move</c> only takes the copy-then-delete fallback
    /// across volumes). The caller deletes it.
    /// </summary>
    private string? OtherVolumeLanding()
    {
        var other = DriveInfo.GetDrives().FirstOrDefault(d =>
            d.IsReady && d.DriveType == DriveType.Fixed &&
            !string.Equals(d.RootDirectory.FullName, Path.GetPathRoot(_tmp), StringComparison.OrdinalIgnoreCase));
        if (other is null) return null;

        var landing = Path.Combine(other.RootDirectory.FullName, "wmcp-xvol4-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(landing); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return null; }
        return landing;
    }

    /// <summary>
    /// A directory name long enough that a runaway copy into its own subtree bottoms out on the
    /// path limit within a couple of hundred levels rather than thousands (the sibling class
    /// makes the same trade). Under 255 characters, the NTFS limit for one component.
    /// </summary>
    private static readonly string SubtreeName =
        "sub-a-copy-must-never-descend-into-" + new string('x', 200);

    /// <summary>
    /// The self-referencing junction's name. Shorter than <see cref="SubtreeName"/> because
    /// <c>mklink</c> runs through cmd.exe, which is not long-path aware - but still long enough
    /// that a copy which DOES follow the link bottoms out on the path limit in a couple of
    /// hundred levels instead of a couple of thousand.
    /// </summary>
    private static readonly string SelfLinkName = "loops-back-into-its-own-source-" + new string('y', 90);

    /// <summary>
    /// How long a test waits for the operation it is racing to reach the point it has to be caught
    /// at (round 4d). Generous — a loaded box under Defender is slow — and never an outcome: every
    /// wait that ends on this ceiling FAILS its test explicitly, because the assertions after a
    /// handshake that did not happen are about nothing.
    /// </summary>
    private static readonly TimeSpan HandshakeCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A read-only file, remembered so <see cref="Dispose"/> can clear the attribute again. Round
    /// 4b turns the read-only attribute from "a delete this service cannot do" into "an attribute
    /// the delete clears", so several tests need one in the way.
    /// </summary>
    private string ReadOnlyFile(string relativePath, string content = "x")
    {
        var path = File_(relativePath, content);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        _readOnly.Add(path);
        return path;
    }

    /// <summary>
    /// Clears the read-only attribute wherever a failed run left the file — at the destination
    /// (<paramref name="relative"/> null when the destination IS the file), or inside the
    /// <c>.replaced.</c> aside a failed replace left behind. <see cref="Dispose"/> cannot simply
    /// walk the tree looking for read-only files: a self-referencing junction would walk it into
    /// the path limit, so each test says where to look.
    /// </summary>
    private static void ReleaseReadOnly(string dst, string? relative = null)
    {
        var candidates = new List<string> { relative is null ? dst : Path.Combine(dst, relative) };
        try
        {
            foreach (var aside in AsidesOf(dst))
                candidates.Add(relative is null ? aside : Path.Combine(aside, relative));
        }
        catch { /* the destination's parent is gone: there is nothing left to look through */ }

        foreach (var candidate in candidates)
            try { if (File.Exists(candidate)) File.SetAttributes(candidate, FileAttributes.Normal); }
            catch { /* best effort */ }
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); return; } catch { /* too deep for the plain form */ }
        try { Directory.Delete(@"\\?\" + Path.GetFullPath(path), true); return; } catch { /* still too deep */ }

        var empty = Path.Combine(Path.GetTempPath(), "wmcp-fs4-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(empty);
            RunCmd($"robocopy \"{empty}\" \"{path}\" /MIR /NJH /NJS /NP /NFL /NDL /R:0 /W:0");
            Directory.Delete(path, true);
        }
        catch { /* best effort: Dispose must not throw */ }
        finally { try { Directory.Delete(empty, true); } catch { /* best effort */ } }
    }

    // ---- R4-1: verify before mutating; move aside, restore on failure ------------------------

    /// <summary>
    /// R4-1: the source is checked FIRST. Today the destination is cleared before anything looks
    /// at the source, so a typo in <c>src</c> costs the caller the destination tree and gives back
    /// a FileNotFoundException about a path that no longer matters.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_missing_source_and_leaves_the_destination_untouched()
    {
        var src = Path.Combine(_tmp, "never-created");
        var dst = Dir("copy-missing-dst");
        File_(Path.Combine("copy-missing-dst", "keep.txt"), "keep");
        var before = EntriesUnder(dst);

        var act = () => Svc().CopyAsync(src, dst, overwrite: true);

        (await act.Should().ThrowAsync<FileNotFoundException>(
                "a copy whose source does not exist has nothing to say but that"))
            .Which.Message.Should().Contain(src, "the refusal names the source it could not find");
        Directory.Exists(dst).Should().BeTrue("the destination was never the problem");
        EntriesUnder(dst).Should().BeEquivalentTo(before);
        (await File.ReadAllTextAsync(Path.Combine(dst, "keep.txt"))).Should().Be("keep");
        AsidesOf(dst).Should().BeEmpty("a refusal leaves no .replaced. sibling behind");
    }

    [Fact]
    public async Task MoveAsync_refuses_a_missing_source_and_leaves_the_destination_untouched()
    {
        var src = Path.Combine(_tmp, "never-created-either");
        var dst = Dir("move-missing-dst");
        File_(Path.Combine("move-missing-dst", "keep.txt"), "keep");
        var before = EntriesUnder(dst);

        var act = () => Svc().MoveAsync(src, dst, overwrite: true);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.Message.Should().Contain(src);
        Directory.Exists(dst).Should().BeTrue();
        EntriesUnder(dst).Should().BeEquivalentTo(before);
        AsidesOf(dst).Should().BeEmpty();
    }

    /// <summary>
    /// R4-1, the case that costs data: a same-volume directory move whose source cannot be moved
    /// (a file inside it is held open, so <c>Directory.Move</c> fails with "access is denied").
    /// The destination has already been cleared by then, so the caller ends up with neither the
    /// move nor what was there before. The destination must come back.
    /// </summary>
    [Fact]
    public async Task MoveAsync_of_a_locked_source_puts_the_destination_back()
    {
        var src = Dir("locked-src");
        var locked = File_(Path.Combine("locked-src", "locked.txt"), "locked");
        var dst = Dir("locked-dst");
        File_(Path.Combine("locked-dst", "stale.txt"), "stale");
        File_(Path.Combine("locked-dst", "sub", "deep.txt"), "deep");
        var before = EntriesUnder(dst);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => Svc().MoveAsync(src, dst, overwrite: true);

            await act.Should().ThrowAsync<IOException>("Directory.Move refuses a source with an open handle inside it");
        }

        EntriesUnder(dst).Should().BeEquivalentTo(before,
            "the destination holds exactly what it held before the failed move");
        (await File.ReadAllTextAsync(Path.Combine(dst, "stale.txt"))).Should().Be("stale");
        (await File.ReadAllTextAsync(Path.Combine(dst, "sub", "deep.txt"))).Should().Be("deep");
        AsidesOf(dst).Should().BeEmpty("the aside is put back, not left as a sibling for the caller to find");
        (await File.ReadAllTextAsync(locked)).Should().Be("locked", "a failed move leaves the source alone");
    }

    /// <summary>R4-1 across volumes: the same restore after the copy-then-delete fallback fails.</summary>
    [Fact]
    public async Task MoveAsync_across_volumes_that_fails_puts_the_destination_back()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;   // one volume on this box: nothing to move across

        try
        {
            var src = Dir("xvol-locked-src");
            var locked = File_(Path.Combine("xvol-locked-src", "locked.txt"), "locked");
            var dst = Path.Combine(landing, "dst");
            Directory.CreateDirectory(dst);
            await File.WriteAllTextAsync(Path.Combine(dst, "stale.txt"), "stale");
            var before = EntriesUnder(dst);

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var act = () => Svc().MoveAsync(src, dst, overwrite: true);

                await act.Should().ThrowAsync<IOException>();
            }

            EntriesUnder(dst).Should().BeEquivalentTo(before,
                "the cross-volume fallback copies file by file, so a failure part-way through has to be undone too");
            (await File.ReadAllTextAsync(Path.Combine(dst, "stale.txt"))).Should().Be("stale");
            AsidesOf(dst).Should().BeEmpty();
            Directory.Exists(src).Should().BeTrue("nothing of the source is deleted until the copy landed");
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// R4-1: a copy that fails on the third file. The first two files are already in place, so
    /// "delete the partial destination and put the aside back" is the only way the caller ends up
    /// with what they started with.
    /// </summary>
    [Fact]
    public async Task CopyAsync_that_fails_mid_tree_leaves_the_destination_as_it_was()
    {
        var src = Dir("midfail-src");
        File_(Path.Combine("midfail-src", "1.txt"), "one");
        File_(Path.Combine("midfail-src", "2.txt"), "two");
        var locked = File_(Path.Combine("midfail-src", "3.txt"), "three");
        File_(Path.Combine("midfail-src", "4.txt"), "four");
        var dst = Dir("midfail-dst");
        File_(Path.Combine("midfail-dst", "stale.txt"), "stale");
        File_(Path.Combine("midfail-dst", "sub", "deep.txt"), "deep");
        var before = EntriesUnder(dst);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => Svc().CopyAsync(src, dst, overwrite: true);

            await act.Should().ThrowAsync<IOException>("File.Copy cannot read a source held with FileShare.None");
        }

        EntriesUnder(dst).Should().BeEquivalentTo(before,
            "a half-copied tree over the caller's data is worse than no copy at all");
        (await File.ReadAllTextAsync(Path.Combine(dst, "stale.txt"))).Should().Be("stale");
        AsidesOf(dst).Should().BeEmpty();
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "1.txt", "2.txt", "3.txt", "4.txt" },
            "a copy leaves the source alone");
    }

    /// <summary>
    /// R4-1 under cancellation. The handshake is the first copied file appearing under the
    /// destination: only the walk creates it, so by then the destination has been cleared (today)
    /// or moved aside (after the fix), and the cancel below can only be observed by the walk
    /// itself. 1500 files leave room to cancel well before the end.
    /// </summary>
    [Fact]
    public async Task CopyAsync_cancelled_mid_tree_restores_the_destination()
    {
        const int Count = 1500;
        var src = Dir("cancel-src");
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(src, $"f{i:D4}.txt"), "x");
        var dst = Dir("cancel-dst");
        File_(Path.Combine("cancel-dst", "stale.txt"), "stale");
        var before = EntriesUnder(dst);

        using var cts = new CancellationTokenSource();
        var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: true, cts.Token));

        var spin = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(dst, "f0000.txt")) && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            Thread.SpinWait(20);
        // Round 4d: the ceiling is a way OUT of the wait, not an outcome. A cancel that never
        // raced a running copy would make the assertions below about nothing, so the three
        // possible exits are told apart here rather than blamed on the service.
        (File.Exists(Path.Combine(dst, "f0000.txt")) || copy.IsCompleted).Should().BeTrue(
            $"the copy never began writing into '{dst}' within {HandshakeCeiling.TotalSeconds:0} s, so this run "
            + "tested nothing: the cancel has to interrupt a copy in progress for the restore to mean anything");
        copy.IsCompleted.Should().BeFalse(
            "the copy finished before the cancel could reach it, so nothing was cancelled - "
            + $"{Count} files were meant to keep it busy long enough to be interrupted");
        cts.Cancel();

        var act = () => copy;
        await act.Should().ThrowAsync<OperationCanceledException>();

        EntriesUnder(dst).Should().BeEquivalentTo(before,
            "a cancelled copy is a failed copy: the destination goes back to what it was");
        (await File.ReadAllTextAsync(Path.Combine(dst, "stale.txt"))).Should().Be("stale");
        AsidesOf(dst).Should().BeEmpty();
        Directory.EnumerateFiles(src).Count().Should().Be(Count, "the source is untouched");
    }

    /// <summary>R4-1: the aside is bookkeeping, not a result — a success must not leave one behind.</summary>
    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task A_successful_replace_leaves_no_aside_behind(string verb)
    {
        var src = Dir($"ok-src-{verb}");
        File_(Path.Combine($"ok-src-{verb}", "new.txt"), "new");
        var dst = Dir($"ok-dst-{verb}");
        File_(Path.Combine($"ok-dst-{verb}", "stale.txt"), "stale");
        var svc = Svc();

        Func<Task> act = () => verb == "copy"
            ? svc.CopyAsync(src, dst, overwrite: true)
            : svc.MoveAsync(src, dst, overwrite: true);
        await act.Should().NotThrowAsync();

        EntriesUnder(dst).Should().BeEquivalentTo(new[] { "new.txt" }, "the destination was replaced");
        AsidesOf(dst).Should().BeEmpty("the aside is removed once the operation succeeded");
    }

    // ---- R4-2: canonical paths for the containment check --------------------------------------

    /// <summary>
    /// R4-2: <c>\\?\C:\x</c> and <c>C:\x</c> are the same directory, and
    /// <c>Path.GetFullPath</c> leaves the prefix alone — so a containment check that compares the
    /// strings sees two unrelated paths. With <c>overwrite:true</c> that means clearing the
    /// destination deletes the source.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_the_extended_length_form_of_the_same_path(bool overwrite)
    {
        var dir = Dir($"ext-same-{overwrite}");
        File_(Path.Combine($"ext-same-{overwrite}", "keep.txt"), "keep");

        var act = () => Svc().CopyAsync(dir, @"\\?\" + dir, overwrite);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                @"\\?\C:\x IS C:\x, whatever a string comparison says")).Which.Message;
        message.Should().ContainEquivalentOf("same path");
        message.Should().NotContain("overwrite:true",
            "the refusal holds whatever overwrite says, so offering it as the remedy would be false");
        (await File.ReadAllTextAsync(Path.Combine(dir, "keep.txt"))).Should().Be("keep",
            "clearing the destination here would delete the source");
    }

    /// <summary>
    /// R4-2, the other direction: an extended-length destination that CONTAINS the source. Today
    /// the containment check does not see it, so <c>overwrite:true</c> deletes the parent - and
    /// the source with it - before the copy starts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_an_extended_length_destination_that_contains_the_source(bool overwrite)
    {
        var outer = Dir($"ext-outer-{overwrite}");
        var inner = Dir(Path.Combine($"ext-outer-{overwrite}", "inner"));
        File_(Path.Combine($"ext-outer-{overwrite}", "inner", "keep.txt"), "keep");

        var act = () => Svc().CopyAsync(inner, @"\\?\" + outer, overwrite);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain(inner, "the refusal names the source it would have deleted");
        Directory.Exists(inner).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(inner, "keep.txt"))).Should().Be("keep");
    }

    /// <summary>
    /// R4-2: an extended-length destination INSIDE the source. Same runaway the plain form is
    /// already refused for (the copy keeps finding the directory it just created), reached through
    /// a prefix the string comparison does not recognise.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_an_extended_length_destination_inside_the_source(bool overwrite)
    {
        var src = Dir($"ext-runaway-{overwrite}");
        File_(Path.Combine($"ext-runaway-{overwrite}", "one.txt"), "one");
        var dst = Path.Combine(src, SubtreeName);

        var act = () => Svc().CopyAsync(src, @"\\?\" + dst, overwrite);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a copy into the source's own subtree can never terminate")).Which.Message
            .Should().Contain(src);
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "one.txt" });
    }

    /// <summary>
    /// R4-2: the same containment, laundered through a junction. <c>link\sub</c> where
    /// <c>link -> src</c> IS <c>src\sub</c>; only resolving the reparse points before comparing
    /// catches it.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_destination_inside_the_source_through_a_junction()
    {
        var src = Dir("junc-src");
        File_(Path.Combine("junc-src", "one.txt"), "one");
        var link = Junction("junc-link", src);
        var dst = Path.Combine(link, SubtreeName);

        var act = () => Svc().CopyAsync(src, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "the junction makes the destination a subtree of the source under another name"))
            .Which.Message.Should().Contain(src);
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "one.txt" }, "a refusal creates nothing");
    }

    // ---- R4-3: roots ---------------------------------------------------------------------------

    /// <summary>R4-3: a volume root is not a thing to copy or move; it is refused before anything else.</summary>
    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task Copy_and_move_refuse_a_volume_root_as_the_source(string verb)
    {
        var target = Dir("root-src-target");
        File_(Path.Combine("root-src-target", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;   // no free drive letter: a root cannot be produced on this box
        var dst = Path.Combine(_tmp, $"root-src-landing-{verb}");
        var svc = Svc();

        Func<Task> act = () => verb == "copy"
            ? svc.CopyAsync(root, dst, overwrite: false)
            : svc.MoveAsync(root, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a whole volume is never what the caller meant to copy")).Which.Message
            .Should().Contain(root.TrimEnd('\\'), "the refusal names the root it is talking about");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue();
    }

    /// <summary>
    /// R4-3: a root as the DESTINATION without overwrite. It already fails today - but with
    /// "pass overwrite:true to replace it", which is advice that would clear the volume. The
    /// refusal has to be the root rule, not the existing-destination rule.
    /// </summary>
    [Fact]
    public async Task Copy_refuses_a_volume_root_as_the_destination_without_offering_overwrite()
    {
        var target = Dir("root-dst-target");
        File_(Path.Combine("root-dst-target", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;
        var src = Dir("root-dst-src");
        File_(Path.Combine("root-dst-src", "new.txt"), "new");

        var act = () => Svc().CopyAsync(src, root, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().NotContain("overwrite:true",
                "telling the caller to pass overwrite:true here is telling them to erase a volume");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue();
    }

    /// <summary>R4-3: with overwrite, the clear-first step would empty the volume. Refused first.</summary>
    [Fact]
    public async Task Copy_refuses_a_volume_root_as_the_destination_with_overwrite()
    {
        var target = Dir("root-ow-target");
        File_(Path.Combine("root-ow-target", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;
        var src = Dir("root-ow-src");
        File_(Path.Combine("root-ow-src", "new.txt"), "new");

        var act = () => Svc().CopyAsync(src, root, overwrite: true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain(root.TrimEnd('\\'));
        File.Exists(Path.Combine(target, "canary.txt")).Should()
            .BeTrue("nothing under the root is deleted - that is the whole point of the rule");
    }

    /// <summary>R4-3: and a root is not a delete target either, with or without recursive.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_refuses_a_volume_root(bool recursive)
    {
        var target = Dir($"root-del-target-{recursive}");
        File_(Path.Combine($"root-del-target-{recursive}", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;

        var act = () => Svc().DeleteAsync(root, recursive);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "'delete C:\\' is never a request to honour")).Which.Message;
        message.Should().Contain(root.TrimEnd('\\'));
        message.Should().NotContain("recursive:true",
            "recursive:true is not the missing ingredient - a root is refused whatever it says");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue();
    }

    // ---- R4-4: junctions and symlinks ----------------------------------------------------------

    /// <summary>
    /// R4-4: a directory reparse point inside the source is neither followed nor recreated. Today
    /// the copy walks through it, so a junction to C:\ under the source copies the machine, and a
    /// self-referencing junction recurses until the path limit stops it.
    /// </summary>
    [Fact]
    public async Task CopyAsync_does_not_descend_into_a_junction_and_does_not_recreate_it()
    {
        var outside = Dir("junc-outside");
        File_(Path.Combine("junc-outside", "outside.txt"), "outside");
        var src = Dir("junc-tree");
        File_(Path.Combine("junc-tree", "top.txt"), "top");
        File_(Path.Combine("junc-tree", "real", "deep.txt"), "deep");
        Junction(Path.Combine("junc-tree", "to-outside"), outside);
        Junction(Path.Combine("junc-tree", SelfLinkName), src);   // self-referencing
        var dst = Path.Combine(_tmp, "junc-copy");

        await Svc().CopyAsync(src, dst, overwrite: false);

        (await File.ReadAllTextAsync(Path.Combine(dst, "top.txt"))).Should().Be("top");
        (await File.ReadAllTextAsync(Path.Combine(dst, "real", "deep.txt"))).Should().Be("deep",
            "the rest of the tree is copied as usual");
        EntriesUnder(dst).Should().BeEquivalentTo(new[] { "top.txt", "real", Path.Combine("real", "deep.txt") },
            "a link is not copied as a directory and is not recreated as a link either");
        (await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt"))).Should().Be("outside",
            "the junction's target is outside the copy and is not touched");
    }

    /// <summary>
    /// R4-4: deleting a junction removes the link. Today the emptiness check enumerates THROUGH
    /// it, so a link to a non-empty directory is refused for want of recursive:true - and passing
    /// recursive:true is how a caller who wanted to unlink deletes someone else's data.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_of_a_junction_removes_the_link_and_leaves_the_target()
    {
        var target = Dir("del-junc-target");
        File_(Path.Combine("del-junc-target", "inside.txt"), "inside");
        var link = Junction("del-junc-link", target);

        var act = () => Svc().DeleteAsync(link, recursive: false);

        await act.Should().NotThrowAsync("removing a link is not a recursive delete");
        Directory.Exists(link).Should().BeFalse("the link is gone");
        (await File.ReadAllTextAsync(Path.Combine(target, "inside.txt"))).Should()
            .Be("inside", "the target the link pointed at is not the caller's delete target");
    }

    // ---- R4-5: the listing is bounded and cancellable ------------------------------------------

    [Fact]
    public async Task ListAsync_stops_at_max_entries_and_says_it_truncated()
    {
        var root = Dir("cap");
        for (var i = 0; i < 5; i++) File_(Path.Combine("cap", $"f{i}.txt"), "x");

        var listing = await Svc().ListAsync(root, null, recursive: false, includeHidden: false, maxEntries: 3);

        listing.Entries.Should().HaveCount(3, "the walk stops at the cap instead of building a 42 MB answer");
        listing.Truncated.Should().BeTrue("the caller has to be able to tell a capped listing from a complete one");
        listing.MaxEntries.Should().Be(3, "the cap that applied is echoed back");
    }

    [Fact]
    public async Task ListAsync_under_the_cap_is_not_truncated()
    {
        var root = Dir("uncapped");
        for (var i = 0; i < 3; i++) File_(Path.Combine("uncapped", $"f{i}.txt"), "x");

        var listing = await Svc().ListAsync(root, null, recursive: false, includeHidden: false, maxEntries: 10);

        listing.Entries.Should().HaveCount(3);
        listing.Truncated.Should().BeFalse();
        listing.MaxEntries.Should().Be(10);
    }

    /// <summary>
    /// R4-5: the token is observed per entry. C:\Windows recursive is the listing that motivated
    /// the cap (160 000 entries, 42 MB) and it is read-only here: a walk that ignores the token
    /// runs it to the end, which is exactly the failure this pins.
    /// </summary>
    [Fact]
    public async Task ListAsync_cancelled_mid_walk_stops_walking()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => Svc().ListAsync(windows, null, recursive: true, includeHidden: true,
            maxEntries: 1_000_000, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a recursive listing of a big tree has to be abandonable, not only refusable up front");
    }

    // ---- R4-6: no orphaned temp file -----------------------------------------------------------

    /// <summary>
    /// R4-6: the write path renames a temp file into place. A read-only target makes that rename
    /// throw UnauthorizedAccessException, which the retry loop does not catch - so the temp file
    /// is left beside the target, and a caller who retries collects one per attempt.
    /// </summary>
    [Fact]
    public async Task WriteTextAsync_leaves_no_temp_file_when_the_rename_is_denied()
    {
        var dir = Dir("ro");
        var target = File_(Path.Combine("ro", "target.txt"), "original");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        _readOnly.Add(target);

        var act = () => Svc().WriteTextAsync(target, "replacement", "utf-8", append: false, createParents: true);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        TempFilesIn(dir).Should().BeEmpty("the temp file is cleaned up before the failure propagates");
        File.SetAttributes(target, FileAttributes.Normal);
        (await File.ReadAllTextAsync(target)).Should().Be("original", "a failed write changes nothing");
    }

    /// <summary>
    /// R4-6, the same leak through the other kind of target: a rename onto a path that is a
    /// DIRECTORY. Same UnauthorizedAccessException, same orphan - which is why the cleanup belongs
    /// on the failure path rather than on a list of known errors.
    /// <para>
    /// R4-6's third case, "a cancel during the retry delay", has no test here: the delay only runs
    /// when <c>File.Move</c> throws IOException, and on this runtime every way to fail the rename -
    /// target open with FileShare.None/Read/ReadWrite/Delete, target read-only, target a directory -
    /// raises UnauthorizedAccessException instead (probed), while a file write does not observe
    /// cancellation once it has started. The branch is unreachable from a test; the implementation
    /// should still clean up there, and these two cases pin the rule it follows.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WriteTextAsync_leaves_no_temp_file_when_the_target_is_a_directory()
    {
        var dir = Dir("dir-target");
        var target = Path.Combine(dir, "in-the-way");
        Directory.CreateDirectory(target);

        var act = () => Svc().WriteTextAsync(target, "replacement", "utf-8", append: false, createParents: true);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        TempFilesIn(dir).Should().BeEmpty("the temp file is cleaned up before the failure propagates");
    }

    /// <summary>
    /// R4-6: zip deletes the destination archive before it starts writing, so a zip that fails -
    /// here because the source directory is not there - costs the caller the archive they already
    /// had. Writing beside it and moving it into place keeps the old one until the new one exists.
    /// </summary>
    [Fact]
    public async Task ZipAsync_that_fails_leaves_the_existing_archive_in_place()
    {
        var dir = Dir("zip");
        var archive = File_(Path.Combine("zip", "backup.zip"), "the archive that already exists");
        var missing = Path.Combine(_tmp, "no-such-directory");

        var act = () => Svc().ZipAsync(missing, archive);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        File.Exists(archive).Should().BeTrue("a failed zip must not take the previous archive with it");
        (await File.ReadAllTextAsync(archive)).Should().Be("the archive that already exists");
        TempFilesIn(dir).Should().BeEmpty("and it leaves no half-written temp archive behind either");
    }

    /// <summary>
    /// R4-2, the case the containment check gives up on rather than guesses at: two junctions
    /// pointing at each other. <c>Directory.Exists</c> says the link is a directory and its
    /// attributes say reparse point, so canonicalisation walks into it - and
    /// <c>ResolveLinkTarget(returnFinalTarget: true)</c> answers "the name of the file cannot be
    /// resolved by the system". Swallowing that would compare the unresolved path and let a
    /// containment violation through; the call is refused instead, at either end.
    /// </summary>
    [Theory]
    [InlineData("src")]
    [InlineData("dst")]
    public async Task Copy_refuses_a_path_that_goes_through_a_junction_cycle(string end)
    {
        // a -> b and b -> a: neither exists as a real directory, and each resolves only through
        // the other. mklink is happy to create a junction whose target is not there yet.
        var a = Junction($"cycle-a-{end}", Path.Combine(_tmp, $"cycle-b-{end}"));
        Junction($"cycle-b-{end}", a);
        var through = Path.Combine(a, "x");
        var real = Dir($"cycle-real-{end}");
        File_(Path.Combine($"cycle-real-{end}", "keep.txt"), "keep");
        var landing = Path.Combine(_tmp, $"cycle-landing-{end}");
        var svc = Svc();

        Func<Task> act = end == "src"
            ? () => svc.CopyAsync(through, landing, overwrite: false)
            : () => svc.CopyAsync(real, through, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a link the system cannot resolve cannot be compared either, and a copy that "
                + "cannot be checked must not run")).Which.Message;
        message.Should().Contain("cannot be resolved", "the refusal says why, not just that");
        message.Should().Contain(through, "and names the path it could not follow");
        Directory.Exists(landing).Should().BeFalse("a refusal creates nothing");
        EntriesUnder(real).Should().BeEquivalentTo(new[] { "keep.txt" });
    }

    /// <summary>
    /// R4-1 with a FILE at each end - the aside path the directory tests never reach. The source
    /// is held open so <c>File.Copy</c> cannot read it; the destination file has already been
    /// moved aside by then, so without the restore the caller loses a file they still had a
    /// moment ago and gets no copy either.
    /// </summary>
    [Fact]
    public async Task CopyAsync_of_a_locked_source_file_puts_the_destination_file_back()
    {
        var src = File_("locked-file-src.txt", "replacement");
        var dst = File_("locked-file-dst.txt", "original");

        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => Svc().CopyAsync(src, dst, overwrite: true);

            await act.Should().ThrowAsync<IOException>("File.Copy cannot read a source held with FileShare.None");
        }

        File.Exists(dst).Should().BeTrue("the destination file is put back, not left as a .replaced. sibling");
        (await File.ReadAllTextAsync(dst)).Should().Be("original", "and it is the file that was there, unchanged");
        AsidesOf(dst).Should().BeEmpty();
        (await File.ReadAllTextAsync(src)).Should().Be("replacement", "a failed copy leaves the source alone");
    }

    /// <summary>
    /// R4-6's other half: the temp-then-move rewrite has to still PRODUCE the archive. Nothing
    /// else in the suite zips successfully, so a rewrite that left the archive at
    /// <c>&lt;dst&gt;.tmp.&lt;guid&gt;</c> - or wrote an empty one - would ship green.
    /// </summary>
    [Fact]
    public async Task ZipAsync_writes_the_archive_and_replaces_the_previous_one()
    {
        var source = Dir("zip-ok-src");
        File_(Path.Combine("zip-ok-src", "a.txt"), "alpha");
        File_(Path.Combine("zip-ok-src", "sub", "b.txt"), "beta");
        var archive = File_(Path.Combine("zip-ok-out", "backup.zip"), "the archive that already exists");
        var outDir = Path.GetDirectoryName(archive)!;

        await Svc().ZipAsync(source, archive);

        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(new[] { "a.txt", "sub/b.txt" },
                "the whole tree goes in, and the archive that replaced the old one is a real one");
            using var reader = new StreamReader(zip.GetEntry("a.txt")!.Open());
            (await reader.ReadToEndAsync()).Should().Be("alpha");
        }
        TempFilesIn(outDir).Should().BeEmpty("the temp archive is moved into place, not left beside it");
        Directory.EnumerateFiles(outDir).Should().ContainSingle().Which.Should().Be(archive);
    }

    /// <summary>
    /// R4-5 at the bottom of the range. <c>maxEntries: 0</c> is not "no limit" and it is not an
    /// empty listing that claims <c>Truncated</c> either - the walk cannot express it, so it is
    /// refused naming the parameter. (The tool clamps to 1-100000 before it ever gets here; this
    /// is the service's own answer to a caller that does not.)
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListAsync_refuses_a_cap_below_one(int maxEntries)
    {
        var root = Dir("cap-guard");
        File_(Path.Combine("cap-guard", "one.txt"), "x");

        var act = () => Svc().ListAsync(root, null, recursive: false, includeHidden: false, maxEntries: maxEntries);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>(
            "an empty listing flagged Truncated would read as 'the directory is full of files you cannot see'"))
            .Which;
        thrown.Message.Should().Contain("max_entries", "the refusal names the parameter as the CALLER spells it");
        thrown.ParamName.Should().Be("maxEntries");
    }

    // ==== round 4b: the review of round 4 =========================================================

    // ---- R4b-1: Commit never triggers Restore ---------------------------------------------------

    /// <summary>
    /// R4b-1 + R4b-3: the destination tree being replaced holds a read-only file. Removing the
    /// aside is the LAST step, after the move has already succeeded — so when
    /// <c>Directory.Delete(aside, recursive)</c> trips over the read-only file, today's catch runs
    /// Restore, which deletes the tree that was just moved into place and puts the old one back.
    /// The caller loses the source. The read-only attribute is not a second gate: the caller
    /// confirmed the replacement.
    /// </summary>
    [Fact]
    public async Task MoveAsync_replaces_a_destination_tree_that_holds_a_read_only_file()
    {
        var src = Dir("ro-tree-src");
        File_(Path.Combine("ro-tree-src", "new.txt"), "new");
        var dst = Dir("ro-tree-dst");
        ReadOnlyFile(Path.Combine("ro-tree-dst", "stale.txt"), "stale");

        try
        {
            var act = () => Svc().MoveAsync(src, dst, overwrite: true);

            await act.Should().NotThrowAsync(
                "the caller confirmed the replacement; a read-only file in the tree being replaced is not a second gate");
            EntriesUnder(dst).Should().BeEquivalentTo(new[] { "new.txt" }, "the moved tree is what is at the destination");
            (await File.ReadAllTextAsync(Path.Combine(dst, "new.txt"))).Should().Be("new");
            Directory.Exists(src).Should().BeFalse("a move leaves nothing at the source");
            AsidesOf(dst).Should().BeEmpty("the aside is removed once the move succeeded");
        }
        finally { ReleaseReadOnly(dst, "stale.txt"); }
    }

    /// <summary>
    /// R4b-1 + R4b-3, the same failure through the other common obstacle: a junction inside the
    /// tree being replaced. <c>Directory.Delete(aside, recursive: true)</c> refuses it, Restore
    /// deletes the moved tree, and the source is gone for good — while the junction's target, which
    /// nobody asked to touch, is what the whole thing tripped over.
    /// </summary>
    [Fact]
    public async Task MoveAsync_replaces_a_destination_tree_that_holds_a_junction()
    {
        var outside = Dir("junc-dst-outside");
        File_(Path.Combine("junc-dst-outside", "outside.txt"), "outside");
        var src = Dir("junc-dst-src");
        File_(Path.Combine("junc-dst-src", "new.txt"), "new");
        var dst = Dir("junc-dst");
        Junction(Path.Combine("junc-dst", "to-outside"), outside);

        var act = () => Svc().MoveAsync(src, dst, overwrite: true);

        await act.Should().NotThrowAsync(
            "removing the aside is a tree removal like any other: a junction inside it is a link, unlinked, not walked");
        EntriesUnder(dst).Should().BeEquivalentTo(new[] { "new.txt" }, "the moved tree is what is at the destination");
        Directory.Exists(src).Should().BeFalse("a move leaves nothing at the source");
        AsidesOf(dst).Should().BeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt"))).Should().Be("outside",
            "the junction inside the replaced tree pointed at someone else's data");
    }

    /// <summary>
    /// R4b-1 with FILES at both ends: <c>File.Delete</c> refuses a read-only file exactly as the
    /// tree remover does, so the same Commit-into-Restore path costs the caller the file they had
    /// just moved into place.
    /// </summary>
    [Fact]
    public async Task MoveAsync_replaces_a_read_only_destination_file()
    {
        var src = File_("ro-file-src.txt", "replacement");
        var dst = ReadOnlyFile("ro-file-dst.txt", "original");

        try
        {
            var act = () => Svc().MoveAsync(src, dst, overwrite: true);

            await act.Should().NotThrowAsync("replacing a read-only file is what overwrite:true asked for");
            (await File.ReadAllTextAsync(dst)).Should().Be("replacement");
            File.Exists(src).Should().BeFalse("a move leaves nothing at the source");
            AsidesOf(dst).Should().BeEmpty();
        }
        finally { ReleaseReadOnly(dst); }
    }

    /// <summary>
    /// R4b-1's own rule, once R4b-3 has taken the ordinary reasons away: a Commit that genuinely
    /// cannot remove the aside. A handle is opened on a file INSIDE the aside while the copy is
    /// still walking (the aside appears the moment the destination is renamed, long before 3 000
    /// files have been copied), so the removal fails with the file in use.
    /// <para>
    /// Today that is caught by the same <c>catch</c> as a failed copy: Restore deletes the copy
    /// that DID land and tries to move the aside back over it. The copy succeeded; only the
    /// housekeeping failed, so the result stays and the caller is told where the old destination
    /// is — an <see cref="InvalidOperationException"/> naming the aside.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_commit_that_cannot_remove_the_aside_keeps_the_result_and_says_where_it_is()
    {
        const int Count = 3000;
        var src = Dir("commit-src");
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(src, $"f{i:D4}.txt"), "x");
        var dst = Dir("commit-dst");
        File_(Path.Combine("commit-dst", "stale.txt"), "stale");

        var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: true));
        FileStream? held = null;
        try
        {
            var spin = Stopwatch.StartNew();
            while (held is null && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            {
                var aside = AsidesOf(dst).FirstOrDefault();
                if (aside is null) { Thread.SpinWait(20); continue; }
                held = new FileStream(Path.Combine(aside, "stale.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            // Round 4d: the ceiling is a way OUT of the wait, not an outcome — the two ways of
            // reaching it are told apart so a run that tested nothing says so.
            (held is not null || copy.IsCompleted).Should().BeTrue(
                $"no '.replaced.' aside appeared beside '{dst}' within {HandshakeCeiling.TotalSeconds:0} s, so this "
                + "run tested nothing: the handshake has to happen for the rest of the test to mean anything");
            held.Should().NotBeNull(
                "the copy finished before the aside could be held open, so nothing blocked the commit - "
                + "the test has to hold the aside open while the copy is still running, or it proves nothing");

            var act = () => copy;
            var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                    "the copy landed; what failed is the removal of the aside, which is a different sentence"))
                .Which.Message;
            message.Should().Contain(".replaced.",
                "the caller is told where the previous destination still is, or it is just litter they cannot find");

            Directory.EnumerateFiles(dst).Should().HaveCount(Count,
                "the copy succeeded, so it stays: a failed cleanup is not a reason to undo the work");
            AsidesOf(dst).Should().ContainSingle("the aside could not be removed, so it is still there");
        }
        finally
        {
            held?.Dispose();
            foreach (var aside in AsidesOf(dst)) ForceDelete(aside);
        }
    }

    // ---- R4b-2: a cross-volume move never leaves a file in zero places --------------------------

    /// <summary>
    /// R4b-2, the data loss: across volumes the move is a copy then a delete of the source. When
    /// the delete fails part-way (one file is held open, the rest are already gone), today's catch
    /// runs Restore — which deletes the complete copy at the destination. The files the source
    /// delete DID remove then exist nowhere at all. The copy has landed: it stays, and the caller
    /// is told what is still at the source.
    /// </summary>
    [Fact]
    public async Task MoveAsync_across_volumes_that_cannot_remove_the_source_keeps_the_destination()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;   // one volume on this box: nothing to move across

        try
        {
            var src = Dir("xvol-keep-src");
            var names = new[] { "a.txt", "b.txt", "held.txt", "z.txt" };
            foreach (var name in names) File_(Path.Combine("xvol-keep-src", name), name);
            var dst = Path.Combine(landing, "dst");
            Directory.CreateDirectory(dst);
            await File.WriteAllTextAsync(Path.Combine(dst, "stale.txt"), "stale");

            using (new FileStream(Path.Combine(src, "held.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var act = () => Svc().MoveAsync(src, dst, overwrite: true);

                (await act.Should().ThrowAsync<InvalidOperationException>(
                        "the copy landed and the old destination is gone; what failed is the removal of the source"))
                    .Which.Message.Should().Contain(src, "the caller is told what is still at the source");
            }

            EntriesUnder(dst).Should().BeEquivalentTo(names,
                "the destination holds the whole source tree - it is the only complete copy left");
            foreach (var name in names)
                (await File.ReadAllTextAsync(Path.Combine(dst, name))).Should().Be(name);
            File.Exists(Path.Combine(src, "held.txt")).Should().BeTrue(
                "a file in two places is survivable; a file in none is not");
            AsidesOf(dst).Should().BeEmpty("the aside is committed before the source is touched");
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// R4b-2 + R4b-3 across volumes: the copy does not carry links over (R4-4), so the source
    /// delete meets a junction the copy skipped. One tree remover unlinks it; today's
    /// <c>Directory.Delete(src, recursive: true)</c> refuses, and Restore then removes the copy —
    /// leaving the plain files of the tree nowhere, because the source delete had already reached
    /// them.
    /// </summary>
    [Fact]
    public async Task MoveAsync_across_volumes_drops_a_link_and_leaves_its_target()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;   // one volume on this box: nothing to move across

        try
        {
            var outside = Dir("xvol-junc-outside");
            File_(Path.Combine("xvol-junc-outside", "outside.txt"), "outside");
            var src = Dir("xvol-junc-src");
            File_(Path.Combine("xvol-junc-src", "top.txt"), "top");
            Junction(Path.Combine("xvol-junc-src", "to-outside"), outside);
            var dst = Path.Combine(landing, "dst");

            var act = () => Svc().MoveAsync(src, dst, overwrite: false);

            await act.Should().NotThrowAsync("a link in the tree is not a reason for the move to fail");
            EntriesUnder(dst).Should().BeEquivalentTo(new[] { "top.txt" },
                "links are not carried across volumes - the description says so");
            Directory.Exists(src).Should().BeFalse("the source goes once the copy has landed");
            (await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt"))).Should().Be("outside",
                "the junction's target was never part of what was moved");
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    // ---- R4b-3: one tree remover ----------------------------------------------------------------

    /// <summary>
    /// R4b-3: <c>delete recursive:true</c> of a tree holding both obstacles at once. The caller
    /// confirmed the delete and named the tree; a read-only file inside is an attribute to clear,
    /// and a junction inside is a link to remove — never a door into someone else's data.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_recursive_removes_a_tree_holding_a_junction_and_a_read_only_file()
    {
        var outside = Dir("del-tree-outside");
        File_(Path.Combine("del-tree-outside", "outside.txt"), "outside");
        var tree = Dir("del-tree");
        File_(Path.Combine("del-tree", "plain.txt"), "plain");
        ReadOnlyFile(Path.Combine("del-tree", "sub", "ro.txt"), "ro");
        Junction(Path.Combine("del-tree", "to-outside"), outside);

        try
        {
            var act = () => Svc().DeleteAsync(tree, recursive: true);

            await act.Should().NotThrowAsync(
                "one tree remover: the read-only attribute is cleared and the junction is unlinked");
            Directory.Exists(tree).Should().BeFalse("the whole tree goes");
            (await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt"))).Should().Be("outside",
                "the junction's target is not part of the tree the caller asked to delete");
        }
        finally { ReleaseReadOnly(tree, Path.Combine("sub", "ro.txt")); }
    }

    /// <summary>
    /// R4b-3 (GREEN): the obstacle none of the file cases reach — a read-only DIRECTORY inside the
    /// tree. <c>RemoveDirectory</c> answers "access is denied" for one exactly as
    /// <c>DeleteFile</c> does for a read-only file (measured: <c>rmdir</c> on a read-only directory
    /// exits 5), so the tree remover has to clear the attribute on directories as well, or a delete
    /// the caller confirmed stops half-way and leaves the tree partly gone.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_recursive_removes_a_tree_holding_a_read_only_directory()
    {
        var tree = Dir("del-ro-dir");
        var sub = Dir(Path.Combine("del-ro-dir", "sub"));
        File_(Path.Combine("del-ro-dir", "sub", "inside.txt"), "inside");
        File.SetAttributes(sub, File.GetAttributes(sub) | FileAttributes.ReadOnly);

        try
        {
            var act = () => Svc().DeleteAsync(tree, recursive: true);

            await act.Should().NotThrowAsync(
                "a read-only directory is an attribute to clear, not a second gate on a confirmed delete");
            Directory.Exists(tree).Should().BeFalse(
                "the whole tree goes - not everything except the read-only directory and its parents");
        }
        finally
        {
            try { if (Directory.Exists(sub)) File.SetAttributes(sub, File.GetAttributes(sub) & ~FileAttributes.ReadOnly); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// R4b-3's third caller is Restore. <c>File.Copy</c> carries the read-only attribute over, so a
    /// copy that fails after a read-only file has already landed leaves one inside the partial
    /// destination — and the restore's own tree removal trips over it. The caller then gets
    /// "cannot create a file when that file already exists" (the aside failing to move back) in
    /// place of the copy's real error, over a half-copied destination.
    /// </summary>
    [Fact]
    public async Task A_restore_over_a_partial_copy_holding_a_read_only_file_still_puts_the_destination_back()
    {
        var src = Dir("restore-ro-src");
        ReadOnlyFile(Path.Combine("restore-ro-src", "a-read-only.txt"), "carried");
        var locked = File_(Path.Combine("restore-ro-src", "b-locked.txt"), "locked");
        var dst = Dir("restore-ro-dst");
        File_(Path.Combine("restore-ro-dst", "stale.txt"), "stale");
        var before = EntriesUnder(dst);

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var act = () => Svc().CopyAsync(src, dst, overwrite: true);

                await act.Should().ThrowAsync<IOException>("File.Copy cannot read a source held with FileShare.None");
            }

            EntriesUnder(dst).Should().BeEquivalentTo(before,
                "the read-only file the copy had already laid down is part of the partial result to clear away, "
                + "not a second gate on clearing it");
            (await File.ReadAllTextAsync(Path.Combine(dst, "stale.txt"))).Should().Be("stale");
            AsidesOf(dst).Should().BeEmpty("the aside went back to being the destination");
        }
        finally { ReleaseReadOnly(dst, "a-read-only.txt"); }
    }

    /// <summary>
    /// R4b-3 does not widen the gate: a non-empty directory is still refused without
    /// <c>recursive</c>, junction inside or not.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_without_recursive_still_refuses_a_tree_that_holds_a_junction()
    {
        var outside = Dir("del-guard-outside");
        File_(Path.Combine("del-guard-outside", "outside.txt"), "outside");
        var tree = Dir("del-guard");
        Junction(Path.Combine("del-guard", "to-outside"), outside);

        var act = () => Svc().DeleteAsync(tree, recursive: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "one remover for every caller does not mean one gate fewer")).Which.Message
            .Should().Contain("recursive:true", "the refusal names the flag that would allow it");
        Directory.Exists(tree).Should().BeTrue("a refusal deletes nothing");
        (await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt"))).Should().Be("outside");
    }

    // ---- R4b-4: every spelling of the device prefix ---------------------------------------------

    /// <summary>
    /// R4b-4: Windows turns <c>/</c> into <c>\</c> before it looks at a path, so <c>//?/C:\x</c> IS
    /// <c>\\?\C:\x</c> — and the containment guard, which strips only the backslash spelling, sees
    /// two unrelated strings. With <c>overwrite:true</c> that is not a refusal that fails to fire:
    /// the destination is moved aside (taking the source with it, they are the same directory), an
    /// empty directory is "copied", and the aside — the caller's data — is then removed as a
    /// successful replace.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_the_slash_spelling_of_the_same_path(bool overwrite)
    {
        var dir = Dir($"slash-same-{overwrite}");
        File_(Path.Combine($"slash-same-{overwrite}", "keep.txt"), "keep");

        var act = () => Svc().CopyAsync(dir, "//?/" + dir, overwrite);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a path spelled with forward slashes is the same path")).Which.Message;
        message.Should().ContainEquivalentOf("same path");
        (await File.ReadAllTextAsync(Path.Combine(dir, "keep.txt"))).Should().Be("keep",
            "the source and the destination are one directory: replacing one deletes the other");
    }

    /// <summary>R4b-4: the runaway the plain form is refused for, spelled with forward slashes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_a_slash_spelled_destination_inside_the_source(bool overwrite)
    {
        var src = Dir($"slash-runaway-{overwrite}");
        File_(Path.Combine($"slash-runaway-{overwrite}", "one.txt"), "one");
        var dst = Path.Combine(src, SubtreeName);

        var act = () => Svc().CopyAsync(src, "//?/" + dst, overwrite);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a copy into the source's own subtree can never terminate, whatever the prefix is spelled like"))
            .Which.Message.Should().Contain(src);
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
    }

    /// <summary>R4b-4: and the destination that contains the source, spelled with forward slashes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_a_slash_spelled_destination_that_contains_the_source(bool overwrite)
    {
        var outer = Dir($"slash-outer-{overwrite}");
        var inner = Dir(Path.Combine($"slash-outer-{overwrite}", "inner"));
        File_(Path.Combine($"slash-outer-{overwrite}", "inner", "keep.txt"), "keep");

        var act = () => Svc().CopyAsync(inner, "//?/" + outer, overwrite);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain(inner, "the refusal names the source it would have deleted");
        Directory.Exists(inner).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(inner, "keep.txt"))).Should().Be("keep");
    }

    /// <summary>
    /// R4-3's other root and R4-2's other prefix (GREEN): a SHARE root. <c>\\server\share</c> is
    /// its own path root, so "a whole volume cannot be the source" covers it too - and
    /// <c>\\?\UNC\server\share</c> is the same share, which only the UNC arm of the prefix strip
    /// turns back into a comparable path. The refusal is a decision about strings, taken before the
    /// first file-system call, so the server name deliberately does not resolve: no test here
    /// needs - or touches - a network.
    /// </summary>
    [Theory]
    [InlineData(@"\\wmcp-no-such-server\share")]
    [InlineData(@"\\?\UNC\wmcp-no-such-server\share")]
    public async Task CopyAsync_refuses_a_share_root_as_the_source(string share)
    {
        var dst = Path.Combine(_tmp, "share-landing");

        var act = () => Svc().CopyAsync(share, dst, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a whole share is no more copyable than a whole volume")).Which.Message;
        message.Should().ContainEquivalentOf("root",
            "the refusal is the root rule, not 'the source does not exist' - which is what a caller "
            + "would get if the share were simply walked");
        message.Should().Contain(share, "the refusal names the path the caller sent, as they spelled it");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
    }

    // ---- R4b-5: roots are checked on the canonical path too --------------------------------------

    /// <summary>
    /// R4b-5: <c>Y:\</c> is refused as a source (R4-3), but a LINK to <c>Y:\</c> is the same volume
    /// under a name that is not a root. Only checking the resolved path as well catches it.
    /// <para>
    /// The link is a directory symlink because <c>mklink /J</c> will not point a junction at a
    /// subst drive ("local volumes are required"); creating a symlink needs Developer Mode or the
    /// privilege, so a box without either has nothing to test and the test returns. The destination
    /// exists on purpose: today's answer is the existing-destination refusal, so no copy runs
    /// either way and the difference between the two answers is exactly the message.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_link_to_a_volume_root_as_the_source()
    {
        var target = Dir("link-root-target");
        File_(Path.Combine("link-root-target", "canary.txt"), "canary");
        var substRoot = SubstRoot(target);
        if (substRoot is null) return;   // no free drive letter: a root cannot be produced on this box
        var link = Path.Combine(_tmp, "link-to-a-whole-volume");
        try { Directory.CreateSymbolicLink(link, substRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // no Developer Mode / no SeCreateSymbolicLink: the link cannot be made here
        }
        _junctions.Add(link);

        var landing = OtherVolumeLanding();
        var dst = Path.Combine(landing ?? _tmp, "landing-for-the-link");
        Directory.CreateDirectory(dst);
        try
        {
            var act = () => Svc().CopyAsync(link, dst, overwrite: false);

            (await act.Should().ThrowAsync<InvalidOperationException>(
                    "a link to a whole volume is a whole volume")).Which.Message
                .Should().ContainEquivalentOf("volume root",
                    "the refusal is the root rule, not 'the destination already exists' - the caller who "
                    + "reads the second one passes overwrite:true and asks to copy a volume over it");
            Directory.EnumerateFileSystemEntries(dst).Should().BeEmpty("a refusal copies nothing");
        }
        finally { if (landing is not null) { try { Directory.Delete(landing, true); } catch { /* best effort */ } } }
    }

    // ---- R4b-6: the listing does not follow links ------------------------------------------------

    /// <summary>
    /// R4b-6: <c>EnumerationOptions.RecurseSubdirectories</c> follows reparse points, so a
    /// self-referencing junction turns a listing into a walk that ends at the path limit — the
    /// caller gets an exception (whose message is a 32 KB path) instead of a listing. The link is
    /// an entry, flagged <c>IsLink</c>, and the walk does not go through it.
    /// </summary>
    [Fact]
    public async Task ListAsync_lists_a_link_without_walking_into_it()
    {
        var root = Dir("list-links");
        File_(Path.Combine("list-links", "plain.txt"), "plain");
        File_(Path.Combine("list-links", "real", "deep.txt"), "deep");
        var self = Junction(Path.Combine("list-links", SelfLinkName), root);

        FileListing listing = null!;
        var act = async () => { listing = await Svc().ListAsync(root, null, recursive: true, includeHidden: false, maxEntries: 100_000); };

        await act.Should().NotThrowAsync(
            "a walk that follows a self-referencing junction runs into the path limit instead of answering");
        listing.Entries.Should().Contain(e => e.Path.Equals(self, StringComparison.OrdinalIgnoreCase),
                "the link itself is an entry - the caller has to be able to see it is there")
            .Which.IsLink.Should().BeTrue("and to see that it is a link rather than a directory");
        listing.Entries.Should().NotContain(
            e => e.Path.StartsWith(self + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the link is listed, never descended into");
        listing.Entries.Should().Contain(e => e.Name == "deep.txt", "the rest of the tree is walked as usual")
            .Which.IsLink.Should().BeFalse("a plain file is not a link");
        listing.Truncated.Should().BeFalse("nothing was cut: the cap was 100 000 and the tree is tiny");
    }

    // ---- R4b-7: small honesties -------------------------------------------------------------------

    /// <summary>
    /// R4b-7: a directory copy creates its destination, parents and all. A file copy refusing one
    /// is the same call failing on a detail the caller cannot see coming.
    /// </summary>
    [Fact]
    public async Task CopyAsync_of_a_file_creates_the_missing_destination_parent()
    {
        var src = File_("parent-copy-src.txt", "content");
        var dst = Path.Combine(_tmp, "made", "by", "the", "copy.txt");

        var act = () => Svc().CopyAsync(src, dst, overwrite: false);

        await act.Should().NotThrowAsync("a directory copy creates the destination; a file copy has no reason not to");
        (await File.ReadAllTextAsync(dst)).Should().Be("content");
        File.Exists(src).Should().BeTrue("a copy leaves the source alone");
    }

    [Fact]
    public async Task MoveAsync_of_a_file_creates_the_missing_destination_parent()
    {
        var src = File_("parent-move-src.txt", "content");
        var dst = Path.Combine(_tmp, "made", "by", "the", "move.txt");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        await act.Should().NotThrowAsync();
        (await File.ReadAllTextAsync(dst)).Should().Be("content");
        File.Exists(src).Should().BeFalse("a move leaves nothing at the source");
    }

    // ==== round 4c: the second review =============================================================

    // ---- R4c-1: a root is a root with a trailing separator ---------------------------------------

    /// <summary>
    /// Every spelling of a root, paired with the fragment of it a refusal has to name. The share is
    /// deliberately on a host that does not resolve: the root rule is a decision about STRINGS,
    /// taken before the first file-system call, so no case here needs — or waits on — a network.
    /// <para>
    /// The trailing separator is what tab completion and every "directory path" convention produce,
    /// and it is exactly what today's check misses: <c>Path.GetPathRoot(@"\\host\share\")</c>
    /// answers <c>\\host\share</c> — without the separator — so comparing the two untrimmed forms
    /// says "not a root" and the delete goes ahead. Measured on this runtime:
    /// <c>C:\</c> and <c>\\host\share</c> compare equal; <c>\\host\share\</c>,
    /// <c>\\host\share\\</c> and <c>\\?\UNC\host\share\</c> do not.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> RootSpellings => new()
    {
        { @"\\wmcp-no-such-server\share",        @"wmcp-no-such-server\share" },
        { @"\\wmcp-no-such-server\share\",       @"wmcp-no-such-server\share" },
        { @"\\wmcp-no-such-server\share\\",      @"wmcp-no-such-server\share" },
        { @"\\?\UNC\wmcp-no-such-server\share\", @"wmcp-no-such-server\share" },
        { @"C:\",                                "C:" },
        { "C:/",                                 "C:" },
    };

    /// <summary>
    /// R4c-1: <c>delete</c> is the verb with no second line of defence — copy and move canonicalise
    /// and check the resolved path too, delete checks the given one and nothing else. So a share
    /// root with a trailing separator is walked and removed entry by entry, which is the one delete
    /// this service exists to refuse. The elapsed-time assertion pins the other half of the rule:
    /// the guard runs BEFORE any access, so an unreachable host is answered instantly instead of
    /// after an SMB timeout.
    /// </summary>
    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task DeleteAsync_refuses_a_root_however_it_is_spelled(string root, string named)
    {
        var clock = Stopwatch.StartNew();
        var act = () => Svc().DeleteAsync(root, recursive: true);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "'delete {0}' is never a request to honour, and a trailing separator does not make it one", root))
            .Which.Message;
        var elapsed = clock.Elapsed;

        message.Should().ContainEquivalentOf("volume root",
            "the refusal is the root rule - the same sentence C:\\ gets");
        message.Should().Contain(named, "the refusal names the root it is talking about");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the guard is a string comparison taken before the first file-system call: a host that does not "
            + "exist must be refused, not waited on");
    }

    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task CopyAsync_refuses_a_root_source_however_it_is_spelled(string root, string named)
    {
        var dst = Path.Combine(_tmp, "root-spelling-copy-landing");

        var act = () => Svc().CopyAsync(root, dst, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a whole volume or share is never what the caller meant to copy")).Which.Message;
        message.Should().ContainEquivalentOf("volume root");
        message.Should().Contain(named);
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
    }

    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task CopyAsync_refuses_a_root_destination_however_it_is_spelled(string root, string named)
    {
        var src = Dir("root-spelling-copy-src");
        File_(Path.Combine("root-spelling-copy-src", "one.txt"), "one");

        var act = () => Svc().CopyAsync(src, root, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("volume root");
        message.Should().Contain(named);
        message.Should().NotContainEquivalentOf("overwrite:true",
            "telling the caller to pass overwrite:true here is telling them to erase a volume");
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "one.txt" }, "a refusal touches nothing");
    }

    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task MoveAsync_refuses_a_root_source_however_it_is_spelled(string root, string named)
    {
        var dst = Path.Combine(_tmp, "root-spelling-move-landing");

        var act = () => Svc().MoveAsync(root, dst, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("volume root");
        message.Should().Contain(named);
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
    }

    [Theory]
    [MemberData(nameof(RootSpellings))]
    public async Task MoveAsync_refuses_a_root_destination_however_it_is_spelled(string root, string named)
    {
        var src = Dir("root-spelling-move-src");
        File_(Path.Combine("root-spelling-move-src", "one.txt"), "one");

        var act = () => Svc().MoveAsync(src, root, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("volume root");
        message.Should().Contain(named);
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "one.txt" },
            "a refusal leaves the source where it is - a move that took it would be unrecoverable");
    }

    // ---- R4c-2: a restore that could not put the destination back says so -------------------------

    /// <summary>
    /// R4c-2: the restore is best effort by design (it runs inside a <c>catch</c> and must not
    /// replace the original failure with one of its own) — but "best effort" and "silent" are
    /// different things. Here the partial destination cannot be removed (a directory the copy
    /// created is a live process's working directory), so the aside cannot move back either: the
    /// caller is handed the copy's IOException while their previous destination sits under a
    /// <c>.replaced.&lt;guid&gt;</c> name they were never told about, and the destination holds a
    /// half-copied tree. The failure they get has to say both.
    /// </summary>
    [Fact]
    public async Task A_restore_that_cannot_put_the_destination_back_says_where_the_previous_content_is()
    {
        const int Count = 3000;
        var src = Dir("restore-fail-src");
        var first = Dir(Path.Combine("restore-fail-src", "a"));
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(first, $"f{i:D4}.txt"), "x");
        // The second subdirectory is where the copy fails: 'a' is copied first (enumeration order),
        // so the partial destination exists - and is pinned below - before this one is reached.
        var locked = File_(Path.Combine("restore-fail-src", "b", "locked.txt"), "locked");
        var dst = Dir("restore-fail-dst");
        File_(Path.Combine("restore-fail-dst", "stale.txt"), "stale");

        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Process? pin = null;
        try
        {
            var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: true));
            var partial = Path.Combine(dst, "a");
            var spin = Stopwatch.StartNew();
            while (pin is null && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            {
                if (!Directory.Exists(partial)) { Thread.SpinWait(20); continue; }
                pin = PinDirectory(partial);
            }
            // Round 4d: the ceiling is a way OUT of the wait, not an outcome — falling through it
            // with no pin would run the assertions below against a copy that never started.
            (pin is not null || copy.IsCompleted).Should().BeTrue(
                $"the partial destination '{partial}' never appeared within {HandshakeCeiling.TotalSeconds:0} s, "
                + "so this run tested nothing: the handshake has to happen for the rest of the test to mean anything");
            pin.Should().NotBeNull(
                "the copy finished before the partial destination could be pinned, so nothing held it open - "
                + "the test has to pin it while the copy is still running, or it proves nothing");

            var thrown = await Record.ExceptionAsync(() => copy);

            // Asserted BEFORE the exception type: this is what the test is constructing, and a run
            // that lost the race (the copy failed before the pin was in place, so the restore
            // worked) has to say THAT rather than quietly become an assertion about the old
            // behaviour.
            var aside = AsidesOf(dst).Should().ContainSingle(
                "the put-back could not run, so the previous destination is still at the aside").Which;
            (await File.ReadAllTextAsync(Path.Combine(aside, "stale.txt"))).Should().Be("stale",
                "and it is intact: what could not be restored must at least still exist");

            var failure = thrown.Should().BeOfType<InvalidOperationException>(
                    "the copy failed AND the put-back failed: 'the file is in use' alone leaves the caller "
                    + "looking at a half-copied destination with no idea where their data went")
                .Which;
            failure.Message.Should().Contain(dst, "the destination is the path the caller has to look at");
            failure.Message.Should().Contain(".replaced.",
                "the previous content is under the aside's name; unnamed, it is litter the caller cannot find");
            failure.InnerException.Should().BeAssignableTo<IOException>(
                "the copy's own failure is why any of this happened - it is kept, not replaced");
        }
        finally
        {
            hold.Dispose();
            ReleasePin(pin);
        }
    }

    /// <summary>
    /// R4c-2 with no aside to name: the destination did not exist, so nothing was moved aside — but
    /// the copy still built part of it before failing, and that partial tree cannot be removed
    /// either (a live process's working directory). The caller is left with half a tree at a path
    /// that was empty a moment ago, so the failure has to say the destination is not as it was —
    /// and must NOT point at a <c>.replaced.</c> sibling, because there is none to look in.
    /// </summary>
    [Fact]
    public async Task A_failed_copy_that_cannot_remove_its_own_partial_destination_says_so()
    {
        const int Count = 3000;
        var src = Dir("partial-only-src");
        var first = Dir(Path.Combine("partial-only-src", "a"));
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(first, $"f{i:D4}.txt"), "x");
        // 'a' is copied first (enumeration order), so the partial destination exists - and is
        // pinned below - before the copy reaches the file it cannot read.
        var locked = File_(Path.Combine("partial-only-src", "b", "locked.txt"), "locked");
        var dst = Path.Combine(_tmp, "partial-only-dst");   // deliberately NOT created

        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Process? pin = null;
        try
        {
            var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: false));
            var partial = Path.Combine(dst, "a");
            var spin = Stopwatch.StartNew();
            while (pin is null && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            {
                if (!Directory.Exists(partial)) { Thread.SpinWait(20); continue; }
                pin = PinDirectory(partial);
            }
            // Round 4d: the ceiling is a way OUT of the wait, not an outcome. Falling through it
            // with no pin would run the assertions below against a copy that never started, so the
            // three possible exits are told apart here rather than blamed on the service.
            (pin is not null || copy.IsCompleted).Should().BeTrue(
                $"the partial destination '{partial}' never appeared within {HandshakeCeiling.TotalSeconds:0} s, "
                + "so this run tested nothing: the handshake has to happen for the rest of the test to mean anything");
            pin.Should().NotBeNull(
                "the copy finished before the partial destination could be pinned, so nothing held it open - "
                + "the test has to pin it while the copy is still running, or it proves nothing");

            var thrown = await Record.ExceptionAsync(() => copy);

            var failure = thrown.Should().BeOfType<InvalidOperationException>(
                    "'the file is in use' alone reads as 'nothing happened', and something did: there is now a "
                    + "half-copied tree at a path the caller believed to be free")
                .Which;
            failure.Message.Should().Contain(dst, "the destination is the path the caller has to look at");
            failure.Message.Should().NotContain(".replaced.",
                "nothing was moved aside - naming an aside that does not exist sends the caller looking for it");
            failure.InnerException.Should().BeAssignableTo<IOException>(
                "the copy's own failure is why any of this happened - it is kept, not replaced");
            AsidesOf(dst).Should().BeEmpty("a destination that did not exist has nothing to put aside");
        }
        finally
        {
            hold.Dispose();
            ReleasePin(pin);
        }
    }

    // ---- R4c-3: a cross-volume move that did not complete says so ---------------------------------

    /// <summary>
    /// R4c-3: across volumes the move is aside → copy → commit → remove the source. A commit that
    /// cannot remove the aside (a handle is held inside it while the copy runs) stops the sequence
    /// before the source is touched — so the move did NOT happen: the source is still whole, the
    /// destination holds a copy of it, and the previous destination is at the aside. Today's
    /// sentence ("'dst' was replaced, but the previous destination could not be removed") is the
    /// same-volume one, and it is false here: it tells a caller the move went through.
    /// </summary>
    [Fact]
    public async Task MoveAsync_across_volumes_that_cannot_remove_the_aside_says_the_move_did_not_complete()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;   // one volume on this box: nothing to move across

        const int Count = 2000;
        try
        {
            var src = Dir("xvol-aside-src");
            for (var i = 0; i < Count; i++)
                await File.WriteAllTextAsync(Path.Combine(src, $"f{i:D4}.txt"), "x");
            var dst = Path.Combine(landing, "dst");
            Directory.CreateDirectory(dst);
            await File.WriteAllTextAsync(Path.Combine(dst, "stale.txt"), "stale");

            var move = Task.Run(() => Svc().MoveAsync(src, dst, overwrite: true));
            FileStream? held = null;
            try
            {
                var spin = Stopwatch.StartNew();
                while (held is null && !move.IsCompleted && spin.Elapsed < HandshakeCeiling)
                {
                    var aside = AsidesOf(dst).FirstOrDefault();
                    if (aside is null) { Thread.SpinWait(20); continue; }
                    held = new FileStream(Path.Combine(aside, "stale.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                // Round 4d: the ceiling is a way OUT of the wait, not an outcome.
                (held is not null || move.IsCompleted).Should().BeTrue(
                    $"no '.replaced.' aside appeared beside '{dst}' within {HandshakeCeiling.TotalSeconds:0} s, so "
                    + "this run tested nothing: the handshake has to happen for the rest of the test to mean anything");
                held.Should().NotBeNull(
                    "the move finished before the aside could be held open, so nothing blocked the commit - "
                    + "the test has to hold the aside open while the copy is still running, or it proves nothing");

                var act = () => move;
                var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;

                message.Should().Contain(src,
                    "the source is still there in full - a caller told only about the destination deletes it");
                var saysItDidNotComplete =
                    message.Contains("not complete", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("incomplete", StringComparison.OrdinalIgnoreCase);
                saysItDidNotComplete.Should().BeTrue(
                    "the source was never removed, so this is a move that did not happen - not a move that "
                    + "happened and left litter; the message has to say so (\"did not complete\"/\"incomplete\"). "
                    + $"It said: {message}");
                message.Should().Contain(".replaced.", "and where the previous destination is");

                Directory.EnumerateFiles(src).Should().HaveCount(Count, "the source is still at the source");
                Directory.EnumerateFiles(dst).Should().HaveCount(Count, "and the copy is at the destination");
                AsidesOf(dst).Should().ContainSingle("the aside is what could not be removed");
            }
            finally
            {
                held?.Dispose();
                foreach (var aside in AsidesOf(dst)) ForceDelete(aside);
            }
        }
        finally { try { Directory.Delete(landing, true); } catch { /* best effort */ } }
    }

    // ---- R4c-4: a chain of links to a root, and an ordinary subdirectory under one ----------------

    /// <summary>
    /// R4c-4: <c>Canonical</c> checks a link's IMMEDIATE target and its FINAL target. A chain hides
    /// the root between them: <c>l1 → l2 → S:\</c> has an immediate target that is a link and a
    /// final target that is the directory the subst drive maps to — neither is a root, so a whole
    /// volume is copied under a name that looks like an ordinary directory.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_chain_of_links_that_ends_at_a_volume_root_as_the_source()
    {
        var target = Dir("chain-src-target");
        File_(Path.Combine("chain-src-target", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;               // no free drive letter: no root to point a chain at
        var l2 = Symlink("chain-src-l2", root);
        if (l2 is null) return;                 // no Developer Mode / no SeCreateSymbolicLink: no chain
        var l1 = Symlink("chain-src-l1", l2);
        if (l1 is null) return;
        var dst = Path.Combine(_tmp, "chain-src-landing");

        var act = () => Svc().CopyAsync(l1, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a link to a link to a whole volume is still a whole volume")).Which.Message
            .Should().ContainEquivalentOf("volume root",
                "the refusal is the root rule, whichever hop of the chain the root is on");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue();
    }

    /// <summary>
    /// R4c-4 at the other end. The destination exists (it is the volume), so today's answer is the
    /// existing-destination refusal — whose advice, <c>overwrite:true</c>, is how a caller who
    /// follows it empties a volume.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_chain_of_links_that_ends_at_a_volume_root_as_the_destination()
    {
        var target = Dir("chain-dst-target");
        File_(Path.Combine("chain-dst-target", "canary.txt"), "canary");
        var root = SubstRoot(target);
        if (root is null) return;               // no free drive letter
        var l2 = Symlink("chain-dst-l2", root);
        if (l2 is null) return;                 // no Developer Mode / no SeCreateSymbolicLink
        var l1 = Symlink("chain-dst-l1", l2);
        if (l1 is null) return;
        var src = Dir("chain-dst-src");
        File_(Path.Combine("chain-dst-src", "new.txt"), "new");

        var act = () => Svc().CopyAsync(src, l1, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().ContainEquivalentOf("volume root",
                "'already exists; pass overwrite:true to replace it' invites the caller to replace a VOLUME");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue();
    }

    /// <summary>
    /// R4c-4's other half, and the reason the check is on the path itself rather than its
    /// ancestors: <c>lroot\sub</c> where <c>lroot → S:\</c> is an ordinary subdirectory of an
    /// ordinary volume. Refusing it would make every path under a mapped root uncopyable — the
    /// rule is "this path IS a root", not "a root is somewhere above it".
    /// </summary>
    [Fact]
    public async Task A_subdirectory_reached_through_a_link_to_a_volume_root_copies_both_ways()
    {
        var target = Dir("under-root-target");
        File_(Path.Combine("under-root-target", "sub", "inside.txt"), "inside");
        var root = SubstRoot(target);
        if (root is null) return;               // no free drive letter
        var lroot = Symlink("under-root-link", root);
        if (lroot is null) return;              // no Developer Mode / no SeCreateSymbolicLink
        var svc = Svc();
        var landing = Path.Combine(_tmp, "under-root-landing");

        var copyOut = () => svc.CopyAsync(Path.Combine(lroot, "sub"), landing, overwrite: false);
        await copyOut.Should().NotThrowAsync(
            "'link\\sub' is a subdirectory that happens to be reached through a link to a root - the root is "
            + "an ancestor, not the path");
        (await File.ReadAllTextAsync(Path.Combine(landing, "inside.txt"))).Should().Be("inside",
            "and the copy really carried the files, rather than making an empty directory");

        var copyIn = () => svc.CopyAsync(landing, Path.Combine(lroot, "dest"), overwrite: false);
        await copyIn.Should().NotThrowAsync("the same holds for a destination under the link");
        (await File.ReadAllTextAsync(Path.Combine(target, "dest", "inside.txt"))).Should().Be("inside",
            "the copy landed on the volume the link points at");
    }

    /// <summary>R4c-4: and <c>delete</c> agrees with <c>copy</c> about what is an ordinary directory.</summary>
    [Fact]
    public async Task A_subdirectory_reached_through_a_link_to_a_volume_root_deletes()
    {
        var target = Dir("under-root-del-target");
        var sub = Dir(Path.Combine("under-root-del-target", "sub"));
        File_(Path.Combine("under-root-del-target", "sub", "inside.txt"), "inside");
        var root = SubstRoot(target);
        if (root is null) return;               // no free drive letter
        var lroot = Symlink("under-root-del-link", root);
        if (lroot is null) return;              // no Developer Mode / no SeCreateSymbolicLink

        var act = () => Svc().DeleteAsync(Path.Combine(lroot, "sub"), recursive: true);

        await act.Should().NotThrowAsync(
            "a subdirectory under a link to a root is an ordinary tree; delete and copy have to agree on that");
        Directory.Exists(sub).Should().BeFalse("the tree the caller named is gone");
        Directory.Exists(target).Should().BeTrue("and nothing above it was touched");
    }

    /// <summary>
    /// R4c-4's other refusal, and the one clause of the hop walk nothing else reaches: a chain that
    /// never loops but is absurdly long. The visited set catches <c>a → b → a</c>; forty links in a
    /// row each resolve to something new, so only the 32-hop cap ends the walk. Without it the walk
    /// length is whatever the caller's own file system will hold, before a single check has run.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_link_chain_longer_than_the_hop_cap()
    {
        var target = Dir("hop-cap-target");
        File_(Path.Combine("hop-cap-target", "canary.txt"), "canary");
        var head = target;
        for (var i = 0; i < 40; i++)   // well inside Windows' own 63-reparse-point resolution limit
        {
            var link = Symlink($"hop-cap-{i:D2}", head);
            if (link is null) return;   // no Developer Mode / no SeCreateSymbolicLink: no chain to build
            head = link;
        }
        var dst = Path.Combine(_tmp, "hop-cap-landing");

        var act = () => Svc().CopyAsync(head, dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a path the containment check cannot resolve in a bounded number of hops cannot be compared, "
                + "and a copy that cannot be checked must not run")).Which.Message
            .Should().Contain("cannot be resolved", "the refusal says why, not just that");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue("and touches nothing at the far end");
    }

    /// <summary>
    /// The other side of the same cap (GREEN): a chain WITHIN it resolves and copies. A guard that
    /// refused every chain, or a visited set that mistook distinct hops for a loop, would leave
    /// this failing — and links to links are ordinary on a developer's machine.
    /// </summary>
    [Fact]
    public async Task CopyAsync_follows_a_link_chain_within_the_hop_cap()
    {
        var target = Dir("hop-ok-target");
        File_(Path.Combine("hop-ok-target", "canary.txt"), "canary");
        var head = target;
        for (var i = 0; i < 8; i++)
        {
            var link = Symlink($"hop-ok-{i:D2}", head);
            if (link is null) return;   // no Developer Mode / no SeCreateSymbolicLink: no chain to build
            head = link;
        }
        var dst = Path.Combine(_tmp, "hop-ok-landing");

        var act = () => Svc().CopyAsync(head, dst, overwrite: false);

        await act.Should().NotThrowAsync("eight hops is a chain, not a loop, and it ends at a real directory");
        (await File.ReadAllTextAsync(Path.Combine(dst, "canary.txt"))).Should().Be("canary",
            "and the copy carried what was at the end of the chain");
    }

    // ---- R4c-5: the read-only bit on a directly named target --------------------------------------

    /// <summary>
    /// R4c-5: the tree remover clears the read-only attribute (R4b-3) and so does the aside — but a
    /// file named DIRECTLY still goes through <c>File.Delete</c>, which refuses one with
    /// UnauthorizedAccessException. The caller confirmed the delete and named this exact file; the
    /// attribute is not a second gate, and <c>recursive</c> is not the missing ingredient either.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_of_a_read_only_file_named_directly_deletes_it(bool recursive)
    {
        var file = ReadOnlyFile($"ro-named-{recursive}.txt", "ro");

        var act = () => Svc().DeleteAsync(file, recursive);

        await act.Should().NotThrowAsync(
            "'delete this file' with the read-only bit set is still 'delete this file' - and the same call "
            + "inside a tree already succeeds");
        File.Exists(file).Should().BeFalse("the file the caller named is gone");
    }

    /// <summary>
    /// R4c-5, the directory the tree remover never sees: an EMPTY read-only directory named
    /// directly goes through <c>Directory.Delete(path, false)</c>, and RemoveDirectory answers
    /// "access is denied" for a read-only directory exactly as DeleteFile does for a read-only file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_of_a_read_only_empty_directory_named_directly_deletes_it(bool recursive)
    {
        var dir = Dir($"ro-empty-{recursive}");
        File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.ReadOnly);

        try
        {
            var act = () => Svc().DeleteAsync(dir, recursive);

            await act.Should().NotThrowAsync(
                "an empty directory goes without recursive:true (R2), and the read-only bit does not add a gate");
            Directory.Exists(dir).Should().BeFalse("the directory the caller named is gone");
        }
        finally
        {
            try { if (Directory.Exists(dir)) File.SetAttributes(dir, File.GetAttributes(dir) & ~FileAttributes.ReadOnly); }
            catch { /* best effort */ }
        }
    }

    // ---- R4c-6: a list pattern is a NAME glob ------------------------------------------------------

    /// <summary>
    /// R4c-6: <c>MatchesSimpleExpression</c> is matched against the file NAME, so a pattern with a
    /// path separator in it matches nothing, ever. The caller gets an empty listing — which reads
    /// as "there is nothing there" — instead of being told that <c>recursive:true</c> is how you
    /// descend.
    /// </summary>
    [Theory]
    [InlineData(@"sub\*.txt")]
    [InlineData("sub/*.txt")]
    public async Task ListAsync_refuses_a_pattern_with_a_path_separator(string pattern)
    {
        var root = Dir("pattern-guard");
        File_(Path.Combine("pattern-guard", "sub", "one.txt"), "one");

        var act = () => Svc().ListAsync(root, pattern, recursive: false, includeHidden: false, maxEntries: 1000);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>(
                "an empty listing is indistinguishable from an empty directory; a pattern that CANNOT match is "
                + "a mistake to report, not a result to return")).Which;
        thrown.Message.Should().Contain("pattern", "the refusal names the parameter as the caller spells it");
        thrown.ParamName.Should().Be("pattern");
    }

    // ---- R4c-8: a recursive delete observes the token ----------------------------------------------

    /// <summary>
    /// R4c-8: <c>DeleteTree</c> takes no token, so <c>delete recursive:true</c> of a big tree runs
    /// to the end whatever the caller does — the one operation in this service where "stop" matters
    /// most, because every entry it walks past is gone for good. (The aside's own removals and the
    /// cross-volume source removal deliberately pass none: those must finish.)
    /// </summary>
    [Fact]
    public async Task DeleteAsync_recursive_cancelled_mid_walk_stops_deleting()
    {
        const int Count = 3000;
        var tree = Dir("cancel-delete");
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(tree, $"f{i:D4}.txt"), "x");

        using var cts = new CancellationTokenSource();
        var delete = Task.Run(() => Svc().DeleteAsync(tree, recursive: true, cts.Token));

        // The handshake is the first entry disappearing: only the walk can do that, so by then the
        // delete is under way and the cancel below can only be observed by the walk itself.
        var spin = Stopwatch.StartNew();
        while (FileCount(tree) == Count && !delete.IsCompleted && spin.Elapsed < HandshakeCeiling)
            Thread.SpinWait(20);
        // Round 4d: hitting the ceiling means the walk never started, and cancelling a delete that
        // has not begun proves nothing about cancelling one that has — the assertions below would
        // pass or fail for reasons that have nothing to do with R4c-8. Say so here instead.
        (FileCount(tree) < Count || delete.IsCompleted).Should().BeTrue(
            $"the recursive delete had not removed a single one of the {Count} files after "
            + $"{HandshakeCeiling.TotalSeconds:0} s, so the cancel below is not racing the walk at all");
        cts.Cancel();

        var act = () => delete;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a recursive delete of a big tree has to be abandonable, not only refusable up front");
        Directory.Exists(tree).Should().BeTrue("a cancelled delete stops where it is");
        Directory.EnumerateFileSystemEntries(tree).Should().NotBeEmpty(
            "'cancelled' and 'finished the tree first' cannot be the same outcome");
    }

    // ---- R4c-11: the extended prefix is stripped only from a drive or UNC path ----------------------

    /// <summary>
    /// R4c-11: <c>\\?\Volume{…}\</c> is a volume root — the spelling <c>mountvol</c> prints and the
    /// only name a volume with no drive letter has. Stripping <c>\\?\</c> from it leaves
    /// <c>Volume{…}\</c>, which is RELATIVE: <c>GetFullPath</c> resolves it against the server's
    /// working directory, the root check then compares a path on the wrong volume entirely, and the
    /// caller is told their volume "does not exist".
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_volume_guid_root_as_the_source()
    {
        var dst = Path.Combine(_tmp, "volume-guid-landing");

        var act = () => Svc().CopyAsync(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\", dst, overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a FileNotFoundException here is the strip's accident, not an answer: the path IS a root and the "
                + "root rule is what the caller has to be told")).Which.Message
            .Should().ContainEquivalentOf("volume root");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
    }

    /// <summary>
    /// R4c-11's other side (GREEN): narrowing the strip must not break it. <c>\\?\C:\x</c> IS
    /// <c>C:\x</c> and the containment checks depend on the strip to see that (R4-2).
    /// </summary>
    [Fact]
    public async Task CopyAsync_still_strips_the_extended_prefix_from_a_drive_path()
    {
        var src = Dir("ext-strip-src");
        File_(Path.Combine("ext-strip-src", "one.txt"), "one");
        var dst = Path.Combine(_tmp, "ext-strip-dst");

        var act = () => Svc().CopyAsync(@"\\?\" + src, dst, overwrite: false);

        await act.Should().NotThrowAsync(
            "refusing \\\\?\\Volume{...} must not turn into refusing every extended-length form");
        (await File.ReadAllTextAsync(Path.Combine(dst, "one.txt"))).Should().Be("one");
    }

    // ---- R4d-1: a drive letter is a second spelling of the same directory ---------------------------

    /// <summary>
    /// The destination leaf name for the alias tests. Long on purpose, like
    /// <see cref="SubtreeName"/>: when the refusal does NOT happen, the copy descends into the
    /// directory it has just created inside its own source, and a 238-character component bottoms
    /// that runaway out on the path limit within a couple of hundred levels instead of a couple of
    /// thousand. Under 255, the NTFS limit for one component.
    /// </summary>
    private static readonly string AliasBackupName =
        "backup-a-copy-must-never-descend-into-" + new string('x', 200);

    /// <summary>
    /// R4d-1, the PR #25 runaway reached through a drive letter: <c>subst</c> maps <c>L:</c> onto
    /// <c>&lt;tmp&gt;\vol</c>, so <c>L:\project\backup</c> IS a directory inside
    /// <c>&lt;tmp&gt;\vol\project</c> — and no string comparison of the two spellings can see it.
    /// The copy walks into what it just created; the move takes the cross-volume branch (the roots
    /// differ) and does the same. Only asking the volume for its own spelling of each end catches
    /// it, which is what <see cref="PathCanonical"/> is for.
    /// </summary>
    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public async Task Copy_and_move_refuse_a_destination_inside_the_source_through_a_drive_alias(string verb)
    {
        var vol = Dir("alias-vol-" + verb);
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: R4d-6's canary is what fails for this
        var src = Dir(Path.Combine("alias-vol-" + verb, "project"));
        File_(Path.Combine("alias-vol-" + verb, "project", "keep.txt"), "keep");
        var dst = Path.Combine(root, "project", AliasBackupName);
        var svc = Svc();

        Func<Task> act = () => verb == "copy"
            ? svc.CopyAsync(src, dst, overwrite: false)
            : svc.MoveAsync(src, dst, overwrite: false);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a drive letter is a second name for a directory, not a second directory: this destination is "
                + "inside the source, and copying a tree into its own subtree never terminates on purpose")).Which.Message;
        message.Should().ContainEquivalentOf("inside",
            "the caller has to be told WHICH rule stopped them - 'the path is too long' after 130 levels is not that");
        Directory.Exists(dst).Should().BeFalse("a refusal creates nothing");
        EntriesUnder(src).Should().BeEquivalentTo(new[] { "keep.txt" },
            "the source is left exactly as it was - not one level of a runaway copy deeper");
    }

    /// <summary>
    /// R4d-1 the other way round: the destination CONTAINS the source, seen only through the
    /// letter. With <c>overwrite:true</c> the aside step renames the destination — which is the
    /// source's own parent — out from under the copy, so what the caller is copying disappears
    /// before it is read. Nothing may be renamed at all.
    /// </summary>
    [Fact]
    public async Task Copy_refuses_a_destination_that_contains_the_source_through_a_drive_alias()
    {
        var vol = Dir("alias-contains-vol");
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: see the canary
        var project = Dir(Path.Combine("alias-contains-vol", "project"));
        File_(Path.Combine("alias-contains-vol", "project", "top.txt"), "top");
        File_(Path.Combine("alias-contains-vol", "project", "backup", "keep.txt"), "keep");
        var srcThroughTheLetter = Path.Combine(root, "project", "backup");

        var act = () => Svc().CopyAsync(srcThroughTheLetter, project, overwrite: true);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "replacing a directory that holds the source deletes what is being copied")).Which.Message;
        message.Should().ContainEquivalentOf("contains",
            "the rule that fires is the containment one; anything else sends the caller looking in the wrong place");
        AsidesOf(project).Should().BeEmpty(
            "a refusal renames nothing - a .replaced. sibling here would BE the source, moved out from under the copy");
        EntriesUnder(project).Should().BeEquivalentTo(
            new[] { "top.txt", "backup", Path.Combine("backup", "keep.txt") },
            "both ends are untouched");
    }

    /// <summary>
    /// R4d-1: one directory, two letters, so <c>src</c> and <c>dst</c> are the same thing. Without
    /// <c>overwrite</c> today's answer is "pass overwrite:true to replace it" — advice that tells
    /// the caller to replace a directory with itself; with it, the aside renames the source away
    /// and the copy then reads a path that is no longer there. Neither is an outcome: it is the
    /// same path, and nothing may be renamed.
    /// </summary>
    [Theory]
    [InlineData("copy", false)]
    [InlineData("copy", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    public async Task Copy_and_move_refuse_the_same_directory_spelled_through_two_drive_letters(string verb, bool overwrite)
    {
        var name = $"alias-same-vol-{verb}-{overwrite}";
        var vol = Dir(name);
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: see the canary
        var real = Dir(Path.Combine(name, "project"));
        File_(Path.Combine(name, "project", "keep.txt"), "keep");
        var aliased = Path.Combine(root, "project");
        var svc = Svc();

        Func<Task> act = () => verb == "copy"
            ? svc.CopyAsync(aliased, real, overwrite)
            : svc.MoveAsync(aliased, real, overwrite);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("same path",
            "the two spellings name one directory; 'already exists, pass overwrite:true' invites the caller to "
            + "replace it with itself");
        message.Should().NotContain("overwrite:true",
            "there is no flag that makes copying a directory onto itself sensible");
        AsidesOf(real).Should().BeEmpty("a refusal renames nothing");
        EntriesUnder(real).Should().BeEquivalentTo(new[] { "keep.txt" }, "and moves nothing");
    }

    // ---- R4d-2: a failed copy or move removes the parents it created --------------------------------

    /// <summary>
    /// R4d-2: <c>CreateParent</c> makes the whole missing chain before the copy runs, and today
    /// nothing takes it away again when the copy fails. The caller asked for one file and, after a
    /// failure, is left with three directories they never had — at a path they may well have
    /// mistyped, which is how they got here.
    /// </summary>
    [Fact]
    public async Task CopyAsync_that_fails_removes_the_destination_parents_it_created()
    {
        var src = File_("made-up-src.txt", "payload");
        var made = Path.Combine(_tmp, "made");
        var dst = Path.Combine(made, "up", "tree", "out.txt");

        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => Svc().CopyAsync(src, dst, overwrite: false);

            await act.Should().ThrowAsync<IOException>("File.Copy cannot read a source held with FileShare.None");
        }

        File.Exists(dst).Should().BeFalse("the copy never happened");
        Directory.Exists(Path.Combine(made, "up", "tree")).Should().BeFalse("the deepest directory the copy made goes first");
        Directory.Exists(Path.Combine(made, "up")).Should().BeFalse("and then its parent");
        Directory.Exists(made).Should().BeFalse(
            "a failed copy leaves the file system as it found it: none of these three existed before the call");
    }

    /// <summary>
    /// R4d-2's other half, and the one a careless clean-up gets wrong: <c>&lt;tmp&gt;\kept-made</c>
    /// was there before the call and is EMPTY, so "remove every empty parent up the chain" would
    /// take it too. Only the directories the operation itself created are its to remove.
    /// </summary>
    [Fact]
    public async Task CopyAsync_that_fails_leaves_a_destination_parent_that_was_already_there()
    {
        var src = File_("kept-parent-src.txt", "payload");
        var made = Dir("kept-made");   // empty, and NOT the copy's to remove
        var dst = Path.Combine(made, "up", "tree", "out.txt");

        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => Svc().CopyAsync(src, dst, overwrite: false);

            await act.Should().ThrowAsync<IOException>("File.Copy cannot read a source held with FileShare.None");
        }

        Directory.Exists(made).Should().BeTrue(
            "an empty directory that existed before the copy is still the caller's, not litter to tidy away");
        Directory.Exists(Path.Combine(made, "up")).Should().BeFalse("what the copy created, the copy removes");
    }

    /// <summary>R4d-2 for the other verb: a move that fails owes the same tidy-up.</summary>
    [Fact]
    public async Task MoveAsync_that_fails_removes_the_destination_parents_it_created()
    {
        var src = File_("moved-up-src.txt", "payload");
        var made = Path.Combine(_tmp, "moved-made");
        var dst = Path.Combine(made, "up", "tree", "out.txt");

        Exception? thrown;
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            thrown = await Record.ExceptionAsync(() => Svc().MoveAsync(src, dst, overwrite: false));
        }

        thrown.Should().NotBeNull("a file held with FileShare.None cannot be moved either");
        File.Exists(src).Should().BeTrue("a failed move leaves the source where it is");
        Directory.Exists(made).Should().BeFalse(
            "and leaves no half-built destination chain behind: none of made/up/tree existed before the call");
    }

    // ---- R4d-3: a drive reference is not a path ------------------------------------------------------

    /// <summary>
    /// The two spellings that survive the <c>\\?\</c> strip as something that is NOT an absolute
    /// path. <c>C:</c> and <c>C:relative</c> are drive-RELATIVE: <c>GetFullPath</c> resolves them
    /// against whatever directory the process last had on that drive, so the same call means
    /// different things in different servers — and in a long-lived one, at different times.
    /// </summary>
    public static TheoryData<string> DriveReferences => new() { @"\\?\C:", @"\\?\C:relative" };

    public static TheoryData<string, string> DriveReferencesByVerb => new()
    {
        { "copy", @"\\?\C:" },
        { "copy", @"\\?\C:relative" },
        { "move", @"\\?\C:" },
        { "move", @"\\?\C:relative" },
    };

    /// <summary>Where a drive reference would actually land, so a test can prove nothing landed there.</summary>
    private static string LandingOf(string driveReference) =>
        Path.GetFullPath(driveReference.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? driveReference[4..]
            : driveReference);

    private static bool AnythingAt(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>A file-name-safe tag for a drive reference, so each theory case gets its own paths.</summary>
    private static string Slug(string driveReference) =>
        driveReference.Contains("relative", StringComparison.Ordinal) ? "rel" : "bare";

    /// <summary>
    /// The refusal R4d-3 asks for, in either of its two honest shapes: the argument was wrong
    /// (<see cref="ArgumentException"/>) or the call cannot be made as asked
    /// (<see cref="InvalidOperationException"/>). Both name the path the CALLER wrote and say what
    /// is wrong with it; a <see cref="FileNotFoundException"/> about a resolved path the caller
    /// never typed, or "that is a volume root" about a path that is not one, do not.
    /// </summary>
    private static void ShouldRefuseAsNotAbsolute(Exception? thrown, string path)
    {
        thrown.Should().NotBeNull(
            $"'{path}' is a drive reference, not a path - it means 'wherever this process last was on that drive'");
        (thrown is ArgumentException or InvalidOperationException).Should().BeTrue(
            $"the refusal has to read as 'that is not a path I can work with'. It was: {thrown}");
        thrown!.Message.Should().Contain(path,
            "the refusal names the path as the caller spelled it, not the one it happened to resolve to");
        thrown.Message.Should().ContainEquivalentOf("absolute",
            "and says what is missing - an absolute path; 'volume root' is a different (and here untrue) story");
    }

    [Theory]
    [MemberData(nameof(DriveReferencesByVerb))]
    public async Task Copy_and_move_refuse_a_drive_reference_as_the_source(string verb, string path)
    {
        var dst = Path.Combine(_tmp, $"drive-ref-src-landing-{verb}-{Slug(path)}");
        var svc = Svc();

        var thrown = await Record.ExceptionAsync(() => verb == "copy"
            ? svc.CopyAsync(path, dst, overwrite: false)
            : svc.MoveAsync(path, dst, overwrite: false));

        ShouldRefuseAsNotAbsolute(thrown, path);
        AnythingAt(dst).Should().BeFalse("a refusal creates nothing");
    }

    /// <summary>
    /// R4d-3 where it costs something: a real source and a drive reference for the destination.
    /// The check has to fire BEFORE the copy, or the caller's file is written to whatever
    /// <c>C:relative</c> resolves to on the server today.
    /// </summary>
    [Theory]
    [MemberData(nameof(DriveReferencesByVerb))]
    public async Task Copy_and_move_refuse_a_drive_reference_as_the_destination(string verb, string path)
    {
        var src = File_($"drive-ref-dst-src-{verb}-{Slug(path)}.txt", "payload");
        var landing = LandingOf(path);
        var landingWasThere = AnythingAt(landing);
        var svc = Svc();

        try
        {
            var thrown = await Record.ExceptionAsync(() => verb == "copy"
                ? svc.CopyAsync(src, path, overwrite: false)
                : svc.MoveAsync(src, path, overwrite: false));

            ShouldRefuseAsNotAbsolute(thrown, path);
            AnythingAt(landing).Should().Be(landingWasThere,
                $"nothing may appear at '{landing}' - the caller never named it, the drive's current directory did");
            File.Exists(src).Should().BeTrue("and the source is still where it was");
            (await File.ReadAllTextAsync(src)).Should().Be("payload");
        }
        finally
        {
            // A RED run that DID resolve the reference wrote there; do not leave it behind.
            if (!landingWasThere && File.Exists(landing))
                try { File.Delete(landing); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// R4d-3 for delete, where today's answer is silence: <c>Directory.Exists</c> and
    /// <c>File.Exists</c> both say no for the spelling as written, so the service returns as if it
    /// had tidied something up and the tool reports the path was already gone. The caller believes
    /// a delete happened.
    /// </summary>
    [Theory]
    [MemberData(nameof(DriveReferences))]
    public async Task DeleteAsync_refuses_a_drive_reference(string path)
    {
        var thrown = await Record.ExceptionAsync(() => Svc().DeleteAsync(path, recursive: false));

        ShouldRefuseAsNotAbsolute(thrown, path);
    }

    /// <summary>
    /// The device forms that survive <c>StripExtendedPrefix</c> as themselves — the prefix is only
    /// stripped when a drive letter or a UNC name follows it (R4c-11), so these reach the rest of
    /// the service exactly as written. The tool layer refuses them first (R4-2/R4b-4), which is why
    /// these are the SERVICE's own guard: the day a second caller reaches <c>IFileSystemService</c>
    /// without going through <c>FileTools</c>, this is what is left. Deliberately named after a
    /// device that does not exist: a regression here must fail the copy harmlessly, not write into
    /// the raw volume namespace.
    /// </summary>
    public static TheoryData<string> DeviceForms => new()
    {
        @"\\?\GLOBALROOT\Device\wmcp-no-such-device\file.txt",
        @"\\.\wmcp-no-such-device\file.txt",
    };

    [Theory]
    [MemberData(nameof(DeviceForms))]
    public async Task CopyAsync_refuses_a_device_form_as_the_source(string path)
    {
        var dst = Path.Combine(_tmp, "device-form-landing-" + Guid.NewGuid().ToString("N"));

        var thrown = await Record.ExceptionAsync(() => Svc().CopyAsync(path, dst, overwrite: false));

        ShouldRefuseAsNotAbsolute(thrown, path);
        AnythingAt(dst).Should().BeFalse("a refusal creates nothing");
    }

    /// <summary>
    /// The dangerous end: a device path as the DESTINATION is a write into the raw namespace, so
    /// the refusal has to come before <c>File.Copy</c> is handed the string — a
    /// <c>DirectoryNotFoundException</c> from the framework would mean the guard was not what
    /// stopped it.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeviceForms))]
    public async Task CopyAsync_refuses_a_device_form_as_the_destination(string path)
    {
        var src = File_("device-form-src-" + Guid.NewGuid().ToString("N") + ".txt", "payload");

        var thrown = await Record.ExceptionAsync(() => Svc().CopyAsync(src, path, overwrite: false));

        ShouldRefuseAsNotAbsolute(thrown, path);
        File.Exists(src).Should().BeTrue("and the source is still where it was");
    }

    [Theory]
    [MemberData(nameof(DeviceForms))]
    public async Task DeleteAsync_refuses_a_device_form(string path)
    {
        var thrown = await Record.ExceptionAsync(() => Svc().DeleteAsync(path, recursive: true));

        ShouldRefuseAsNotAbsolute(thrown, path);
    }

    // ---- R4d-4: list of a file says so ---------------------------------------------------------------

    /// <summary>
    /// R4d-4: <c>list</c> of a file is a mistake with an obvious correction, and the framework's
    /// own answer for it ("the directory name is invalid") reads like a broken path. Say which
    /// path, and that it is a file.
    /// </summary>
    [Fact]
    public async Task ListAsync_of_a_file_says_it_is_not_a_directory()
    {
        var file = File_("listing-target.txt", "x");

        var thrown = await Record.ExceptionAsync(() =>
            Svc().ListAsync(file, null, recursive: false, includeHidden: false, maxEntries: 100));

        thrown.Should().NotBeNull("a listing of a file cannot return entries, so it has to say why");
        thrown!.Message.Should().Contain(file, "the caller has to know WHICH path was wrong");
        thrown.Message.Should().ContainEquivalentOf("not a directory",
            "'the directory name is invalid' sounds like a broken path; this one is fine, it is just a file");
    }

    // ---- R4d-6: the canary for the environment-dependent tests ---------------------------------------

    /// <summary>
    /// R4d-6: every root and link test above returns silently when no drive letter is free —
    /// xunit 2.x has no runtime skip, so a box where <c>subst</c> stopped working would report the
    /// whole family as green. This one test fails instead, and names the reason, so the family
    /// cannot vanish without a sound.
    /// </summary>
    [Fact]
    public void A_subst_drive_can_be_created_for_the_root_and_alias_tests()
    {
        var target = Dir("canary-target");

        var root = SubstRoot(target);

        root.Should().NotBeNull(
            "every root, link and drive-alias test in this class returns silently when subst cannot produce a "
            + "drive - so without this one, that whole family would report green on a box where it never ran. "
            + "Free a drive letter (subst /D) or fix subst before trusting the rest of this file");
        File.WriteAllText(Path.Combine(root!, "canary.txt"), "canary");
        File.Exists(Path.Combine(target, "canary.txt")).Should().BeTrue(
            "the letter has to be a second spelling of the target directory - that IS what these tests test");
    }

    // ---- R4e-1: a destination that is a LINK standing inside the source ------------------------------

    /// <summary>
    /// The name of the link that stands inside the source. Long on purpose, like
    /// <see cref="SubtreeName"/>: when the refusal does NOT happen the copy descends into the
    /// directory it has just created, and a 126-character component bottoms that runaway out on
    /// the path limit within a couple of hundred levels instead of a couple of thousand. Short
    /// enough that <c>mklink</c> — which runs through cmd.exe, and cmd.exe is not long-path aware
    /// — can still create it inside a source directory under the temp path.
    /// </summary>
    private static readonly string LinkInsideName = "link-a-copy-must-never-descend-into-" + new string('z', 90);

    /// <summary>
    /// Three seconds and no more for a copy that should never have started. The long component
    /// above is the primary bound (the path limit stops the runaway); this is the belt to those
    /// braces, so a RED run cannot sit in a loop that outlives the test session. A refusal never
    /// reaches the copy, so a passing run never observes this token at all.
    /// </summary>
    private static CancellationTokenSource RunawayGuard() => new(TimeSpan.FromSeconds(3));

    /// <summary>
    /// R4e-1: the destination is a junction that lives INSIDE the source and points outside it.
    /// Its target is not inside the source, so a containment check done only on the canonical
    /// paths sees an unrelated directory — but the place the destination occupies is inside, and
    /// that is what the copy walks. With <c>overwrite:true</c> the aside step renames the link
    /// away and the copy then descends into the ordinary directory it just created in its own
    /// source (PR #25's runaway, laundered through a link); with <c>overwrite:false</c> the
    /// caller is told to pass <c>overwrite:true</c>, which is the advice that starts it.
    /// </summary>
    [Theory]
    [InlineData("copy", false)]
    [InlineData("copy", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    public async Task Copy_and_move_refuse_a_destination_that_is_a_junction_inside_the_source(string verb, bool overwrite)
    {
        var name = $"j-in-{verb}-{overwrite}";
        var src = Dir(name);
        File_(Path.Combine(name, "keep.txt"), "keep");
        var outside = Dir("out-" + name);
        File_(Path.Combine("out-" + name, "marker.txt"), "marker");
        var link = Junction(Path.Combine(name, LinkInsideName), outside);
        var svc = Svc();
        using var guard = RunawayGuard();

        Func<Task> act = () => verb == "copy"
            ? svc.CopyAsync(src, link, overwrite, guard.Token)
            : svc.MoveAsync(src, link, overwrite, guard.Token);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "the destination's PLACE is inside the source whatever its target is - and the aside step would "
                + "move the link out of the way and let the copy recurse into what it created")).Which.Message;
        message.Should().ContainEquivalentOf("inside",
            "the caller has to be told WHICH rule stopped them; 'the path is too long' after 200 levels is not that");
        if (!overwrite)
            message.Should().NotContain("overwrite:true",
                "'already exists; pass overwrite:true' is advice that starts the runaway this rule exists to stop");
        AsidesOf(link).Should().BeEmpty("a refusal renames nothing");
        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue(
            "the link is still a link - not an ordinary directory a copy filled with the source");
        Directory.EnumerateFileSystemEntries(src).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "keep.txt", LinkInsideName },
                "a refusal creates nothing under the source - not one level of a runaway copy deeper");
        EntriesUnder(outside).Should().BeEquivalentTo(new[] { "marker.txt" },
            "and writes nothing through the link into the directory it points at");
    }

    /// <summary>
    /// R4e-1 with the other kind of link. A directory symlink is resolved by the same seam as a
    /// junction and stands in the same place; <c>Aside.Take</c> renames it the same way. Returns
    /// silently where a symlink cannot be made (no Developer Mode, no SeCreateSymbolicLink) — the
    /// junction rows above still cover the rule.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_refuses_a_destination_that_is_a_directory_symlink_inside_the_source(bool overwrite)
    {
        var name = $"sym-in-{overwrite}";
        var src = Dir(name);
        File_(Path.Combine(name, "keep.txt"), "keep");
        var outside = Dir("out-" + name);
        File_(Path.Combine("out-" + name, "marker.txt"), "marker");
        var link = Symlink(Path.Combine(name, LinkInsideName), outside);
        if (link is null) return;   // no Developer Mode / no SeCreateSymbolicLink: the junction rows cover the rule
        using var guard = RunawayGuard();

        Func<Task> act = () => Svc().CopyAsync(src, link, overwrite, guard.Token);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>(
                "a symlink inside the source occupies a place inside the source, whatever it points at")).Which.Message;
        message.Should().ContainEquivalentOf("inside", "the containment rule is the one that fires");
        if (!overwrite)
            message.Should().NotContain("overwrite:true", "there is no flag that makes this destination sensible");
        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("the link is still a link");
        EntriesUnder(outside).Should().BeEquivalentTo(new[] { "marker.txt" }, "and its target is untouched");
    }

    /// <summary>
    /// R4e-1 through a drive letter, where neither spelling on its own gives the game away:
    /// <c>subst</c> maps <c>L:</c> onto <c>&lt;tmp&gt;\vol</c>, so <c>L:\s\link</c> is literally
    /// inside <c>&lt;tmp&gt;\vol\s</c> — but the as-written paths share nothing, and the canonical
    /// path of the link is the directory outside the source that it points at. Only canonicalising
    /// the destination's PARENT and keeping its own last segment as written sees it.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_junction_inside_the_source_reached_through_a_drive_alias()
    {
        var vol = Dir("j-alias-vol");
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: R4d-6's canary is what fails for this
        var src = Dir(Path.Combine("j-alias-vol", "s"));
        File_(Path.Combine("j-alias-vol", "s", "keep.txt"), "keep");
        var outside = Dir("j-alias-outside");
        File_(Path.Combine("j-alias-outside", "marker.txt"), "marker");
        var link = Junction(Path.Combine("j-alias-vol", "s", LinkInsideName), outside);
        var dst = Path.Combine(root, "s", LinkInsideName);
        using var guard = RunawayGuard();

        Func<Task> act = () => Svc().CopyAsync(src, dst, overwrite: true, guard.Token);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "the letter is a second spelling of the source's own parent: this destination stands inside the source"))
            .Which.Message.Should().ContainEquivalentOf("inside",
                "the containment rule has to fire on the literal place, through the alias as well as without it");
        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("the link is still a link");
        EntriesUnder(outside).Should().BeEquivalentTo(new[] { "marker.txt" }, "its target is untouched");
        Directory.EnumerateFileSystemEntries(src).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "keep.txt", LinkInsideName }, "and nothing was created under the source");
    }

    // ---- R4e-2: a parent chain that fails part-way leaves nothing behind ------------------------------

    /// <summary>
    /// The two ways one component of a destination path is a name Windows will not make: a
    /// reserved character, and a component past the 255-character limit for one NTFS name. Both
    /// are ordinary caller typos, and both fail <c>CreateParent</c> AFTER it has already made the
    /// components above them.
    /// </summary>
    public static TheoryData<string, string> UnmakeableComponents => new()
    {
        { "copy", "illegal" },
        { "copy", "overlong" },
        { "move", "illegal" },
        { "move", "overlong" },
    };

    /// <summary>
    /// R4e-2: <c>CreateParent</c> builds the whole missing chain and only then hands back the list
    /// of what it made — so a chain that fails on its second component throws with the first one
    /// still on disk and nothing tracking it. The caller asked to copy one file to
    /// <c>&lt;tmp&gt;\newB\x*y\leaf</c>, got a failure, and is left with a directory
    /// <c>&lt;tmp&gt;\newB</c> they never had.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnmakeableComponents))]
    public async Task Copy_and_move_that_cannot_finish_the_parent_chain_remove_what_they_made(string verb, string kind)
    {
        var src = File_($"chain-src-{verb}-{kind}.txt", "payload");
        var made = Path.Combine(_tmp, $"newB-{verb}-{kind}");
        var middle = kind == "illegal" ? "x*y" : new string('n', 300);
        var dst = Path.Combine(made, middle, "leaf");
        var svc = Svc();

        var thrown = await Record.ExceptionAsync(() => verb == "copy"
            ? svc.CopyAsync(src, dst, overwrite: false)
            : svc.MoveAsync(src, dst, overwrite: false));

        thrown.Should().NotBeNull(
            "'*' is a reserved character and a 300-character component is past the NTFS name limit: neither is a "
            + "directory Windows will create, so the call cannot succeed");
        Directory.Exists(made).Should().BeFalse(
            $"the call created '{made}' on its way to a component it could not create; a failed copy or move "
            + "leaves the file system as it found it, and this one never existed before the call");
        File.Exists(src).Should().BeTrue("and the source is still where it was: nothing happened");
    }

    /// <summary>
    /// R4e-2 on the failure path that does not reach the tidy-up at all: <c>RollBack</c> throws
    /// <c>NotRestored</c> before <c>RemoveCreated</c> runs, so on this path nothing removes what
    /// the call made. Here the parents survive anyway — they hold the partial destination nobody
    /// could remove — which is the other half of the rule: a created parent goes only while it is
    /// EMPTY, and the half-copied tree the caller was just told about is not tidied away with it.
    /// The removal must move onto this path without taking either of those with it.
    /// <para>
    /// R4f-2, on the sentence the caller reads: nothing existed at this destination before the
    /// call, so there is no aside and nothing that could be "put back" — the fact they have to act
    /// on is that the half-copied tree was LEFT at the destination. Today's wording ("could not be
    /// put back as it was") describes a restore of content they never had.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failed_copy_that_cannot_restore_its_destination_leaves_only_what_it_could_not_remove()
    {
        const int Count = 3000;
        var src = Dir("pin-parent-src");
        var first = Dir(Path.Combine("pin-parent-src", "a"));
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(first, $"f{i:D4}.txt"), "x");
        // 'a' is copied first (enumeration order), so the partial destination exists - and is
        // pinned below - before the copy reaches the file it cannot read.
        var locked = File_(Path.Combine("pin-parent-src", "b", "locked.txt"), "locked");
        var made = Path.Combine(_tmp, "pin-parent-made");
        var dst = Path.Combine(made, "up", "dst");   // two parents the copy has to create first

        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Process? pin = null;
        try
        {
            var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: false));
            var partial = Path.Combine(dst, "a");
            var spin = Stopwatch.StartNew();
            while (pin is null && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            {
                if (!Directory.Exists(partial)) { Thread.SpinWait(20); continue; }
                pin = PinDirectory(partial);
            }
            (pin is not null || copy.IsCompleted).Should().BeTrue(
                $"the partial destination '{partial}' never appeared within {HandshakeCeiling.TotalSeconds:0} s, "
                + "so this run tested nothing: the handshake has to happen for the rest of the test to mean anything");
            pin.Should().NotBeNull(
                "the copy finished before the partial destination could be pinned, so nothing held it open - "
                + "the test has to pin it while the copy is still running, or it proves nothing");

            var thrown = await Record.ExceptionAsync(() => copy);

            var message = thrown.Should().BeOfType<InvalidOperationException>(
                    "the copy failed and its partial destination could not be removed - the caller has to hear both")
                .Which.Message;
            message.Should().Contain(dst, "the destination is the path the caller has to look at");
            message.Should().ContainEquivalentOf("left",
                "R4f-2: what is true here is that the partial result was LEFT at the destination - that is the "
                + "sentence the caller can act on, and it is the only thing distinguishing this outcome from a "
                + "failure that cleaned up after itself");
            message.Should().NotContainEquivalentOf("put back",
                "R4f-2: this destination did not exist before the call, so nothing was ever moved aside and "
                + "nothing could be put back; the phrase sends the caller hunting for content of their own that "
                + "was never there");
            Directory.Exists(partial).Should().BeTrue(
                "the pinned partial is what could not be removed - the caller is told where it is, not quietly "
                + "robbed of it by a tidy-up that tries harder than the restore did");
            new[] { made, Path.Combine(made, "up") }.Should().OnlyContain(parent => Directory.Exists(parent),
                "the parents the call created still hold that partial destination, so neither is the call's to remove");
            new[] { made, Path.Combine(made, "up") }
                .Where(parent => Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                .Should().BeEmpty(
                    "a parent this call created is removed on every failure path once it is EMPTY - including this "
                    + "one, where the destination could not be put back; these two survive only because they are not");
        }
        finally
        {
            hold.Dispose();
            ReleasePin(pin);
        }
    }

    /// <summary>
    /// R4e-2's other half, and the one branch of <c>RemoveCreated</c> nothing else reaches: a
    /// parent this call created, now empty, that Windows will not let it remove (it is a live
    /// process's working directory). The tidy-up runs inside the <c>catch</c> that is about to
    /// rethrow the caller's own failure, so trouble of its OWN escaping from there would replace
    /// "the source file is in use" with "that directory is in use" and send the caller after a
    /// problem they do not have. The parent is left where it is; the original exception is what
    /// comes out.
    /// </summary>
    [Fact]
    public async Task A_created_parent_that_cannot_be_removed_is_left_and_does_not_replace_the_failure()
    {
        const int Count = 3000;
        var src = Dir("rm-pin-src");
        var first = Dir(Path.Combine("rm-pin-src", "a"));
        for (var i = 0; i < Count; i++)
            await File.WriteAllTextAsync(Path.Combine(first, $"f{i:D4}.txt"), "x");
        // 'a' is copied first (enumeration order), so the copy is still running - and the parent
        // below already pinned - by the time it reaches the file it cannot read.
        var locked = File_(Path.Combine("rm-pin-src", "b", "locked.txt"), "locked");
        var made = Path.Combine(_tmp, "rm-pin-made");
        var upper = Path.Combine(made, "up");
        var dst = Path.Combine(upper, "dst");   // two parents the call has to create first

        var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Process? pin = null;
        try
        {
            var copy = Task.Run(() => Svc().CopyAsync(src, dst, overwrite: false));
            var spin = Stopwatch.StartNew();
            while (pin is null && !copy.IsCompleted && spin.Elapsed < HandshakeCeiling)
            {
                if (!Directory.Exists(upper)) { Thread.SpinWait(20); continue; }
                pin = PinDirectory(upper);
            }
            (pin is not null || copy.IsCompleted).Should().BeTrue(
                $"the created parent '{upper}' never appeared within {HandshakeCeiling.TotalSeconds:0} s, so this "
                + "run tested nothing: the handshake has to happen for the rest of the test to mean anything");
            pin.Should().NotBeNull(
                "the copy finished before the parent could be pinned, so nothing held it open - the test has to "
                + "pin it while the copy is still running, or it proves nothing");

            var thrown = await Record.ExceptionAsync(() => copy);

            // Asserted before the exception type, like its siblings: a run that lost the race has
            // to say so rather than turn into an assertion about a different code path.
            Directory.Exists(dst).Should().BeFalse(
                "the partial destination was removed, so the restore SUCCEEDED and the tidy-up went on to the "
                + "parents - which is the path this test is about");
            var failure = thrown.Should().BeAssignableTo<IOException>(
                    "the caller's failure is that their source file is in use; the tidy-up's own trouble with a "
                    + "directory it made is not theirs to hear about")
                .Which;
            failure.Message.Should().Contain("locked.txt",
                "which file stopped the copy is the whole answer - the caller can act on that one");
            failure.Message.Should().NotContain(upper,
                "naming the parent here would mean RemoveCreated's own failure had replaced the copy's");
            Directory.Exists(upper).Should().BeTrue("a parent that cannot be removed is left where it is");
            Directory.EnumerateFileSystemEntries(upper).Should().BeEmpty(
                "and it is EMPTY - so it was a removal candidate that failed, not one skipped for still holding "
                + "something; without the catch around the removal this test could not tell the two apart");
            Directory.Exists(made).Should().BeTrue(
                "the parent above still holds that one, so it is not empty and never was this call's to remove");
        }
        finally
        {
            hold.Dispose();
            ReleasePin(pin);
        }
    }

    // ---- R4e-4: a junction is a spelling of another VOLUME ---------------------------------------------

    /// <summary>
    /// R4e-4: the destination <c>&lt;tmp&gt;\j2\moved</c> stands on the SECOND volume the junction
    /// points at, while the source <c>&lt;tmp&gt;\tree</c> is on the first — a difference neither
    /// path spells out, since both read as "C:". <c>SameRoot</c> compares the CANONICAL roots for
    /// that reason (round 4e); comparing them as written would say "same volume" and leave
    /// <c>Directory.Move</c>'s refusal uncaught by the cross-volume fallback that exists for
    /// exactly this case. The move is a legitimate one: the caller may not even know the junction
    /// is there.
    /// </summary>
    [Fact]
    public async Task MoveAsync_of_a_directory_through_a_junction_to_a_second_volume_completes()
    {
        var landing = OtherVolumeLanding();
        if (landing is null) return;   // one volume on this box: nothing to move across
        string? link = null;
        try
        {
            var tree = Dir("xvol-junction-tree");
            File_(Path.Combine("xvol-junction-tree", "one.txt"), "one");
            File_(Path.Combine("xvol-junction-tree", "deep", "two.txt"), "two");
            link = Junction("xvol-junction-link", landing);
            var dst = Path.Combine(link, "moved");

            var act = () => Svc().MoveAsync(tree, dst, overwrite: false);

            await act.Should().NotThrowAsync(
                "the destination is on another volume however it is spelled, so the move has to take the "
                + "copy-then-delete path it already has - a junction in the way is not a failure");
            Directory.Exists(tree).Should().BeFalse("a completed move leaves nothing at the source");
            (await File.ReadAllTextAsync(Path.Combine(landing, "moved", "one.txt"))).Should().Be("one",
                "the files land on the other volume, under the directory the junction points at");
            (await File.ReadAllTextAsync(Path.Combine(landing, "moved", "deep", "two.txt"))).Should().Be("two",
                "the whole tree, not just its top level");
        }
        finally
        {
            if (link is not null) try { Directory.Delete(link); } catch { /* best effort */ }
            try { Directory.Delete(landing, true); } catch { /* best effort */ }
        }
    }

    // ---- R4f-1: a drive letter is a root to Directory.Move, whatever it stands for ----------------------

    /// <summary>
    /// The two-level tree every R4f-1 case moves, relative to its own root — enough to tell "the
    /// move completed" from "the top level was copied and the rest was not".
    /// </summary>
    private static readonly string[] MovedTree = ["one.txt", "deep", @"deep\two.txt"];

    /// <summary>Builds <see cref="MovedTree"/> at <paramref name="src"/>, wherever that is.</summary>
    private static async Task WriteMovedTreeAsync(string src)
    {
        Directory.CreateDirectory(Path.Combine(src, "deep"));
        await File.WriteAllTextAsync(Path.Combine(src, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(src, "deep", "two.txt"), "two");
    }

    /// <summary>
    /// R4f-1, the case R4e-4 traded away: <c>Directory.Move</c> compares the roots of the two
    /// paths AS WRITTEN, so <c>Y:\a</c> to <c>&lt;tmp&gt;\landing</c> is refused ("Source and
    /// destination path must have identical roots") even though <c>Y:</c> is a <c>subst</c> alias
    /// of a directory on that very volume and the bytes would never leave it. <c>SameRoot</c> now
    /// compares CANONICAL roots, which are equal here — so <c>when (!SameRoot(src, dst))</c> does
    /// not catch that refusal, the copy-then-delete fallback is skipped, and the caller gets an
    /// IOException about roots for a move Windows would have done either way. Whether the
    /// fallback is needed is <c>Directory.Move</c>'s answer to give, not a root comparison's.
    /// </summary>
    [Fact]
    public async Task MoveAsync_out_of_a_subst_drive_onto_the_same_volume_completes()
    {
        var vol = Dir("subst-out-vol");
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: R4d-6's canary is what fails for this
        var src = Path.Combine(root, "a");
        await WriteMovedTreeAsync(src);
        var dst = Path.Combine(_tmp, "subst-out-landing");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        await act.Should().NotThrowAsync(
            "both ends are on one physical volume, and the letter is the caller's own spelling of a directory "
            + "they may move out of; Directory.Move refuses it on the roots as written, and the copy-then-delete "
            + "fallback exists for precisely the refusal it just raised");
        EntriesUnder(dst).Should().BeEquivalentTo(MovedTree,
            "the whole tree arrives, not just its top level");
        (await File.ReadAllTextAsync(Path.Combine(dst, "deep", "two.txt"))).Should().Be("two",
            "with its content - a move that arrives empty is not a move");
        Directory.Exists(src).Should().BeFalse("a completed move leaves nothing at the source");
        Directory.Exists(Path.Combine(vol, "a")).Should().BeFalse(
            "and nothing under the directory the letter stands for either - one place, two spellings");
    }

    /// <summary>
    /// R4f-1 with the letter at the other end: the destination is <c>Y:\landing2</c> and the
    /// source a plain path on the volume the letter stands for. Same physical volume, same
    /// refusal from <c>Directory.Move</c>, same fallback skipped by a <c>SameRoot</c> that says
    /// "same volume" — spelled the way a caller who keeps a project behind a mapped letter spells
    /// it.
    /// </summary>
    [Fact]
    public async Task MoveAsync_into_a_subst_drive_from_the_same_volume_completes()
    {
        var vol = Dir("subst-in-vol");
        var root = SubstRoot(vol);
        if (root is null) return;   // no free drive letter: R4d-6's canary is what fails for this
        var src = Path.Combine(_tmp, "subst-in-src");
        await WriteMovedTreeAsync(src);
        var dst = Path.Combine(root, "landing2");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        await act.Should().NotThrowAsync(
            "the destination drive IS the source's own volume; a move that refuses itself on the spelling of the "
            + "destination is the same defect the other way round");
        EntriesUnder(Path.Combine(vol, "landing2")).Should().BeEquivalentTo(MovedTree,
            "the tree lands under the directory the letter stands for, whole");
        (await File.ReadAllTextAsync(Path.Combine(dst, "deep", "two.txt"))).Should().Be("two",
            "and reads back through the letter with its content");
        Directory.Exists(src).Should().BeFalse("a completed move leaves nothing at the source");
    }

    /// <summary>
    /// R4f-1 with no second volume anywhere in the picture: TWO letters onto ONE directory. The
    /// roots as written (<c>Y:\</c> and <c>X:\</c>) differ, so <c>Directory.Move</c> refuses; the
    /// canonical roots are the same physical volume, so the fallback is skipped — the pair of
    /// facts that leaves this move with nothing to carry it out. Returns silently when the box has
    /// only one free drive letter: R4d-6's canary requires one, and a second is not something a
    /// box can be held to.
    /// </summary>
    [Fact]
    public async Task MoveAsync_between_two_subst_letters_for_one_directory_completes()
    {
        var vol = Dir("subst-two-vol");
        var from = SubstRoot(vol);
        if (from is null) return;   // no free drive letter: R4d-6's canary is what fails for this
        var to = ExtraSubstRoot(vol);
        if (to is null) return;     // only one free letter on this box: nothing to move between
        var src = Path.Combine(from, "a");
        await WriteMovedTreeAsync(src);
        var dst = Path.Combine(to, "landing3");

        var act = () => Svc().MoveAsync(src, dst, overwrite: false);

        await act.Should().NotThrowAsync(
            "two letters for one directory are two roots to Directory.Move and one volume to SameRoot; between "
            + "them the move is refused by the first and denied the fallback by the second");
        EntriesUnder(Path.Combine(vol, "landing3")).Should().BeEquivalentTo(MovedTree,
            "the tree arrives whole under the one directory both letters stand for");
        (await File.ReadAllTextAsync(Path.Combine(dst, "deep", "two.txt"))).Should().Be("two",
            "with its content, read back through the destination letter");
        Directory.Exists(Path.Combine(vol, "a")).Should().BeFalse(
            "a completed move leaves nothing at the source, under any of its spellings");
    }

    // ---- R4e-5: a file spelled with a trailing separator is still a file --------------------------------

    /// <summary>
    /// R4e-5: <c>File.Exists</c> answers false for <c>&lt;file&gt;\</c> — a trailing separator
    /// means "directory" to Windows — so the R4d-4 sentence is skipped and the caller gets the
    /// framework's "could not find a part of the path", which sends them hunting for a typo in a
    /// path that is perfectly good.
    /// <para>
    /// R4f-3: <c>Path.TrimEndingDirectorySeparator</c> takes ONE separator off, so the same path
    /// with two of them — a joined path where both halves carried one, or a mixed
    /// <c>\/</c> from a caller pasting between shells — still misses the check, and the answer is
    /// "The parameter is incorrect.", which names nothing at all. Every trailing separator comes
    /// off: they are all the same spelling of the same file.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("\\\\")]
    [InlineData("//")]
    [InlineData("\\/")]
    [InlineData("/\\")]
    public async Task ListAsync_of_a_file_spelled_with_a_trailing_separator_says_it_is_not_a_directory(string separator)
    {
        var file = File_("trailing-separator-target.txt", "x");

        var thrown = await Record.ExceptionAsync(() =>
            Svc().ListAsync(file + separator, null, recursive: false, includeHidden: false, maxEntries: 100));

        thrown.Should().NotBeNull("a file has no entries to list, whatever the path ends with");
        thrown!.Message.Should().Contain(file, "the caller has to know WHICH path was wrong");
        thrown.Message.Should().ContainEquivalentOf("not a directory",
            "'could not find a part of the path' is false here - the path is fine and the thing at the end of it "
            + "is a file; the caller needs the same sentence they get without the trailing separator");
    }

    // ---- round 4c helpers ---------------------------------------------------------------------------

    /// <summary>
    /// A directory symlink, remembered so <see cref="Dispose"/> removes it before the subst drive.
    /// Null when this box has neither Developer Mode nor SeCreateSymbolicLink — <c>mklink /J</c> is
    /// no substitute here: a junction cannot point at a subst drive ("local volumes are required").
    /// </summary>
    private string? Symlink(string linkName, string target)
    {
        var link = Path.Combine(_tmp, linkName);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        _junctions.Add(link);
        return link;
    }

    /// <summary>
    /// A live process whose WORKING DIRECTORY is <paramref name="directory"/>. Windows refuses to
    /// remove a directory that is a process's working directory, so this pins one open the way a
    /// caller's own shell or editor does — and unlike a file handle it survives the directory being
    /// emptied first, which is exactly what a failed copy's cleanup does. <c>pause</c> blocks on a
    /// redirected stdin nobody writes to; <see cref="ReleasePin"/> ends it.
    /// </summary>
    private static Process PinDirectory(string directory) =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

    private static void ReleasePin(Process? pin)
    {
        if (pin is null) return;
        try { pin.Kill(entireProcessTree: true); pin.WaitForExit(10_000); }
        catch { /* already gone */ }
        pin.Dispose();
    }

    /// <summary>The files directly under <paramref name="dir"/>, or 0 once it has gone — the poll a cancel races against.</summary>
    private static int FileCount(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Count(); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException) { return 0; }
    }
}
