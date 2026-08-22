using FluentAssertions;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

[Trait("Category", "Unit")]
public class ServerOptionsTests
{
    private const string Key = "0123456789abcdef";                                   // exactly MinApiKeyLength
    private const string Thumb = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";        // 40 hex

    private static readonly Func<string, string?> NoEnv = _ => null;

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs) =>
        name => pairs.FirstOrDefault(p => p.Name == name).Value;

    private static ServerOptions Parse(params string[] args) => ServerOptions.Parse(args, NoEnv);

    // ---- defaults -------------------------------------------------------------------------

    [Fact]
    public void No_arguments_is_plain_stdio()
    {
        var o = Parse();

        o.Should().Be(ServerOptions.Stdio);
        o.Transport.Should().Be(TransportKind.Stdio);
        o.IsHttp.Should().BeFalse();
        o.ShowHelp.Should().BeFalse();
    }

    [Fact]
    public void Http_defaults_to_all_interfaces_on_the_default_port_without_tls_or_key()
    {
        var o = Parse("--transport", "http");

        o.Transport.Should().Be(TransportKind.Http);
        o.BindAddress.Should().Be("0.0.0.0");
        o.Port.Should().Be(ServerOptions.DefaultPort);
        o.CertThumbprint.Should().BeNull();
        o.ApiKey.Should().BeNull();
        o.UseTls.Should().BeFalse();
        o.Scheme.Should().Be("http");
        o.IsLoopback.Should().BeFalse("0.0.0.0 accepts remote connections");
    }

    // ---- flag forms -----------------------------------------------------------------------

    [Fact]
    public void Accepts_space_separated_and_equals_forms_and_is_case_insensitive()
    {
        var o = Parse("--Transport=HTTP", "--port", "8443", "--bind=127.0.0.1",
                      "--cert-thumbprint", Thumb, "--API-KEY=" + Key);

        o.Transport.Should().Be(TransportKind.Http);
        o.Port.Should().Be(8443);
        o.BindAddress.Should().Be("127.0.0.1");
        o.CertThumbprint.Should().Be(Thumb);
        o.ApiKey.Should().Be(Key);
        o.UseTls.Should().BeTrue();
        o.Scheme.Should().Be("https");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void Help_flag_wins_over_everything_else(string flag)
    {
        var o = Parse("--transport", "http", flag, "--bogus");

        o.ShowHelp.Should().BeTrue();
    }

    [Fact]
    public void Usage_documents_every_option()
    {
        foreach (var opt in new[] { "--transport", "--port", "--bind", "--cert-thumbprint", "--api-key", "--help",
                                    "WINDOWSMCP_API_KEY", "WINDOWSMCP_TRANSPORT", "/mcp" })
            ServerOptions.Usage.Should().Contain(opt);
    }

    // ---- environment ----------------------------------------------------------------------

    [Fact]
    public void Environment_variables_are_fallbacks()
    {
        var env = Env(("WINDOWSMCP_TRANSPORT", "http"), ("WINDOWSMCP_PORT", "9000"),
                      ("WINDOWSMCP_BIND", "192.168.1.5"), ("WINDOWSMCP_CERT_THUMBPRINT", Thumb),
                      ("WINDOWSMCP_API_KEY", Key));

        var o = ServerOptions.Parse([], env);

        o.Transport.Should().Be(TransportKind.Http);
        o.Port.Should().Be(9000);
        o.BindAddress.Should().Be("192.168.1.5");
        o.CertThumbprint.Should().Be(Thumb);
        o.ApiKey.Should().Be(Key);
    }

    [Fact]
    public void Command_line_beats_environment()
    {
        var env = Env(("WINDOWSMCP_PORT", "9000"), ("WINDOWSMCP_API_KEY", "environment-key-value"));

        var o = ServerOptions.Parse(["--transport", "http", "--port", "8080", "--api-key", Key], env);

        o.Port.Should().Be(8080);
        o.ApiKey.Should().Be(Key);
    }

    [Fact]
    public void Http_only_environment_variables_are_ignored_in_stdio_mode()
    {
        // A globally exported WINDOWSMCP_API_KEY must not break the stdio plugin on the same box.
        var env = Env(("WINDOWSMCP_API_KEY", Key), ("WINDOWSMCP_PORT", "8443"));

        ServerOptions.Parse([], env).Should().Be(ServerOptions.Stdio);
        ServerOptions.Parse(["--transport", "stdio"], env).Should().Be(ServerOptions.Stdio);
    }

    [Fact]
    public void Blank_environment_values_count_as_unset()
    {
        var env = Env(("WINDOWSMCP_TRANSPORT", "  "), ("WINDOWSMCP_PORT", ""));

        ServerOptions.Parse([], env).Should().Be(ServerOptions.Stdio);
        ServerOptions.Parse(["--transport", "http"], env).Port.Should().Be(ServerOptions.DefaultPort);
    }

    // ---- bind / loopback ------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("10.0.0.7", false)]
    public void IsLoopback_reflects_the_bind_address(string bind, bool loopback)
    {
        Parse("--transport", "http", "--bind", bind).IsLoopback.Should().Be(loopback);
    }

    [Fact]
    public void Localhost_is_accepted_as_an_alias_for_ipv4_loopback()
    {
        var o = Parse("--transport", "http", "--bind", "LocalHost");

        o.BindAddress.Should().Be("127.0.0.1");
        o.IsLoopback.Should().BeTrue();
    }

    // ---- thumbprint -----------------------------------------------------------------------

    [Theory]
    [InlineData("a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90 12 34 56 78")]
    [InlineData("a1:b2:c3:d4:e5:f6:07:18:29:3a:4b:5c:6d:7e:8f:90:12:34:56:78")]
    [InlineData("‎a1b2c3d4e5f60718293a4b5c6d7e8f9012345678")]   // certmgr copy-paste left-to-right mark
    [InlineData("  A1B2C3D4E5F60718293A4B5C6D7E8F9012345678  ")]
    public void Thumbprint_is_normalized_to_forty_uppercase_hex_digits(string raw)
    {
        ServerOptions.NormalizeThumbprint(raw).Should().Be(Thumb);
        Parse("--transport", "http", "--cert-thumbprint", raw).CertThumbprint.Should().Be(Thumb);
    }

    [Theory]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F901234567")]     // 39
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F90123456789")]   // 41
    [InlineData("G1B2C3D4E5F60718293A4B5C6D7E8F9012345678")]    // non-hex
    public void Malformed_thumbprint_is_rejected(string raw)
    {
        var act = () => Parse("--transport", "http", "--cert-thumbprint", raw);

        act.Should().Throw<OptionsException>().WithMessage("*40 hex digits*");
    }

    // ---- validation errors ----------------------------------------------------------------

    [Theory]
    [InlineData("--transport", "tcp")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "-1")]
    [InlineData("--port", "eighty")]
    [InlineData("--bind", "not-an-ip")]
    [InlineData("--bind", "example.com")]
    [InlineData("--api-key", "tooshort")]
    [InlineData("--api-key", "has a space in it!")]
    [InlineData("--api-key", "nön-ascii-key-value")]
    public void Invalid_values_are_rejected(string flag, string value)
    {
        var args = flag == "--transport"
            ? new[] { flag, value }
            : new[] { "--transport", "http", flag, value };

        var act = () => Parse(args);

        act.Should().Throw<OptionsException>().Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("--port", "8443")]
    [InlineData("--bind", "127.0.0.1")]
    [InlineData("--cert-thumbprint", Thumb)]
    [InlineData("--api-key", Key)]
    public void Http_only_flags_are_an_error_without_http_transport(string flag, string value)
    {
        var implicitStdio = () => Parse(flag, value);
        var explicitStdio = () => Parse("--transport", "stdio", flag, value);

        implicitStdio.Should().Throw<OptionsException>().WithMessage($"*{flag}*--transport http*");
        explicitStdio.Should().Throw<OptionsException>().WithMessage($"*{flag}*--transport http*");
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        var act = () => Parse("--transport", "http", "--verbose", "true");

        act.Should().Throw<OptionsException>().WithMessage("*--verbose*");
    }

    [Fact]
    public void Positional_argument_is_an_error()
    {
        var act = () => Parse("http");

        act.Should().Throw<OptionsException>().WithMessage("*'http'*");
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port=")]
    public void Option_without_a_value_is_an_error(string arg)
    {
        var act = () => Parse("--transport", "http", arg);

        act.Should().Throw<OptionsException>().WithMessage("*requires a value*");
    }

    [Fact]
    public void Repeated_option_is_an_error()
    {
        var act = () => Parse("--transport", "http", "--port", "1", "--port", "2");

        act.Should().Throw<OptionsException>().WithMessage("*more than once*");
    }
}
