using System.Text.Json;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Reads the host-managed plugin settings and the plugin's own per-paper state. Protocol 2.0 gives
/// app runtimes a read-only settings view, so the editor only uses these values as seeds.
/// </summary>
internal static class BulkImportSettings
{
    /// <summary>Last todo paper the user edited; used when the button is clicked on a non-todo paper.</summary>
    public static string? LastTargetPaperId { get; set; }

    public static BulkEditOptions Parse(string? settingsJson)
    {
        var options = new BulkEditOptions();
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return options;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return options;
            }

            options.StripMarkersOnNewLines = ReadBoolean(root, "stripMarkers", options.StripMarkersOnNewLines);
            options.DedupeNewLines = ReadBoolean(root, "dedupeBatch", options.DedupeNewLines);
        }
        catch (JsonException)
        {
            // A malformed settings blob must never stop the editor from opening.
        }

        return options;
    }

    public static string? ReadLastTargetPaperId(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("lastTargetPaperId", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var id = value.GetString();
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }
        catch (JsonException)
        {
            // Ignore unreadable frontend state; target selection simply starts empty.
        }

        return null;
    }

    public static string WriteLastTargetPaperId(string paperId) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["lastTargetPaperId"] = paperId });

    private static bool ReadBoolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
