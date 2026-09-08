namespace WindowsMcp.Abstractions.Models;

public record HttpResponseDto(int Status, IDictionary<string, string> Headers, string Body);

/// <summary>
/// C-5: the one fact about the transport a tool needs — whether the server keeps a session with
/// its client. The Streamable HTTP transport is stateless by decision (a fresh server per
/// request; see <c>WindowsMcpHost.BuildHttpApp</c>), so a server-to-client request such as
/// sampling has no session to travel on and <c>scrape(summarize:true)</c> must say so instead of
/// blaming the client. Registered by the host like <see cref="ScreenshotOptions"/>; a tool built
/// without one (a unit test) assumes stdio, where the session lives as long as the process.
/// </summary>
public record TransportOptions(bool Stateless)
{
    public static TransportOptions Stdio { get; } = new(false);
}

/// <summary>
/// C-5 (roadmap R8): what <c>scrape</c> answers with, whether it fetched a URL or read the
/// browser's live page. Replaces the bare markdown string the tool used to return.
/// </summary>
/// <param name="Source"><c>http</c> or <c>dom</c> — where the text came from.</param>
/// <param name="Url">
/// The URL the content came from: for <c>http</c> the response's request URI (so a redirect is
/// reported at its destination), for <c>dom</c> the page's own URL. Null when unknown.
/// </param>
/// <param name="Title">The page title (HTML <c>&lt;title&gt;</c> or the document's Name); null when absent or blank.</param>
/// <param name="Chars">The size of the text BEFORE the cap.</param>
/// <param name="Truncated">The cap cut the text.</param>
/// <param name="Content">What came back: the summary when <see cref="Summarized"/>, else the text.</param>
/// <param name="Summarized">The client's model produced <see cref="Content"/>.</param>
/// <param name="Model">The model the client sampled, when <see cref="Summarized"/>; null otherwise.</param>
/// <param name="Note">Why a request was not honoured (no sampling capability, no page document, …).</param>
public record ScrapeResult(
    string Source,
    string? Url,
    string? Title,
    int Chars,
    bool Truncated,
    string Content,
    bool Summarized = false,
    string? Model = null,
    string? Note = null);
