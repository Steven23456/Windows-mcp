using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class SecurityTools
{
    private readonly IAuthenticodeInspector _authenticode;
    private readonly ISecurityService _security;

    public SecurityTools(IAuthenticodeInspector authenticode, ISecurityService security)
    {
        _authenticode = authenticode;
        _security = security;
    }

    [McpServerTool, Description(
        "Verify a file's Authenticode code-signing trust. Catalog-aware: Windows system files and " +
        "drivers signed via security catalogs (not embedded certificates) are correctly reported as " +
        "trusted. Returns {trusted, signer}; signer is the embedded certificate subject and is null " +
        "for catalog-signed files even when trusted. Useful for vetting a suspicious binary surfaced " +
        "elsewhere (e.g. a process path or an unknown autostart entry).")]
    public string VerifySignature(
        [Description("Full path to the file to verify")] string path)
    {
        var info = _authenticode.Inspect(path);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool, Description(
        "Get Microsoft Defender posture: real-time protection, tamper protection, behavior monitoring, " +
        "signature version + last-updated, and last quick/full scan times. Null fields (with a Note) mean " +
        "Defender is disabled or replaced by a third-party AV.")]
    public async Task<string> DefenderStatus(CancellationToken ct = default)
    {
        var status = await _security.GetDefenderStatusAsync(ct);
        return JsonSerializer.Serialize(status);
    }
}
