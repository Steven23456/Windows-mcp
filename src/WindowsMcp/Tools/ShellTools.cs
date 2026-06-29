using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ShellTools
{
    private readonly IPowerShellService _ps;

    public ShellTools(IPowerShellService ps)
    {
        _ps = ps;
    }

    [McpServerTool, Description("Execute a PowerShell command and return the result including stdout, stderr, and exit code.")]
    public async Task<string> Powershell(
        [Description("PowerShell command or script to execute")] string command,
        CancellationToken ct = default)
    {
        var result = await _ps.RunAsync(command, ct);
        return JsonSerializer.Serialize(result);
    }
}
