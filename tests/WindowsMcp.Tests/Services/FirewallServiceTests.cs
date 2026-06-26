using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class FirewallServiceTests
{
    private static PSResult Ok(string stdout) => new(true, stdout, "", 0, Array.Empty<string>());
    private static PSResult Fail(string stderr) => new(false, "", stderr, 1, new[] { stderr });

    [Fact]
    public void ParseRules_handles_a_json_array()
    {
        var rules = FirewallService.ParseRules(
            """[{"Name":"a","DisplayName":"A","Enabled":"True","Direction":"Inbound","Action":"Allow"},{"Name":"b","DisplayName":"B","Enabled":"True","Direction":"Outbound","Action":"Block"}]""");

        rules.Should().HaveCount(2);
        rules[0].DisplayName.Should().Be("A");
        rules[1].Direction.Should().Be("Outbound");
    }

    [Fact]
    public void ParseRules_handles_a_single_object()
    {
        // ConvertTo-Json emits a bare object (not an array) for one rule.
        var rules = FirewallService.ParseRules(
            """{"Name":"a","DisplayName":"Solo","Enabled":"True","Direction":"Inbound","Action":"Allow"}""");

        rules.Should().ContainSingle();
        rules[0].DisplayName.Should().Be("Solo");
    }

    [Fact]
    public void ParseRules_returns_empty_for_blank_output()
        => FirewallService.ParseRules("").Should().BeEmpty();

    [Fact]
    public async Task ListAsync_parses_rules_from_shell()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Ok("""[{"Name":"x","DisplayName":"X","Enabled":"True","Direction":"Inbound","Action":"Allow"}]"""));

        var rules = await new FirewallService(ps.Object).ListAsync(null, 100);

        rules.Should().ContainSingle();
        rules[0].Name.Should().Be("x");
    }

    [Fact]
    public async Task AddAsync_throws_when_cmdlet_fails()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(Fail("Access denied"));

        var act = () => new FirewallService(ps.Object).AddAsync("R", "Inbound", "Allow", 80);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*add failed*");
    }
}
