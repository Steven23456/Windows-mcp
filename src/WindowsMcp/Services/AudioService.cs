// TODO(v0.3.0): swap to NAudio/AudioDeviceCmdlets for accurate get/set
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class AudioService : IAudioService
{
    private readonly IPowerShellService _ps;

    public AudioService(IPowerShellService ps) => _ps = ps;

    public async Task<AudioState> GetAsync(CancellationToken ct = default)
    {
        var script = @"
            $level = (Get-AudioDevice -PlaybackVolume) 2>$null
            if (-not $level) { $level = 50 }
            ""$level""
        ";
        var r = await _ps.RunAsync(script, ct);
        var lvl = int.TryParse(r.Stdout.Trim(), out var v) ? v : 50;
        return new AudioState(lvl, false);
    }

    public async Task SetVolumeAsync(int level, CancellationToken ct = default)
    {
        level = Math.Clamp(level, 0, 100);
        // SendKeys approach: each press is ~2%; go to 0 first then up to target
        var script = $@"
            $wsh = New-Object -ComObject WScript.Shell
            for ($i = 0; $i -lt 50; $i++) {{ $wsh.SendKeys([char]174) }}
            for ($i = 0; $i -lt {level / 2}; $i++) {{ $wsh.SendKeys([char]175) }}
        ";
        await _ps.RunAsync(script, ct);
    }

    public async Task SetMutedAsync(bool muted, CancellationToken ct = default)
    {
        await _ps.RunAsync(@"
            $wsh = New-Object -ComObject WScript.Shell
            $wsh.SendKeys([char]173)
        ", ct);
    }
}
