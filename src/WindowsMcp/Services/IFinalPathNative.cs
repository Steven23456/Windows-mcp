namespace WindowsMcp.Services;

/// <summary>
/// C-1 R4d-1: the seam <see cref="PathCanonical"/> asks for the volume's own spelling of a
/// directory — <c>GetFinalPathNameByHandle</c> (<c>VOLUME_NAME_DOS</c>) on a handle opened with
/// backup semantics. A <c>subst</c> or mapped network drive resolves to its target, a junction or
/// symlink to its target, an ordinary path to itself. Internal so the comparison in
/// <see cref="PathCanonical"/> can be unit-tested without a second volume.
/// </summary>
internal interface IFinalPathNative
{
    /// <summary>
    /// The volume's own spelling of <paramref name="existingDirectory"/>, or <c>null</c> when
    /// Windows cannot say — the directory is not there, or the handle could not be opened. A
    /// caller that gets <c>null</c> must fall back to the path as written rather than guess.
    /// </summary>
    string? FinalPathOf(string existingDirectory);
}
