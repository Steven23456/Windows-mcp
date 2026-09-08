using ModelContextProtocol.Protocol;

namespace WindowsMcp.Services;

// MCP9005: the SDK marks the Sampling feature obsolete as of specification version 2026-07-28
// (SEP-2577). See the note on Tools/ISamplingClient.cs — C-5 summarises only through client-side
// sampling, so the deprecation is accepted deliberately and confined to these two files.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): the pure builder for the sampling request <c>scrape(summarize:true)</c> sends
/// to the CLIENT's model. Pure so the prompt is unit-testable without a server or a model.
/// </summary>
internal static class ScrapeSummary
{
    /// <summary>The output budget asked of the client's model.</summary>
    internal const int MaxTokens = 2048;

    private const string Common =
        "You are given the text of a web page, extracted from its HTML or read from the live " +
        "browser tab. Ignore navigation menus, headers, footers, cookie and consent banners, " +
        "advertisements, and repeated boilerplate; work only from the page's own content. Keep " +
        "names, numbers, dates, prices and quoted values exactly as they appear; do not invent " +
        "anything the page does not say. ";

    private const string Partial =
        " The text you are given is only the beginning of a longer page: it was cut before the " +
        "end. Say so, and do not conclude that the page lacks something that may lie beyond the cut.";

    /// <summary>
    /// The system prompt: strip navigation, headers, footers, cookie and consent banners,
    /// advertisements and repeated boilerplate; keep names, numbers, dates, prices and quoted
    /// values verbatim; invent nothing. With a <paramref name="query"/> it answers that from the
    /// page and says plainly when the page does not answer it; without one it summarises the page
    /// faithfully.
    /// </summary>
    /// <param name="truncated">
    /// Review F13: the text is only the beginning of a longer page (the <c>max_chars</c> cap, or
    /// a walk the element budget cut short). The model has to be told, or it answers "the page
    /// does not mention it" about a part of the page it was never shown.
    /// </param>
    internal static string SystemPrompt(string? query, bool truncated)
    {
        var prompt = string.IsNullOrWhiteSpace(query)
            ? Common + "Summarise the page faithfully and completely enough that the reader does not need to open it."
            : Common + "Answer this from the page, quoting the relevant passages, and say plainly when the page " +
              $"does not answer it: {query.Trim()}";
        return truncated ? prompt + Partial : prompt;
    }

    /// <summary>
    /// The request: <see cref="SystemPrompt"/>, one user message carrying the (already capped)
    /// <paramref name="content"/>, and <see cref="MaxTokens"/>.
    /// </summary>
    internal static CreateMessageRequestParams Request(string content, string? query, bool truncated) => new()
    {
        SystemPrompt = SystemPrompt(query, truncated),
        Messages =
        [
            new SamplingMessage
            {
                Role = Role.User,
                Content = [new TextContentBlock { Text = content }],
            },
        ],
        MaxTokens = MaxTokens,
    };
}
#pragma warning restore MCP9005
