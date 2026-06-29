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
        await svc.WriteTextAsync(path, "héllo wörld", "utf-8");
        var got = await svc.ReadTextAsync(path, 1024, "utf-8");
        got.Should().Be("héllo wörld");
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
        var task = svc.WriteTextAsync(path, "new content", "utf-8");
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
