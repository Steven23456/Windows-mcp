using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IFileStreamService
{
    /// <summary>NTFS alternate data streams + reparse (symlink/junction) target for a path.</summary>
    Task<FileStreamsDto> GetStreamsAsync(string path, CancellationToken ct = default);
}
