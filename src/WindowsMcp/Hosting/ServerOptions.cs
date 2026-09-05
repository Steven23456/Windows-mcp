using System.Globalization;
using System.Net;

namespace WindowsMcp.Hosting;

/// <summary>Which MCP transport the process serves.</summary>
internal enum TransportKind
{
    /// <summary>JSON-RPC over stdin/stdout — the default, and what an MCP host that spawns the exe expects.</summary>
    Stdio,

    /// <summary>Streamable HTTP (optionally HTTPS) on a TCP port — for clients on other machines.</summary>
    Http,
}

/// <summary>
/// A bad command line. The message is user-facing; <see cref="Program"/> prints it followed by
/// <see cref="ServerOptions.Usage"/> and exits with code 2.
/// </summary>
internal sealed class OptionsException(string message) : Exception(message);

/// <summary>
/// Process-level options from the command line, with <c>WINDOWSMCP_*</c> environment fallbacks
/// (command line wins). Pure — no I/O — so the parser is unit-tested exhaustively.
/// </summary>
/// <remarks>
/// With no arguments the result is <see cref="Stdio"/>, exactly what the plugin's <c>.mcp.json</c>
/// (<c>"args": []</c>) launches. In stdio mode the HTTP-only environment variables are ignored
/// rather than rejected, so a globally exported <c>WINDOWSMCP_API_KEY</c> can't break the
/// stdio plugin on the same box; the HTTP-only <b>flags</b>, however, are an error without
/// <c>--transport http</c> because a typed flag that silently does nothing is a trap.
/// </remarks>
internal sealed record ServerOptions(
    TransportKind Transport,
    string BindAddress,
    int Port,
    string? CertThumbprint,
    string? ApiKey,
    bool ShowHelp = false,
    // A-9: process-wide multiplier on every screenshot's own 'scale' argument, [0.1, 1.0].
    double ScreenshotScale = 1.0,
    // A-4: the element budget snapshot/get_state use when a call names none; at least 1.
    int MaxTreeElements = 500)
{
    public const int DefaultPort = 8765;
    public const string DefaultBind = "0.0.0.0";
    public const int MinApiKeyLength = 16;
    public const string EnvPrefix = "WINDOWSMCP_";

    /// <summary>The no-argument configuration: plain stdio.</summary>
    public static ServerOptions Stdio { get; } = new(TransportKind.Stdio, DefaultBind, DefaultPort, null, null);

    public bool IsHttp => Transport == TransportKind.Http;
    public bool UseTls => CertThumbprint is not null;
    public string Scheme => UseTls ? "https" : "http";

    /// <summary>True when the bind address only accepts connections from this machine (127.0.0.0/8, ::1).</summary>
    public bool IsLoopback => IPAddress.TryParse(BindAddress, out var ip) && IPAddress.IsLoopback(ip);

    private static readonly string[] HttpOnlyOptions = ["port", "bind", "cert-thumbprint", "api-key"];
    private static readonly HashSet<string> KnownOptions =
        new(["transport", "screenshot-scale", "max-tree-elements", .. HttpOnlyOptions], StringComparer.OrdinalIgnoreCase);

    public static string Usage => $"""
        Usage: WindowsMcp.exe [--transport stdio|http] [options]

        Transports:
          stdio (default)   JSON-RPC over stdin/stdout, for MCP hosts that spawn this exe.
          http              Streamable HTTP at <scheme>://<bind>:<port>{WindowsMcpHost.McpPath}, for remote clients.

        HTTP options:
          --port <1-65535>          TCP port to listen on (default {DefaultPort}).
          --bind <ip>               Listen address (default {DefaultBind} = every interface;
                                    127.0.0.1 = this machine only).
          --cert-thumbprint <hex>   SHA-1 thumbprint of a certificate with a private key in
                                    LocalMachine\My or CurrentUser\My. Turns the listener into
                                    HTTPS; plain HTTP is then NOT served on the port.
          --api-key <key>           Bearer token every request must carry
                                    (Authorization: Bearer <key>). Printable ASCII, no spaces,
                                    at least {MinApiKeyLength} characters. REQUIRED unless --bind is a
                                    loopback address: every tool (powershell, file_write,
                                    registry_set, process kill, ...) is reachable on this port.
          -h, --help                Show this help.

        Capture options (both transports):
          --screenshot-scale <0.1-1.0>
                                    Multiply every screenshot's own scale by this (default 1.0):
                                    a cheap way to shrink what the model sees on a 4K desktop.
          --max-tree-elements <n>   Element budget for snapshot and get_state when a call does not
                                    name its own (default 500, at least 1); the walk stops there and
                                    says so.

        Environment fallbacks (a flag on the command line wins): {EnvPrefix}TRANSPORT,
        {EnvPrefix}PORT, {EnvPrefix}BIND, {EnvPrefix}CERT_THUMBPRINT, {EnvPrefix}API_KEY,
        {EnvPrefix}SCREENSHOT_SCALE, {EnvPrefix}MAX_TREE_ELEMENTS.
        Prefer {EnvPrefix}API_KEY over --api-key: it stays out of process command lines.

        Examples:
          WindowsMcp.exe --transport http --bind 127.0.0.1
          set {EnvPrefix}API_KEY=...  &&  WindowsMcp.exe --transport http --port 8443 --cert-thumbprint <hex>
        """;

    /// <summary>
    /// Parses <paramref name="args"/> (<c>--name value</c> or <c>--name=value</c>) with
    /// <paramref name="getEnv"/> supplying <c>WINDOWSMCP_*</c> fallbacks.
    /// </summary>
    /// <exception cref="OptionsException">Any unknown, malformed, duplicated, or inapplicable option.</exception>
    public static ServerOptions Parse(string[] args, Func<string, string?> getEnv)
    {
        var cli = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?" or "/?")
                return Stdio with { ShowHelp = true };

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                throw new OptionsException($"Unexpected argument '{arg}'.");

            string name, value;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                name = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg[2..];
                if (i + 1 >= args.Length)
                    throw new OptionsException($"Option '{arg}' requires a value.");
                value = args[++i];
            }

            if (!KnownOptions.Contains(name))
                throw new OptionsException($"Unknown option '--{name}'.");
            if (value.Length == 0)
                throw new OptionsException($"Option '--{name}' requires a value.");
            if (!cli.TryAdd(name, value))
                throw new OptionsException($"Option '--{name}' was given more than once.");
        }

        string? Get(string option, string envSuffix)
        {
            if (cli.TryGetValue(option, out var fromCli)) return fromCli;
            var fromEnv = getEnv(EnvPrefix + envSuffix);
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
        }

        var transportRaw = Get("transport", "TRANSPORT") ?? "stdio";
        var transport = transportRaw.ToLowerInvariant() switch
        {
            "stdio" => TransportKind.Stdio,
            "http" => TransportKind.Http,
            _ => throw new OptionsException($"Unknown transport '{transportRaw}'; expected 'stdio' or 'http'."),
        };

        // Not HTTP-only: parsed before the stdio early return so it applies to both transports.
        var screenshotScale = 1.0;
        if (Get("screenshot-scale", "SCREENSHOT_SCALE") is { } scaleRaw)
        {
            // AllowDecimalPoint only: no sign, no exponent, no NaN/Infinity, no thousands separator.
            if (!double.TryParse(scaleRaw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out screenshotScale)
                || !(screenshotScale >= 0.1 && screenshotScale <= 1.0))   // positive test: NaN parses, must fail
                throw new OptionsException($"Invalid --screenshot-scale '{scaleRaw}'; expected a number from 0.1 to 1.0.");
        }

        var maxTreeElements = 500;
        if (Get("max-tree-elements", "MAX_TREE_ELEMENTS") is { } treeRaw)
        {
            // NumberStyles.None: digits only — no sign, no decimal point, no exponent, no separators.
            if (!int.TryParse(treeRaw, NumberStyles.None, CultureInfo.InvariantCulture, out maxTreeElements) || maxTreeElements < 1)
                throw new OptionsException($"Invalid --max-tree-elements '{treeRaw}'; expected a whole number of at least 1.");
        }

        if (transport == TransportKind.Stdio)
        {
            var stray = HttpOnlyOptions.FirstOrDefault(cli.ContainsKey);
            if (stray is not null)
                throw new OptionsException($"'--{stray}' only applies with '--transport http'.");
            return Stdio with { ScreenshotScale = screenshotScale, MaxTreeElements = maxTreeElements };
        }

        var port = DefaultPort;
        if (Get("port", "PORT") is { } portRaw)
        {
            if (!int.TryParse(portRaw, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
                throw new OptionsException($"Invalid port '{portRaw}'; expected a number from 1 to 65535.");
        }

        var bind = DefaultBind;
        if (Get("bind", "BIND") is { } bindRaw)
        {
            if (string.Equals(bindRaw, "localhost", StringComparison.OrdinalIgnoreCase))
                bind = IPAddress.Loopback.ToString();
            else if (IPAddress.TryParse(bindRaw, out var ip))
                bind = ip.ToString();
            else
                throw new OptionsException($"Invalid bind address '{bindRaw}'; expected an IPv4/IPv6 address (e.g. 0.0.0.0, 127.0.0.1, ::).");
        }

        string? thumbprint = null;
        if (Get("cert-thumbprint", "CERT_THUMBPRINT") is { } thumbprintRaw)
            thumbprint = NormalizeThumbprint(thumbprintRaw);

        string? apiKey = null;
        if (Get("api-key", "API_KEY") is { } apiKeyRaw)
        {
            if (apiKeyRaw.Length < MinApiKeyLength)
                throw new OptionsException($"API key is too short; use at least {MinApiKeyLength} characters.");
            if (apiKeyRaw.Any(c => c is < '!' or > '~'))
                throw new OptionsException("API key must be printable ASCII with no spaces (it travels in an HTTP header).");
            apiKey = apiKeyRaw;
        }

        return new ServerOptions(TransportKind.Http, bind, port, thumbprint, apiKey,
            ScreenshotScale: screenshotScale, MaxTreeElements: maxTreeElements);
    }

    /// <summary>
    /// Canonical thumbprint form: 40 upper-case hex digits. Tolerates the separators and the
    /// invisible marks (U+200E/U+200F/U+FEFF) that certmgr / PowerShell copy-paste commonly adds.
    /// </summary>
    /// <exception cref="OptionsException">Not 40 hex digits after cleanup.</exception>
    public static string NormalizeThumbprint(string raw)
    {
        var cleaned = new string(raw
            .Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or '‎' or '‏' or '﻿'))
            .ToArray())
            .ToUpperInvariant();

        if (cleaned.Length != 40 || !cleaned.All(Uri.IsHexDigit))
            throw new OptionsException(
                $"Invalid certificate thumbprint '{raw}'; expected 40 hex digits (SHA-1), e.g. from Get-ChildItem Cert:\\CurrentUser\\My.");

        return cleaned;
    }
}
