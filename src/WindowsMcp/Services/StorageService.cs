using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Storage-health diagnostics. Metadata-first and hang-safe: disk/volume/SMART metadata is
/// read instantly; free-space (which reads the filesystem and stalls on slow/sleeping/USB
/// drives) is only probed when requested, each probe time-boxed in an in-process runspace,
/// and the whole query runs under a CancellationToken budget that kills a wedged shell.
/// </summary>
public sealed class StorageService : IStorageService
{
    private readonly IPowerShellService _ps;

    public StorageService(IPowerShellService ps) => _ps = ps;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<StorageHealthReport> GetHealthAsync(
        string? driveLetter = null,
        bool includeUsage = false,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var budget = Math.Clamp(timeoutSeconds, 5, 300);
        var script = BuildScript(driveLetter, includeUsage);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(budget));

        PSResult result;
        try
        {
            result = await _ps.RunAsync(script, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller didn't cancel, so this is our budget timer — a drive likely wedged the shell.
            return EmptyReport($"storage query exceeded the {budget}s budget and was aborted (a drive may be unresponsive)");
        }

        var json = result.Stdout?.Trim();
        if (string.IsNullOrEmpty(json))
        {
            var why = result.Errors.Length > 0 ? ": " + string.Join("; ", result.Errors) : "";
            return EmptyReport("no storage data returned" + why);
        }

        try
        {
            return JsonSerializer.Deserialize<StorageHealthReport>(json, JsonOpts)
                   ?? EmptyReport("storage data could not be parsed");
        }
        catch (JsonException ex)
        {
            return EmptyReport("storage data could not be parsed: " + ex.Message);
        }
    }

    private static StorageHealthReport EmptyReport(string note) =>
        new([], [], [], [], [note]);

    /// <summary>Null for blank/invalid input, otherwise the single uppercase drive letter A-Z.</summary>
    internal static string? NormalizeLetter(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var c = char.ToUpperInvariant(s.Trim()[0]);
        return c is >= 'A' and <= 'Z' ? c.ToString() : null;
    }

