using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class RegistryTools
{
    private readonly IRegistryService _registry;

    public RegistryTools(IRegistryService registry)
    {
        _registry = registry;
    }

    [McpServerTool, Description("Read a Windows registry value.")]
    public async Task<string> RegistryGet(
        [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
        [Description("Subkey path like 'Software\\Microsoft\\Windows'")] string path,
        [Description("Specific value name; if omitted lists all value names")] string? value_name = null)
    {
        var result = await _registry.GetAsync(hive, path, value_name);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Write a Windows registry value. Requires confirm: true.")]
    public async Task<string> RegistrySet(
        [Description("Hive: HKCU, HKLM, HKCR, HKU")] string hive,
        [Description("Subkey path like 'Software\\Microsoft\\Windows'")] string path,
        [Description("Value name to set")] string value_name,
        [Description("Data to write")] string data,
        [Description("Registry value kind: String, DWord, QWord, Binary, MultiString, ExpandString")] string kind,
        [Description("Must be true to confirm the registry write")] bool confirm = false)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for registry writes");
        await _registry.SetAsync(hive, path, value_name, data, kind);
        return $"set {hive}\\{path}\\{value_name}";
    }
}
