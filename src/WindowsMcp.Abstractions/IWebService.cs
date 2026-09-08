using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IWebService
{
    /// <summary>
    /// C-5: fetch <paramref name="url"/>, convert the HTML to Markdown and report it as a
    /// <see cref="ScrapeResult"/>. The private-address check runs first, before anything is
    /// fetched. <paramref name="maxChars"/> (1…) cuts the text; <c>Chars</c> is the size before
    /// the cut and <c>Truncated</c> says it was cut. Replaces the string-returning overload.
    /// </summary>
    Task<ScrapeResult> ScrapeAsync(string url, int maxChars = 100000, CancellationToken ct = default);
    Task<HttpResponseDto> RequestAsync(string url, string method, IDictionary<string, string>? headers, string? body, CancellationToken ct = default);
}
