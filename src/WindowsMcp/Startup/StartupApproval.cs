namespace WindowsMcp.Startup;

/// <summary>
/// Decodes the Explorer <c>StartupApproved</c> flag that records whether a Run / Startup-folder
/// item is enabled. The flag's first byte is even when enabled and odd when disabled; an absent
/// flag means the item has never been disabled and is therefore enabled.
/// </summary>
public static class StartupApproval
{
    public static bool IsEnabled(byte[]? flag) =>
        flag is null || flag.Length == 0 || (flag[0] & 1) == 0;
}
