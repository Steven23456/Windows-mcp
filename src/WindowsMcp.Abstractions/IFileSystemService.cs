using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IFileSystemService
{
    Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct = default);
    Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default);
    Task WriteTextAsync(string path, string content, string encoding, CancellationToken ct = default);
    Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct = default);
    Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct = default);
    Task CopyAsync(string src, string dst, CancellationToken ct = default);
    Task MoveAsync(string src, string dst, CancellationToken ct = default);
    Task DeleteAsync(string path, CancellationToken ct = default);
    Task<string[]> ListAsync(string path, CancellationToken ct = default);
    Task ZipAsync(string srcDir, string dstZip, CancellationToken ct = default);
    Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct = default);
}
