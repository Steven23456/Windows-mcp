using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class SecurityService : ISecurityService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IPowerShellService _ps;

    public SecurityService(IPowerShellService ps) => _ps = ps;

    public async Task<SecurityAuditDto> AuditAsync(CancellationToken ct = default)
    {
        var result = await _ps.RunAsync(AuditScript, ct);
        var stdout = result.Stdout?.Trim();

        // Empty output means every probe failed (typically no admin) — return a typed result
        // with a note rather than a blank string, so callers always get a parseable shape.
        if (string.IsNullOrEmpty(stdout))
            return new SecurityAuditDto(null, null, null, null, "all probes failed; likely no admin");

        return JsonSerializer.Deserialize<SecurityAuditDto>(stdout, JsonOpts)
            ?? new SecurityAuditDto(null, null, null, null, "audit returned unparseable output");
    }

    // Each probe is isolated in its own try/catch so one missing cmdlet or permission failure
    // doesn't blank the whole report. Keys are PascalCase to match SecurityAuditDto.
    private const string AuditScript = @"
$result = [ordered]@{
  FirewallEnabled = $null
  DefenderRunning = $null
  UacLevel        = $null
  BitlockerStatus = $null
}
try { $result.FirewallEnabled = [bool]((Get-NetFirewallProfile -ErrorAction Stop | Where-Object Enabled).Count -gt 0) } catch {}
try { $result.DefenderRunning = ((Get-Service WinDefend -ErrorAction Stop).Status -eq 'Running') } catch {}
try { $result.UacLevel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Stop).ConsentPromptBehaviorAdmin } catch {}
try { $result.BitlockerStatus = (Get-BitLockerVolume -MountPoint C: -ErrorAction Stop).ProtectionStatus.ToString() } catch {}
$result | ConvertTo-Json -Compress
";
}
