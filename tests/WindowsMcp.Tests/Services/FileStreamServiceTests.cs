using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

public class FileStreamServiceUnitTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ParseStreams_drops_the_default_data_stream()
    {
        var streams = FileStreamService.ParseStreams(
            """[{"Stream":":$DATA","Length":12},{"Stream":":Zone.Identifier","Length":26}]""");

        streams.Should().ContainSingle();
        streams[0].Name.Should().Be(":Zone.Identifier");
        streams[0].Size.Should().Be(26);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseStreams_handles_single_object_and_blank()
    {
        FileStreamService.ParseStreams("""{"Stream":":SmartScreen","Length":7}""")
            .Should().ContainSingle().Which.Name.Should().Be(":SmartScreen");
        FileStreamService.ParseStreams("").Should().BeEmpty();
    }
}

[Trait("Category", "Integration")]
public class FileStreamServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"wm-ads-{Guid.NewGuid():N}.txt");

    public FileStreamServiceTests() => File.WriteAllText(_tmp, "main content");
    public void Dispose() { try { File.Delete(_tmp); } catch { } }

    [Fact]
    public async Task GetStreamsAsync_finds_a_real_alternate_data_stream()
    {
        // NTFS supports ADS via the file:stream path syntax.
        await File.WriteAllTextAsync(_tmp + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var svc = new FileStreamService(new PowerShellService(NullLogger.Instance));
        var result = await svc.GetStreamsAsync(_tmp);

        result.Path.Should().Be(_tmp);
        result.LinkTarget.Should().BeNull(); // a normal file, not a reparse point
        result.AlternateStreams.Should().Contain(s => s.Name.Contains("Zone.Identifier"));
    }
}
