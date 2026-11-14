using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// PR #47 finding 2, R8: <c>file_edit</c> through the real <see cref="FileSystemService"/> on a real
/// temp file. The overflow case (2 MiB of 'a', old "a", a 1,024-character new_string, replace_all)
/// used to escape as ArgumentOutOfRangeException from a negative StringBuilder capacity; the caller
/// must get the deliberate refusal - an InvalidOperationException, which ToolErrors surfaces - and
/// the file must be untouched. One temp directory, removed in <see cref="Dispose"/>.
/// </summary>
[Trait("Category", "Integration")]
public class FileToolsEditIntegrationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wmcp-edit-tool-" + Guid.NewGuid().ToString("N"));

    public FileToolsEditIntegrationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static FileTools Tools() => new(
        new FileSystemService(),
        new Mock<IInputService>().Object,
        new Mock<IFileStreamService>().Object);

    [Fact]
    public async Task An_edit_whose_result_would_overflow_is_refused_as_a_caller_facing_error_and_the_file_is_unchanged()
    {
        var path = Path.Combine(_tmp, "huge.txt");
        File.WriteAllText(path, new string('a', 2 * 1024 * 1024));
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var act = () => Tools().FileEdit(path, "a", new string('b', 1024), replace_all: true, confirm: true);

        var error = (await act.Should().ThrowExactlyAsync<InvalidOperationException>(
            "the size refusal is deliberate, not an ArgumentOutOfRangeException from a negative capacity")).Which;
        ToolErrors.IsCallerFacing(error).Should().BeTrue("the client sees the message, not the SDK's masked error");
        error.Message.Should().Contain(path).And.ContainEquivalentOf("nothing was changed");
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Should().Be(before);
        Directory.EnumerateFileSystemEntries(_tmp).Should().ContainSingle("no temp or backup is left beside the file");
    }
}
