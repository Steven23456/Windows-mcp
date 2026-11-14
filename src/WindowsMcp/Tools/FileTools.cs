using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

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

    [McpServerTool(Title = "Search files", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Search for files. root: starting directory. pattern: glob (e.g. '*.txt'). min_size: bytes. modified_since: ISO 8601 datetime. find_duplicates: group identical files. Returns [{Path, Size, Modified}]. " +
                 "With content (a .NET regex matched against each line; (?i) at its start ignores case) it searches INSIDE the files that pattern, min_size and modified_since choose, and returns " +
                 "{matches:[{path, line (1-based), text, before?, after?}], filesSearched, filesSkipped, truncated, maxMatches}: max_matches (1-1000, default 100) stops the search and sets truncated:true " +
                 "(there may be more: narrow root or pattern), context_lines (0-5) adds the lines around each match, and each line is clipped at 400 characters. The search also stops, truncated:true, once the lines returned total about 60,000 characters. " +
                 "Hidden and system files are read. Skipped and counted in filesSkipped: binary files (a NUL byte in the first 8 KiB and no byte-order mark), files over 16 MiB, files that cannot be opened, " +
                 "symlinks to files, and cloud-only (offline) files, which are never downloaded. Folders that cannot be listed are passed over and junctions and symlinked folders are not entered. " +
                 "In a content search pattern is a file-name glob only ('*.config'): a folder in it is refused, so put the folder in root. " +
                 "A content regex that is too slow on a line (2 s) stops the search with an error. content cannot be combined with find_duplicates.")]
    public async Task<string> FileSearch(
        [Description("Root directory to search from")] string root,
        [Description("Glob pattern, e.g. '*.txt'")] string? pattern = null,
        [Description("Minimum file size in bytes")] long? min_size = null,
        [Description("Only files modified since this datetime (ISO 8601)")] string? modified_since = null,
        [Description("Group results by content hash to find duplicates")] bool find_duplicates = false,
        [Description("Search inside files: a .NET regular expression matched against each line ((?i) for case-insensitive). Blank = search by name, size and date only")] string? content = null,
        [Description("With content: stop after this many matching lines, 1-1000 (default 100); the result then says truncated:true")] int max_matches = 100,
        [Description("With content: lines of context to return before and after each match, 0-5 (default 0)")] int context_lines = 0,
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

        // E-9: range-checked always, so a bad value is not silently accepted by the name search.
        if (max_matches is < 1 or > 1000)
            throw new ArgumentException($"'max_matches' must be between 1 and 1000, got {max_matches}", nameof(max_matches));
        if (context_lines is < 0 or > 5)
            throw new ArgumentException($"'context_lines' must be between 0 and 5, got {context_lines}", nameof(context_lines));

        if (string.IsNullOrWhiteSpace(content))
        {
            var hits = await _fs.SearchAsync(root, pattern, min_size, since, find_duplicates, ct);
            return JsonSerializer.Serialize(hits);
        }

        if (find_duplicates)
            throw new ArgumentException(
                "'content' and 'find_duplicates' are two different searches; pass one of them", nameof(content));
        if (pattern is not null && pattern.AsSpan().IndexOfAny('\\', '/') >= 0)
            throw new ArgumentException(
                $"'pattern' is a file-name glob in a content search and cannot contain a folder (got '{pattern}'); put the folder in root", nameof(pattern));
        try
        {
            // Checked here so a bad pattern is refused before anything is enumerated.
            _ = new System.Text.RegularExpressions.Regex(content, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'content' is not a valid regular expression: {ex.Message}", nameof(content));
        }

        var found = await _fs.SearchContentAsync(
            new ContentSearchRequest(root, content, pattern, min_size, since, max_matches, context_lines), ct);
        return JsonSerializer.Serialize(new
        {
            matches = found.Matches.Select(m => new { path = m.Path, line = m.Line, text = m.Text, before = m.Before, after = m.After }).ToArray(),
            filesSearched = found.FilesSearched,
            filesSkipped = found.FilesSkipped,
            truncated = found.Truncated,
            maxMatches = found.MaxMatches,
        }, OmitNulls);
    }

    /// <summary>before / after are absent, not null, when no context was asked for.</summary>
    private static readonly JsonSerializerOptions OmitNulls = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Title = "Manage files", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("File operations: copy, move, delete, list. Paths must be absolute (plain form, no \\\\?\\ prefix). " +
                 "copy/move refuse an existing destination unless overwrite:true, which REPLACES it — whatever was " +
                 "there, a file or a whole directory tree, is deleted once the copy or move has succeeded; a " +
                 "failure or a cancel puts it back (best effort: if it cannot, the error says so and the previous " +
                 "content is beside the destination under its .replaced. name). A missing destination parent " +
                 "directory is created. A directory " +
                 "is copied as a tree without descending into junctions or symlinks; a move across volumes is a " +
                 "copy then a delete, and links inside the tree are not carried across volumes (dropped). A volume root, the same " +
                 "path, a destination inside the source or one that contains it are refused. delete requires " +
                 "confirm:true and refuses a non-empty directory unless recursive:true; deleting a junction removes " +
                 "the link only. list returns {Entries:[{Path, Name, IsDirectory, Size, Modified, Hidden, IsLink}], " +
                 "Truncated, MaxEntries} — pattern is a name glob ('*.txt', case-insensitive, files and " +
                 "directories), recursive descends but never into a junction or symlink (listed with IsLink:true), " +
                 "hidden or system entries are skipped unless include_hidden:true, and the walk stops at " +
                 "max_entries (default 1000) with Truncated:true.")]
    public async Task<string> FileManage(
        [Description("Action: copy, move, delete, list")] string action,
        [Description("Source path (absolute)")] string src,
        [Description("Destination path (absolute; required for copy/move)")] string? dst = null,
        [Description("Must be true to confirm destructive delete action")] bool confirm = false,
        [Description("copy/move: replace an existing destination (default false: refused)")] bool overwrite = false,
        [Description("delete: remove a non-empty directory and everything under it (default false: refused); list: descend into sub-directories")] bool recursive = false,
        [Description("list: name glob such as '*.log' (case-insensitive); default every entry")] string? pattern = null,
        [Description("list: include hidden and system entries (default false)")] bool include_hidden = false,
        [Description("list: stop after this many entries (1-100000, default 1000) and say Truncated:true")] int max_entries = 1000,
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
                // Round 4: the reply tells the truth — a link is unlinked (its target untouched),
                // a path that was never there is not "deleted". Read-only metadata, checked here.
                bool existed = Directory.Exists(src) || File.Exists(src);
                bool isLink = existed && (File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0;
                await _fs.DeleteAsync(src, recursive, ct);
                if (isLink) return $"removed link '{src}' (its target is untouched)";
                return existed ? $"deleted '{src}'" : $"nothing at '{src}' to delete";

            case "list":
                if (pattern is not null && pattern.IndexOfAny(['\\', '/']) >= 0)
                    throw new ArgumentException(
                        $"'pattern' is a name glob such as '*.txt', got '{pattern}'; a path separator is not allowed — pass recursive:true to descend",
                        nameof(pattern));
                if (max_entries is < 1 or > 100_000)
                    throw new ArgumentException(
                        $"'max_entries' must be between 1 and 100000, got {max_entries} (0 is not 'all' here: a listing is always bounded)",
                        nameof(max_entries));
                var listing = await _fs.ListAsync(src, pattern, recursive, include_hidden, max_entries, ct);
                return JsonSerializer.Serialize(listing);

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

    private static readonly string[] EditEncodings = ["auto", "utf-8", "utf-16", "ascii", "latin1"];

    [McpServerTool(Title = "Edit file", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Change part of an existing text file (absolute path) without sending the file: only old_string and new_string cross the wire. Requires confirm:true. " +
                 "old_string must match the file's text exactly and exactly once (unique); zero matches, or several without replace_all:true, is refused saying how many, with nothing written - " +
                 "add surrounding lines to make it unique, or pass replace_all:true to replace every occurrence. The file's encoding, byte-order mark (BOM) and line endings are preserved: " +
                 "a \\n in the strings matches the file's own line ending and is written as it (in a file that mixes line endings, or has none, new_string is written as given), and every byte outside the replaced text is left as it was. " +
                 "The file's permissions, attributes, creation time and alternate data streams are kept where the file system supports it. " +
                 "Refused with nothing written: a file that does not exist (use file_write), a directory, a symlink or junction (edit its target), a file with more than one hard link, a read-only file, a file over 16 MiB or an edit that would make it larger than 16 MiB, " +
                 "bytes that are not valid in the encoding (with no byte-order mark, auto also refuses text containing NUL characters: pass encoding:utf-16 if the file is UTF-16, or encoding:utf-8 if it really is UTF-8), " +
                 "a new_string the encoding cannot represent, and a file that changed while it was being edited. If the swap itself fails the original is left in place or put back; a file that was removed meanwhile is not recreated. " +
                 "Returns {path, replacements, firstLine, encoding, bom, lineEndings (crlf|lf|mixed|none), bytesBefore, bytesAfter}.")]
    public async Task<string> FileEdit(
        [Description("File to edit (absolute path; it must exist)")] string path,
        [Description("The exact text to replace, copied from the file (file_read); must occur once unless replace_all")] string old_string,
        [Description("The text to put in its place; an empty string deletes old_string (null is refused)")] string new_string,
        [Description("Replace every occurrence instead of requiring exactly one (default false)")] bool replace_all = false,
        [Description("How to read a file with no byte-order mark: auto (default, UTF-8) | utf-8 | utf-16 | ascii | latin1 (any single-byte legacy file); a byte-order mark always decides")] string encoding = "auto",
        [Description("Must be true to confirm the edit")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for file edits");
        RequireAbsolute(path, "path");
        if (string.IsNullOrEmpty(old_string))
            throw new ArgumentException("'old_string' is empty; give the exact text to replace", nameof(old_string));
        if (new_string is null)
            throw new ArgumentException("'new_string' is missing; pass an empty string to delete old_string", nameof(new_string));
        if (string.Equals(old_string, new_string, StringComparison.Ordinal))
            throw new ArgumentException("'old_string' and 'new_string' are the same, so nothing would change", nameof(new_string));
        if (encoding is null || !EditEncodings.Contains(encoding, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown encoding '{encoding}'; expected one of: {string.Join(", ", EditEncodings)}", nameof(encoding));

        var result = await _fs.EditTextAsync(path, old_string, new_string, replace_all, encoding, ct);
        return JsonSerializer.Serialize(new
        {
            path = result.Path,
            replacements = result.Replacements,
            firstLine = result.FirstLine,
            encoding = result.Encoding,
            bom = result.Bom,
            lineEndings = result.LineEndings,
            bytesBefore = result.BytesBefore,
            bytesAfter = result.BytesAfter,
        });
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
        // Round 4: the extended-length and device forms bypass path normalisation (and so the
        // containment checks); a caller has no reason to send them. Every spelling counts —
        // GetFullPath turns //?/ into \\?\ too (round 4b).
        var trimmed = value.TrimStart().Replace('/', '\\');
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{name}' must be a plain absolute path (got '{value}'); the \\\\?\\ and \\\\.\\ device forms are refused", name);
    }

    [McpServerTool(Title = "Hash file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Compute a file's hash digest for integrity checks or IOC lookups (e.g. VirusTotal). algorithm: sha256 (default), sha1, or md5. Returns the lowercase hex digest.")]
    public async Task<string> FileHash(
        [Description("File path to hash (absolute)")] string path,
        [Description("Hash algorithm: sha256, sha1, or md5")] string algorithm = "sha256",
        CancellationToken ct = default)
    {
        RequireAbsolute(path, "path");
        return await _fs.HashFileAsync(path, algorithm, ct);
    }

    [McpServerTool(Title = "File streams", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List NTFS alternate data streams (e.g. Zone.Identifier or hidden payloads) on a file, and the reparse target if the path is a symlink/junction. Forensic checks that file_info doesn't surface.")]
    public async Task<string> FileStreams(
        [Description("File or directory path to inspect (absolute)")] string path,
        CancellationToken ct = default)
    {
        RequireAbsolute(path, "path");
        var streams = await _streams.GetStreamsAsync(path, ct);
        return JsonSerializer.Serialize(streams);
    }

    [McpServerTool(Title = "File info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Get metadata for a file or directory.")]
    public async Task<string> FileInfo(
        [Description("Path to inspect (absolute)")] string path,
        CancellationToken ct = default)
    {
        RequireAbsolute(path, "path");
        var info = await _fs.GetInfoAsync(path, ct);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool(Title = "Zip or unzip", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Zip or unzip an archive. action: zip|unzip.")]
    public async Task<string> Archive(
        [Description("Action: zip or unzip")] string action,
        [Description("Source path (absolute; directory to zip, or zip file to unzip)")] string src,
        [Description("Destination path (absolute; zip file to create, or directory to extract to)")] string dst,
        CancellationToken ct = default)
    {
        RequireAbsolute(src, "src");
        RequireAbsolute(dst, "dst");
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
