using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace WindowsMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // CRITICAL: MCP stdio servers must log to stderr only. stdout is JSON-RPC.
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();   // source generator discovers [McpServerTool] methods

        await builder.Build().RunAsync();
        return 0;
    }
}
