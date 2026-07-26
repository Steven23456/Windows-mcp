using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// End-to-end coverage for <c>disk_inspect mode:reclaimable</c> against a REAL
/// <see cref="PowerShellService"/>.
/// </summary>
/// <remarks>
/// The unit tests in <see cref="DiskServiceTests"/> mock <see cref="IPowerShellService"/> and feed
/// <see cref="DiskService.GetReclaimableAsync"/> a hand-written JSON string, so they only ever
/// exercised the parsing half. That is exactly why the real defect shipped undetected: the
/// PowerShell invocation returned EMPTY stdout on exit 0 (the script was piped to
/// <c>-Command -</c>, which evaluates stdin line by line and mangled the trailing multi-line
/// <c>[PSCustomObject]@{...} | ConvertTo-Json</c>), while every mocked test stayed green.
///
/// Mocking the collaborator that is broken hides the bug. These tests drive the real thing.
/// </remarks>
[Trait("Category", "Integration")]
public class DiskServiceReclaimableIntegrationTests
{
    private static DiskService MakeReal(IPowerShellService ps)
        => new(new Mock<IFileSystemService>().Object, ps);

    [Fact]
    public async Task GetReclaimableAsync_returns_real_data_through_actual_powershell()
    {
        using var ps = new PowerShellService(NullLogger.Instance);

        // Must not throw. The service deliberately throws on empty stdout rather than returning a
        // zeroed result, so the original defect surfaces here as an InvalidOperationException.
        var result = await MakeReal(ps).GetReclaimableAsync();

        result.Should().NotBeNull();
        result.TotalBytes.Should().Be(
            result.TempBytes + result.InetCacheBytes + result.RecycleBinBytes,
            "the script computes Total as the sum of its parts");
        result.TotalBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Directly pins the root cause: a script whose final statement spans multiple lines must
    /// still produce output. This is the shape of every "…| ConvertTo-Json" script in the repo.
    /// </summary>
    [Fact]
    public async Task Multiline_ConvertToJson_script_is_not_silently_swallowed()
    {
        using var ps = new PowerShellService(NullLogger.Instance);

        var script = "$a = 2\n$b = 3\n[PSCustomObject]@{\n    Sum = $a + $b\n} | ConvertTo-Json";
        var result = await ps.RunAsync(script);

        result.Stdout.Should().NotBeNullOrWhiteSpace(
            "exit 0 with empty stdout is the silent-failure signature this guards against");
        result.Stdout.Should().Contain("\"Sum\"").And.Contain("5");
    }
}
