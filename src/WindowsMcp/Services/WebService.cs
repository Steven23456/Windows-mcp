using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WebService : IWebService
{
    // Singleton HttpClient per process — standard guidance for .NET HttpClient lifecycle.
    private static readonly HttpClient _client = new();

    private readonly bool _allowPrivateIps;
    private readonly ILogger? _log;

    /// <summary>Production constructor: SSRF protection is active (allowPrivateIps = false).</summary>
    public WebService(ILogger<WebService>? log = null)
        : this(allowPrivateIps: false, log) { }

    /// <summary>
    /// Test-accessible constructor. Set allowPrivateIps: true when using LocalHttpServerFixture
    /// (which binds to 127.0.0.1, otherwise blocked by SSRF protection).
    /// </summary>
    public WebService(bool allowPrivateIps, ILogger<WebService>? log = null)
    {
        _allowPrivateIps = allowPrivateIps;
        _log = log;
    }

    public async Task<string> ScrapeAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await ValidateUrlAsync(url, ct);
        var html = await _client.GetStringAsync(url, ct);
        var converter = new ReverseMarkdown.Converter();
        return converter.Convert(html);
    }

    public async Task<HttpResponseDto> RequestAsync(
        string url,
        string method,
        IDictionary<string, string>? headers,
        string? body,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await ValidateUrlAsync(url, ct);

        var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (headers != null)
        {
            foreach (var kv in headers)
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        if (body != null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(
                h => h.Key,
                h => string.Join(", ", h.Value));

        return new HttpResponseDto(
            Status: (int)response.StatusCode,
            Headers: responseHeaders,
            Body: responseBody);
    }

    private async Task ValidateUrlAsync(string url, CancellationToken ct)
    {
        if (_allowPrivateIps) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid URL format");

        // Resolve hostname and check ALL resolved IPs (defends against DNS rebinding)
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (SocketException)
        {
            // If resolution fails, let the HTTP client fail naturally
            return;
        }

        foreach (var addr in addresses)
        {
            if (IsPrivateAddress(addr))
                throw new InvalidOperationException(
                    $"URL targets a private IP address; refusing (resolved: {addr})");
        }
    }

    // internal for white-box testing of the SSRF range logic (InternalsVisibleTo).
    internal static bool IsPrivateAddress(IPAddress addr)
    {
        // Normalize IPv4-mapped IPv6 addresses (e.g. ::ffff:127.0.0.1)
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();
            // 127.0.0.0/8 — loopback
            if (bytes[0] == 127) return true;
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12 — 172.16.x.x to 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 — link-local
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            return false;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 — IPv6 loopback
            if (addr.Equals(IPAddress.IPv6Loopback)) return true;
            var bytes = addr.GetAddressBytes();
            // fc00::/7 — unique local (fc00:: and fd00::)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // fe80::/10 — link-local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
            return false;
        }

        return false;
    }
}
