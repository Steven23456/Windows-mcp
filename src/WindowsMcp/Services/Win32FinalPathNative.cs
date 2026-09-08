using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace WindowsMcp.Services;

/// <summary>
/// C-1 round 4d: the production <see cref="IFinalPathNative"/>. Opens the directory with backup
/// semantics and no access rights (a handle, not a read) and asks
/// <c>GetFinalPathNameByHandle</c> for the normalised DOS spelling — which resolves a
/// <c>subst</c> or mapped drive, a junction and a symlink alike. Null when the path does not
/// exist or Windows will not open it.
/// </summary>
internal sealed class Win32FinalPathNative : IFinalPathNative
{
    internal static Win32FinalPathNative Instance { get; } = new();

    public string? FinalPathOf(string existingDirectory)
    {
        using var handle = PInvoke.CreateFile(
            ExtendedLength(existingDirectory),
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null);
        if (handle.IsInvalid) return null;

        Span<char> buffer = stackalloc char[1024];
        uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer,
            GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS);
        if (length == 0) return null;
        string final;
        if (length > buffer.Length)
        {
            var larger = new char[length + 1];
            length = PInvoke.GetFinalPathNameByHandle(handle, larger,
                GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS);
            if (length == 0 || length > larger.Length) return null;
            final = new string(larger, 0, (int)length);
        }
        else
        {
            final = new string(buffer[..(int)length]);
        }

        // The DOS form comes back extended-length: \\?\C:\… or \\?\UNC\host\share\…
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) return final[4..];
        return final;
    }

    /// <summary>
    /// Round 4e: opened in the extended-length form, so an ancestor beyond 260 characters is
    /// resolved whether or not the box has long paths enabled. Never doubled; a UNC path takes
    /// the <c>\\?\UNC\</c> spelling.
    /// </summary>
    internal static string ExtendedLength(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }
}
