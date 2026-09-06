namespace WindowsMcp.Services;

/// <summary>
/// B-7: one entry of a batch — a point or an element id, plus the B-1 typing options for
/// <c>multi_edit</c>. Exactly one of (<see cref="X"/> and <see cref="Y"/>) or
/// <see cref="ElementId"/> is set; the tool resolves it through the same C1 resolver every verb uses.
/// </summary>
internal sealed record BatchTarget(
    int? X,
    int? Y,
    string? ElementId,
    string? Text = null,
    bool Clear = false,
    bool PressEnter = false);

/// <summary>
/// B-7: the pure parser behind <c>multi_select</c> and <c>multi_edit</c>. Both parameters arrive
/// as a STRING (a JSON array, possibly already stringified by the client and possibly padded with
/// whitespace or CRLF), so parsing is one place with one set of refusals — each naming the
/// parameter and the index of the offending entry.
/// </summary>
internal static class BatchTargets
{
    /// <summary><c>multi_select</c>'s targets: <c>{x,y}</c> or <c>{element_id}</c>, no text.</summary>
    internal static IReadOnlyList<BatchTarget> ParseTargets(string json) => Parse(json, "targets_json", requireText: false);

    /// <summary><c>multi_edit</c>'s entries: the same target plus a required <c>text</c> and the optional <c>clear</c> / <c>press_enter</c>.</summary>
    internal static IReadOnlyList<BatchTarget> ParseEntries(string json) => Parse(json, "entries_json", requireText: true);

    /// <summary>
    /// A JSON array of objects — sent as JSON text or as a JSON string holding that text (the
    /// Claude Desktop quirk). Every refusal names the parameter and the entry's index so the
    /// caller can fix one entry instead of guessing.
    /// </summary>
    private static IReadOnlyList<BatchTarget> Parse(string json, string parameter, bool requireText)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"{parameter} must be a JSON array of targets; it was empty.", parameter);

        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(json); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException($"{parameter} must be a JSON array of targets; it did not parse: {ex.Message}", parameter);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                // Stringified JSON: unwrap once and parse again.
                return Parse(root.GetString() ?? "", parameter, requireText);
            }
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new ArgumentException($"{parameter} must be a JSON array of targets, got {root.ValueKind}.", parameter);

            var targets = new List<BatchTarget>();
            int index = 0;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new ArgumentException($"{parameter}[{index}] must be an object ({{x,y}} or {{element_id}}), got {item.ValueKind}.", parameter);

                int? x = ReadInt(item, "x", parameter, index), y = ReadInt(item, "y", parameter, index);
                string? id = ReadString(item, "element_id", parameter, index, required: false);
                bool hasPoint = x is not null || y is not null;
                if (hasPoint && id is not null)
                    throw new ArgumentException($"{parameter}[{index}]: give either x and y or element_id, not both.", parameter);
                if (!hasPoint && id is null)
                    throw new ArgumentException($"{parameter}[{index}]: give a target, x and y or element_id.", parameter);
                if (hasPoint && (x is null || y is null))
                    throw new ArgumentException($"{parameter}[{index}]: x and y must be given together.", parameter);

                string? text = ReadString(item, "text", parameter, index, required: requireText);
                bool clear = ReadBool(item, "clear", parameter, index);
                bool enter = ReadBool(item, "press_enter", parameter, index);
                targets.Add(new BatchTarget(x, y, id, text, clear, enter));
                index++;
            }
            if (targets.Count == 0)
                throw new ArgumentException($"{parameter} must hold at least one target.", parameter);
            return targets;
        }
    }

    private static int? ReadInt(System.Text.Json.JsonElement item, string name, string parameter, int index)
    {
        if (!item.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        throw new ArgumentException($"{parameter}[{index}].{name} must be an integer.", parameter);
    }

    private static string? ReadString(System.Text.Json.JsonElement item, string name, string parameter, int index, bool required)
    {
        if (!item.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            if (required) throw new ArgumentException($"{parameter}[{index}] needs {name} (a string).", parameter);
            return null;
        }
        if (v.ValueKind != System.Text.Json.JsonValueKind.String)
            throw new ArgumentException($"{parameter}[{index}].{name} must be a string.", parameter);
        return v.GetString();
    }

    private static bool ReadBool(System.Text.Json.JsonElement item, string name, string parameter, int index)
    {
        if (!item.TryGetProperty(name, out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null) return false;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => throw new ArgumentException($"{parameter}[{index}].{name} must be true or false.", parameter),
        };
    }
}
