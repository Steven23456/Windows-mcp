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
                                    "--screenshot-scale", "--max-tree-elements", "--flash", "--profile-snapshot",
                                    "WINDOWSMCP_API_KEY", "WINDOWSMCP_TRANSPORT", "WINDOWSMCP_SCREENSHOT_SCALE",
                                    "WINDOWSMCP_MAX_TREE_ELEMENTS", "WINDOWSMCP_FLASH", "WINDOWSMCP_PROFILE_SNAPSHOT",
                                    "/mcp" })
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
    // ---- A-9 (R5) — --screenshot-scale / WINDOWSMCP_SCREENSHOT_SCALE ------------------------

    [Fact]
    public void Screenshot_scale_defaults_to_one_under_both_transports()
    {
        Parse().ScreenshotScale.Should().Be(1.0);
        Parse("--transport", "http").ScreenshotScale.Should().Be(1.0);
    }

    [Fact]
    public void Screenshot_scale_applies_to_stdio_too_it_is_not_an_http_only_option()
    {
        // Unlike --port/--bind/--api-key, this one configures a tool, not a listener: rejecting
        // it (or dropping it) in stdio mode would make the env var useless where it matters most.
        var fromFlag = Parse("--screenshot-scale", "0.25");

        fromFlag.Transport.Should().Be(TransportKind.Stdio);
        fromFlag.ScreenshotScale.Should().Be(0.25);

        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_SCALE", "0.5")))
            .ScreenshotScale.Should().Be(0.5);
    }

    [Fact]
    public void Screenshot_scale_comes_from_the_environment_under_http_as_well()
    {
        var o = ServerOptions.Parse(["--transport", "http"], Env(("WINDOWSMCP_SCREENSHOT_SCALE", "0.5")));

        o.Transport.Should().Be(TransportKind.Http);
        o.ScreenshotScale.Should().Be(0.5);
    }

    [Fact]
    public void Screenshot_scale_on_the_command_line_beats_the_environment()
    {
        var env = Env(("WINDOWSMCP_SCREENSHOT_SCALE", "0.5"));

        ServerOptions.Parse(["--screenshot-scale", "0.25"], env).ScreenshotScale.Should().Be(0.25);
        ServerOptions.Parse(["--screenshot-scale=0.25"], env).ScreenshotScale.Should().Be(0.25);
    }

    [Fact]
    public void Blank_screenshot_scale_in_the_environment_counts_as_unset()
    {
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_SCALE", "   "))).ScreenshotScale.Should().Be(1.0);
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_SCALE", ""))).ScreenshotScale.Should().Be(1.0);
    }

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("1.0", 1.0)]
    [InlineData("0.1", 0.1)]
    [InlineData("0.50", 0.5)]
    [InlineData("0.333", 0.333)]
    public void Valid_screenshot_scales_are_accepted(string raw, double expected)
    {
        Parse("--screenshot-scale", raw).ScreenshotScale.Should().BeApproximately(expected, 1e-12);
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_SCALE", raw)))
            .ScreenshotScale.Should().BeApproximately(expected, 1e-12);
    }

    [Theory]
    [InlineData("0")]        // below the 0.1 floor
    [InlineData("0.09")]
    [InlineData("1.5")]      // above 1.0: upscaling is not a thing this option does
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("0,5")]      // invariant culture: a comma decimal is not a number here
    [InlineData(",5")]
    [InlineData("1e-1")]     // no exponent forms; the value is user-facing, not machine-generated
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Invalid_screenshot_scales_are_rejected_from_the_command_line(string raw)
    {
        var act = () => Parse("--screenshot-scale", raw);

        var message = act.Should().Throw<OptionsException>().Which.Message;
        message.Should().Contain("screenshot-scale", "the message names the option");
        message.Should().Contain("0.1").And.Contain("1.0", "the message names the accepted range");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("0,5")]
    public void Invalid_screenshot_scales_are_rejected_from_the_environment_too(string raw)
    {
        var act = () => ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_SCALE", raw)));

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain("0.1");
    }

    [Fact]
    public void Screenshot_scale_flag_without_a_value_is_an_error()
    {
        var missing = () => Parse("--screenshot-scale");
        var empty = () => Parse("--screenshot-scale=");

        missing.Should().Throw<OptionsException>().WithMessage("*requires a value*");
        empty.Should().Throw<OptionsException>().WithMessage("*requires a value*");
    }

    [Fact]
    public void Repeated_screenshot_scale_is_an_error()
    {
        var act = () => Parse("--screenshot-scale", "0.5", "--screenshot-scale", "0.6");

        act.Should().Throw<OptionsException>().WithMessage("*more than once*");
    }

    // ---- A-2 / A-4 (R1) — --max-tree-elements / WINDOWSMCP_MAX_TREE_ELEMENTS -----------------
    // The element budget every UI walk spends (snapshot AND get_state). Like --screenshot-scale it
    // configures a tool rather than a listener, so it is parsed BEFORE the stdio early return and
    // applies to both transports; unlike it, the value is a count, so the accepted set is the
    // whole numbers from 1 up.

    [Fact]
    public void Max_tree_elements_defaults_to_500_under_both_transports()
    {
        Parse().MaxTreeElements.Should().Be(500);
        Parse("--transport", "http").MaxTreeElements.Should().Be(500);
        ServerOptions.Stdio.MaxTreeElements.Should().Be(500, "the no-argument configuration carries the default too");
    }

    [Fact]
    public void Max_tree_elements_applies_to_stdio_too_it_is_not_an_http_only_option()
    {
        var fromFlag = Parse("--max-tree-elements", "200");

        fromFlag.Transport.Should().Be(TransportKind.Stdio);
        fromFlag.MaxTreeElements.Should().Be(200);

        ServerOptions.Parse([], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", "200")))
            .MaxTreeElements.Should().Be(200);
    }

    [Fact]
    public void Max_tree_elements_comes_from_the_environment_under_http_as_well()
    {
        var o = ServerOptions.Parse(["--transport", "http"], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", "200")));

        o.Transport.Should().Be(TransportKind.Http);
        o.MaxTreeElements.Should().Be(200);
    }

    [Fact]
    public void Max_tree_elements_on_the_command_line_beats_the_environment()
    {
        var env = Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", "200"));

        ServerOptions.Parse(["--max-tree-elements", "50"], env).MaxTreeElements.Should().Be(50);
        ServerOptions.Parse(["--max-tree-elements=50"], env).MaxTreeElements.Should().Be(50);
    }

    [Fact]
    public void Blank_max_tree_elements_in_the_environment_counts_as_unset()
    {
        ServerOptions.Parse([], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", "   "))).MaxTreeElements.Should().Be(500);
        ServerOptions.Parse([], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", ""))).MaxTreeElements.Should().Be(500);
    }

    [Theory]
    [InlineData("1", 1)]            // the floor: one element is a legal (useless) budget, zero is not
    [InlineData("2", 2)]
    [InlineData("500", 500)]
    [InlineData("5000", 5000)]
    [InlineData("2147483647", int.MaxValue)]
    public void Valid_max_tree_elements_are_accepted(string raw, int expected)
    {
        Parse("--max-tree-elements", raw).MaxTreeElements.Should().Be(expected);
        ServerOptions.Parse([], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", raw))).MaxTreeElements.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]            // a budget of 0 walks nothing and reports every desktop truncated
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]          // a count, not a scale
    [InlineData("1e3")]          // no exponent forms; the value is user-facing
    [InlineData("1,000")]
    [InlineData("0x10")]
    [InlineData("2147483648")]   // one past int.MaxValue: overflow is a refusal, not a wrap
    [InlineData("+5")]           // digits only: a signed form is a typo, not a budget
    [InlineData(" 5")]           // and no surrounding whitespace, so '--max-tree-elements " 5"' is not silently accepted
    [InlineData("5 ")]
    public void Invalid_max_tree_elements_are_rejected_from_the_command_line(string raw)
    {
        var act = () => Parse("--max-tree-elements", raw);

        var message = act.Should().Throw<OptionsException>().Which.Message;
        message.Should().Contain("max-tree-elements", "the message names the option");
        message.Should().MatchRegex("(at least 1|from 1|>= ?1|minimum of 1|1 or more|greater than 0)",
            "the message names the minimum, so the operator knows what to pass instead");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void Invalid_max_tree_elements_are_rejected_from_the_environment_too(string raw)
    {
        var act = () => ServerOptions.Parse([], Env(("WINDOWSMCP_MAX_TREE_ELEMENTS", raw)));

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain("max-tree-elements");
    }

    [Fact]
    public void Max_tree_elements_flag_without_a_value_is_an_error()
    {
        var missing = () => Parse("--max-tree-elements");
        var empty = () => Parse("--max-tree-elements=");

        missing.Should().Throw<OptionsException>().WithMessage("*requires a value*");
        empty.Should().Throw<OptionsException>().WithMessage("*requires a value*");
    }

    [Fact]
    public void Repeated_max_tree_elements_is_an_error()
    {
        var act = () => Parse("--max-tree-elements", "100", "--max-tree-elements", "200");

        act.Should().Throw<OptionsException>().WithMessage("*more than once*");
    }

    // ---- A-14 (R1) - --flash / WINDOWSMCP_FLASH and --profile-snapshot / WINDOWSMCP_PROFILE_SNAPSHOT ----
    // Two on/off switches that configure a tool rather than a listener, so - like --screenshot-scale
    // and --max-tree-elements - they are parsed BEFORE the stdio early return and apply to both
    // transports. The flash matters MORE under HTTP, not less (roadmap section 7): the glow is the
    // only signal a person at the target machine gets that a remote agent just captured their screen.

    /// <summary>Every spelling of "true" and "false" this parser accepts, in both cases.</summary>
    public static TheoryData<string, bool> BooleanValues => new()
    {
        { "on", true }, { "ON", true }, { "On", true },
        { "true", true }, { "TRUE", true }, { "True", true },
        { "1", true },
        { "off", false }, { "OFF", false }, { "Off", false },
        { "false", false }, { "FALSE", false }, { "False", false },
        { "0", false },
    };

    [Fact]
    public void Flash_is_on_by_default_under_both_transports()
    {
        Parse().Flash.Should().BeTrue("the courtesy glow is opt-OUT, not opt-in");
        Parse("--transport", "http").Flash.Should().BeTrue("it matters more under HTTP, not less");
        ServerOptions.Stdio.Flash.Should().BeTrue("the no-argument configuration carries the default too");
    }

    [Fact]
    public void Profile_snapshot_is_off_by_default_under_both_transports()
    {
        Parse().ProfileSnapshot.Should().BeFalse();
        Parse("--transport", "http").ProfileSnapshot.Should().BeFalse();
        ServerOptions.Stdio.ProfileSnapshot.Should().BeFalse();
    }

    [Fact]
    public void Flash_applies_to_stdio_too_it_is_not_an_http_only_option()
    {
        var fromFlag = Parse("--flash", "off");

        fromFlag.Transport.Should().Be(TransportKind.Stdio);
        fromFlag.Flash.Should().BeFalse();

        ServerOptions.Parse([], Env(("WINDOWSMCP_FLASH", "off"))).Flash.Should().BeFalse();
    }

    [Fact]
    public void Profile_snapshot_applies_to_stdio_too_it_is_not_an_http_only_option()
    {
        var fromFlag = Parse("--profile-snapshot", "on");

        fromFlag.Transport.Should().Be(TransportKind.Stdio);
        fromFlag.ProfileSnapshot.Should().BeTrue();

        ServerOptions.Parse([], Env(("WINDOWSMCP_PROFILE_SNAPSHOT", "on"))).ProfileSnapshot.Should().BeTrue();
    }

    [Fact]
    public void Flash_and_profile_snapshot_come_from_the_environment_under_http_as_well()
    {
        var o = ServerOptions.Parse(["--transport", "http"],
            Env(("WINDOWSMCP_FLASH", "off"), ("WINDOWSMCP_PROFILE_SNAPSHOT", "on")));

        o.Transport.Should().Be(TransportKind.Http);
        o.Flash.Should().BeFalse();
        o.ProfileSnapshot.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(BooleanValues))]
    public void Flash_accepts_on_off_true_false_one_zero_in_any_case(string raw, bool expected)
    {
        Parse("--flash", raw).Flash.Should().Be(expected);
        Parse("--flash=" + raw).Flash.Should().Be(expected);
        ServerOptions.Parse([], Env(("WINDOWSMCP_FLASH", raw))).Flash.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(BooleanValues))]
    public void Profile_snapshot_accepts_on_off_true_false_one_zero_in_any_case(string raw, bool expected)
    {
        Parse("--profile-snapshot", raw).ProfileSnapshot.Should().Be(expected);
        Parse("--profile-snapshot=" + raw).ProfileSnapshot.Should().Be(expected);
        ServerOptions.Parse([], Env(("WINDOWSMCP_PROFILE_SNAPSHOT", raw))).ProfileSnapshot.Should().Be(expected);
    }

    [Fact]
    public void Flash_and_profile_snapshot_on_the_command_line_beat_the_environment()
    {
        var env = Env(("WINDOWSMCP_FLASH", "on"), ("WINDOWSMCP_PROFILE_SNAPSHOT", "off"));

        var o = ServerOptions.Parse(["--flash", "off", "--profile-snapshot", "on"], env);

        o.Flash.Should().BeFalse();
        o.ProfileSnapshot.Should().BeTrue();
    }

    [Fact]
    public void Blank_flash_and_profile_snapshot_in_the_environment_count_as_unset()
    {
        ServerOptions.Parse([], Env(("WINDOWSMCP_FLASH", "   "))).Flash.Should().BeTrue();
        ServerOptions.Parse([], Env(("WINDOWSMCP_FLASH", ""))).Flash.Should().BeTrue();
        ServerOptions.Parse([], Env(("WINDOWSMCP_PROFILE_SNAPSHOT", "   "))).ProfileSnapshot.Should().BeFalse();
        ServerOptions.Parse([], Env(("WINDOWSMCP_PROFILE_SNAPSHOT", ""))).ProfileSnapshot.Should().BeFalse();
    }

    [Theory]
    [InlineData("yes")]          // close enough to be a plausible typo, and still not accepted
    [InlineData("no")]
    [InlineData("enabled")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("o n")]
    [InlineData(" on")]          // no surrounding whitespace, like --max-tree-elements
    [InlineData("on ")]
    public void Invalid_flash_values_are_rejected_from_the_command_line(string raw)
    {
        var act = () => Parse("--flash", raw);

        var message = act.Should().Throw<OptionsException>().Which.Message;
        message.Should().Contain("flash", "the message names the option");
        message.Should().Contain("on").And.Contain("off", "the message names the accepted values");
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("2")]
    public void Invalid_profile_snapshot_values_are_rejected_from_the_command_line(string raw)
    {
        var act = () => Parse("--profile-snapshot", raw);

        var message = act.Should().Throw<OptionsException>().Which.Message;
        message.Should().Contain("profile-snapshot", "the message names the option");
        message.Should().Contain("on").And.Contain("off", "the message names the accepted values");
    }

    [Theory]
    [InlineData("WINDOWSMCP_FLASH")]
    [InlineData("WINDOWSMCP_PROFILE_SNAPSHOT")]
    public void Invalid_switch_values_are_rejected_from_the_environment_too(string variable)
    {
        var act = () => ServerOptions.Parse([], Env((variable, "yes")));

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain("on");
    }

    [Theory]
    [InlineData("--flash")]
    [InlineData("--profile-snapshot")]
    public void Switch_flags_without_a_value_are_an_error(string flag)
    {
        // This parser has no valueless flags (--help aside): every option takes a value, which is
        // why the roadmap's --no-flash ships as '--flash off'. A bare '--flash' must therefore say
        // it needs a value rather than being read as "turn it on" or "turn it off".
        var missing = () => Parse(flag);
        var empty = () => Parse(flag + "=");

        missing.Should().Throw<OptionsException>().WithMessage("*requires a value*");
        empty.Should().Throw<OptionsException>().WithMessage("*requires a value*");
    }

    [Theory]
    [InlineData("--flash")]
    [InlineData("--profile-snapshot")]
    public void Repeated_switches_are_an_error(string flag)
    {
        var act = () => Parse(flag, "on", flag, "off");

        act.Should().Throw<OptionsException>().WithMessage("*more than once*");
    }

    [Theory]
    [InlineData("--no-flash")]
    [InlineData("--disable-flash")]
    [InlineData("--profile")]
    public void The_valueless_spellings_the_roadmap_sketched_are_unknown_options(string flag)
    {
        // The roadmap sketch said '--no-flash'; the parser has no valueless flags, so the shipped
        // spelling is '--flash off'. Pinned so the sketch cannot come back as a silent alias: an
        // operator who types the old name is told, not ignored.
        var act = () => Parse(flag, "x");

        act.Should().Throw<OptionsException>().WithMessage($"*Unknown option '{flag}'*");
    }

    [Fact]
    public void The_disable_flash_environment_variable_the_roadmap_sketched_has_no_effect()
    {
        // WINDOWSMCP_DISABLE_FLASH is not read: WINDOWSMCP_FLASH=off is the one switch. An unknown
        // WINDOWSMCP_* variable is ignored rather than rejected, as every other one is.
        ServerOptions.Parse([], Env(("WINDOWSMCP_DISABLE_FLASH", "1"))).Flash
            .Should().BeTrue("the only switch is WINDOWSMCP_FLASH / --flash");
    }
    // ---- A-10 (R1) - --screenshot-backend / WINDOWSMCP_SCREENSHOT_BACKEND ----------------------
    // A knob that configures a tool rather than a listener, so - like --screenshot-scale,
    // --max-tree-elements, --flash and --profile-snapshot - it is parsed BEFORE the stdio early
    // return and applies to both transports. The vocabulary is auto|gdi|wgc, matching the tool's
    // own 'backend' argument; an operator who types 'dxcam' (upstream's name) is told, not ignored.

    [Fact]
    public void Screenshot_backend_defaults_to_auto_under_both_transports()
    {
        Parse().ScreenshotBackend.Should().Be("auto", "auto prefers the compositor and falls back to GDI");
        Parse("--transport", "http").ScreenshotBackend.Should().Be("auto");
        ServerOptions.Stdio.ScreenshotBackend.Should().Be("auto", "the no-argument configuration carries the default too");
    }

    [Fact]
    public void Screenshot_backend_applies_to_stdio_too_it_is_not_an_http_only_option()
    {
        var fromFlag = Parse("--screenshot-backend", "wgc");

        fromFlag.Transport.Should().Be(TransportKind.Stdio);
        fromFlag.ScreenshotBackend.Should().Be("wgc");

        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", "gdi"))).ScreenshotBackend.Should().Be("gdi");
    }

    [Fact]
    public void Screenshot_backend_comes_from_the_environment_under_http_as_well()
    {
        var o = ServerOptions.Parse(["--transport", "http"], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", "wgc")));

        o.Transport.Should().Be(TransportKind.Http);
        o.ScreenshotBackend.Should().Be("wgc");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public void Screenshot_backend_accepts_each_of_the_three_values_in_both_flag_forms(string value)
    {
        Parse("--screenshot-backend", value).ScreenshotBackend.Should().Be(value);
        Parse("--screenshot-backend=" + value).ScreenshotBackend.Should().Be(value);
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", value))).ScreenshotBackend.Should().Be(value);
    }

    [Theory]
    [InlineData("AUTO", "auto")]
    [InlineData("Gdi", "gdi")]
    [InlineData("WGC", "wgc")]
    public void Screenshot_backend_is_case_insensitive_and_is_stored_lower_case(string raw, string expected)
    {
        // Canonicalised on the way in, like the thumbprint and the bind address: what is stored is
        // what ScreenshotService.ResolveBackend compares and what the metadata reports.
        Parse("--screenshot-backend", raw).ScreenshotBackend.Should().Be(expected);
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", raw))).ScreenshotBackend.Should().Be(expected);
    }

    [Fact]
    public void Screenshot_backend_on_the_command_line_beats_the_environment()
    {
        var o = ServerOptions.Parse(["--screenshot-backend", "gdi"], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", "wgc")));

        o.ScreenshotBackend.Should().Be("gdi");
    }

    [Fact]
    public void Blank_screenshot_backend_in_the_environment_counts_as_unset()
    {
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", "   "))).ScreenshotBackend.Should().Be("auto");
        ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", ""))).ScreenshotBackend.Should().Be("auto");
    }

    [Theory]
    [InlineData("dxcam")]        // upstream's backend registry names
    [InlineData("mss")]
    [InlineData("pillow")]
    [InlineData("dxgi")]
    [InlineData("directx")]
    [InlineData(" wgc")]         // no surrounding whitespace, like every other option
    [InlineData("wgc ")]
    public void Invalid_screenshot_backend_values_are_rejected_from_the_command_line(string raw)
    {
        var act = () => Parse("--screenshot-backend", raw);

        var message = act.Should().Throw<OptionsException>().Which.Message;
        message.Should().Contain("screenshot-backend", "the message names the option");
        message.Should().Contain("auto").And.Contain("gdi").And.Contain("wgc",
            "the message names the three accepted values");
    }

    [Fact]
    public void Invalid_screenshot_backend_values_are_rejected_from_the_environment_too()
    {
        var act = () => ServerOptions.Parse([], Env(("WINDOWSMCP_SCREENSHOT_BACKEND", "dxcam")));

        act.Should().Throw<OptionsException>().Which.Message.Should().Contain("wgc");
    }

    [Fact]
    public void Screenshot_backend_without_a_value_is_an_error()
    {
        var missing = () => Parse("--screenshot-backend");
        var empty = () => Parse("--screenshot-backend=");

        missing.Should().Throw<OptionsException>().WithMessage("*requires a value*");
        empty.Should().Throw<OptionsException>().WithMessage("*requires a value*");
    }

    [Fact]
    public void Repeated_screenshot_backend_is_an_error()
    {
        var act = () => Parse("--screenshot-backend", "gdi", "--screenshot-backend", "wgc");

        act.Should().Throw<OptionsException>().WithMessage("*more than once*");
    }

    [Fact]
    public void Usage_documents_the_screenshot_backend_option()
    {
        ServerOptions.Usage.Should().Contain("--screenshot-backend");
        ServerOptions.Usage.Should().Contain("WINDOWSMCP_SCREENSHOT_BACKEND");
        foreach (var value in new[] { "auto", "gdi", "wgc" })
            ServerOptions.Usage.Should().Contain(value, "the help text lists what the option accepts");
    }
}
