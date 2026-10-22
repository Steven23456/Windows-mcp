namespace WindowsMcp.Hosting;

/// <summary>
/// S-11: the wire names of the tools that inject input or move focus. The control indicator is up
/// while any of these runs; every other tool (window, launch, powershell, the read-only ones) leaves
/// it alone. Pinned against the tool inventory by <c>DrivingToolsTests</c>.
/// </summary>
internal static class DrivingTools
{
    internal static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "click", "drag", "hover", "type", "key", "shortcut", "scroll", "multi_select", "multi_edit",
        "interact_element", "file_dialog", "focus", "switch_to_window",
    };
}
