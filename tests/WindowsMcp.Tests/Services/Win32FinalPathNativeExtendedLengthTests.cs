using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-1 R4e, the pure half of <see cref="Win32FinalPathNative"/>: the extended-length prefix
/// arithmetic on its own, with no volume involved. <see cref="Win32FinalPathNativeTests"/> is where
/// the answers are Windows' own, but it cannot pin this — the previous GREEN pass found that this
/// box has <c>LongPathsEnabled</c>, so a plain path past <c>MAX_PATH</c> opens whether or not the
/// prefix is applied and the long-path integration tests stay green either way. The three branches
/// are therefore asserted here as string-to-string, where a machine-wide registry setting cannot
/// hide a wrong one.
/// </summary>
[Trait("Category", "Unit")]
public class Win32FinalPathNativeExtendedLengthTests
{
    /// <summary>
    /// The two spellings that get a prefix. A drive path takes <c>\\?\</c>; a UNC path cannot —
    /// <c>\\?\</c> followed by <c>\\host\share</c> is not a path Windows opens — so its two leading
    /// separators are replaced by <c>\\?\UNC\</c>. Getting the UNC form wrong is invisible until
    /// someone copies through a file share, which is where the containment check most needs the
    /// seam to answer.
    /// </summary>
    [Theory]
    [InlineData(@"C:\a\b", @"\\?\C:\a\b")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"\\host\share\x", @"\\?\UNC\host\share\x")]
    [InlineData(@"\\host\share", @"\\?\UNC\host\share")]
    [InlineData("", @"\\?\")]
    public void ExtendedLength_prefixes_a_path_that_does_not_have_one(string path, string expected)
    {
        Win32FinalPathNative.ExtendedLength(path).Should().Be(expected,
            "the seam opens the path as it is given, and CreateFileW stops at MAX_PATH unless the "
            + "path is in its extended-length form");
    }

    /// <summary>
    /// The gate, and it has to be checked BEFORE the UNC branch: <c>\\?\UNC\host\share</c> starts
    /// with two separators as well, so a UNC-first ordering would answer
    /// <c>\\?\UNC\?\UNC\host\share</c> and every open of an already-extended path would fail. A
    /// device path (<c>\\.\</c>) is already a form the kernel takes literally and must not be
    /// rewritten either.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\C:\a")]
    [InlineData(@"\\?\UNC\host\share")]
    [InlineData(@"\\.\pipe\x")]
    public void ExtendedLength_leaves_a_path_that_already_has_one_alone(string path)
    {
        Win32FinalPathNative.ExtendedLength(path).Should().Be(path,
            "a prefix applied twice is not a longer path, it is a path that does not exist");
    }

    /// <summary>
    /// "Never doubled" as a property rather than a table: whatever the spelling, applying the
    /// transform a second time changes nothing. <c>PathCanonical</c> walks ancestors and hands each
    /// one back to the seam, so a form that grew on every pass would compound.
    /// </summary>
    [Theory]
    [InlineData(@"C:\a\b")]
    [InlineData(@"\\host\share\x")]
    [InlineData(@"\\?\C:\a")]
    [InlineData(@"\\?\UNC\host\share")]
    [InlineData(@"\\.\pipe\x")]
    public void ExtendedLength_applied_twice_is_the_same_as_once(string path)
    {
        var once = Win32FinalPathNative.ExtendedLength(path);

        Win32FinalPathNative.ExtendedLength(once).Should().Be(once,
            "the extended-length form is a fixed point of its own transform");
    }
}
