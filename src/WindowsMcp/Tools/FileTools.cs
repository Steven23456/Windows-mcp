using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class FileTools
{
    private readonly IFileSystemService _fs;
    private readonly IInputService _input;
    private readonly IFileStreamService _streams;

    public FileTools(IFileSystemService fs, IInputService input, IFileStreamService streams)
    {
        _fs = fs;
        _input = input;
        _streams = streams;
    }

    [McpServerTool(Title = "Search files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Search for files. root: starting directory. pattern: glob (e.g. '*.txt'). min_size: bytes. modified_since: ISO 8601 datetime. find_duplicates: group identical files.")]
    public async Task<string> FileSearch(
        [Description("Root directory to search from")] string root,
        [Description("Glob pattern, e.g. '*.txt'")] string? pattern = null,
        [Description("Minimum file size in bytes")] long? min_size = null,
        [Description("Only files modified since this datetime (ISO 8601)")] string? modified_since = null,
        [Description("Group results by content hash to find duplicates")] bool find_duplicates = false,
        CancellationToken ct = default)
    {
        RequireAbsolute(root, "root");
        DateTime? since = null;
        if (!string.IsNullOrWhiteSpace(modified_since))
        {
            if (!DateTime.TryParse(modified_since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new ArgumentException($"'modified_since' must be a valid ISO 8601 datetime, got: '{modified_since}'");
            since = parsed;
        }

        var hits = await _fs.SearchAsync(root, pattern, min_size, since, find_duplicates, ct);
        return JsonSerializer.Serialize(hits);
    }

    [McpServerTool(Title = "Manage files", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("File operations: copy, move, delete, list. Paths must be absolute. copy/move refuse an existing " +
                 "destination unless overwrite:true (a directory is copied as a tree; a move across volumes is a " +
                 "copy then a delete). delete requires confirm:true and refuses a non-empty directory unless " +
                 "recursive:true. list returns [{Path, Name, IsDirectory, Size, Modified, Hidden}] — pattern is a " +
                 "name glob ('*.txt', case-insensitive, files and directories), recursive descends, and hidden or " +
                 "system entries are skipped unless include_hidden:true.")]
    public async Task<string> FileManage(
        [Description("Action: copy, move, delete, list")] string action,
        [Description("Source path (absolute)")] string src,
        [Description("Destination path (absolute; required for copy/move)")] string? dst = null,
        [Description("Must be true to confirm destructive delete action")] bool confirm = false,
        [Description("copy/move: replace an existing destination (default false: refused)")] bool overwrite = false,
        [Description("delete: remove a non-empty directory and everything under it (default false: refused); list: descend into sub-directories")] bool recursive = false,
        [Description("list: name glob such as '*.log' (case-insensitive); default every entry")] string? pattern = null,
        [Description("list: include hidden and system entries (default false)")] bool include_hidden = false,
        CancellationToken ct = default)
    {
        RequireAbsolute(src, "src");
        switch (action.ToLowerInvariant())
        {
            case "copy":
                if (string.IsNullOrWhiteSpace(dst))
                    throw new ArgumentException("'copy' requires dst");
                RequireAbsolute(dst, "dst");
                await _fs.CopyAsync(src, dst, overwrite, ct);
                return $"copied '{src}' to '{dst}'";

            case "move":
                if (string.IsNullOrWhiteSpace(dst))
                    throw new ArgumentException("'move' requires dst");
                RequireAbsolute(dst, "dst");
                await _fs.MoveAsync(src, dst, overwrite, ct);
                return $"moved '{src}' to '{dst}'";

            case "delete":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for delete");
                await _fs.DeleteAsync(src, recursive, ct);
                return $"deleted '{src}'";

            case "list":
                var entries = await _fs.ListAsync(src, pattern, recursive, include_hidden, ct);
                return JsonSerializer.Serialize(entries);

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected copy|move|delete|list");
        }
    }

    [McpServerTool(Title = "Type into file dialog", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Type a file path into a focused open/save dialog.")]
    public async Task<string> FileDialog(
        [Description("File path to type into the active open/save dialog")] string path)
    {
        await _input.TypeAsync(path);
        return "typed path into focused dialog";
    }

    [McpServerTool(Title = "Read file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Read a file as text (absolute path). Whole file by default. With offset_lines (1-based; 0 and 1 " +
                 "both mean the first line) and/or limit_lines (0 = to the end) it returns a line window as " +
                 "{path, totalLines, offset, returned, truncated, content} — use it to page a large log instead of " +
                 "raising max_bytes, which still bounds the file's size.")]
    public async Task<string> FileRead(
        [Description("File path to read (absolute)")] string path,
        [Description("Maximum file size in bytes; larger files are refused")] long max_bytes = 1048576,
        [Description("Text encoding: auto, utf-8, utf-16, ascii")] string encoding = "auto",
        [Description("First line of the window, 1-based (0 = from the top)")] int offset_lines = 0,
        [Description("Lines in the window (0 = to the end)")] int limit_lines = 0,
        CancellationToken ct = default)
    {
        RequireAbsolute(path, "path");
        if (offset_lines < 0)
            throw new ArgumentException("'offset_lines' must be 0 or more (1-based)", nameof(offset_lines));
        if (limit_lines < 0)
            throw new ArgumentException("'limit_lines' must be 0 (to the end) or more", nameof(limit_lines));
        if (offset_lines == 0 && limit_lines == 0)
            return await _fs.ReadTextAsync(path, max_bytes, encoding, ct);

        var window = await _fs.ReadLinesAsync(path, max_bytes, encoding, offset_lines, limit_lines, ct);
        return JsonSerializer.Serialize(new
        {
            path,
            totalLines = window.TotalLines,
            offset = window.Offset,
            returned = window.Returned,
            truncated = window.Truncated,
            content = window.Content,
        });
    }

    [McpServerTool(Title = "Write file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Write text content to a file (absolute path). Requires confirm:true. Replaces the file by " +
                 "default; append:true adds to the end instead. A missing parent directory is created unless " +
                 "create_parents:false, which refuses it.")]
    public async Task<string> FileWrite(
        [Description("File path to write (absolute)")] string path,
        [Description("Text content to write")] string content,
        [Description("Text encoding, e.g. utf-8")] string encoding = "utf-8",
        [Description("Must be true to confirm the file write")] bool confirm = false,
        [Description("Append to the file instead of replacing it")] bool append = false,
        [Description("Create a missing parent directory (default true)")] bool create_parents = true,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for file writes");
        RequireAbsolute(path, "path");
        await _fs.WriteTextAsync(path, content, encoding, append, create_parents, ct);
        return append
            ? $"appended {content.Length} chars to '{path}'"
            : $"wrote {content.Length} chars to '{path}'";
    }

    /// <summary>
    /// C-1 (roadmap R1): a relative path would resolve against the server's working directory,
    /// which is whatever the MCP host set and nothing the caller can see. UNC paths are fully
    /// qualified and pass.
    /// </summary>
    private static void RequireAbsolute(string? value, string name)
    {
        if (value is null) return;
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException(
                $"'{name}' must be an absolute path (got '{value}'); relative paths are refused because the " +
                "server's working directory is not the caller's", name);
    }

    [McpServerTool(Title = "Hash file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Compute a file's hash digest for integrity checks or IOC lookups (e.g. VirusTotal). algorithm: sha256 (default), sha1, or md5. Returns the lowercase hex digest.")]
    public async Task<string> FileHash(
        [Description("File path to hash")] string path,
        [Description("Hash algorithm: sha256, sha1, or md5")] string algorithm = "sha256",
        CancellationToken ct = default)
    {
        return await _fs.HashFileAsync(path, algorithm, ct);
    }

    [McpServerTool(Title = "File streams", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List NTFS alternate data streams (e.g. Zone.Identifier or hidden payloads) on a file, and the reparse target if the path is a symlink/junction. Forensic checks that file_info doesn't surface.")]
    public async Task<string> FileStreams(
        [Description("File or directory path to inspect")] string path,
        CancellationToken ct = default)
    {
        var streams = await _streams.GetStreamsAsync(path, ct);
        return JsonSerializer.Serialize(streams);
    }

    [McpServerTool(Title = "File info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Get metadata for a file or directory.")]
    public async Task<string> FileInfo(
        [Description("Path to inspect")] string path,
        CancellationToken ct = default)
    {
        var info = await _fs.GetInfoAsync(path, ct);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool(Title = "Zip or unzip", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Zip or unzip an archive. action: zip|unzip.")]
    public async Task<string> Archive(
        [Description("Action: zip or unzip")] string action,
        [Description("Source path (directory to zip, or zip file to unzip)")] string src,
        [Description("Destination path (zip file to create, or directory to extract to)")] string dst,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "zip":
                await _fs.ZipAsync(src, dst, ct);
                return $"zipped '{src}' to '{dst}'";

            case "unzip":
                await _fs.UnzipAsync(src, dst, ct);
                return $"unzipped '{src}' to '{dst}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected zip|unzip");
        }
    }
}
