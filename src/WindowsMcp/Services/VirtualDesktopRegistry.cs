using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-12's pure core: the virtual-desktop inventory from the two registry blobs Explorer keeps
/// (<c>VirtualDesktopIDs</c>: 16 bytes per desktop in order; <c>CurrentVirtualDesktop</c>: one
/// GUID) and the per-desktop <c>Name</c> values. No registry access here — the service reads,
/// this decides — so every rule is unit-tested on byte arrays.
/// </summary>
internal static class VirtualDesktopRegistry
{
    /// <summary>
    /// One entry per complete 16-byte GUID in <paramref name="ids"/> (a trailing partial GUID is
    /// ignored), in registry order; <c>IsCurrent</c> on the one <paramref name="current"/> names
    /// (none when it is absent, malformed, or unknown); the stored name, or <c>Desktop N</c> when
    /// none (blank counts as none). Never null.
    /// </summary>
    internal static VirtualDesktopInfo[] Parse(byte[]? ids, byte[]? current, Func<Guid, string?> nameOf)
    {
        if (ids is null || ids.Length < 16) return [];

        Guid? currentGuid = current is { Length: >= 16 } ? new Guid(current.AsSpan(0, 16)) : null;

        var count = ids.Length / 16;
        var result = new VirtualDesktopInfo[count];
        for (int i = 0; i < count; i++)
        {
            var guid = new Guid(ids.AsSpan(i * 16, 16));
            var stored = nameOf(guid);
            var name = string.IsNullOrWhiteSpace(stored) ? $"Desktop {i + 1}" : stored;
            result[i] = new VirtualDesktopInfo(Id(guid), name, i, currentGuid == guid);
        }
        return result;
    }

    /// <summary>The wire form of a desktop id: lower-case, dashed, no braces.</summary>
    internal static string Id(Guid g) => g.ToString("D").ToLowerInvariant();

    /// <summary>The registry subkey Explorer uses for a desktop: upper-case, braced.</summary>
    internal static string GuidKey(Guid g) => g.ToString("B").ToUpperInvariant();
}
