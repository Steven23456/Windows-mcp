namespace WindowsMcp.Services;

/// <summary>
/// E-8: the name of a mandatory integrity level from its RID (the last sub-authority of the
/// token's label SID). Pure. Each named level covers the RIDs up to the next one, so a level
/// between two well-known ones is reported as the lower.
/// </summary>
internal static class IntegrityLevels
{
    internal static string Name(uint rid) => rid switch
    {
        < 0x1000 => "Untrusted",
        < 0x2000 => "Low",
        < 0x2100 => "Medium",
        < 0x3000 => "MediumPlus",
        < 0x4000 => "High",
        < 0x5000 => "System",
        _ => "Protected",
    };
}
