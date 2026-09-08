using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R4d-1, the joint between the two halves: the service's containment check has to run on the
/// canonical paths, which means <c>FileSystemService</c> has to actually ASK the injected
/// <see cref="IFinalPathNative"/>. <see cref="PathCanonicalTests"/> proves the walk,
/// <see cref="Win32FinalPathNativeTests"/> proves Windows' answers, and
/// <c>FileSystemServiceRound4Tests.Copy_and_move_refuse_a_destination_inside_the_source_through_a_drive_alias</c>
/// proves the whole thing end to end with a real <c>subst</c> drive — but only on a box with a
/// free drive letter. These two run anywhere, in milliseconds, and fail if the wiring is cut.
/// <para>
/// Nothing here touches the disk: the refusal is taken before the source is even looked for, which
/// is R4-1 ("verify before mutating") and is asserted by the paths below not existing.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class FileSystemServiceSeamTests
{
    private static readonly string Real = Path.Combine(Path.GetTempPath(), "wmcp-seam-real-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// A drive letter that may well exist on the box (every letter is somebody's) with a leaf name
    /// that will not: the seam decides what these two spellings mean, and nothing else may.
    /// </summary>
    private static readonly string Alias = @"Q:\wmcp-seam-alias-" + Guid.NewGuid().ToString("N");

    /// <summary>The two spellings the seam equates: <c>Q:\aliased</c> IS <paramref name="real"/>.</summary>
    private static PathCanonicalTests.FakeFinalPath Aliased(string real) => new PathCanonicalTests.FakeFinalPath()
        .Map(Alias, real)
        .Map(real, real);

    /// <summary>
    /// The destination is inside the source, visible only once both ends are the volume's own
    /// spelling. A service that canonicalised with string transforms alone would compare
    /// <c>Q:\aliased\backup</c> against a temp path, find them unrelated, and copy a tree into its
    /// own subtree.
    /// </summary>
    [Fact]
    public async Task CopyAsync_refuses_a_destination_inside_the_source_as_the_seam_spells_them()
    {
        var svc = new FileSystemService(Aliased(Real));

        var act = () => svc.CopyAsync(Real, Path.Combine(Alias, "backup"), overwrite: false);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "the two spellings are one directory, so this destination is inside the source"))
            .Which.Message.Should().ContainEquivalentOf("inside");
        Directory.Exists(Real).Should().BeFalse("the refusal is taken before anything is created or looked for");
    }

    /// <summary>The same directory under two names is not a copy anybody can mean; it is the same path.</summary>
    [Fact]
    public async Task MoveAsync_refuses_the_same_path_as_the_seam_spells_it()
    {
        var svc = new FileSystemService(Aliased(Real));

        var act = () => svc.MoveAsync(Alias, Real, overwrite: true);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().ContainEquivalentOf("same path",
                "'already exists, pass overwrite:true' would invite the caller to replace a directory with itself");
    }

    /// <summary>
    /// And the seam is what decides it: the same two paths, with a seam that speaks for neither,
    /// are two unrelated directories and the call gets as far as looking for the source. Without
    /// this, both tests above would pass against a service that refused every copy.
    /// </summary>
    [Fact]
    public async Task Two_unaliased_paths_are_not_the_same_path()
    {
        var svc = new FileSystemService(new PathCanonicalTests.FakeFinalPath());

        var act = () => svc.MoveAsync(Alias, Real, overwrite: true);

        await act.Should().ThrowAsync<FileNotFoundException>(
            "nothing links these two spellings, so the next check is the one that asks whether the source exists");
    }
}
