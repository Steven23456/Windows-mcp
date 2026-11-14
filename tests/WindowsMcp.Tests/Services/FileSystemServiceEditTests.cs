using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-9: <see cref="FileSystemService.EditTextAsync"/> against real files in a fresh temp directory
/// (docs/design/E-9-file-edit-content-search.md, "Refused after looking at the file", "Encoding and
/// BOM", "The write"). The pure rules are pinned in <see cref="TextEditTests"/>; what is proved here
/// is what only a disk can show - the bytes on disk, the BOM, the attributes and streams a replace
/// keeps, and that every refusal, cancel and failure leaves the original byte-for-byte with no temp
/// file beside it. Nothing outside <c>_tmp</c> is touched.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemServiceEditTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-edit-" + Guid.NewGuid().ToString("N"));

    public FileSystemServiceEditTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            // Top level only: every file these tests make is directly under _tmp. A read-only file
            // would stop the recursive delete, and so would a junction left in the tree.
            foreach (var entry in new DirectoryInfo(_tmp).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                if (entry is DirectoryInfo { Attributes: var a } link && (a & FileAttributes.ReparsePoint) != 0)
                    link.Delete();
            }
            Directory.Delete(_tmp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: Dispose must not throw; a leftover under the temp folder is all it costs
        }
        GC.SuppressFinalize(this);
    }

    private static FileSystemService Svc() => new();

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string text) => Write(name, new UTF8Encoding(false).GetBytes(text));

    private static byte[] Bytes(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Every entry in the temp directory: a stray <c>.tmp.</c> file beside the target shows up here.</summary>
    private string[] Entries() =>
        Directory.EnumerateFileSystemEntries(_tmp).Select(e => Path.GetFileName(e)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private void OnlyTheseEntries(params string[] names) =>
        Entries().Should().BeEquivalentTo(names, "a refused, cancelled or failed edit leaves no temp file beside the target");

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ==== the edit itself ============================================================================

    /// <summary>
    /// "Bytes outside the replaced spans are never touched." The file mixes CRLF, a bare LF, a lone
    /// CR, trailing spaces, non-ASCII and an astral character, and has no final newline; the expected
    /// result is built from the original's own bytes either side of the span, so any normalisation
    /// anywhere else in the file fails the comparison.
    /// </summary>
    [Fact]
    public async Task EditTextAsync_changes_only_the_replaced_span()
    {
        const string Before = "# café \U0001F600  \r\n[server]\r\nport = 8080\nhost = a\rb  \r\nend";
        var original = new UTF8Encoding(false).GetBytes(Before);
        var path = Write("app.ini", original);

        var result = await Svc().EditTextAsync(path, "port = 8080", "port = 9090", replaceAll: false, "auto");

        var at = Before.IndexOf("port = 8080", StringComparison.Ordinal);
        var expected = Bytes(
            new UTF8Encoding(false).GetBytes(Before[..at]),
            "port = 9090"u8.ToArray(),
            new UTF8Encoding(false).GetBytes(Before[(at + "port = 8080".Length)..]));
        File.ReadAllBytes(path).Should().Equal(expected);

        result.Path.Should().Be(path);
        result.Replacements.Should().Be(1);
        result.FirstLine.Should().Be(3);
        result.Encoding.Should().Be("utf-8");
        result.Bom.Should().BeFalse();
        result.LineEndings.Should().Be("mixed");
        result.BytesBefore.Should().Be(original.Length);
        result.BytesAfter.Should().Be(expected.Length);
        OnlyTheseEntries("app.ini");
    }

    [Fact]
    public async Task EditTextAsync_with_replace_all_replaces_every_occurrence()
    {
        var path = Write("hosts.txt", "10.0.0.1 a\n10.0.0.1 b\n10.0.0.2 c\n");

        var result = await Svc().EditTextAsync(path, "10.0.0.1", "10.0.0.9", replaceAll: true, "auto");

        File.ReadAllText(path).Should().Be("10.0.0.9 a\n10.0.0.9 b\n10.0.0.2 c\n");
        result.Replacements.Should().Be(2);
        result.FirstLine.Should().Be(1);
        result.LineEndings.Should().Be("lf");
    }

    /// <summary>
    /// The case the note opens with: the model sends LF, the Windows config file has CRLF. The
    /// edit lands and the file is still CRLF throughout.
    /// </summary>
    [Fact]
    public async Task EditTextAsync_keeps_a_crlf_file_crlf_when_the_strings_use_lf()
    {
        var path = Write("web.config", "<a>\r\n  <b x=\"1\" />\r\n  <c />\r\n</a>\r\n");

        var result = await Svc().EditTextAsync(path, "  <b x=\"1\" />\n  <c />", "  <b x=\"2\" />\n  <c y=\"3\" />", false, "auto");

        File.ReadAllText(path).Should().Be("<a>\r\n  <b x=\"2\" />\r\n  <c y=\"3\" />\r\n</a>\r\n");
        result.LineEndings.Should().Be("crlf");
        result.FirstLine.Should().Be(2);
    }

    /// <summary>A name with spaces and non-ASCII characters is just a path.</summary>
    [Fact]
    public async Task EditTextAsync_edits_a_file_whose_name_has_spaces_and_unicode()
    {
        var path = Write("my conf ü 文件.ini", "a=1\n");

        await Svc().EditTextAsync(path, "a=1", "a=2", false, "auto");

        File.ReadAllText(path).Should().Be("a=2\n");
        OnlyTheseEntries("my conf ü 文件.ini");
    }

    // ==== encoding and BOM preserved =================================================================

    /// <summary>"The result is encoded with the same encoding, and has a BOM exactly when the original had one."</summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16-be")]
    [InlineData("utf-32")]
    public async Task EditTextAsync_keeps_the_bom_and_the_encoding(string name)
    {
        Encoding enc = name switch
        {
            "utf-8" => new UTF8Encoding(true),
            "utf-16" => new UnicodeEncoding(false, true),
            "utf-16-be" => new UnicodeEncoding(true, true),
            _ => new UTF32Encoding(false, true),
        };
        var preamble = enc.GetPreamble();
        var path = Write("bom.txt", Bytes(preamble, enc.GetBytes("alpha\r\nbeta é\r\n")));

        // "ascii" contradicts every one of these BOMs: the BOM decides.
        var result = await Svc().EditTextAsync(path, "beta", "gamma", false, "ascii");

        File.ReadAllBytes(path).Should().Equal(Bytes(preamble, enc.GetBytes("alpha\r\ngamma é\r\n")));
        result.Encoding.Should().Be(name);
        result.Bom.Should().BeTrue();
        OnlyTheseEntries("bom.txt");
    }

    [Theory]
    [InlineData("auto", "utf-8")]
    [InlineData("utf-8", "utf-8")]
    [InlineData("utf-16", "utf-16")]
    [InlineData("ascii", "ascii")]
    [InlineData("latin1", "latin1")]
    public async Task EditTextAsync_writes_no_bom_where_there_was_none(string encoding, string expectedName)
    {
        Encoding enc = expectedName switch
        {
            "utf-16" => new UnicodeEncoding(false, false),
            "ascii" => Encoding.ASCII,
            "latin1" => Encoding.Latin1,
            _ => new UTF8Encoding(false),
        };
        var path = Write("nobom.txt", enc.GetBytes("key=old\n"));

        var result = await Svc().EditTextAsync(path, "old", "new", false, encoding);

        File.ReadAllBytes(path).Should().Equal(enc.GetBytes("key=new\n"));
        result.Bom.Should().BeFalse();
        result.Encoding.Should().Be(expectedName);
    }

    /// <summary>
    /// "latin1 → every byte is a character, so any single-byte legacy file can be edited without
    /// loss": E9 and FF are not valid UTF-8 on their own and must come back exactly.
    /// </summary>
    [Fact]
    public async Task EditTextAsync_as_latin1_keeps_every_high_byte()
    {
        var original = Bytes("name=caf"u8.ToArray(), [0xE9], "\r\nflag=1 "u8.ToArray(), [0xFF, 0xA0], "\r\n"u8.ToArray());
        var path = Write("legacy.ini", original);

        await Svc().EditTextAsync(path, "flag=1", "flag=0", false, "latin1");

        File.ReadAllBytes(path).Should().Equal(
            Bytes("name=caf"u8.ToArray(), [0xE9], "\r\nflag=0 "u8.ToArray(), [0xFF, 0xA0], "\r\n"u8.ToArray()));
    }

    /// <summary>
    /// "The bytes are not valid in the encoding → InvalidOperationException naming the encoding and
    /// saying which encoding values exist." The same legacy file read as auto (UTF-8) is refused,
    /// and nothing is rewritten with replacement characters.
    /// </summary>
    [Fact]
    public async Task EditTextAsync_refuses_bytes_that_are_not_valid_in_the_encoding_and_writes_nothing()
    {
        var original = Bytes("name=caf"u8.ToArray(), [0xE9], "\nflag=1\n"u8.ToArray());
        var path = Write("legacy.ini", original);

        var act = () => Svc().EditTextAsync(path, "flag=1", "flag=0", false, "auto");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("utf-8").And.Contain("latin1");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("legacy.ini");
    }

    /// <summary>"A new_string the encoding cannot represent ... is refused with nothing written."</summary>
    [Theory]
    [InlineData("ascii", "café")]
    [InlineData("latin1", "€")]
    public async Task EditTextAsync_refuses_a_new_string_the_encoding_cannot_represent(string encoding, string newString)
    {
        var original = "price=10\n"u8.ToArray();
        var path = Write("price.txt", original);

        var act = () => Svc().EditTextAsync(path, "10", newString, false, encoding);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("price.txt");
    }

    /// <summary>
    /// The lone-surrogate half of the rule above, through the service. The code unit is an int and
    /// the string is built here: an attribute string is stored as UTF-8, so a lone surrogate in
    /// <c>[InlineData]</c> arrives as U+FFFD and the row would test nothing.
    /// </summary>
    [Theory]
    [InlineData("auto", 0xD800)]
    [InlineData("utf-16", 0xDC00)]
    public async Task EditTextAsync_refuses_a_new_string_with_a_lone_surrogate(string encoding, int codeUnit)
    {
        var enc = encoding == "utf-16" ? (Encoding)new UnicodeEncoding(false, false) : new UTF8Encoding(false);
        var original = enc.GetBytes("price=10\n");
        var path = Write("price.txt", original);
        var newString = "1" + (char)codeUnit;

        var act = () => Svc().EditTextAsync(path, "10", newString, false, encoding);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("price.txt");
    }

    // ==== refused after looking at the file =========================================================

    [Fact]
    public async Task EditTextAsync_refuses_a_missing_file_naming_it_and_creates_nothing()
    {
        var path = Path.Combine(_tmp, "absent.ini");

        var act = () => Svc().EditTextAsync(path, "a", "b", false, "auto");

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.Message.Should().Contain(path);
        OnlyTheseEntries();
    }

    [Fact]
    public async Task EditTextAsync_refuses_a_directory()
    {
        var dir = Path.Combine(_tmp, "conf.d");
        Directory.CreateDirectory(dir);

        var act = () => Svc().EditTextAsync(dir, "a", "b", false, "auto");

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(dir).Should().BeTrue();
        OnlyTheseEntries("conf.d");
    }

    /// <summary>
    /// "A symlink or other reparse point → InvalidOperationException saying to edit its target
    /// (replacing the file would replace the link)." File.Replace on the link swaps the link itself
    /// for a regular file: the target never changes and the link is gone.
    /// </summary>
    [FileSymlinkFact]
    public async Task EditTextAsync_refuses_a_file_symlink_saying_to_edit_its_target()
    {
        var target = Write("real.ini", "a=1\n");
        var link = Path.Combine(_tmp, "link.ini");
        File.CreateSymbolicLink(link, target);

        var act = () => Svc().EditTextAsync(link, "a=1", "a=2", false, "auto");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().ContainEquivalentOf("target");
        File.ReadAllText(target).Should().Be("a=1\n");
        (File.GetAttributes(link) & FileAttributes.ReparsePoint).Should().NotBe(0, "the link is still a link");
        OnlyTheseEntries("link.ini", "real.ini");
    }

    [JunctionFact]
    public async Task EditTextAsync_refuses_a_junction()
    {
        var target = Path.Combine(_tmp, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_tmp, "junction");
        JunctionFactAttribute.TryCreateJunction(link, target, out var error).Should().BeTrue(error);

        var act = () => Svc().EditTextAsync(link, "a", "b", false, "auto");

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(target).Should().BeTrue();
        OnlyTheseEntries("junction", "target");
    }

    /// <summary>"Larger than 16 MiB (TextEdit.MaxFileBytes) → InvalidOperationException naming the size and the cap."</summary>
    [Fact]
    public async Task EditTextAsync_refuses_a_file_over_16_MiB_naming_the_size_and_the_cap()
    {
        var path = Path.Combine(_tmp, "big.log");
        using (var fs = File.Create(path))
            fs.SetLength(TextEdit.MaxFileBytes + 1);
        var before = Sha(path);

        var act = () => Svc().EditTextAsync(path, "a", "b", false, "auto");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain((TextEdit.MaxFileBytes + 1).ToString(), "the refusal names the file's size");
        (message.Contains("16 MiB", StringComparison.OrdinalIgnoreCase) || message.Contains(TextEdit.MaxFileBytes.ToString()))
            .Should().BeTrue($"the refusal names the cap; it said: {message}");
        Sha(path).Should().Be(before);
        OnlyTheseEntries("big.log");
    }

    [Fact]
    public void The_edit_size_cap_is_16_MiB()
    {
        TextEdit.MaxFileBytes.Should().Be(16L * 1024 * 1024);
    }

    /// <summary>"Larger than" 16 MiB: a file of exactly 16 MiB is edited.</summary>
    [Fact]
    public async Task EditTextAsync_edits_a_file_of_exactly_16_MiB()
    {
        var bytes = new byte[TextEdit.MaxFileBytes];
        Array.Fill(bytes, (byte)'x');
        "needle\n"u8.CopyTo(bytes);
        var path = Write("exact.log", bytes);

        var result = await Svc().EditTextAsync(path, "needle", "NEEDLE", false, "auto");

        result.Replacements.Should().Be(1);
        result.BytesBefore.Should().Be(TextEdit.MaxFileBytes);
        new FileInfo(path).Length.Should().Be(TextEdit.MaxFileBytes);
    }

    [Fact]
    public async Task EditTextAsync_refuses_zero_matches_naming_the_path_and_writes_nothing()
    {
        var original = "a=1\nb=2\n"u8.ToArray();
        var path = Write("ab.ini", original);
        var stages = new List<FileSystemService.Stage>();
        var svc = new FileSystemService { StageHook = (stage, _) => stages.Add(stage) };

        var act = () => svc.EditTextAsync(path, "c=3", "c=4", false, "auto");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().ContainEquivalentOf("not found").And.Contain(path);
        File.ReadAllBytes(path).Should().Equal(original);
        stages.Should().BeEmpty("a content refusal comes before anything is written - no temp file is ever started");
        OnlyTheseEntries("ab.ini");
    }

    [Fact]
    public async Task EditTextAsync_refuses_an_ambiguous_match_saying_how_many_and_writes_nothing()
    {
        var original = "x=1\nx=1\n"u8.ToArray();
        var path = Write("dup.ini", original);

        var act = () => Svc().EditTextAsync(path, "x=1", "x=2", false, "auto");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("2").And.Contain("replace_all");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("dup.ini");
    }

    [Fact]
    public async Task EditTextAsync_on_an_empty_file_finds_nothing_and_leaves_it_empty()
    {
        var path = Write("empty.txt", Array.Empty<byte>());

        var act = () => Svc().EditTextAsync(path, "a", "b", false, "auto");

        await act.Should().ThrowAsync<InvalidOperationException>();
        new FileInfo(path).Length.Should().Be(0);
        OnlyTheseEntries("empty.txt");
    }

    // ==== the write: refusals, cancellation, concurrent change =======================================

    /// <summary>"A read-only target is refused by Windows (UnauthorizedAccessException); it is not made writable."</summary>
    [Fact]
    public async Task EditTextAsync_refuses_a_read_only_file_and_leaves_it_read_only_and_unchanged()
    {
        var original = "a=1\n"u8.ToArray();
        var path = Write("ro.ini", original);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var act = () => Svc().EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (File.GetAttributes(path) & FileAttributes.ReadOnly).Should().NotBe(0, "the read-only bit is not cleared");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("ro.ini");
    }

    [Fact]
    public async Task EditTextAsync_with_a_cancelled_token_does_nothing()
    {
        var original = "a=1\n"u8.ToArray();
        var path = Write("c.ini", original);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Svc().EditTextAsync(path, "a=1", "a=2", false, "auto", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("c.ini");
    }

    /// <summary>"Cancellation is honoured ... before the temp write."</summary>
    [Fact]
    public async Task A_cancel_after_the_read_writes_no_temp_file_and_leaves_the_original()
    {
        var original = "a=1\n"u8.ToArray();
        var path = Write("c.ini", original);
        using var cts = new CancellationTokenSource();
        var stages = new List<FileSystemService.Stage>();
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                stages.Add(stage);
                if (stage == FileSystemService.Stage.EditRead) cts.Cancel();
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stages.Should().Equal([FileSystemService.Stage.EditRead], "the temp file is never written after the cancel");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("c.ini");
    }

    /// <summary>
    /// "Cancellation is honoured ... before the replace. A cancel ... deletes the temp file." The hook
    /// also pins where the temp file is: beside the target, named <c>&lt;path&gt;.tmp.&lt;guid&gt;</c>,
    /// complete with the edited bytes.
    /// </summary>
    [Fact]
    public async Task A_cancel_before_the_replace_deletes_the_temp_file_and_leaves_the_original()
    {
        var original = "a=1\n"u8.ToArray();
        var path = Write("c.ini", original);
        using var cts = new CancellationTokenSource();
        string? temp = null;
        byte[]? tempBytes = null;
        var svc = new FileSystemService
        {
            StageHook = (stage, arg) =>
            {
                if (stage != FileSystemService.Stage.EditTempWritten) return;
                temp = arg;
                tempBytes = File.ReadAllBytes(arg);
                cts.Cancel();
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        temp.Should().NotBeNull("the temp file was written before the replace");
        Path.GetDirectoryName(temp).Should().Be(_tmp, "the temp file is beside the target, so File.Replace stays on one volume");
        Path.GetFileName(temp).Should().MatchRegex("^" + Regex.Escape("c.ini") + @"\.tmp\.[0-9a-fA-F-]{32,36}$");
        tempBytes.Should().Equal("a=2\n"u8.ToArray());
        File.Exists(temp).Should().BeFalse("a cancel deletes the temp file");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("c.ini");
    }

    /// <summary>
    /// "Immediately before the replace the target's length and last-write time are compared with
    /// what was read; if they changed, the edit is abandoned with an IOException ("changed while it
    /// was being edited"), nothing written." Another writer's content wins; the edit does not
    /// overwrite it.
    /// </summary>
    [Fact]
    public async Task A_file_that_grows_between_read_and_replace_is_refused_and_the_other_writer_wins()
    {
        var path = Write("race.ini", "a=1\n");
        var wroteConcurrently = false;
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage != FileSystemService.Stage.EditTempWritten) return;
                File.WriteAllText(path, "a=1\nb=2 from someone else\n");
                wroteConcurrently = true;
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=9", false, "auto");

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().ContainEquivalentOf("changed while it was being edited");
        wroteConcurrently.Should().BeTrue("the service must not hold the target open in a way that blocks other writers - the scenario has to be real");
        File.ReadAllText(path).Should().Be("a=1\nb=2 from someone else\n");
        OnlyTheseEntries("race.ini");
    }

    /// <summary>The time half of the check: the same length, a different last-write time.</summary>
    [Fact]
    public async Task A_same_length_change_with_a_new_write_time_is_refused()
    {
        var path = Write("race.ini", "a=1\n");
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage != FileSystemService.Stage.EditTempWritten) return;
                File.WriteAllText(path, "z=7\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=9", false, "auto");

        await act.Should().ThrowAsync<IOException>();
        File.ReadAllText(path).Should().Be("z=7\n");
        OnlyTheseEntries("race.ini");
    }

    /// <summary>The length half of the check: a longer file with its last-write time put back.</summary>
    [Fact]
    public async Task A_length_change_with_the_write_time_put_back_is_refused()
    {
        var path = Write("race.ini", "a=1\n");
        var stamp = File.GetLastWriteTimeUtc(path);
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage != FileSystemService.Stage.EditTempWritten) return;
                File.WriteAllText(path, "a=1\nmore\n");
                File.SetLastWriteTimeUtc(path, stamp);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=9", false, "auto");

        await act.Should().ThrowAsync<IOException>();
        File.ReadAllText(path).Should().Be("a=1\nmore\n");
        OnlyTheseEntries("race.ini");
    }

    /// <summary>
    /// "A sharing violation is retried twice (50 ms, 100 ms) as file_write does." Another process
    /// (an editor, an indexer) holds the target open without delete sharing for ~20 ms - well inside
    /// the first retry delay - and the edit still lands.
    /// </summary>
    [Fact]
    public async Task A_sharing_violation_that_clears_is_retried_and_the_edit_lands()
    {
        var path = Write("busy.ini", "a=1\n");
        Task? release = null;
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage != FileSystemService.Stage.EditTempWritten) return;
                var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // no FileShare.Delete: File.Replace fails
                release = Task.Delay(20).ContinueWith(_ => holder.Dispose(), TaskScheduler.Default);
            },
        };

        var result = await svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        release.Should().NotBeNull("the StageHook seam is called once the temp file is written");
        await release!;
        result.Replacements.Should().Be(1);
        File.ReadAllText(path).Should().Be("a=2\n");
        OnlyTheseEntries("busy.ini");
    }

    /// <summary>
    /// "any failure deletes the temp file and leaves the original byte-for-byte": a target held for
    /// the whole call fails the replace on every attempt; the caller gets the IOException, the
    /// original is untouched and no temp file is left behind.
    /// </summary>
    [Fact]
    public async Task A_sharing_violation_that_never_clears_fails_with_the_original_intact_and_no_temp_file()
    {
        var original = "a=1\n"u8.ToArray();
        var path = Write("busy.ini", original);
        FileStream? holder = null;
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage == FileSystemService.Stage.EditTempWritten)
                    holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            },
        };
        try
        {
            var edit = svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

            // "retried twice (50 ms, 100 ms)": a lasting violation gives up in well under a second.
            (await Task.WhenAny(edit, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(edit,
                "a sharing violation is retried twice, not until the other process lets go");
            var act = () => edit;
            await act.Should().ThrowAsync<IOException>();
        }
        finally
        {
            holder?.Dispose();
        }
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("busy.ini");
    }

    // ==== R2 decision 1: a failed replace never loses the file ======================================

    /// <summary>An IOException with the Win32 code in its HResult, as File.Replace throws one.</summary>
    private static IOException Win32Io(int code, string message) => new(message, unchecked((int)0x80070000) | code);

    private static async Task<Exception?> Record(Func<Task> call)
    {
        try { await call(); return null; }
        catch (Exception ex) { return ex; }
    }

    private const string BackupName = @"^r\.ini\.bak\.[0-9a-fA-F-]{32,36}$";

    /// <summary>
    /// (a) Second review pass, decision A - REVERSES the round-2 row that expected the temp to be
    /// moved into place and the call to succeed. With a backup name every documented ReplaceFile
    /// failure leaves the target or the backup, so "no file at the path and no backup" means someone
    /// else deleted the target, and their deletion wins: the call throws an IOException saying the
    /// file was removed while it was being edited, naming the temp, which keeps the edited content;
    /// path stays absent; no retry (path is gone) and no backup litter.
    /// </summary>
    [Fact]
    public async Task A_target_removed_during_the_replace_is_not_recreated_and_the_error_names_the_temp()
    {
        var path = Write("r.ini", "a=1\n");
        var calls = 0;
        string? temp = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                temp = source;
                File.Delete(destination);
                throw Win32Io(1176, "Unable to move the replacement file to the file to be replaced.");
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        var message = (await act.Should().ThrowAsync<IOException>()).Which.Message;
        message.Should().MatchRegex("(?i)removed|deleted", "the error says the file was removed while it was being edited");
        message.Should().Contain(temp!, "and names the file that holds the edited content");
        calls.Should().Be(1, "path was gone after the first attempt, so there is no retry");
        File.Exists(path).Should().BeFalse("the recovery never recreates a file someone else deleted");
        File.ReadAllText(temp!).Should().Be("a=2\n", "the temp is left holding the edited content");
        OnlyTheseEntries(Path.GetFileName(temp!));
    }

    /// <summary>
    /// (b) The 1177 shape: the original was moved to the backup name, then the replace failed.
    /// "the backup is moved back to path when it exists". The backup name is &lt;path&gt;.bak.&lt;guid&gt;
    /// beside the target.
    /// </summary>
    [Fact]
    public async Task A_replace_that_moved_the_original_to_the_backup_and_failed_puts_the_original_back()
    {
        var path = Write("r.ini", "a=1\n");
        string? backupSeen = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                backupSeen ??= backup;
                File.Move(destination, backup ?? destination + ".lost");
                throw Win32Io(1177, "Unable to move the replacement file to the file to be replaced.");
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowAsync<IOException>();
        backupSeen.Should().NotBeNull("the replace is given a backup name, so the original always survives under some name");
        Path.GetDirectoryName(backupSeen).Should().Be(_tmp, "the backup is beside the target");
        Path.GetFileName(backupSeen).Should().MatchRegex(BackupName);
        File.ReadAllText(path).Should().Be("a=1\n", "the original is restored from the backup");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>(c) "on success the backup is deleted (best effort)".</summary>
    [Fact]
    public async Task A_successful_replace_uses_a_backup_beside_the_target_and_leaves_none()
    {
        var path = Write("r.ini", "a=1\n");
        string? backupSeen = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                backupSeen = backup;
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        await svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        File.ReadAllText(path).Should().Be("a=2\n");
        backupSeen.Should().NotBeNull();
        Path.GetFileName(backupSeen).Should().MatchRegex(BackupName);
        File.Exists(backupSeen!).Should().BeFalse("the backup is deleted once the replace has succeeded");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>(d) A sharing violation leaves path in place, so it is retried - through the seam.</summary>
    [Fact]
    public async Task A_sharing_violation_with_the_target_in_place_is_retried_through_the_replace_seam()
    {
        var path = Write("r.ini", "a=1\n");
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (++calls == 1)
                    throw Win32Io(32, "The process cannot access the file because it is being used by another process.");
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        await svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        calls.Should().Be(2);
        File.ReadAllText(path).Should().Be("a=2\n");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>
    /// (e) Decision A with a cancel requested in the same failure - REVERSES the round-2 row
    /// (success). Pinned as IOException, not OperationCanceledException: the exception being handled
    /// is the replace's own IOException; the token is observed only in the retry delay, which is not
    /// reached because path is gone; and the caller has to be told where the edited content is,
    /// which a bare cancel would hide.
    /// </summary>
    [Fact]
    public async Task A_cancel_with_a_replace_that_removed_the_target_still_reports_where_the_content_is()
    {
        var path = Write("r.ini", "a=1\n");
        using var cts = new CancellationTokenSource();
        var calls = 0;
        string? temp = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                temp = source;
                File.Delete(destination);
                cts.Cancel();
                throw Win32Io(1176, "Unable to move the replacement file to the file to be replaced.");
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto", cts.Token);

        (await act.Should().ThrowExactlyAsync<IOException>()).Which.Message.Should().Contain(temp!);
        calls.Should().Be(1);
        File.Exists(path).Should().BeFalse();
        File.ReadAllText(temp!).Should().Be("a=2\n");
        OnlyTheseEntries(Path.GetFileName(temp!));
    }

    /// <summary>(e) A cancel during the retry delay after a sharing violation: the original stays, no temp, no backup.</summary>
    [Fact]
    public async Task A_cancel_during_the_retry_delay_leaves_the_original_and_no_litter()
    {
        var path = Write("r.ini", "a=1\n");
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                cts.Cancel();
                throw Win32Io(32, "The process cannot access the file because it is being used by another process.");
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1, "the cancel is honoured in the retry delay");
        File.ReadAllText(path).Should().Be("a=1\n");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>
    /// (f) Re-read against decision A: the temp is no longer moved, so a lock on it changes nothing -
    /// the IOException names the temp either way, and the temp (held here by another process) keeps
    /// the edited content. Kept as the "another process holds the temp" variant of row (a).
    /// </summary>
    [Fact]
    public async Task When_nothing_can_be_put_back_the_error_names_the_file_holding_the_content()
    {
        var path = Write("r.ini", "a=1\n");
        FileStream? held = null;
        string? temp = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                temp = source;
                File.Delete(destination);
                held ??= new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
                throw Win32Io(1176, "Unable to move the replacement file to the file to be replaced.");
            },
        };
        try
        {
            var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

            var message = (await act.Should().ThrowAsync<IOException>()).Which.Message;
            temp.Should().NotBeNull("the replace seam was called");
            message.Should().Contain(temp!, "the content survives only in the temp file, and the caller is told where");
        }
        finally
        {
            held?.Dispose();
        }
        File.ReadAllText(temp!).Should().Be("a=2\n", "the temp is not deleted while path is missing");
    }

    /// <summary>
    /// The other restore that can fail: the original went to the backup name (1177 shape) and the
    /// backup cannot be moved back. The message names the backup - the file holding the content.
    /// </summary>
    [Fact]
    public async Task When_the_backup_cannot_be_put_back_the_error_names_the_backup()
    {
        var path = Write("r.ini", "a=1\n");
        FileStream? held = null;
        string? backupSeen = null;
        string? temp = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                backupSeen = backup;
                temp = source;
                File.Move(destination, backup!);
                held ??= new FileStream(backup!, FileMode.Open, FileAccess.Read, FileShare.None);
                throw Win32Io(1177, "Unable to move the replacement file to the file to be replaced.");
            },
        };
        try
        {
            var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

            var message = (await act.Should().ThrowAsync<IOException>()).Which.Message;
            message.Should().Contain(backupSeen!, "the original survives only under the backup name, and the caller is told where");
        }
        finally
        {
            held?.Dispose();
        }
        File.ReadAllText(backupSeen!).Should().Be("a=1\n", "nothing is deleted while path is missing");
        File.ReadAllText(temp!).Should().Be("a=2\n", "the temp is deleted only once path exists - the edit survives too");
    }

    /// <summary>
    /// A replace that loses both files (path removed and the temp gone with it) cannot be recovered;
    /// the call must still fail - never report an edit that is nowhere.
    /// </summary>
    [Fact]
    public async Task A_replace_that_loses_both_files_is_reported_as_a_failure()
    {
        var path = Write("r.ini", "a=1\n");
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                File.Delete(destination);
                File.Delete(source);
                throw Win32Io(1176, "Unable to move the replacement file to the file to be replaced.");
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowAsync<IOException>();
    }

    /// <summary>
    /// GREEN round 2 finding. "if they changed, the edit is abandoned with an IOException, nothing
    /// written". When the change is that someone DELETED the target, path is missing, the replace
    /// never started, and the cleanup ("nothing is deleted unless a file is at the path") keeps the
    /// temp: a &lt;path&gt;.tmp.&lt;guid&gt; with the edited content is left where the deleted file was.
    /// The keep-the-temp rule is for a replace that removed the original; here nothing of ours
    /// touched it.
    /// </summary>
    [Fact]
    public async Task A_target_deleted_while_being_edited_is_refused_and_leaves_no_temp_file()
    {
        var path = Write("gone.ini", "a=1\n");
        var svc = new FileSystemService
        {
            StageHook = (stage, _) =>
            {
                if (stage == FileSystemService.Stage.EditTempWritten) File.Delete(path);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().ContainEquivalentOf("changed while it was being edited");
        File.Exists(path).Should().BeFalse("the other party's delete wins, as any other concurrent change does");
        OnlyTheseEntries();
    }

    /// <summary>
    /// A file another process holds with no sharing: the link count cannot be read (it is null, and
    /// the edit proceeds), and the read then fails - an IOException about access, not a hard-link
    /// refusal, with nothing written.
    /// </summary>
    [Fact]
    public async Task A_file_held_without_sharing_is_refused_by_the_read_not_as_a_hard_link()
    {
        var path = Write("locked.ini", "a=1\n");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = () => Svc().EditTextAsync(path, "a=1", "a=2", false, "auto");

            (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().NotMatchRegex("(?i)hard[- ]?link");
        }
        File.ReadAllText(path).Should().Be("a=1\n");
        OnlyTheseEntries("locked.ini");
    }

    // ==== PR #47 finding 1: every retry re-checks the target ==========================================

    private static string ChangedMessage(string path) =>
        $"File '{path}' changed while it was being edited; nothing was written. Read it again and retry the edit.";

    private static IOException SharingViolation() =>
        Win32Io(32, "The process cannot access the file because it is being used by another process.");

    /// <summary>
    /// R1/R2: the length + last-write check ran once, before the retry loop. A sharing violation on
    /// attempt 1, another writer during the delay, and the retry replaced their newer content with
    /// the stale edit. Now the target is re-checked before every attempt: a change is the existing
    /// "changed while it was being edited" refusal, ReplaceFile is not called again, the other
    /// writer's bytes stay, and no temp or backup is left. This row: the new content is LONGER.
    /// </summary>
    [Fact]
    public async Task A_target_rewritten_during_the_retry_delay_is_not_overwritten_by_the_stale_edit()
    {
        var path = Write("r.ini", "a=1\n");
        var theirs = "a=1\nb=2 written during the retry delay\n"u8.ToArray();
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (++calls == 1)
                {
                    File.WriteAllBytes(destination, theirs);
                    throw SharingViolation();
                }
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowExactlyAsync<IOException>().WithMessage(ChangedMessage(path));
        calls.Should().Be(1, "a changed target is refused before ReplaceFile is called again");
        File.ReadAllBytes(path).Should().Equal(theirs, "the other writer's content is kept byte for byte");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>R1/R2, the time half: the same LENGTH, a later last-write time.</summary>
    [Fact]
    public async Task A_same_length_rewrite_during_the_retry_delay_is_not_overwritten_by_the_stale_edit()
    {
        var path = Write("r.ini", "a=1\n");
        var theirs = "z=9\n"u8.ToArray();
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (++calls == 1)
                {
                    File.WriteAllBytes(destination, theirs);
                    File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddHours(1));
                    throw SharingViolation();
                }
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowExactlyAsync<IOException>().WithMessage(ChangedMessage(path));
        calls.Should().Be(1);
        File.ReadAllBytes(path).Should().Equal(theirs);
        OnlyTheseEntries("r.ini");
    }

    /// <summary>
    /// R3: the re-check runs before EVERY retry: attempt 1 fails with the target untouched, attempt
    /// 2 fails and the target changes - refused, no third call.
    /// </summary>
    [Fact]
    public async Task A_target_rewritten_before_the_second_retry_is_refused_without_a_third_call()
    {
        var path = Write("r.ini", "a=1\n");
        var theirs = "a=1\nb=2 written before the second retry\n"u8.ToArray();
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                if (calls == 1) throw SharingViolation();
                if (calls == 2)
                {
                    File.WriteAllBytes(destination, theirs);
                    throw SharingViolation();
                }
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowExactlyAsync<IOException>().WithMessage(ChangedMessage(path));
        calls.Should().Be(2, "the change after attempt 2 is caught before attempt 3");
        File.ReadAllBytes(path).Should().Equal(theirs);
        OnlyTheseEntries("r.ini");
    }

    /// <summary>
    /// R1, the length half on its own: during the delay the target grows but its last-write time is
    /// put back, so only the length says it changed. (Every other "rewrite" row also moves the time,
    /// so without this row the re-check could drop its length test and stay green.)
    /// </summary>
    [Fact]
    public async Task A_longer_rewrite_with_the_write_time_put_back_during_the_retry_delay_is_refused()
    {
        var path = Write("r.ini", "a=1\n");
        var stamp = File.GetLastWriteTimeUtc(path);
        var theirs = "a=1\nmore\n"u8.ToArray();
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (++calls == 1)
                {
                    File.WriteAllBytes(destination, theirs);
                    File.SetLastWriteTimeUtc(destination, stamp);
                    throw SharingViolation();
                }
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        await act.Should().ThrowExactlyAsync<IOException>().WithMessage(ChangedMessage(path));
        calls.Should().Be(1);
        File.ReadAllBytes(path).Should().Equal(theirs);
        OnlyTheseEntries("r.ini");
    }

    /// <summary>R4 (unchanged): three sharing violations with the target untouched still surface the IOException, original intact.</summary>
    [Fact]
    public async Task Three_sharing_violations_with_the_target_untouched_still_surface_the_ioexception()
    {
        var path = Write("r.ini", "a=1\n");
        var calls = 0;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                throw SharingViolation();
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        (await act.Should().ThrowExactlyAsync<IOException>()).Which.Message.Should().Contain("being used by another process");
        calls.Should().Be(3, "the first attempt and two retries");
        File.ReadAllText(path).Should().Be("a=1\n");
        OnlyTheseEntries("r.ini");
    }

    /// <summary>
    /// R4 (unchanged): a target that vanishes DURING the retry delay is not retried and not recreated
    /// (decision A). The deletion comes from another thread 5 ms into the 50 ms delay - the catch
    /// filter has already seen the file - so only a re-check after the delay can notice it. The call
    /// fails naming the temp, which keeps the edited content; path stays absent.
    /// </summary>
    [Fact]
    public async Task A_target_that_vanishes_during_the_retry_delay_is_not_retried_or_recreated()
    {
        var path = Write("r.ini", "a=1\n");
        var calls = 0;
        string? temp = null;
        Thread? deleter = null;
        var svc = new FileSystemService
        {
            ReplaceFile = (source, destination, backup) =>
            {
                calls++;
                temp = source;
                if (calls == 1)
                {
                    deleter = new Thread(() => { Thread.Sleep(5); File.Delete(destination); }) { IsBackground = true };
                    deleter.Start();
                    throw SharingViolation();
                }
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
            },
        };

        var act = () => svc.EditTextAsync(path, "a=1", "a=2", false, "auto");

        var message = (await act.Should().ThrowAsync<IOException>()).Which.Message;
        deleter!.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        calls.Should().Be(1, "a target that is gone after the delay is not replaced again");
        File.Exists(path).Should().BeFalse("the recovery never recreates a file someone else deleted");
        message.Should().Contain(temp!, "the caller is told where the edited content is");
        File.ReadAllText(temp!).Should().Be("a=2\n");
    }

    // ==== PR #47 finding 2: the edited size is bounded, in 64 bits ======================================

    /// <summary>
    /// R7: the cap is also on the ENCODED result. A UTF-16 file of about 2 MiB whose edit makes
    /// 9,437,185 characters - under the 16 MiB character cap - encodes to 18,874,372 bytes (BOM
    /// included): refused naming the path, the byte count and the limit; file unchanged, no litter.
    /// </summary>
    [Fact]
    public async Task An_edit_whose_encoded_result_exceeds_16_MiB_is_refused_and_nothing_is_written()
    {
        var enc = new UnicodeEncoding(false, true);
        var original = enc.GetPreamble().Concat(enc.GetBytes("n" + new string('a', 1 << 20))).ToArray();
        var path = Write("big16.txt", original);
        var before = Sha(path);

        var act = () => Svc().EditTextAsync(path, "a", new string('a', 9), replaceAll: true, "auto");

        var message = (await act.Should().ThrowExactlyAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain(path);
        var compact = System.Text.RegularExpressions.Regex.Replace(message, @"(?<=\d)[,.\u00a0\u202f ](?=\d{3}\b)", "");
        (compact.Contains("18874372") || compact.Contains("18874370")).Should().BeTrue($"the refusal names the encoded size; it said: {message}");
        (compact.Contains("16777216") || message.Contains("16 MiB", StringComparison.OrdinalIgnoreCase)).Should().BeTrue($"and the limit; it said: {message}");
        Sha(path).Should().Be(before);
        OnlyTheseEntries("big16.txt");
    }

    /// <summary>
    /// R7 at the byte boundary. A file of exactly 16 MiB of ASCII where the edit swaps one ASCII
    /// character for "é" (two bytes in UTF-8): the character count stays at the cap, so only the
    /// encoded-size check can refuse it - one byte over, refused, file unchanged. (The exactly-16-MiB
    /// edit row is the other side of the boundary: allowed.)
    /// </summary>
    [Fact]
    public async Task An_edit_that_encodes_one_byte_over_16_MiB_is_refused()
    {
        var bytes = new byte[TextEdit.MaxFileBytes];
        Array.Fill(bytes, (byte)'x');
        "needle\n"u8.CopyTo(bytes);
        var path = Write("edge.log", bytes);
        var before = Sha(path);

        var act = () => Svc().EditTextAsync(path, "needle", "needl\u00e9", false, "auto");

        var message = (await act.Should().ThrowExactlyAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain(path).And.Contain((TextEdit.MaxFileBytes + 1).ToString());
        Sha(path).Should().Be(before);
        OnlyTheseEntries("edge.log");
    }

    // ==== R2 decision 2: no stray \r on disk ===========================================================

    [Fact]
    public async Task Deleting_an_lf_led_line_from_a_crlf_file_leaves_no_stray_cr_on_disk()
    {
        var path = Write("crlf.ini", "a\r\nfoo\r\nb");

        await Svc().EditTextAsync(path, "\nfoo", "", false, "auto");

        File.ReadAllBytes(path).Should().Equal("a\r\nb"u8.ToArray());
    }

    // ==== R2 decision 3: the temp file is as private as the target ===================================

    private static (bool Protected, string[] Rules) Dacl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        return (security.AreAccessRulesProtected,
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(r => $"{r.IdentityReference}|{r.FileSystemRights}|{r.AccessControlType}|inherited={r.IsInherited}")
                .OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// "The temp is created with the target's DACL ... so the edited content of a file with a
    /// restrictive explicit ACL is never briefly readable through the folder's inherited ACL." The
    /// target allows only the current user, protected from inheritance; the temp folder's inherited
    /// ACL is broader. Compared as rule sets plus the protection flag (SDDL flag bits such as AI can
    /// differ between a DACL set afterwards and one given at creation).
    /// </summary>
    [Fact]
    public async Task The_temp_file_has_the_targets_dacl_and_the_target_keeps_it()
    {
        var path = Write("secret.ini", "token=1\n");
        var restrictive = new FileSecurity();
        restrictive.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restrictive.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(restrictive);
        try
        {
            var target = Dacl(path);
            target.Protected.Should().BeTrue("precondition");
            target.Rules.Should().ContainSingle("precondition: only the current user");
            (bool Protected, string[] Rules)? atTemp = null;
            var svc = new FileSystemService
            {
                StageHook = (stage, arg) =>
                {
                    if (stage == FileSystemService.Stage.EditTempWritten) atTemp = Dacl(arg);
                },
            };

            await svc.EditTextAsync(path, "token=1", "token=2", false, "auto");

            atTemp.Should().NotBeNull("the hook saw the temp file");
            atTemp!.Value.Protected.Should().BeTrue("the temp does not inherit the folder's ACL");
            atTemp.Value.Rules.Should().Equal(target.Rules, "the edited content is never readable through the folder's ACL");
            var after = Dacl(path);
            after.Protected.Should().BeTrue();
            after.Rules.Should().Equal(target.Rules, "the target keeps its DACL");
            File.ReadAllText(path).Should().Be("token=2\n");
        }
        finally
        {
            if (File.Exists(path))
            {
                var open = new FileSecurity();
                open.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(path).SetAccessControl(open);
            }
        }
    }

    // ==== R2 decision 5: auto does not guess UTF-16 ====================================================

    /// <summary>A one-character old_string WOULD match ("a" is in "a\0=\01\0..."): the refusal comes first.</summary>
    [Fact]
    public async Task Bomless_utf16_under_auto_is_refused_even_when_old_string_would_match()
    {
        var original = new UnicodeEncoding(false, false).GetBytes("a=1\n");
        var path = Write("u16.ini", original);

        var act = () => Svc().EditTextAsync(path, "a", "b", false, "auto");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("utf-16");
        File.ReadAllBytes(path).Should().Equal(original);
        OnlyTheseEntries("u16.ini");
    }

    [Fact]
    public async Task An_explicit_utf8_edits_a_file_with_a_nul_in_it()
    {
        var path = Write("nul.ini", "a=1\0x\n"u8.ToArray());

        await Svc().EditTextAsync(path, "a=1", "a=2", false, "utf-8");

        File.ReadAllBytes(path).Should().Equal("a=2\0x\n"u8.ToArray());
    }

    [Fact]
    public async Task An_explicit_utf16_edits_bomless_utf16()
    {
        var enc = new UnicodeEncoding(false, false);
        var path = Write("u16.ini", enc.GetBytes("a=1\n"));

        await Svc().EditTextAsync(path, "a=1", "a=2", false, "utf-16");

        File.ReadAllBytes(path).Should().Equal(enc.GetBytes("a=2\n"));
    }

    // ==== identity kept by File.Replace ==============================================================

    /// <summary>
    /// R2 decision 8 (from the GREEN finding): File.Replace gives the path a new file record, so a
    /// hard-linked file's other names would keep the old content. "A target with more than one hard
    /// link is refused with InvalidOperationException naming the link count, nothing written." Three
    /// names, so the count in the message is distinctive. A single-link file is every other row here.
    /// </summary>
    [Fact]
    public async Task A_hard_linked_file_is_refused_naming_the_link_count_and_nothing_is_written()
    {
        var first = Write("first.ini", "x=1\n");
        var second = Path.Combine(_tmp, "second.ini");
        var third = Path.Combine(_tmp, "third.ini");
        HardLink(second, first);
        HardLink(third, first);

        var act = () => Svc().EditTextAsync(first, "x=1", "x=2", false, "auto");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().MatchRegex(@"(?i)hard[- ]?link", "the refusal says why");
        message.Should().MatchRegex(@"\b3\b", "and names the link count");
        File.ReadAllText(first).Should().Be("x=1\n");
        File.ReadAllText(second).Should().Be("x=1\n");
        File.ReadAllText(third).Should().Be("x=1\n");
        OnlyTheseEntries("first.ini", "second.ini", "third.ini");
    }

    private static void HardLink(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/H", link, target }) psi.ArgumentList.Add(arg);
        using var mklink = System.Diagnostics.Process.Start(psi)!;
        mklink.WaitForExit(30_000);
        mklink.ExitCode.Should().Be(0, "a hard link needs no privilege on NTFS - the link is what this test is about");
    }

    /// <summary>
    /// "Unlike the move file_write uses, a replace keeps the target's identity: its ACL, attributes,
    /// creation time and alternate data streams." A move-into-place would give the file the temp's
    /// creation time, drop the hidden bit and lose the stream.
    /// </summary>
    [Fact]
    public async Task The_edit_keeps_the_hidden_attribute_the_creation_time_and_an_alternate_data_stream()
    {
        var path = Write("identity.ini", "a=1\n");
        File.WriteAllText(path + ":wmcp-ads", "stream payload");
        var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);

        await Svc().EditTextAsync(path, "a=1", "a=2", false, "auto");

        File.ReadAllText(path).Should().Be("a=2\n");
        (File.GetAttributes(path) & FileAttributes.Hidden).Should().NotBe(0, "the hidden bit survives the edit");
        File.GetCreationTimeUtc(path).Should().Be(created, "the file keeps its identity, so its creation time");
        File.ReadAllText(path + ":wmcp-ads").Should().Be("stream payload", "the alternate data stream survives");
        OnlyTheseEntries("identity.ini");
    }
}
