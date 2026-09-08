using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsMcp.Tools;

// MCP9005: the SDK marks the whole Sampling feature obsolete as of specification version
// 2026-07-28 (SEP-2577). C-5's decision is to summarise ONLY through client-side sampling —
// there is no replacement API that asks the client's own model — so the deprecation is accepted
// deliberately and confined to this file, Tools/WebTools.cs and Services/ScrapeSummary.cs (and
// the tests that touch the types). If the SDK removes SampleAsync, `scrape(summarize:true)`
// loses its summary path, not its text path.
#pragma warning disable MCP9005

/// <summary>
/// C-5 (roadmap R8): the seam between <c>scrape(summarize:true)</c> and MCP sampling.
/// <see cref="McpServer"/> is abstract and its capabilities are read-only, so the unit tests fake
/// this interface and <c>StreamTransportTests</c> proves the real adapter over an in-process
/// session-keeping transport (the stateless HTTP transport cannot carry a sampling request, so
/// <c>HttpTransportTests</c> pins the fallback note instead).
/// </summary>
internal interface ISamplingClient
{
    /// <summary>The client declared the sampling capability at initialize.</summary>
    bool Supported { get; }

    /// <summary>Ask the client's model. Throws <c>McpException</c> when the client refuses or fails.</summary>
    Task<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken ct);
}

/// <summary>The real seam: the <see cref="McpServer"/> the SDK bound to the tool call.</summary>
internal sealed class McpServerSampling(McpServer server) : ISamplingClient
{
    public bool Supported => server.ClientCapabilities?.Sampling is not null;

    public Task<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken ct)
        => server.SampleAsync(request, ct).AsTask();
}
#pragma warning restore MCP9005
