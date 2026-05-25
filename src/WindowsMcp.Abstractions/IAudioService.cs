namespace WindowsMcp.Abstractions;

public interface IAudioService
{
    /// <summary>
    /// Get the current playback volume level (0-100) and muted state.
    /// </summary>
    /// <remarks>
    /// v0.2.0 limitation: the PowerShell+SendKeys backend cannot read mute state.
    /// <see cref="AudioState.Muted"/> is always reported as <c>false</c>; query
    /// via a real audio API (NAudio, AudioDeviceCmdlets) for accurate readings.
    /// Tracked for v0.3.0.
    /// </remarks>
    Task<AudioState> GetAsync(CancellationToken ct = default);

    Task SetVolumeAsync(int level0to100, CancellationToken ct = default);

    /// <summary>
    /// Set the playback muted state.
    /// </summary>
    /// <remarks>
    /// v0.2.0 limitation: the SendKeys backend can only toggle, not set. Each call
    /// sends VK_VOLUME_MUTE which flips the OS mute state. Two consecutive calls
    /// with the same <paramref name="muted"/> value will cancel out. Track callsite
    /// state externally if you need a true setter, or migrate to v0.3.0 when an
    /// accurate audio API is wired.
    /// </remarks>
    Task SetMutedAsync(bool muted, CancellationToken ct = default);
}

public record AudioState(int Level, bool Muted);
