using System.Text;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class FileSystemServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"wm-test-{Guid.NewGuid():N}");
    public FileSystemServiceTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task WriteText_then_ReadText_roundtrips_utf8()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "test.txt");
        await svc.WriteTextAsync(path, "héllo wörld", "utf-8", append: false, createParents: true);
        var got = await svc.ReadTextAsync(path, 1024, "utf-8");
        got.Should().Be("héllo wörld");
    }

    /// <summary>
    /// The other two encodings the tool advertises. Pre-C-1 behaviour, pinned here because the
    /// write path grew two parameters around this switch and nothing else reads these arms.
    /// </summary>
    [Theory]
    [InlineData("utf-16", "héllo wörld")]
    [InlineData("ascii", "hello world")]
    public async Task WriteText_then_ReadText_roundtrips_the_named_encoding(string encoding, string content)
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, $"enc-{encoding}.txt");

        await svc.WriteTextAsync(path, content, encoding, append: false, createParents: true);

        (await svc.ReadTextAsync(path, 1024, encoding)).Should().Be(content);
    }

    /// <summary>
    /// "auto" is <c>file_read</c>'s DEFAULT encoding and the one a C-1 line window decodes
    /// through, so the BOM sniff decides what a caller who said nothing at all gets back.
    /// </summary>
    [Fact]
    public async Task ReadText_auto_decodes_by_the_byte_order_mark()
    {
        var svc = new FileSystemService();
        var utf8 = Path.Combine(_tmp, "bom-utf8.txt");
        var utf16 = Path.Combine(_tmp, "bom-utf16.txt");
        await File.WriteAllTextAsync(utf8, "héllo wörld", new UTF8Encoding(true));
        await File.WriteAllTextAsync(utf16, "héllo wörld", new UnicodeEncoding(false, true));

        (await svc.ReadTextAsync(utf8, 1024, "auto")).Should().Be("héllo wörld");
        (await svc.ReadTextAsync(utf16, 1024, "auto")).Should().Be("héllo wörld",
            "a UTF-16 file read as UTF-8 would come back as mojibake with a NUL between every letter");
    }

    [Fact]
    public async Task ReadText_throws_when_file_exceeds_max_bytes()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "big.txt");
        await File.WriteAllTextAsync(path, new string('x', 2000));
        Func<Task> act = () => svc.ReadTextAsync(path, 100, "utf-8");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public async Task WriteText_is_atomic_via_temp_file_rename()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "atomic.txt");
        await File.WriteAllTextAsync(path, "original");

        // Start a write and verify the original is intact until rename
        var task = svc.WriteTextAsync(path, "new content", "utf-8", append: false, createParents: true);
        await task;
        (await File.ReadAllTextAsync(path)).Should().Be("new content");
    }

    [Fact]
    public async Task Search_finds_files_matching_pattern()
    {
        var svc = new FileSystemService();
        await File.WriteAllTextAsync(Path.Combine(_tmp, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(_tmp, "c.log"), "c");
        var hits = await svc.SearchAsync(_tmp, "*.txt", null, null, false);
        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task HashFileAsync_computes_known_sha256()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "abc.txt");
        await File.WriteAllTextAsync(path, "abc");

        var hash = await svc.HashFileAsync(path, "sha256");

        // Canonical SHA-256("abc").
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task HashFileAsync_rejects_unknown_algorithm()
    {
        var svc = new FileSystemService();
        var path = Path.Combine(_tmp, "x.txt");
        await File.WriteAllTextAsync(path, "x");

        var act = () => svc.HashFileAsync(path, "crc32");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*algorithm*");
    }

    [Fact]
    public async Task Search_find_duplicates_skips_locked_files_without_aborting()
    {
        var svc = new FileSystemService();
        const string content = "duplicate-content-xyz";
        var f1 = Path.Combine(_tmp, "dup1.bin");
        var f2 = Path.Combine(_tmp, "dup2.bin");
        var locked = Path.Combine(_tmp, "dup3-locked.bin");
        await File.WriteAllTextAsync(f1, content);
        await File.WriteAllTextAsync(f2, content);
        await File.WriteAllTextAsync(locked, content);

        // Hold the third file open exclusively so HashFile's File.OpenRead throws IOException.
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var dups = await svc.SearchAsync(_tmp, "*.bin", null, null, findDuplicates: true);

        // The two accessible identical files are still found; the locked one is skipped, not fatal.
        dups.Select(d => d.Path).Should().BeEquivalentTo(new[] { f1, f2 });
    }

    // ---- C-1 R4d-5: the failure sentence and the tail are two sentences -------------------------

    /// <summary>
    /// The second half of <c>Aside.NotRestored</c>'s message, quoted here as the caller sees it so
    /// the join is asserted against a real tail rather than a placeholder.
    /// </summary>
    private const string Tail = "The destination 'C:\\dst' could not be put back as it was; its previous content is at 'C:\\dst.replaced.0'.";

    /// <summary>
    /// R4d-5: <c>NotRestored</c> glues the underlying failure's message to its own sentence. Most
    /// framework messages end in a period, so the join reads correctly by luck; the ones that do
    /// not — a Win32 message, or the very common "…the file 'C:\x'" that ends on a quote — run the
    /// two sentences together into one unreadable line ("cannot access the file 'C:\x' The
    /// destination…"). The cause is terminated when it does not terminate itself, and an empty
    /// cause contributes nothing at all rather than a leading ". ".
    /// <para>
    /// A unit test rather than an integration one because the causes that need this are exactly
    /// the ones a test cannot provoke on demand: the IOException a locked file produces
    /// (<see cref="FileSystemServiceRound4Tests.A_restore_that_cannot_put_the_destination_back_says_where_the_previous_content_is"/>)
    /// already ends in a period, so it exercises the first row only.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("boom", "boom. " + Tail)]
    [InlineData("boom.", "boom. " + Tail)]
    [InlineData("boom!", "boom! " + Tail)]
    [InlineData("boom?", "boom? " + Tail)]
    [InlineData("boom'", "boom'. " + Tail)]
    [InlineData("  boom  ", "boom. " + Tail)]
    [InlineData("", Tail)]
    [InlineData("   ", Tail)]
    public void TwoSentences_terminates_a_cause_that_does_not_terminate_itself(string cause, string expected)
        => FileSystemService.TwoSentences(cause, Tail).Should().Be(expected);

    /// <summary>
    /// C-1 R4e-5: a cause that ends in a colon, a semicolon or an ellipsis already reads as the
    /// end of a clause; the added period turns it into <c>":."</c> — which no message should ever
    /// contain, and Win32 causes end in a colon often enough ("the process cannot access the file:")
    /// for it to be the first thing a caller sees. Terminating punctuation is what
    /// <see cref="FileSystemService.TwoSentences"/> is looking for, and these three are it.
    /// </summary>
    [Theory]
    [InlineData("ends with colon:", "ends with colon: " + Tail)]
    [InlineData("ends with semicolon;", "ends with semicolon; " + Tail)]
    [InlineData("ends with an ellipsis…", "ends with an ellipsis… " + Tail)]
    public void TwoSentences_adds_no_period_after_a_colon_semicolon_or_ellipsis(string cause, string expected)
        => FileSystemService.TwoSentences(cause, Tail).Should().Be(expected);
}
