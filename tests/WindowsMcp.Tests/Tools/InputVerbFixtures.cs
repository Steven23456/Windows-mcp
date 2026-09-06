using System.Text.Json;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// Shared scaffolding for the four B phase-2 input verbs (B-4 click, B-1 type, B-3 scroll,
/// B-2 drag). They all take the same three collaborators and all answer with a small JSON object,
/// so the construction and the JSON reads live in one place.
/// </summary>
internal static class InputVerb
{
    internal static InputTools Tools(
        Mock<IInputService>? input = null,
        Mock<IUIAutomationService>? uia = null,
        Mock<IClipboardService>? clipboard = null)
        => new((input ?? new Mock<IInputService>()).Object,
               (clipboard ?? new Mock<IClipboardService>()).Object,
               (uia ?? new Mock<IUIAutomationService>()).Object);

    internal static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>An element whose centre is (120, 210) unless the caller moves it.</summary>
    internal static ElementInfo Element(
        string id = "el_12", string name = "Save",
        int x = 100, int y = 200, int width = 40, int height = 20, bool offscreen = false)
        => new(id, name, "Button", IsEnabled: true, IsOffscreen: offscreen,
               Bounds: new Bounds(x, y, width, height), Value: null, IsChecked: null, IsSelected: null);

    /// <summary>An element the resolver must refuse: reported on screen, but with no rectangle.</summary>
    internal static ElementInfo Boundless(string id = "el_99")
        => new(id, "Ghost", "Button", IsEnabled: true, IsOffscreen: false,
               Bounds: null, Value: null, IsChecked: null, IsSelected: null);

    /// <summary>True when the key is missing or null - how an optional field is reported.</summary>
    internal static bool Absent(JsonElement root, string name)
        => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    internal static string Str(JsonElement root, string name) => root.GetProperty(name).GetString()!;

    internal static int Num(JsonElement root, string name) => root.GetProperty(name).GetInt32();

    internal static bool Flag(JsonElement root, string name) => root.GetProperty(name).GetBoolean();
}
