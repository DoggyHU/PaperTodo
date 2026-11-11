using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>Host limits that one apply pass must never exceed.</summary>
internal static class BulkImportLimits
{
    /// <summary>Mirrors the host limit for one append batch (PaperWindow.MaxPastedTodoLines).</summary>
    public const int MaxAddsPerBatch = 200;

    /// <summary>Mirrors the host limit for one todo text (PaperWindow.TodoTextMaxLength).</summary>
    public const int MaxItemLength = 5000;
}

/// <summary>Pure text helpers for turning the editor's text into a list of task lines.</summary>
internal static class BulkImportText
{
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

    /// <summary>
    /// Splits editor text into one entry per non-empty line. CRLF, CR and LF all separate lines,
    /// surrounding whitespace is trimmed and blank lines never become tasks (the host rejects
    /// empty todo text).
    /// </summary>
    public static List<string> SplitLines(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.Trim().Trim('\uFEFF', '\u200B', '\u200E', '\u202A', '\u202C').Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// Cleans one newly added line: drops a leading list marker / numbering and collapses runs of
    /// spaces. Existing task lines are never passed through this, so an edit can't silently renumber
    /// tasks the user already has.
    /// </summary>
    public static string CleanNewLine(string line, bool stripMarker)
    {
        var value = line.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (stripMarker)
        {
            if (MarkerOnlyPattern.IsMatch(value))
            {
                return string.Empty;
            }

            var stripped = ListMarkerPattern.Replace(value, string.Empty, 1).TrimStart();
            if (stripped.Length == 0 || MarkerOnlyPattern.IsMatch(stripped))
            {
                return string.Empty;
            }

            value = stripped;
        }

        return InlineSpacePattern.Replace(value, " ").Trim();
    }

    public static string Truncate(string line) =>
        line.Length > BulkImportLimits.MaxItemLength
            ? line[..BulkImportLimits.MaxItemLength]
            : line;

    public static string Normalize(string? value) => (value ?? string.Empty).Trim();

    /// <summary>
    /// Similarity in [0,1] between an existing task and an edited line. Used only to decide whether
    /// an edited line is a rewrite of an existing task (keep its id / done state / position) rather
    /// than a delete + add, so it favors recall: one text containing the other, or a decent overlap
    /// of characters or character pairs, both count as a rewrite.
    /// </summary>
    public static double Similarity(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        // Growing or shortening a task in place ("二" -> "二改") is the most common edit.
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return 0.95;
        }

        var unigrams = Jaccard(Unigrams(a), Unigrams(b));
        var bigrams = Jaccard(Bigrams(a), Bigrams(b));
        return Math.Max(unigrams, bigrams);
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var gram in left)
        {
            if (right.Contains(gram))
            {
                intersection++;
            }
        }

        var union = left.Count + right.Count - intersection;
        return union <= 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Unigrams(string value)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in value)
        {
            grams.Add(character.ToString());
        }
        return grams;
    }

    private static HashSet<string> Bigrams(string value)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (value.Length == 1)
        {
            grams.Add(value);
            return grams;
        }

        for (var index = 0; index < value.Length - 1; index++)
        {
            grams.Add(value.Substring(index, 2));
        }
        return grams;
    }
}
