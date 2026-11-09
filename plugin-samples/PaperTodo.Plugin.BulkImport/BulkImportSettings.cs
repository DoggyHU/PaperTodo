using System.Text.Json;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Reads the host-managed plugin settings and the plugin's own per-paper state. Protocol 2.0 gives
/// app runtimes a read-only settings view, so the dialog only uses these values as seeds for its
/// checkboxes.
/// </summary>
internal static class BulkImportSettings
{
    /// <summary>Last todo paper the user imported into; used when the button is clicked on a non-todo paper.</summary>
    public static string? LastTargetPaperId { get; set; }

    public static BulkImportOptions Parse(string? settingsJson)
    {
        var options = new BulkImportOptions();
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

            if (root.TryGetProperty("splitMode", out var mode) &&
                mode.ValueKind == JsonValueKind.String)
            {
                options.Mode = string.Equals(
                    mode.GetString(),
                    "paragraph",
                    StringComparison.OrdinalIgnoreCase)
                    ? BulkSplitMode.Paragraphs
                    : BulkSplitMode.Lines;
            }

            options.StripListMarkers = ReadBoolean(root, "stripMarkers", options.StripListMarkers);
            options.DedupeWithinBatch = ReadBoolean(root, "dedupeBatch", options.DedupeWithinBatch);
            options.SkipExisting = ReadBoolean(root, "skipExisting", options.SkipExisting);
            options.CollapseWhitespace = ReadBoolean(root, "collapseWhitespace", options.CollapseWhitespace);
        }
        catch (JsonException)
        {
            // A malformed settings blob must never stop the import surface from opening.
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
