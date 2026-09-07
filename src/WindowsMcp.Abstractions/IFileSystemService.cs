using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IFileSystemService
{
    Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct = default);
    /// <summary>C-1: a line window of the decoded file. <paramref name="offsetLines"/> is 1-based
    /// (0 and 1 both mean the first line); <paramref name="limitLines"/> 0 means to the end.
    /// <paramref name="maxBytes"/> still bounds the FILE, not the window.</summary>
    Task<TextWindow> ReadLinesAsync(string path, long maxBytes, string encoding, int offsetLines, int limitLines, CancellationToken ct = default);
    Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default);
    /// <summary>C-1: <paramref name="append"/> opens for append (no temp-file rename);
    /// <paramref name="createParents"/> false refuses a missing parent directory by name.</summary>
    Task WriteTextAsync(string path, string content, string encoding, bool append, bool createParents, CancellationToken ct = default);
    Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct = default);
    Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct = default);
    /// <summary>Hex digest of a file. <paramref name="algorithm"/>: sha256 (default), sha1, or md5.</summary>
    Task<string> HashFileAsync(string path, string algorithm = "sha256", CancellationToken ct = default);
    /// <summary>C-1 R2: refuses an existing destination unless <paramref name="overwrite"/>;
    /// a directory source copies the tree.</summary>
    Task CopyAsync(string src, string dst, bool overwrite, CancellationToken ct = default);
    /// <summary>C-1 R2: refuses an existing destination unless <paramref name="overwrite"/>;
    /// falls back to copy-then-delete across volumes.</summary>
    Task MoveAsync(string src, string dst, bool overwrite, CancellationToken ct = default);
    /// <summary>C-1 R2: refuses a NON-EMPTY directory unless <paramref name="recursive"/>;
    /// a file and an empty directory go without it.</summary>
    Task DeleteAsync(string path, bool recursive, CancellationToken ct = default);
    /// <summary>C-1 R3: entries of a directory. <paramref name="pattern"/> is a name glob;
    /// hidden AND system entries are skipped unless <paramref name="includeHidden"/>, and
    /// recursion does not descend into skipped directories.</summary>
    Task<FileEntry[]> ListAsync(string path, string? pattern, bool recursive, bool includeHidden, CancellationToken ct = default);
    Task ZipAsync(string srcDir, string dstZip, CancellationToken ct = default);
    Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct = default);
}
