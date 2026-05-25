using ModelContextProtocol.Server;
using System.ComponentModel;

namespace WindowsMcp.Tools;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echo the input text back. Used to verify the server is alive.")]
    public static string Echo(
        [Description("Text to echo back")] string text) => text;
}
