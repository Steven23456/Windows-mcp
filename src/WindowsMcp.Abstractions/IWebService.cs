using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IWebService
{
    Task<string> ScrapeAsync(string url, CancellationToken ct = default);
    Task<HttpResponseDto> RequestAsync(string url, string method, IDictionary<string, string>? headers, string? body, CancellationToken ct = default);
}
