using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class AudioService : IAudioService
{
    public Task<AudioState> GetAsync(CancellationToken ct = default) =>
        throw new NotImplementedException("Wired in Task 6 when PowerShellService lands.");
    public Task SetVolumeAsync(int level, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task SetMutedAsync(bool muted, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
