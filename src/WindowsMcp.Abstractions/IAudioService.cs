namespace WindowsMcp.Abstractions;

public interface IAudioService
{
    Task<AudioState> GetAsync(CancellationToken ct = default);
    Task SetVolumeAsync(int level0to100, CancellationToken ct = default);
    Task SetMutedAsync(bool muted, CancellationToken ct = default);
}

public record AudioState(int Level, bool Muted);
