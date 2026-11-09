using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>How pasted text is cut into individual todo items.</summary>
internal enum BulkSplitMode
{
    /// <summary>Every non-empty line becomes one todo.</summary>
    Lines,

    /// <summary>Blank lines separate paragraphs; soft wraps inside a paragraph are joined.</summary>
    Paragraphs
}

/// <summary>
/// User choices for one import. Defaults match the plugin's manifest defaults; the host settings
/// only seed the dialog, the dialog itself owns the live values.
/// </summary>
internal sealed class BulkImportOptions
{
    public BulkSplitMode Mode { get; set; } = BulkSplitMode.Lines;
    public bool StripListMarkers { get; set; } = true;
    public bool DedupeWithinBatch { get; set; } = true;
    public bool SkipExisting { get; set; }
    public bool CollapseWhitespace { get; set; }

    public BulkImportOptions Clone() => new()
    {
        Mode = Mode,
        StripListMarkers = StripListMarkers,
        DedupeWithinBatch = DedupeWithinBatch,
        SkipExisting = SkipExisting,
        CollapseWhitespace = CollapseWhitespace
    };
}

/// <summary>Outcome of one parse pass. Item ordering follows the pasted document.</summary>
internal sealed class BulkParseResult
{
    public IReadOnlyList<string> Items { get; init; } = [];
    public int BlankLines { get; init; }
    public int Duplicates { get; init; }
    public int Existing { get; init; }
    public int Truncated { get; init; }
    public int Overflow { get; init; }

    public bool HasWarnings =>
        Truncated > 0 || Overflow > 0;
}

/// <summary>
/// Pure text-to-todos parser. Host limits are duplicated here on purpose: a batch that violates
/// them is rejected as a whole by <c>todos.append</c>, so the UI must never submit one.
/// </summary>
internal static class BulkImportParser
{
    /// <summary>Mirrors the host limit for one append batch (PaperWindow.MaxPastedTodoLines).</summary>
    public const int MaxItems = 200;

    /// <summary>Mirrors the host limit for one todo text (PaperWindow.TodoTextMaxLength).</summary>
    public const int MaxItemLength = 5000;

    private const int MaxPreviewScanLength = 400_000;

    // 1. / 1、/ 1) / (1) / - / * / + / • / · / ► / ▶ / ✓ / ✔ / # / ## / □ / ☐ / ☑ / [ ] / [x]
    // A "3." directly followed by another digit is a decimal ("3.5 米"), not a list marker.
    private static readonly Regex ListMarkerPattern = new(
        @"^(?:[-*+•·–—►▶✓✔]\s+|\d{1,3}[\.、\)）](?!\d)\s*|[（\(]\d{1,3}[\)）](?!\d)\s*|[#]{1,6}\s+|(?:\[[ xX✓✔\-]\]|☐|☑|□)\s*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A line that carries only list-marker glyphs has no task text of its own.
    private static readonly Regex MarkerOnlyPattern = new(
        @"^[-*+•·–—►▶✓✔#\s]+$|^\d{1,3}[\.、\)）]$|^[（\(]\d{1,3}[\)）]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InlineSpacePattern = new(
        @"[ \t\u00A0\u3000]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParagraphBreakPattern = new(
        @"\n[ \t\u00A0\u3000]*\n+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BulkParseResult Parse(
        string? rawText,
        BulkImportOptions options,
        IEnumerable<string>? existingTexts = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new BulkParseResult();
        }

        var text = rawText!;
        if (text.Length > MaxPreviewScanLength)
        {
            text = text[..MaxPreviewScanLength];
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var chunks = options.Mode == BulkSplitMode.Paragraphs
            ? SplitParagraphs(normalized)
            : normalized.Split('\n');

        var existingSet = options.SkipExisting && existingTexts != null
            ? new HashSet<string>(
                existingTexts
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()),
                StringComparer.OrdinalIgnoreCase)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var items = new List<string>(Math.Min(chunks.Length, MaxItems));
        var blank = 0;
        var duplicates = 0;
        var existing = 0;
        var truncated = 0;
        var overflow = 0;

        foreach (var chunk in chunks)
        {
            var line = Normalize(chunk, options);
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (options.DedupeWithinBatch && !seen.Add(line))
            {
                duplicates++;
                continue;
            }

            if (existingSet != null && existingSet.Contains(line))
            {
                existing++;
                continue;
            }

            if (line.Length > MaxItemLength)
            {
                line = line[..MaxItemLength];
                truncated++;
            }

            if (items.Count >= MaxItems)
            {
                overflow++;
                continue;
            }

            items.Add(line);
        }

        return new BulkParseResult
        {
            Items = items,
            BlankLines = blank,
            Duplicates = duplicates,
            Existing = existing,
            Truncated = truncated,
            Overflow = overflow
        };
    }

    private static string[] SplitParagraphs(string normalized)
    {
        var paragraphs = ParagraphBreakPattern.Split(normalized);
        for (var index = 0; index < paragraphs.Length; index++)
        {
            // Soft wraps inside one paragraph are not task separators.
            paragraphs[index] = paragraphs[index].Replace('\n', ' ');
        }
        return paragraphs;
    }

    private static string Normalize(string chunk, BulkImportOptions options)
    {
        var value = chunk.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        // Strip BOM / zero-width artifacts that survive a copy from some editors and PDF tools.
        value = value.Trim('\uFEFF', '\u200B', '\u200E', '\u202A', '\u202C').Trim();

        if (options.StripListMarkers)
        {
            if (MarkerOnlyPattern.IsMatch(value))
            {
                return string.Empty;
            }

            var stripped = ListMarkerPattern.Replace(value, string.Empty, 1).TrimStart();
            // A line that is *only* a marker ("-", "1.") carries no task text.
            if (stripped.Length == 0 || MarkerOnlyPattern.IsMatch(stripped))
            {
                return string.Empty;
            }

            value = stripped;
        }

        if (options.CollapseWhitespace)
        {
            value = InlineSpacePattern.Replace(value, " ");
        }

        return value.Trim();
    }
}
