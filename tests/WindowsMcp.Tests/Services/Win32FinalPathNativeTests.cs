using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R4d-1, the production half of the seam: <see cref="Win32FinalPathNative"/> against the real
/// <c>CreateFile</c> + <c>GetFinalPathNameByHandle</c>. <see cref="PathCanonicalTests"/> tests the
/// walk against a scripted fake, which proves the arithmetic and nothing about Windows — a fake
/// that answers whatever the test wants would keep a seam that always returned <c>null</c> green,
/// and a canonicaliser that always answers "the path as written" is the PR #25 runaway back again.
/// This class is where the answers are Windows' own.
/// <para>
/// In <see cref="FileSystemVolumesCollection"/> because it creates a <c>subst</c> drive, and the
/// sibling classes pick free drive letters out from under each other otherwise.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(FileSystemVolumesCollection.Name)]
public class Win32FinalPathNativeTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-finalpath-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];
    private string? _substLetter;

    private static IFinalPathNative Native => Win32FinalPathNative.Instance;

    public Win32FinalPathNativeTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var link in _junctions)
            try { Directory.Delete(link); } catch { /* best effort: gone already, or never created */ }
        if (_substLetter is not null) RunCmd($"subst {_substLetter} /D");
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_tmp)) Directory.Delete(@"\\?\" + Path.GetFullPath(_tmp), true); }
        catch { /* the deep tree below can outrun the plain form */ }
        GC.SuppressFinalize(this);
    }

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

    private string Dir(string name)
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A drive letter of our own, mapped onto a directory under <c>_tmp</c>; null when every letter is taken.</summary>
    private string? SubstRoot(string targetDirectory)
    {
        foreach (var letter in "YXWVUTSRQPNMKJIHGF".Select(c => c + ":"))
        {
            if (Directory.Exists(letter + "\\")) continue;
            var (exit, _) = RunCmd($"subst {letter} \"{targetDirectory}\"");
            if (exit != 0) continue;
            _substLetter = letter;
            return letter + "\\";
        }
        return null;   // no free drive letter: FileSystemServiceRound4Tests' canary is what fails for that
    }

    /// <summary>A directory junction (no elevation needed), removed in <see cref="Dispose"/>.</summary>
    private string Junction(string linkName, string target)
    {
        var link = Path.Combine(_tmp, linkName);
        var (exit, output) = RunCmd($"mklink /J \"{link}\" \"{target}\"");
        exit.Should().Be(0, "mklink /J has to create the junction this test is about: {0}", output);
        _junctions.Add(link);
        return link;
    }

    // ---- what the seam is for ------------------------------------------------------------------

    /// <summary>
    /// The reason the seam exists: a <c>subst</c> drive (a mapped network drive behaves the same
    /// way) is a second spelling of one directory, and every string comparison in the containment
    /// check sees two unrelated trees. Windows knows better, and this is how it is asked.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_subst_path_is_the_directory_it_maps_to()
    {
        var target = Dir("subst-target");
        var inside = Directory.CreateDirectory(Path.Combine(target, "project")).FullName;
        var root = SubstRoot(target);
        if (root is null) return;   // no free drive letter on this box

        var final = Native.FinalPathOf(Path.Combine(root, "project"));

        final.Should().NotBeNull("the drive exists and so does the directory behind the letter");
        Path.TrimEndingDirectorySeparator(final!).Should().Be(Path.TrimEndingDirectorySeparator(inside),
            "the alias resolves to the volume's own spelling - that equality IS the containment check");
    }

    /// <summary>
    /// The root of the aliased drive is not a root on the volume: it is the directory <c>subst</c>
    /// pointed the letter at. That is why the leaf-link-to-a-root refusal
    /// (<c>FileSystemService.RefuseLeafLinkToRoot</c>) cannot be built on this seam — the final
    /// path of "a whole volume" comes back looking like an ordinary directory.
    /// </summary>
    [Fact]
    public void FinalPathOf_the_root_of_an_aliased_drive_is_the_target_directory()
    {
        var target = Dir("subst-root-target");
        var root = SubstRoot(target);
        if (root is null) return;

        var final = Native.FinalPathOf(root);

        Path.TrimEndingDirectorySeparator(final!).Should().Be(Path.TrimEndingDirectorySeparator(target),
            "'Y:\\' is not a volume root - it is this directory, and the seam says so");
    }

    /// <summary>A junction resolves to its target, which is the other half of what the check has to see through.</summary>
    [Fact]
    public void FinalPathOf_a_junction_is_its_target()
    {
        var target = Dir("junction-target");
        var link = Junction("junction-link", target);

        var final = Native.FinalPathOf(link);

        Path.TrimEndingDirectorySeparator(final!).Should().Be(Path.TrimEndingDirectorySeparator(target),
            "a junction is a spelling of its target, not a directory of its own");
    }

    /// <summary>
    /// An ordinary directory answers with itself — and without the <c>\\?\</c> the API returns it
    /// under. The comparison is against <c>Path.GetFullPath</c>'s output, which never carries the
    /// prefix: a canonical form that did would make every aliased path differ from every plain one
    /// and turn the containment check into a coin toss.
    /// </summary>
    [Fact]
    public void FinalPathOf_an_ordinary_directory_is_itself_without_the_extended_prefix()
    {
        var dir = Dir("plain");

        var final = Native.FinalPathOf(dir);

        final.Should().NotStartWith(@"\\?\", "the caller compares against GetFullPath, which has no prefix");
        Path.TrimEndingDirectorySeparator(final!).Should().Be(Path.TrimEndingDirectorySeparator(dir));
    }

    /// <summary>
    /// A drive root is its own final path, separator and all: <c>PathCanonical</c> walks up to the
    /// root and asks about it, and an answer of <c>C:</c> there would be a drive REFERENCE — a
    /// different path (whatever the process's current directory on C: happens to be).
    /// </summary>
    [Fact]
    public void FinalPathOf_a_drive_root_is_the_root()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_tmp))!;

        Native.FinalPathOf(root).Should().Be(root, "the root of a real volume is spelled 'C:\\', not 'C:'");
    }

    /// <summary>
    /// A file, not a directory: <c>Canonical</c> is called on copy and move sources, and most of
    /// those are files. A seam that only spoke for directories would answer <c>null</c> for every
    /// file copy and fall back to the path as written — which is the bug the round is about.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_file_is_the_file()
    {
        var file = Path.Combine(_tmp, "a-file.txt");
        File.WriteAllText(file, "x");

        var final = Native.FinalPathOf(file);

        final.Should().Be(file, "FILE_FLAG_BACKUP_SEMANTICS allows a directory handle; it does not forbid a file one");
    }

    /// <summary>
    /// The handle is opened with NO access rights, which is what lets the canonicaliser speak for
    /// a file another process holds exclusively. A copy of a locked source still has to be
    /// containment-checked before it fails on the lock, so this must not answer <c>null</c>.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_file_held_exclusively_still_resolves()
    {
        var file = Path.Combine(_tmp, "locked.txt");
        File.WriteAllText(file, "x");

        using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);

        Native.FinalPathOf(file).Should().Be(file,
            "asking for no access rights is what makes this safe on a file somebody else is using");
    }

    /// <summary>
    /// "Windows cannot say" is <c>null</c>, never an exception and never a guess: the walk in
    /// <see cref="PathCanonical"/> uses it to mean "this ancestor does not exist, try its parent",
    /// so a throw here would fail every copy to a destination that does not exist yet — which is
    /// most of them.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData(@"missing\deeper\still")]
    public void FinalPathOf_a_path_that_is_not_there_is_null(string relative)
    {
        var missing = Path.Combine(_tmp, relative);

        Native.FinalPathOf(missing).Should().BeNull("nothing can be opened there, so the volume has no spelling for it");
    }

    /// <summary>
    /// A path Windows will not even parse (a reserved character) is the same answer as a missing
    /// one: <c>null</c>. The canonicaliser has no error channel — an exception out of the seam
    /// would leave a refusal looking like a crash.
    /// </summary>
    [Theory]
    [InlineData(@"C:\wmcp|not-a-path")]
    [InlineData("")]
    public void FinalPathOf_a_path_windows_will_not_open_is_null(string path)
    {
        Native.FinalPathOf(path).Should().BeNull("the seam's only answers are a path and 'I cannot say'");
    }

    /// <summary>
    /// Over 1 024 characters the stack buffer cannot hold the answer and the second, larger call
    /// is what returns it. Silently truncating instead would produce a canonical path that is a
    /// PREFIX of the real one — and a prefix is exactly what <c>IsInside</c> tests for, so a
    /// destination would start reading as inside its source at random.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_path_longer_than_the_stack_buffer_is_complete()
    {
        var deep = Path.GetFullPath(_tmp);
        var component = new string('d', 200);
        for (var i = 0; i < 6; i++)
        {
            deep = Path.Combine(deep, component);
            Directory.CreateDirectory(@"\\?\" + deep);
        }
        deep.Length.Should().BeGreaterThan(1024, "the point of this test is the buffer that is 1 024 characters long");

        var final = Native.FinalPathOf(@"\\?\" + deep);

        final.Should().Be(deep, "the whole path comes back, not the first 1 024 characters of it");
    }

    // ---- R4e-3: the seam is long-path safe on a PLAIN path ---------------------------------------

    /// <summary>
    /// A directory nested until its plain path is past <c>MAX_PATH</c>. The directories are made
    /// through the extended-length form (the BCL's own long-path spelling); what the test then
    /// hands the seam is the plain one, because that is what <c>FileSystemService</c> hands it —
    /// <c>PlainPath</c> strips the prefix before anything else looks at the path.
    /// </summary>
    private string DeepPlainDirectory(int atLeast, int componentLength)
    {
        var deep = Path.GetFullPath(_tmp);
        var component = new string('d', componentLength);
        while (deep.Length < atLeast)
        {
            deep = Path.Combine(deep, component);
            Directory.CreateDirectory(@"\\?\" + deep);
        }
        return deep;
    }

    /// <summary>
    /// R4e-3: the seam opens the path as it is given, and <c>CreateFileW</c> stops at
    /// <c>MAX_PATH</c> unless the path is in its extended-length form. An ancestor beyond 260
    /// characters therefore answers <c>null</c> — "Windows cannot say" — and
    /// <c>PathCanonical.Canonical</c> falls back to the path as written, which is the containment
    /// check comparing spellings again: a junction at that depth stops being seen through.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_junction_at_a_plain_path_past_max_path_is_its_target()
    {
        var target = Dir("deep-junction-target");
        var deep = DeepPlainDirectory(atLeast: 240, componentLength: 60);
        // mklink runs through cmd.exe, which is not long-path aware; the junction is made at a
        // short path and moved into place with Directory.Move, which is.
        var staging = Path.Combine(_tmp, "deep-junction-staging");
        var (exit, output) = RunCmd($"mklink /J \"{staging}\" \"{target}\"");
        exit.Should().Be(0, "mklink /J has to create the junction this test is about: {0}", output);
        var link = Path.Combine(deep, "deep-junction-link");
        Directory.Move(staging, link);
        _junctions.Add(link);
        link.Length.Should().BeGreaterThan(260, "the whole point of this test is a path past MAX_PATH");

        var final = Native.FinalPathOf(link);

        Path.TrimEndingDirectorySeparator(final ?? string.Empty).Should().Be(
            Path.TrimEndingDirectorySeparator(target),
            "a junction is a spelling of its target at any depth; 'I cannot say' here turns the containment "
            + "check back into the string comparison this seam exists to replace");
    }

    /// <summary>
    /// R4e-3 and the buffer test in one: the plain sibling of
    /// <see cref="FinalPathOf_a_path_longer_than_the_stack_buffer_is_complete"/>. That one passes
    /// the extended-length form, which is the only reason <c>CreateFile</c> opens it — a plain
    /// path of the same length is what a caller actually writes.
    /// </summary>
    [Fact]
    public void FinalPathOf_a_plain_path_longer_than_the_stack_buffer_is_complete()
    {
        var deep = DeepPlainDirectory(atLeast: 1025, componentLength: 200);
        deep.Length.Should().BeGreaterThan(1024, "the point of this test is the buffer that is 1 024 characters long");

        var final = Native.FinalPathOf(deep);

        final.Should().Be(deep,
            "the plain form is what the service passes in, and the answer must be the whole path - a truncated "
            + "one is a PREFIX, and a prefix is exactly what IsInside tests for");
    }
}
