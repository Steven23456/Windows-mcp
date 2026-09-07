using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R4d-1, the pure half: <see cref="PathCanonical.Canonical"/> against a faked
/// <see cref="IFinalPathNative"/>, so the comparison that decides whether a destination is inside
/// its source can be tested without a second volume. The service-level half — a real
/// <c>subst</c> drive standing in for a mapped network drive — is in
/// <see cref="FileSystemServiceRound4Tests"/>; neither test is worth much without the other, which
/// is why both exist (a mocked collaborator is not evidence that the real one behaves).
/// </summary>
[Trait("Category", "Unit")]
public class PathCanonicalTests
{
    /// <summary>
    /// Windows' answer, scripted: a map from a spelling to the final path
    /// <c>GetFinalPathNameByHandle</c> would report for it. Anything NOT in the map answers
    /// <c>null</c> — the seam's "Windows cannot say", which is what the real one returns for a
    /// path that is not there (the open fails). So the map doubles as this fake's notion of what
    /// exists, and the segments below the deepest mapped ancestor are the tail.
    /// </summary>
    internal sealed class FakeFinalPath : IFinalPathNative
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every path the canonicaliser asked about, in order.</summary>
        public List<string> Asked { get; } = new();

        public FakeFinalPath Map(string spelling, string finalPath)
        {
            _map[Path.TrimEndingDirectorySeparator(spelling)] = finalPath;
            return this;
        }

        public string? FinalPathOf(string existingDirectory)
        {
            Asked.Add(existingDirectory);
            return _map.TryGetValue(Path.TrimEndingDirectorySeparator(existingDirectory), out var final)
                ? final
                : null;
        }
    }

    /// <summary>A subst drive: <c>Z:\vol</c> IS <c>C:\real\vol</c>, and both spellings are there.</summary>
    private static FakeFinalPath AliasedVolume() => new FakeFinalPath()
        .Map(@"Z:\vol", @"C:\real\vol")
        .Map(@"C:\real\vol", @"C:\real\vol");

    /// <summary>
    /// The one that matters: <c>Z:\vol\project\backup</c> is the volume's
    /// <c>C:\real\vol\project\backup</c>, even though only the mount point itself exists. Without
    /// this, a copy of <c>C:\real\vol\project</c> into <c>Z:\vol\project\backup</c> reads as two
    /// unrelated trees and runs away into its own subtree (PR #25, through a drive letter).
    /// </summary>
    [Fact]
    public void Canonical_resolves_a_drive_alias_to_the_volumes_own_spelling()
    {
        var native = AliasedVolume();

        PathCanonical.Canonical(@"Z:\vol\project\backup", native)
            .Should().Be(@"C:\real\vol\project\backup",
                "the deepest ancestor the volume can speak for is Z:\\vol, and the segments below it are appended as written");
    }

    /// <summary>R4d-1: the two spellings have to COMPARE equal — that is all the containment check asks.</summary>
    [Fact]
    public void Canonical_equates_the_two_spellings_of_the_same_path()
    {
        var native = AliasedVolume();

        PathCanonical.Canonical(@"Z:\vol\project\backup", native)
            .Should().Be(PathCanonical.Canonical(@"C:\real\vol\project\backup", native),
                "a subst drive is a second name for one directory; a check that cannot see that is not a check");
    }

    /// <summary>The path itself is the deepest existing ancestor: nothing to append.</summary>
    [Fact]
    public void Canonical_of_a_directory_the_volume_can_speak_for_is_its_final_path()
    {
        var native = AliasedVolume();

        PathCanonical.Canonical(@"Z:\vol", native).Should().Be(@"C:\real\vol",
            "the mount point resolves to its target, not to itself");
    }

    /// <summary>
    /// Trailing separators are spelling, not structure: <c>Z:\vol\project\</c> and
    /// <c>Z:\vol\project</c> are one path, and <c>IsInside</c> compares against
    /// <c>parent + '\'</c> — a canonical form that kept the separator would make a path look
    /// like its own child.
    /// </summary>
    [Theory]
    [InlineData(@"Z:\vol\project\")]
    [InlineData(@"Z:\vol\project")]
    public void Canonical_trims_a_trailing_separator(string spelling)
    {
        var native = AliasedVolume();

        PathCanonical.Canonical(spelling, native).Should().Be(@"C:\real\vol\project");
    }

    /// <summary>
    /// The DEEPEST ancestor wins, not the first one found: a junction below the mount point
    /// (<c>Z:\vol\project → D:\elsewhere\project</c>) redirects everything under it, so resolving
    /// only <c>Z:\vol</c> and appending <c>project\backup</c> would name a directory that is not
    /// the one the caller reaches.
    /// </summary>
    [Fact]
    public void Canonical_takes_the_deepest_ancestor_the_volume_can_speak_for()
    {
        var native = AliasedVolume().Map(@"Z:\vol\project", @"D:\elsewhere\project");

        PathCanonical.Canonical(@"Z:\vol\project\backup", native)
            .Should().Be(@"D:\elsewhere\project\backup",
                "the link deeper in the path is the one that decides where 'backup' would land");
    }

    /// <summary>
    /// Nothing on this path can be opened — every segment is still to be created. The answer is
    /// the path as written (trimmed), never an empty string and never a guess: a canonicaliser
    /// that dropped the unresolved tail would compare a destination against its own parent.
    /// </summary>
    [Fact]
    public void Canonical_of_a_path_the_volume_cannot_speak_for_is_the_path_as_written()
    {
        var native = new FakeFinalPath();

        PathCanonical.Canonical(@"C:\nowhere\at\all\", native).Should().Be(@"C:\nowhere\at\all");
    }

    /// <summary>
    /// The walk up the ancestors has to stop at the root — a drive root and a share root both.
    /// <c>C:</c> alone is drive-RELATIVE (the current directory on C:), so trimming the root's
    /// separator would turn a path into a different one; and a walk that does not terminate here
    /// hangs the copy instead of refusing it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\host\share\a\b", @"\\host\share\a\b")]
    public void Canonical_walks_up_no_further_than_the_root(string spelling, string expected)
    {
        var native = new FakeFinalPath();

        PathCanonical.Canonical(spelling, native).Should().Be(expected);
    }

    /// <summary>
    /// The seam is asked about the path itself before anything else: it is the only ancestor whose
    /// answer can be complete, and asking further up is wasted work (and, for a real handle open,
    /// wasted I/O) once it has answered.
    /// </summary>
    [Fact]
    public void Canonical_asks_the_volume_about_the_path_itself_first()
    {
        var native = AliasedVolume();

        PathCanonical.Canonical(@"Z:\vol", native);

        native.Asked.Should().NotBeEmpty("the canonical path is the volume's answer, not a string transform");
        Path.TrimEndingDirectorySeparator(native.Asked[0]).Should().Be(@"Z:\vol",
            "the deepest candidate is the path itself; only when it cannot be opened does its parent matter");
    }
}
