using System.IO.Enumeration;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class FileSystemService : IFileSystemService
{
    private readonly IFinalPathNative _finalPath;

    public FileSystemService() : this(Win32FinalPathNative.Instance) { }

    /// <summary>C-1 round 4d: the volume seam behind the containment check's canonical paths.</summary>
    internal FileSystemService(IFinalPathNative finalPath) => _finalPath = finalPath;

    /// <summary>E-9 test seam: the points <see cref="StageHook"/> is called at.</summary>
    internal enum Stage
    {
        /// <summary>file_edit: the file has been read and the edit computed; nothing written yet. Argument: the target path.</summary>
        EditRead,
        /// <summary>file_edit: the temp file is complete; the change check and the replace come next. Argument: the temp path.</summary>
        EditTempWritten,
        /// <summary>content search: a file that passed the name/size/date filters is about to be opened. Argument: its path.</summary>
        SearchFile,
    }

    /// <summary>E-9 test seam: called at each <see cref="Stage"/>; null in production.</summary>
    internal Action<Stage, string>? StageHook { get; init; }

    /// <summary>E-9 test seam: the content search's per-line regex timeout (2 s in production).</summary>
    internal TimeSpan ContentMatchTimeout { get; init; } = ContentSearch.MatchTimeout;

    /// <summary>
    /// E-9 R2 test seam: the replace step of <c>file_edit</c> as (source = the temp file,
    /// destination = the target, backup = <c>&lt;path&gt;.bak.&lt;guid&gt;</c>). Production is
    /// <c>File.Replace</c>; a test swaps in a replace that fails part-way (error 1176 / 1177 shapes).
    /// </summary>
    internal Action<string, string, string?> ReplaceFile { get; init; } =
        static (source, destination, backup) => File.Replace(source, destination, backup, ignoreMetadataErrors: true);

    public async Task<string> ReadTextAsync(string path, long maxBytes, string encoding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        var enc = ResolveEncoding(encoding, info);
        return await File.ReadAllTextAsync(path, enc, ct);
    }

    /// <summary>C-1: the file decoded as <see cref="ReadTextAsync"/> does, then cut by <see cref="LineWindow"/>.</summary>
    public async Task<TextWindow> ReadLinesAsync(string path, long maxBytes, string encoding, int offsetLines, int limitLines, CancellationToken ct = default)
    {
        var text = await ReadTextAsync(path, maxBytes, encoding, ct);
        return LineWindow.Slice(text, offsetLines, limitLines);
    }

    public async Task<byte[]> ReadBytesAsync(string path, long maxBytes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidOperationException($"File size {info.Length} exceeds max_bytes {maxBytes}");
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task WriteTextAsync(string path, string content, string encoding, bool append, bool createParents, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var enc = encoding.ToLowerInvariant() switch
        {
            "utf-16" => Encoding.Unicode,
            "ascii"  => Encoding.ASCII,
            _        => new UTF8Encoding(false)   // utf-8, no BOM
        };

        // C-1: the parent directory is created unless the caller said not to, in which case a
        // missing one is refused naming the flag that would allow it.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (!createParents)
                throw new DirectoryNotFoundException(
                    $"Directory '{directory}' does not exist; pass create_parents:true to create it");
            Directory.CreateDirectory(directory);
        }

        if (append)
        {
            // An append must not rewrite the file, so no temp-file rename here.
            await File.AppendAllTextAsync(path, content, enc, ct);
            return;
        }

        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, content, enc, ct);
            // Atomic rename with retry on Windows EBUSY.
            for (int i = 0; ; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (i < 2)
                {
                    await Task.Delay(50 * (i + 1), ct);
                }
            }
        }
        catch
        {
            // Round 4: whatever stopped the rename — the third IOException, an access-denied
            // target, a cancel during the retry delay — the temp file must not be left beside it.
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    public Task<FileInfoDto> GetInfoAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var size = info is FileInfo fi ? fi.Length : 0;
        return Task.FromResult(new FileInfoDto(
            Path:        info.FullName,
            Size:        size,
            Created:     info.CreationTimeUtc,
            Modified:    info.LastWriteTimeUtc,
            Accessed:    info.LastAccessTimeUtc,
            Attributes:  info.Attributes.ToString(),
            IsDirectory: info is DirectoryInfo));
    }

    public Task<FileSearchHit[]> SearchAsync(string root, string? pattern, long? minSize, DateTime? modifiedSince, bool findDuplicates, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hits = new List<FileSearchHit>();
        var files = Directory.EnumerateFiles(root, pattern ?? "*", SearchOption.AllDirectories);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(f);
                if (minSize.HasValue && info.Length < minSize.Value) continue;
                if (modifiedSince.HasValue && info.LastWriteTimeUtc < modifiedSince.Value) continue;
                hits.Add(new FileSearchHit(info.FullName, info.Length, info.LastWriteTimeUtc));
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible files */ }
        }

        if (findDuplicates)
        {
            // Group by size, then hash equal-size candidates
            var grouped = hits.GroupBy(h => h.Size).Where(g => g.Count() > 1);
            var dups = new List<FileSearchHit>();
            foreach (var group in grouped)
            {
                ct.ThrowIfCancellationRequested();
                // Files that can't be hashed (locked/denied) return null and are skipped —
                // one unreadable file must not abort the whole duplicate search.
                var byHash = group
                    .Select(h => (Hit: h, Hash: HashFile(h.Path)))
                    .Where(x => x.Hash is not null)
                    .GroupBy(x => x.Hash!, x => x.Hit);
                foreach (var hg in byHash.Where(g => g.Count() > 1))
                    dups.AddRange(hg);
            }
            return Task.FromResult(dups.ToArray());
        }
        return Task.FromResult(hits.ToArray());
    }

    public async Task<string> HashFileAsync(string path, string algorithm = "sha256", CancellationToken ct = default)
    {
        using System.Security.Cryptography.HashAlgorithm hasher = algorithm.ToLowerInvariant() switch
        {
            "sha256" => System.Security.Cryptography.SHA256.Create(),
            "sha1"   => System.Security.Cryptography.SHA1.Create(),
            "md5"    => System.Security.Cryptography.MD5.Create(),
            _ => throw new ArgumentException($"Unknown algorithm '{algorithm}'; expected sha256|sha1|md5")
        };
        await using var stream = File.OpenRead(path);
        var hash = await hasher.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or access-denied file: skip it from dedup rather than fail the search.
            return null;
        }
    }

    // C-1 (roadmap R2): copy and move refuse an existing destination unless told to replace it;
    // delete refuses a non-empty directory unless told to take the tree. Each refusal names the
    // flag that would allow it, so the caller fixes the call instead of guessing.
    //
    // Round 4 (review-agent): every check runs before anything is touched — the source must
    // exist, a volume root is never a source or a destination, the two paths are canonicalised
    // (\\?\ stripped, links resolved) before the containment check — and an existing destination
    // is never deleted up front: it is moved aside, the operation runs, and only success removes
    // the aside; any failure or cancel removes the partial result and puts the aside back.

    public Task CopyAsync(string src, string dst, bool overwrite, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefuseRootsAndSelfContainment(src, dst);
        RequireSource(src);
        RefuseExistingDestination(dst, overwrite);

        var aside = Aside.Take(dst);
        var created = new List<string>();
        try
        {
            CreateParent(dst, created);
            if (Directory.Exists(src))
                CopyDirectory(src, dst, ct);
            else
                File.Copy(src, dst, overwrite: true);
        }
        catch (Exception ex)
        {
            RollBack(aside, created, ex);
            throw;
        }
        aside.Commit();   // after the try: a failure here must not run Restore over a finished copy
        return Task.CompletedTask;
    }

    /// <summary>
    /// The one rollback for copy and move: put the destination back; when that is not possible,
    /// the caller's failure goes out wrapped so the message says so (<see cref="Aside.NotRestored"/>);
    /// otherwise the caller's own <c>throw;</c> re-raises it unchanged.
    /// </summary>
    private static void RollBack(Aside aside, List<string> created, Exception cause)
    {
        var restored = aside.Restore();
        RemoveCreated(created);   // on every failure path, including the one that could not restore (round 4e)
        if (!restored) throw aside.NotRestored(cause);
    }

    /// <summary>Round 4d: a cause without terminal punctuation still reads as two sentences.</summary>
    internal static string TwoSentences(string cause, string tail)
    {
        var first = cause.Trim();
        if (first.Length == 0) return tail;
        if (!".!?:;…".Contains(first[^1])) first += ".";
        return first + " " + tail;
    }

    public Task MoveAsync(string src, string dst, bool overwrite, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefuseRootsAndSelfContainment(src, dst);
        RequireSource(src);
        RefuseExistingDestination(dst, overwrite);

        var aside = Aside.Take(dst);
        bool copiedAcrossVolumes = false;
        var created = new List<string>();
        try
        {
            CreateParent(dst, created);
            if (!Directory.Exists(src))
            {
                File.Move(src, dst);   // handles a cross-volume move itself
            }
            else
            {
                try
                {
                    Directory.Move(src, dst);
                }
                catch (IOException) when (!SameRoot(src, dst))
                {
                    // Directory.Move refuses a different volume: copy now, remove the source
                    // only once the copy is complete and the aside is gone (below).
                    CopyDirectory(src, dst, ct);
                    copiedAcrossVolumes = true;
                }
            }
        }
        catch (Exception ex)
        {
            RollBack(aside, created, ex);
            throw;
        }
        try
        {
            aside.Commit();
        }
        catch (InvalidOperationException ex) when (copiedAcrossVolumes)
        {
            // Round 4c: the source has not been removed yet, so this is not a finished move.
            throw new InvalidOperationException(
                $"Move of '{src}' to '{dst}' did not complete: the copy landed at '{dst}', but the previous " +
                $"destination could not be removed ({ex.Message}); the source is still at '{src}' and nothing was deleted from it.", ex);
        }

        if (copiedAcrossVolumes)
        {
            // Round 4b: the destination is complete, so nothing is ever in zero places — a source
            // that cannot be fully removed is reported, and what remains stays where it was.
            try
            {
                DeleteTree(src);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Moved '{src}' to '{dst}' (the copy is complete) but the source could not be fully removed: " +
                    $"{ex.Message} What remains is still at '{src}'.", ex);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A missing destination parent is created, for a file as for a directory copy. Returns the
    /// directories this call created, shallowest first, so a failure can take them away again
    /// (round 4d) without touching a parent that was already there.
    /// </summary>
    private static void CreateParent(string dst, List<string> created)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dst)));
        if (string.IsNullOrEmpty(parent) || Directory.Exists(parent)) return;

        var missing = new Stack<string>();
        for (var p = parent; !string.IsNullOrEmpty(p) && !Directory.Exists(p); p = Path.GetDirectoryName(p))
            missing.Push(p);
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir);
            created.Add(dir);   // recorded as made, so a chain that fails part-way is still undone (round 4e)
        }
    }

    /// <summary>Removes the parents a failed copy or move created, deepest first and only while empty.</summary>
    private static void RemoveCreated(List<string> created)
    {
        for (int i = created.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(created[i]) && !Directory.EnumerateFileSystemEntries(created[i]).Any())
                    Directory.Delete(created[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in place; the original failure is what the caller hears about.
            }
        }
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(PlainPath(path, nameof(path)));
        if (IsRoot(full))
            throw new InvalidOperationException($"'{path}' is a volume root and cannot be deleted");

        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                // A junction or directory symlink: the link goes, its target is not touched and
                // is never enumerated through — so no recursive is needed either.
                Directory.Delete(path, recursive: false);
                return Task.CompletedTask;
            }
            if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException(
                    $"'{path}' is a directory that is not empty; pass recursive:true to delete the whole tree");
            DeleteTree(path, ct);   // an empty directory too: the remover clears a read-only bit and removes it
        }
        else if (File.Exists(path))
        {
            DeleteTarget(path);   // clears a read-only bit first: the caller confirmed the delete
        }
        // A path that is not there (its parent may be missing too) is a no-op; the tool says so.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Round 4b: the one tree remover. A directory reparse point (junction, symlink) goes as a
    /// link without being descended into — its target is never touched; the read-only attribute
    /// is cleared on files first (the caller confirmed the delete; a read-only file is not a
    /// second gate); files go, then directories. Used by <c>delete recursive:true</c>, the aside's
    /// commit and restore, and the cross-volume source removal, so none of them trips on the
    /// framework's recursive delete refusing a junction or a read-only file.
    /// </summary>
    private static void DeleteTree(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(path);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            dir.Delete();
            return;
        }
        foreach (var file in dir.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
        }
        foreach (var sub in dir.EnumerateDirectories())
            DeleteTree(sub.FullName, ct);
        if ((dir.Attributes & FileAttributes.ReadOnly) != 0)
            dir.Attributes &= ~FileAttributes.ReadOnly;
        dir.Delete();
    }

    /// <summary>Removes whatever is at <paramref name="path"/>: a tree through <see cref="DeleteTree"/>, a file with its read-only bit cleared.</summary>
    private static void DeleteTarget(string path)
    {
        if (Directory.Exists(path))
        {
            DeleteTree(path);
        }
        else if (File.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    // ---- the checks, in the order the verbs run them ------------------------------------------

    /// <summary>
    /// The first check on every copy and move: a volume or share root as either end, the same
    /// path, a destination inside the source (a copy into its own subtree recurses into what it
    /// just created until the path length runs out; with <c>overwrite</c> the aside step would
    /// take part of the source), or a destination that contains the source (replacing it would
    /// delete what is being copied). Both paths are canonical first — see <see cref="Canonical"/>
    /// — and compared segment-wise, so <c>pre</c> and <c>prefix</c> are unrelated.
    /// </summary>
    private void RefuseRootsAndSelfContainment(string src, string dst)
    {
        var srcFull = Path.GetFullPath(PlainPath(src, nameof(src)));
        var dstFull = Path.GetFullPath(PlainPath(dst, nameof(dst)));
        if (IsRoot(srcFull))
            throw new InvalidOperationException($"'{src}' is a volume root; a whole volume cannot be the source of a copy or move");
        if (IsRoot(dstFull))
            throw new InvalidOperationException($"'{dst}' is a volume root and cannot be the destination of a copy or move");

        var s = Canonical(srcFull);
        var d = Canonical(dstFull);
        if (IsRoot(s))
            throw new InvalidOperationException($"'{src}' resolves to the volume root '{s}'; a whole volume cannot be the source of a copy or move");
        if (IsRoot(d))
            throw new InvalidOperationException($"'{dst}' resolves to the volume root '{d}' and cannot be the destination of a copy or move");

        // Round 4e: three spellings of each end — as written, canonical, and the literal place
        // (canonical parent plus the leaf as written). A destination that is itself a link
        // inside the source resolves to its target outside it, but its literal place is inside,
        // and that is where the copy would land once the aside had moved the link away.
        string[] sources = [Path.TrimEndingDirectorySeparator(srcFull), s, LiteralPlace(srcFull)];
        string[] destinations = [Path.TrimEndingDirectorySeparator(dstFull), d, LiteralPlace(dstFull)];
        foreach (var a in sources)
        {
            foreach (var b in destinations)
            {
                if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"'{src}' and '{dst}' are the same path");
                if (IsInside(b, a))
                    throw new InvalidOperationException(
                        $"'{dst}' is inside the source '{src}'; a copy or move into its own subtree is refused");
                if (IsInside(a, b))
                    throw new InvalidOperationException(
                        $"'{dst}' contains the source '{src}'; replacing it would delete what is being copied");
            }
        }
    }

    /// <summary>The canonical parent with the last segment as written: where the path literally sits, links at the leaf unresolved.</summary>
    private string LiteralPlace(string full)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent)) return trimmed;
        return Path.Combine(PathCanonical.Canonical(parent, _finalPath), Path.GetFileName(trimmed));
    }

    private static void RequireSource(string src)
    {
        if (!File.Exists(src) && !Directory.Exists(src))
            throw new FileNotFoundException($"Source '{src}' does not exist", src);
    }

    private static void RefuseExistingDestination(string dst, bool overwrite)
    {
        if (!overwrite && (File.Exists(dst) || Directory.Exists(dst)))
            throw new InvalidOperationException($"'{dst}' already exists; pass overwrite:true to replace it");
    }

    /// <summary>
    /// The <c>\\?\</c> and <c>\\?\UNC\</c> forms skip normalisation; compare the plain form. Every
    /// spelling counts — <c>GetFullPath</c> turns <c>//?/</c> into <c>\\?\</c> too (round 4b).
    /// </summary>
    /// <summary>
    /// Round 4d: the plain absolute form or a refusal. <c>\\?\C:</c> and <c>\\?\C:relative</c>
    /// strip to a drive reference, which is the current directory on that drive, not a path;
    /// <c>\\?\Volume{…}\</c> has no plain form and is kept (and refused as a root by the caller).
    /// </summary>
    private static string PlainPath(string path, string name)
    {
        var p = StripExtendedPrefix(path);
        if (p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) return p;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(p))
            throw new ArgumentException($"'{path}' is not a plain absolute path", name);
        return p;
    }

    private static string StripExtendedPrefix(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        // Round 4c: only a drive path follows the prefix in the plain form; \\?\Volume{…}\ has no
        // plain spelling and is left as it is (it is a root, and refused as one).
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length >= 6 && char.IsLetter(p[4]) && p[5] == ':')
            return p[4..];
        return p;
    }

    /// <summary>A volume or share root however it is spelled: <c>C:\</c>, <c>\\host\share</c>, <c>\\host\share\</c> (round 4c: trimmed forms compared).</summary>
    private static bool IsRoot(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <c>Directory.Move</c> can rename between the two: only when the roots agree both
    /// as written (the framework refuses a textual mismatch, so a <c>subst</c> alias of one volume
    /// must take the copy-then-delete path — round 4f) and canonically (a junction to another
    /// volume must too — round 4e).
    /// </summary>
    private bool SameRoot(string a, string b)
    {
        var aFull = Path.GetFullPath(StripExtendedPrefix(a));
        var bFull = Path.GetFullPath(StripExtendedPrefix(b));
        bool asWritten = string.Equals(Path.GetPathRoot(aFull), Path.GetPathRoot(bFull), StringComparison.OrdinalIgnoreCase);
        bool canonical = string.Equals(
            Path.GetPathRoot(PathCanonical.Canonical(aFull, _finalPath)),
            Path.GetPathRoot(PathCanonical.Canonical(bFull, _finalPath)),
            StringComparison.OrdinalIgnoreCase);
        return asWritten && canonical;
    }

    /// <summary><paramref name="child"/> is strictly below <paramref name="parent"/>, segment-wise.</summary>
    private static bool IsInside(string child, string parent)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A full path with every existing ancestor that is a reparse point resolved to its final
    /// target, so <c>C:\j\x</c> with <c>C:\j → C:\a</c> compares as <c>C:\a\x</c>. Segments past
    /// the last existing one are appended as written. A link that cannot be resolved (a cycle,
    /// too many levels) refuses the call rather than guessing.
    /// </summary>
    private string Canonical(string full)
    {
        RefuseLeafLinkToRoot(full);
        return PathCanonical.Canonical(full, _finalPath);
    }

    /// <summary>
    /// The one thing the final path cannot tell: whether the path's own last segment is a link
    /// to a volume root (the final path of a link to a <c>subst</c> drive is the underlying
    /// directory). Walks that chain hop by hop; a loop or an absurd chain is refused too.
    /// </summary>
    private static void RefuseLeafLinkToRoot(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                for (int j = i + 1; j < segments.Length; j++) current = Path.Combine(current, segments[j]);
                break;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            // Round 4b/4c: a link to a volume root is the root — but only when the link is the
            // path itself; an ancestor that is a root link makes the rest an ordinary path on
            // that volume. The chain is walked hop by hop because the final target of a link to
            // a subst drive is the underlying directory, not the root.
            bool isLeaf = i == segments.Length - 1;
            FileSystemInfo hop = info;
            string? resolved = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hop.FullName };
            try
            {
                while (true)
                {
                    var next = hop.ResolveLinkTarget(returnFinalTarget: false);
                    if (next is null) break;
                    if (isLeaf && IsRoot(next.FullName))
                        throw new InvalidOperationException(
                            $"'{full}' is a link to the volume root '{next.FullName}'; a whole volume cannot be copied, moved or deleted");
                    // A cycle (A → B → A) or an absurd chain cannot be resolved; refuse rather than guess.
                    if (!seen.Add(next.FullName) || seen.Count > 32)
                        throw new IOException($"the link chain at '{next.FullName}' loops or is too deep");
                    resolved = next.FullName;
                    if ((next.Attributes & FileAttributes.ReparsePoint) == 0 || !next.Exists) break;
                    hop = next;
                }
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"'{full}' goes through a link that cannot be resolved ({ex.Message}); refusing", ex);
            }
            if (resolved is not null) current = resolved;
        }
    }

    /// <summary>
    /// Copies a tree. Does not descend into a directory reparse point (a junction or symlink)
    /// and does not recreate it: a self-referencing junction would copy itself until the reparse
    /// limit, and one pointing outside the tree would copy that tree in. Observes the token
    /// before every directory as well as every file.
    /// </summary>
    private static void CopyDirectory(string src, string dst, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), ct);
        }
    }

    /// <summary>
    /// Round 4: an existing destination is moved to a sibling (<c>&lt;dst&gt;.replaced.&lt;guid&gt;</c>)
    /// for the duration of the operation. <see cref="Commit"/> removes it; <see cref="Restore"/>
    /// removes whatever partial result is at the destination and puts it back. A destination
    /// that did not exist has no aside, and a failed operation still removes the partial result.
    /// </summary>
    private sealed class Aside
    {
        private readonly string _dst;
        private readonly string? _aside;

        private Aside(string dst, string? aside) { _dst = dst; _aside = aside; }

        public static Aside Take(string dst)
        {
            if (!Directory.Exists(dst) && !File.Exists(dst))
                return new Aside(dst, null);
            var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dst));
            var aside = trimmed + ".replaced." + Guid.NewGuid().ToString("N");
            if (Directory.Exists(dst)) Directory.Move(dst, aside);
            else File.Move(dst, aside);
            return new Aside(dst, aside);
        }

        /// <summary>
        /// Removes the aside once the operation has succeeded. Never called from a failure path
        /// (round 4b): a Commit that cannot remove the aside reports where it is and leaves the
        /// finished result in place.
        /// </summary>
        public void Commit()
        {
            if (_aside is null) return;
            try
            {
                DeleteTarget(_aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"'{_dst}' was replaced, but the previous destination could not be removed and remains at " +
                    $"'{_aside}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Best effort, and never throws: it runs inside a <c>catch</c> whose <c>throw;</c> must
        /// carry the original failure, not a second one from here. A partial result that cannot
        /// be removed, or an aside that cannot be moved back, is left where it is — the aside's
        /// name (<c>&lt;dst&gt;.replaced.&lt;guid&gt;</c>) beside the destination is the trace.
        /// </summary>
        public bool Restore()
        {
            bool partialRemoved;
            try
            {
                DeleteTarget(_dst);
                partialRemoved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The partial result could not be removed; the put-back below still runs if the
                // name is free.
                partialRemoved = false;
            }
            if (_aside is null) return partialRemoved;
            try
            {
                if (Directory.Exists(_aside)) Directory.Move(_aside, _dst);
                else File.Move(_aside, _dst);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The aside stays beside the destination under its .replaced. name.
                return false;
            }
        }

        /// <summary>Round 4c: the caller's failure, wrapped so the message says the destination is not as it was and where the previous content is.</summary>
        public InvalidOperationException NotRestored(Exception cause) =>
            new(
                TwoSentences(cause.Message, _aside is null
                    ? $"A partial result could not be removed and was left at '{_dst}'."
                    : $"The destination '{_dst}' could not be put back as it was; its previous content is at '{_aside}'."),
                cause);
    }

    /// <summary>
    /// C-1 (roadmap R3): the entries with their type and size. <paramref name="pattern"/> is a
    /// name glob (<c>*</c>, <c>?</c>, case-insensitive) applied to files and directories alike;
    /// hidden and system entries are skipped unless asked for, and a skipped directory is not
    /// descended into; an inaccessible entry is skipped rather than failing the listing.
    /// </summary>
    /// <remarks>
    /// Round 4: the walk observes the token before every entry and stops at
    /// <paramref name="maxEntries"/> — a recursive listing of <c>C:\Windows</c> was 160 000
    /// entries and 42 MB in one response — reporting <see cref="FileListing.Truncated"/> when
    /// more remained.
    /// </remarks>
    public Task<FileListing> ListAsync(string path, string? pattern, bool recursive, bool includeHidden, int maxEntries, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxEntries < 1)
            throw new ArgumentException("'max_entries' must be at least 1", nameof(maxEntries));
        if (File.Exists(path.TrimEnd('\\', '/')))   // every trailing separator, any mix (round 4f)
            throw new IOException($"'{path}' is a file, not a directory; list its parent, or read it with file_read");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            MatchType = MatchType.Simple,
        };
        const FileAttributes HiddenOrSystem = FileAttributes.Hidden | FileAttributes.System;
        var glob = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        if (glob.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException(
                $"'pattern' is a name glob such as '*.txt', got '{pattern}'; a path separator is not allowed — pass recursive:true to descend",
                nameof(pattern));
        // Round 4b: a FileSystemEnumerable so the recursion predicate can refuse to walk into a
        // reparse point — a junction or symlink is listed (IsLink) but never descended into, so a
        // self-referencing junction cannot run the walk into the path limit.
        var walk = new FileSystemEnumerable<FileEntry>(
            path,
            (ref FileSystemEntry entry) => new FileEntry(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length,
                entry.LastWriteTimeUtc.UtcDateTime,
                (entry.Attributes & HiddenOrSystem) != 0,
                (entry.Attributes & FileAttributes.ReparsePoint) != 0),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                FileSystemName.MatchesSimpleExpression(glob, entry.FileName, ignoreCase: true),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        var entries = new List<FileEntry>(Math.Min(maxEntries, 4096));
        bool truncated = false;
        foreach (var entry in walk)
        {
            ct.ThrowIfCancellationRequested();
            if (entries.Count >= maxEntries) { truncated = true; break; }
            entries.Add(entry);
        }
        return Task.FromResult(new FileListing(entries.ToArray(), truncated, maxEntries));
    }

    /// <summary>
    /// E-9: one replacement in one existing text file. The file's bytes are read once, decoded
    /// strictly, edited by the pure <see cref="TextEdit"/>, and swapped back in with
    /// <c>File.Replace</c> - which, unlike a move, keeps the target's ACL, attributes, creation
    /// time and alternate data streams. Any refusal, failure or cancel leaves the file in place:
    /// the replace is given a backup name, and the cleanup restores before it deletes.
    /// </summary>
    public async Task<FileEditResult> EditTextAsync(string path, string oldString, string newString, bool replaceAll, string encoding, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                $"File '{path}' does not exist; file_edit changes an existing file (file_write creates one).", path);
        }
        // "Reparse point" is not "link": OneDrive and deduplicated files are reparse points too,
        // and are edited like any file. A link is refused: replacing it would replace the link,
        // not what it points at.
        bool directory = attributes.HasFlag(FileAttributes.Directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)
            && (directory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget) is not null)
            throw new InvalidOperationException(
                $"'{path}' is a symbolic link or junction; file_edit does not write through links. Edit its target instead.");
        if (directory)
            throw new InvalidOperationException($"'{path}' is a directory, not a file.");

        // The replace gives the path a new file record, so the file's other names would keep the
        // old content: one file would silently become two.
        if (HardLinkCount(path) is > 1 and var links)
            throw new InvalidOperationException(
                $"File '{path}' has {links} hard links (other names for the same file); an edit through this name would leave " +
                "the other names with the old content, so it was not edited. Rewrite it with file_write, or remove the extra links first.");

        var before = new FileInfo(path);
        long length = before.Length;
        var lastWrite = before.LastWriteTimeUtc;
        if (length > TextEdit.MaxFileBytes)
            throw new InvalidOperationException(
                $"File '{path}' is {length} bytes; file_edit edits files of up to {TextEdit.MaxFileBytes} bytes (16 MiB).");

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var detected = TextEdit.Detect(bytes, encoding);
        var text = TextEdit.Decode(bytes, detected);
        var outcome = TextEdit.Apply(text, oldString, newString, replaceAll, path);
        var edited = TextEdit.Encode(outcome.Text, detected);

        StageHook?.Invoke(Stage.EditRead, path);
        ct.ThrowIfCancellationRequested();

        var id = Guid.NewGuid().ToString("N");
        var tmp = path + ".tmp." + id;
        var backup = path + ".bak." + id;
        bool replaceStarted = false;
        try
        {
            await using (var stream = CreateTempLike(path, tmp))
            {
                await stream.WriteAsync(edited, ct);
            }
            StageHook?.Invoke(Stage.EditTempWritten, tmp);
            ct.ThrowIfCancellationRequested();

            // Someone else wrote the file since it was read: their content wins. The check and
            // the replace are two steps, so this narrows the window; it cannot close it.
            var now = new FileInfo(path);
            if (!now.Exists || now.Length != bytes.Length || now.LastWriteTimeUtc != lastWrite)
                throw new IOException(
                    $"File '{path}' changed while it was being edited; nothing was written. Read it again and retry the edit.");

            replaceStarted = true;
            for (int i = 0; ; i++)
            {
                try
                {
                    ReplaceFile(tmp, path, backup);
                    break;
                }
                // A sharing violation: someone has the file open. The same patience file_write
                // has - but only while the target is still there to be replaced.
                catch (IOException) when (i < 2 && File.Exists(path))
                {
                    await Task.Delay(50 * (i + 1), ct);
                }
            }
            try { File.Delete(backup); } catch { /* best-effort: the edit has landed */ }
        }
        catch (Exception failure)
        {
            RecoverFromFailedEdit(path, tmp, backup, replaceStarted, failure);
            throw;
        }

        return new FileEditResult(path, outcome.Replacements, outcome.FirstLine, detected.Name,
            detected.BomLength > 0, outcome.LineEndings, bytes.Length, edited.Length);
    }

    /// <summary>
    /// E-9: the cleanup after a failed edit. A replace can fail after it has already moved the
    /// original to the backup name (ReplaceFile error 1177): then the backup is put back before
    /// anything is deleted. It never RECREATES a file: with a backup name every documented failure
    /// leaves the target or the backup, so "neither" means someone else deleted the target while
    /// it was being edited - their deletion wins, and the caller is told where the edited content is.
    /// </summary>
    private static void RecoverFromFailedEdit(string path, string tmp, string backup, bool replaceStarted, Exception failure)
    {
        if (!replaceStarted)
        {
            // Nothing of the target was touched (a changed file, a cancel, a failed temp write):
            // the temp is ours alone and goes, whatever became of the target meanwhile.
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            return;
        }

        if (!File.Exists(path))
        {
            if (File.Exists(backup))
            {
                try
                {
                    File.Move(backup, path);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"The edit of '{path}' failed part-way and the original could not be put back: it is in '{backup}'. " +
                        $"Rename that file to '{Path.GetFileName(path)}'. ({failure.Message})", failure);
                }
            }
            else if (File.Exists(tmp))
            {
                throw new IOException(
                    $"File '{path}' was removed while it was being edited, so the edit was not applied and the file was not recreated. " +
                    $"The edited content is in '{tmp}'. ({failure.Message})", failure);
            }
        }

        // Nothing is deleted unless a file is at the path.
        if (File.Exists(path))
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            try { File.Delete(backup); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// E-9: the temp file, created with the target's own access rules where they can be read and
    /// applied - so the edited content of a file with a restrictive ACL is never briefly readable
    /// through the folder's inherited one. Otherwise an ordinary file: the edit still works.
    /// </summary>
    private static FileStream CreateTempLike(string path, string tmp)
    {
        try
        {
            var rules = new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(rules, AccessControlSections.Access);
            return new FileInfo(tmp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.Asynchronous, security);
        }
        // Best effort by design: whatever stops the ACL being read or applied (no READ_CONTROL, a
        // file system without ACLs, a privilege not held) must not stop the edit.
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or ArgumentException or NotSupportedException)
        {
            try { File.Delete(tmp); } catch { /* nothing was created, or it will be overwritten */ }
            return new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        }
    }

    /// <summary>How many names the file has, or null when Windows will not say.</summary>
    private static int? HardLinkCount(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>
    /// E-9: <c>file_search</c>'s content mode. The name/size/date filters choose the files; each
    /// is read a line at a time by the pure <see cref="ContentSearch"/>. Inaccessible directories
    /// are skipped and reparse points are not followed (a junction loop must not hang a search).
    /// </summary>
    public Task<ContentSearchResult> SearchContentAsync(ContentSearchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // The walk matches each file's NAME: a folder in the pattern would silently match nothing.
        if (request.Pattern is { } glob && glob.AsSpan().IndexOfAny('\\', '/') >= 0)
            throw new ArgumentException(
                $"'pattern' is a file-name glob in a content search and cannot contain a folder (got '{glob}'); put the folder in root.", "pattern");
        if (!Directory.Exists(request.Root))
            throw new DirectoryNotFoundException($"Directory '{request.Root}' does not exist");

        Regex regex;
        try
        {
            regex = new Regex(request.Content, RegexOptions.CultureInvariant, ContentMatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'content' is not a valid regular expression: {ex.Message}", "content");
        }

        // The walk and the reads are synchronous I/O: keep them off the caller's thread.
        return Task.Run(() => SearchContent(request, regex, ct), ct);
    }

    /// <summary>Files whose data is not on disk: opening one would make the sync provider download it.</summary>
    private const FileAttributes NotOnDisk = FileAttributes.Offline | (FileAttributes)0x00040000 /* RecallOnOpen */ | (FileAttributes)0x00400000 /* RecallOnDataAccess */;

    private ContentSearchResult SearchContent(ContentSearchRequest request, Regex regex, CancellationToken ct)
    {
        var name = FileSystemName.TranslateWin32Expression(request.Pattern ?? "*");
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,   // hidden and system files are read; what is skipped is decided below, and counted
            ReturnSpecialDirectories = false,
        };
        var files = new FileSystemEnumerable<(string Path, FileAttributes Attributes, long Length, DateTime Modified)>(
            request.Root,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes, entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
            walk)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && FileSystemName.MatchesWin32Expression(name, entry.FileName, ignoreCase: true),
            // A junction or directory symlink is not followed: a loop must not hang a search. A
            // reparse directory that is not a link (a OneDrive folder) is an ordinary folder here.
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0 || !IsDirectoryLink(entry.ToFullPath()),
        };

        var matches = new List<ContentMatch>();
        int searched = 0, skipped = 0;
        long chars = 0;
        bool truncated = false;
        var head = new byte[ContentSearch.BinaryProbeBytes];

        foreach (var (file, attributes, length, modified) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (request.MinSize is { } min && length < min) continue;
            if (request.ModifiedSince is { } since && modified < since) continue;

            // A file symlink is not followed, and a cloud file that is not on disk is not
            // downloaded: both are skipped where the caller can see it.
            if ((attributes & NotOnDisk) != 0 || ((attributes & FileAttributes.ReparsePoint) != 0 && IsLink(file)))
            {
                skipped++;
                continue;
            }

            StageHook?.Invoke(Stage.SearchFile, file);
            ct.ThrowIfCancellationRequested();

            if (length > ContentSearch.MaxFileBytes)
            {
                skipped++;
                continue;
            }

            ContentScan scan;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (ContentSearch.LooksBinary(head.AsSpan(0, read)))
                {
                    skipped++;
                    continue;
                }
                stream.Position = 0;
                // BOM decides, else UTF-8 - leniently: this only reads.
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                scan = ContentSearch.Scan(reader, file, regex, request.ContextLines, request.MaxMatches - matches.Count, ct,
                    (int)(ContentSearch.MaxResultChars - chars));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;   // locked, denied, or vanished mid-read
                continue;
            }

            searched++;
            matches.AddRange(scan.Matches);
            foreach (var m in scan.Matches)
            {
                chars += m.Text.Length + (m.Before?.Sum(l => (long)l.Length) ?? 0) + (m.After?.Sum(l => (long)l.Length) ?? 0);
            }
            if (scan.Truncated || matches.Count >= request.MaxMatches || chars >= ContentSearch.MaxResultChars)
            {
                truncated = true;
                break;
            }
        }

        return new ContentSearchResult(matches.ToArray(), searched, skipped, truncated, request.MaxMatches);
    }

    private static bool IsDirectoryLink(string directory)
    {
        try { return new DirectoryInfo(directory).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // cannot tell: do not enter it
    }

    private static bool IsLink(string file)
    {
        try { return new FileInfo(file).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // cannot tell: do not follow it
    }

    public Task ZipAsync(string srcDir, string dstZip, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Round 4: ZipFile.CreateFromDirectory creates the archive before it reads the source, so
        // a failing zip used to leave a valid empty archive where the caller's previous one was.
        // Build beside the target and move into place only when it is complete.
        var tmp = dstZip + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            ZipFile.CreateFromDirectory(srcDir, tmp);
            File.Move(tmp, dstZip, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
        return Task.CompletedTask;
    }

    public Task UnzipAsync(string srcZip, string dstDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ZipFile.ExtractToDirectory(srcZip, dstDir, overwriteFiles: true);
        return Task.CompletedTask;
    }

    private static Encoding ResolveEncoding(string encoding, FileInfo info) =>
        encoding.ToLowerInvariant() switch
        {
            "utf-8"  => Encoding.UTF8,
            "utf-16" => Encoding.Unicode,
            "ascii"  => Encoding.ASCII,
            "auto"   => DetectEncodingFromBom(info) ?? Encoding.UTF8,
            _        => Encoding.UTF8
        };

    private static Encoding? DetectEncodingFromBom(FileInfo info)
    {
        using var s = info.OpenRead();
        var bom = new byte[4];
        var read = s.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return null;
    }
}
