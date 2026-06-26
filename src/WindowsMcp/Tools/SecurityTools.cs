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
    private readonly ICertStoreService _certStore;

    public SecurityTools(IAuthenticodeInspector authenticode, ISecurityService security, ICertStoreService certStore)
    {
        _authenticode = authenticode;
        _security = security;
        _certStore = certStore;
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

    [McpServerTool, Description(
        "Enumerate certificates in a Windows certificate store. location: LocalMachine (default) or " +
        "CurrentUser; store_name: Root (default), CA, My, etc. Each cert reports subject, issuer, " +
        "thumbprint, expiry, and self-signed/expired flags. A self-signed cert in the Root store is " +
        "normal for legitimate CAs but is also how a rogue/MITM root persists — review unfamiliar ones.")]
    public async Task<string> CertStore(
        [Description("Store location: LocalMachine or CurrentUser")] string location = "LocalMachine",
        [Description("Store name: Root, CA, My, etc.")] string store_name = "Root",
        CancellationToken ct = default)
    {
        var certs = await _certStore.ListAsync(location, store_name, ct);
        return JsonSerializer.Serialize(certs);
    }
}
