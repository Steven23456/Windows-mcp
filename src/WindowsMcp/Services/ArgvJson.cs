using System.Text.Json;

namespace WindowsMcp.Services;

/// <summary>
/// B-11: the <c>args_json</c> parameter of <c>start_process</c>, parsed. The MCP SDK hands a
/// <c>string?</c> parameter the raw JSON text, so an argv list sent as a JSON array and one sent
/// as a JSON-stringified array (the Claude Desktop quirk) arrive the same way and parse the same
/// way. Anything that is not an array of strings is refused by name.
/// </summary>
internal static class ArgvJson
{
    /// <summary>
    /// Null or blank → null (no argv list: the command keeps today's whole-command-line meaning).
    /// A JSON array of strings → its items, verbatim and in order (an empty array → an empty
    /// array, which still means "argv mode"). Anything else — a JSON object, a bare string, an
    /// array holding a non-string — is an <see cref="ArgumentException"/> naming
    /// <c>args_json</c>.
    /// </summary>
    internal static string[]? Parse(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(argsJson); }
        catch (JsonException ex)
        {
            throw new ArgumentException($"args_json must be a JSON array of strings, e.g. [\"/c\",\"echo hi\"]; it did not parse: {ex.Message}", nameof(argsJson));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"args_json must be a JSON array of strings, got {doc.RootElement.ValueKind}.", nameof(argsJson));

            var items = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"args_json must be a JSON array of strings; item {items.Count} is {element.ValueKind}.", nameof(argsJson));
                items.Add(element.GetString()!);
            }
            return items.ToArray();
        }
    }
}
