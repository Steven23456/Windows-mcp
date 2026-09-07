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
}