    /// <summary>
    /// Builds the diagnostic PowerShell. Each section is independently try/caught so one
    /// failure degrades to a note rather than blanking the report. Validated live before
    /// being embedded here (see git history / scratchpad probe).
    /// </summary>
    internal static string BuildScript(string? driveLetter, bool includeUsage)
    {
        var letter = NormalizeLetter(driveLetter);
        var includeLit = includeUsage ? "$true" : "$false";
        var volFilter = letter is null ? "" : $" | Where-Object {{ $_.DriveLetter -eq '{letter}' }}";
        const int perVolMs = 4000;

        return $$"""
$ErrorActionPreference = 'Stop'
$notes = New-Object System.Collections.ArrayList
$includeUsage = {{includeLit}}
function Add-Note($m) { [void]$notes.Add($m) }

# --- Physical disks + reliability (SMART) ---
$physicalDisks = @()
try {
  $physicalDisks = Get-PhysicalDisk | ForEach-Object {
    $pd = $_; $rel = $null
    try {
      $rc = $pd | Get-StorageReliabilityCounter -ErrorAction Stop
      if ($rc) {
        $rel = [ordered]@{
          temperature            = $rc.Temperature
          readErrorsUncorrected  = $rc.ReadErrorsUncorrected
          writeErrorsUncorrected = $rc.WriteErrorsUncorrected
          wear                   = $rc.Wear
          powerOnHours           = $rc.PowerOnHours
        }
      }
    } catch { }
    [ordered]@{
      deviceId          = [int]$pd.DeviceId
      friendlyName      = $pd.FriendlyName
      mediaType         = "$($pd.MediaType)"
      busType           = "$($pd.BusType)"
      sizeGb            = [math]::Round($pd.Size / 1GB, 1)
      healthStatus      = "$($pd.HealthStatus)"
      operationalStatus = "$($pd.OperationalStatus)"
      reliability       = $rel
    }
  }
} catch { Add-Note "physicalDisks query failed: $($_.Exception.Message)" }

# --- Disks (online/offline) ---
$disks = @()
try {
  $disks = Get-Disk | Sort-Object Number | ForEach-Object {
    [ordered]@{
      number            = [int]$_.Number
      friendlyName      = $_.FriendlyName
      operationalStatus = "$($_.OperationalStatus)"
      healthStatus      = "$($_.HealthStatus)"
      isOffline         = [bool]$_.IsOffline
      partitionStyle    = "$($_.PartitionStyle)"
      sizeGb            = [math]::Round($_.Size / 1GB, 1)
    }
  }
} catch { Add-Note "disks query failed: $($_.Exception.Message)" }

# --- Volumes (metadata-first; free space only when requested, per-volume timeout) ---
$volumes = @()
try {
  $parts = Get-Partition -ErrorAction Stop | Where-Object { $_.DriveLetter }{{volFilter}}
  foreach ($p in $parts) {
    $dl = [string]$p.DriveLetter
    $vol = $null
    try { $vol = Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Volume -Filter "DriveLetter='$dl'" -ErrorAction Stop } catch { }
    $sizeGb = if ($vol) { [math]::Round($vol.Size / 1GB, 1) } else { $null }
    $freeGb = $null; $usageProbed = $false; $usageTimedOut = $false
    if ($includeUsage) {
      # In-process runspace + WaitOne: a timeout without Start-Job's child-process
      # spawn cost (which falsely timed out even healthy drives).
      $rs = [PowerShell]::Create()
      [void]$rs.AddScript('param($d) (Get-Volume -DriveLetter $d -ErrorAction Stop).SizeRemaining').AddArgument($dl)
      $async = $rs.BeginInvoke()
      if ($async.AsyncWaitHandle.WaitOne({{perVolMs}})) {
        try { $rem = @($rs.EndInvoke($async))[0]; if ($null -ne $rem) { $freeGb = [math]::Round($rem / 1GB, 1); $usageProbed = $true } }
        catch { Add-Note "free-space probe failed for ${dl}: $($_.Exception.Message)" }
      } else {
        $usageTimedOut = $true; Add-Note "free-space probe for ${dl}: did not respond in time (drive may be asleep or unresponsive)"
        try { $rs.Stop() } catch { }
      }
      $rs.Dispose()
    }
    $volumes += [ordered]@{
      driveLetter       = $dl
      fileSystemLabel   = if ($vol) { "$($vol.FileSystemLabel)" } else { $null }
      fileSystem        = if ($vol) { "$($vol.FileSystem)" } else { $null }
      healthStatus      = if ($vol) { "$($vol.HealthStatus)" } else { $null }
      operationalStatus = if ($vol) { "$($vol.OperationalStatus)" } else { $null }
      sizeGb            = $sizeGb
      freeGb            = $freeGb
      diskNumber        = [int]$p.DiskNumber
      partitionNumber   = [int]$p.PartitionNumber
      usageProbed       = $usageProbed
      usageTimedOut     = $usageTimedOut
    }
  }
} catch { Add-Note "volumes query failed: $($_.Exception.Message)" }

# --- Recent disk-stack Error/Warning events (System log) ---
$recentEvents = @()
try {
  $evts = Get-WinEvent -FilterHashtable @{ LogName='System'; Level=1,2,3; StartTime=(Get-Date).AddDays(-30) } -ErrorAction Stop |
          Where-Object { $_.ProviderName -match 'disk|ntfs|volmgr|volsnap|partmgr|storahci|stornvme|storport|iaStor|scsi|usbstor|classpnp' } |
          Select-Object -First 25
  $recentEvents = $evts | ForEach-Object {
    [ordered]@{
      time    = $_.TimeCreated.ToString('o')
      id      = [int]$_.Id
      source  = $_.ProviderName
      level   = $_.LevelDisplayName
      message = ($_.Message -split "`n")[0]
    }
  }
} catch {
  if ($_.Exception.Message -notmatch 'No events were found') { Add-Note "events query failed: $($_.Exception.Message)" }
}

[ordered]@{
  physicalDisks = @($physicalDisks)
  disks         = @($disks)
  volumes       = @($volumes)
  recentEvents  = @($recentEvents)
  notes         = @($notes)
} | ConvertTo-Json -Depth 6 -Compress
""";
    }
}
