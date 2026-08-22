using System.Text;
using FluentAssertions;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

public class CertificateLocatorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Malformed_thumbprint_fails_before_any_store_is_touched()
    {
        var act = () => CertificateLocator.Find("not-a-thumbprint");

        act.Should().Throw<OptionsException>().WithMessage("*40 hex digits*");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Unknown_thumbprint_names_both_stores_and_the_fix()
    {
        var act = () => CertificateLocator.Find(new string('0', 40));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain(@"LocalMachine\My").And
                .Contain(@"CurrentUser\My").And
                .Contain("New-SelfSignedCertificate");
    }
}

[Trait("Category", "Unit")]
public class BearerAuthorizationTests
{
    private static readonly byte[] Expected = Encoding.UTF8.GetBytes("correct-horse-battery-staple");

    [Theory]
    [InlineData("Bearer correct-horse-battery-staple")]
    [InlineData("Bearer   correct-horse-battery-staple  ")]   // surrounding whitespace tolerated
    public void Accepts_the_configured_key(string header)
    {
        WindowsMcpHost.IsAuthorized(header, Expected).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("correct-horse-battery-staple")]            // no scheme
    [InlineData("bearer correct-horse-battery-staple")]     // scheme kept strict
    [InlineData("Basic correct-horse-battery-staple")]
    [InlineData("Bearer correct-horse-battery-stapl")]      // one byte short
    [InlineData("Bearer correct-horse-battery-staple1")]    // one byte long
    [InlineData("Bearer Correct-horse-battery-staple")]     // case differs
    [InlineData("Bearer ")]
    public void Rejects_everything_else(string? header)
    {
        WindowsMcpHost.IsAuthorized(header, Expected).Should().BeFalse();
    }
}
