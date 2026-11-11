namespace PaperTodo.Plugin.BulkImport;

/// <summary>One task as the editor knows it, captured when the editor loaded the paper.</summary>
internal sealed record EditorTodo(string Id, string Text, bool Done);

/// <summary>Options that only affect newly added lines; existing tasks are never rewritten.</summary>
internal sealed class BulkEditOptions
{
    public bool StripMarkersOnNewLines { get; set; } = true;
    public bool DedupeNewLines { get; set; } = true;

    public BulkEditOptions Clone() => new()
    {
        StripMarkersOnNewLines = StripMarkersOnNewLines,
        DedupeNewLines = DedupeNewLines
    };
}

/// <summary>One line of the final list, bound to an existing task when it survived the edit.</summary>
internal sealed class PlannedLine
{
    public string? Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsNew => Id == null;
}

/// <summary>
/// What one apply pass must do. The plan is computed before any host call so the UI can show an
/// exact diff and gate destructive passes behind a confirmation.
/// </summary>
internal sealed class TodoEditPlan
{
    public List<PlannedLine> Lines { get; } = [];
    public List<string> Deletes { get; } = [];
    public List<(string Id, string Text)> Renames { get; } = [];
    public List<string> Adds { get; } = [];
    public int Unchanged { get; set; }
    public int Truncated { get; set; }
    public int DroppedDuplicates { get; set; }

    /// <summary>True when the surviving tasks only changed position (no add/rename/delete).</summary>
    public bool Rearranged { get; set; }

    public List<string> Warnings { get; } = [];

    public bool HasChanges =>
        Deletes.Count > 0 || Renames.Count > 0 || Adds.Count > 0 || Rearranged;

    /// <summary>Similarity gate for treating a removed + added line as one in-place rewrite.</summary>
    public const double RenameSimilarity = 0.5;

    /// <summary>
    /// Diffs the editor text against the tasks captured when the editor loaded.
    ///
    /// Identity is preserved by exact text first (so unchanged tasks keep id, done state, reminder
    /// and links), then by fuzzy line pairing for lines that were rewritten in place. Everything
    /// left over becomes a delete or an add.
    /// </summary>
    public static TodoEditPlan Build(
        IReadOnlyList<EditorTodo> baseline,
        string? editorText,
        BulkEditOptions options)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(options);

        var plan = new TodoEditPlan();
        var rawLines = BulkImportText.SplitLines(editorText);
        var used = new bool[baseline.Count];

        // Pending matches keyed by normalized text, in paper order so duplicates pair up fairly.
        var pool = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < baseline.Count; index++)
        {
            var key = BulkImportText.Normalize(baseline[index].Text);
            if (!pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<int>();
                pool[key] = queue;
            }
            queue.Enqueue(index);
        }

        var newLineIndices = new List<int>();

        // Pass 1: exact text match against existing tasks.
        foreach (var raw in rawLines)
        {
            var key = BulkImportText.Normalize(raw);
            if (TryTake(pool, key, out var oldIndex))
            {
                used[oldIndex] = true;
                plan.Lines.Add(new PlannedLine
                {
                    Id = baseline[oldIndex].Id,
                    Text = baseline[oldIndex].Text
                });
                plan.Unchanged++;
                continue;
            }

            plan.Lines.Add(new PlannedLine { Text = raw });
            newLineIndices.Add(plan.Lines.Count - 1);
        }

        // Pass 2: cleaned new lines may still be an existing task pasted with a list marker.
        var survivors = new List<int>();
        foreach (var lineIndex in newLineIndices)
        {
            var cleaned = BulkImportText.CleanNewLine(
                plan.Lines[lineIndex].Text,
                options.StripMarkersOnNewLines);
            if (cleaned.Length == 0)
            {
                plan.Lines[lineIndex].Text = string.Empty; // dropped below
                continue;
            }

            if (TryTake(pool, BulkImportText.Normalize(cleaned), out var oldIndex))
            {
                used[oldIndex] = true;
                plan.Lines[lineIndex].Id = baseline[oldIndex].Id;
                plan.Lines[lineIndex].Text = baseline[oldIndex].Text;
                plan.Unchanged++;
                continue;
            }

            plan.Lines[lineIndex].Text = BulkImportText.Truncate(cleaned);
            if (cleaned.Length > BulkImportLimits.MaxItemLength)
            {
                plan.Truncated++;
            }
            survivors.Add(lineIndex);
        }

        // Dedupe only among the lines that are actually new.
        var keptNew = new List<int>();
        if (options.DedupeNewLines)
        {
            var seen = new HashSet<string>(
                plan.Lines.Where(line => line.Id != null)
                    .Select(line => BulkImportText.Normalize(line.Text)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var lineIndex in survivors)
            {
                if (!seen.Add(BulkImportText.Normalize(plan.Lines[lineIndex].Text)))
                {
                    plan.Lines[lineIndex].Text = string.Empty;
                    plan.DroppedDuplicates++;
                    continue;
                }
                keptNew.Add(lineIndex);
            }
        }
        else
        {
            keptNew.AddRange(survivors);
        }

        // Fuzzy pairing: a rewritten line keeps the id of the task it replaced.
        var oldLeft = Enumerable.Range(0, baseline.Count).Where(index => !used[index]).ToList();
        var pendingNew = new List<int>(keptNew);
        if (oldLeft.Count > 0 && pendingNew.Count > 0)
        {
            PairRewrites(baseline, plan, oldLeft, pendingNew, used);
        }

        plan.Lines.RemoveAll(line => line.Text.Length == 0);

        foreach (var line in plan.Lines)
        {
            if (line.Id == null)
            {
                plan.Adds.Add(line.Text);
            }
        }

        foreach (var oldIndex in Enumerable.Range(0, baseline.Count))
        {
            if (!used[oldIndex])
            {
                plan.Deletes.Add(baseline[oldIndex].Id);
            }
        }

        // Order matters: renames have to be reported with the line's final text.
        foreach (var line in plan.Lines)
        {
            if (line.Id == null)
            {
                continue;
            }

            var original = baseline.FirstOrDefault(todo => todo.Id == line.Id);
            if (original != null &&
                !string.Equals(
                    BulkImportText.Normalize(original.Text),
                    BulkImportText.Normalize(line.Text),
                    StringComparison.Ordinal))
            {
                plan.Renames.Add((line.Id, line.Text));
            }
        }

        if (plan.Truncated > 0)
        {
            plan.Warnings.Add($"{plan.Truncated} 条新任务超过 {BulkImportLimits.MaxItemLength} 字，已截断");
        }
        if (plan.DroppedDuplicates > 0)
        {
            plan.Warnings.Add($"跳过 {plan.DroppedDuplicates} 条重复的新任务");
        }

        // Order-only edits must still count as a change, otherwise a pure reorder is a no-op.
        var plannedIds = plan.Lines
            .Where(line => line.Id != null)
            .Select(line => line.Id!)
            .ToList();
        var survivingBaselineIds = baseline
            .Select(todo => todo.Id)
            .Where(plannedIds.Contains)
            .ToList();
        plan.Rearranged = !plannedIds.SequenceEqual(survivingBaselineIds, StringComparer.Ordinal);

        return plan;
    }

    private static bool TryTake(
        Dictionary<string, Queue<int>> pool,
        string key,
        out int index)
    {
        index = -1;
        if (!pool.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            return false;
        }

        index = queue.Dequeue();
        return true;
    }

    private static void PairRewrites(
        IReadOnlyList<EditorTodo> baseline,
        TodoEditPlan plan,
        List<int> oldLeft,
        List<int> pendingNew,
        bool[] used)
    {
        var available = new List<int>(oldLeft);
        for (var position = 0; position < pendingNew.Count && available.Count > 0; position++)
        {
            var lineIndex = pendingNew[position];
            var text = plan.Lines[lineIndex].Text;
            var bestSlot = -1;
            var bestScore = RenameSimilarity;
            var startSlot = position < available.Count ? position : 0;
            for (var slot = startSlot; slot < available.Count; slot++)
            {
                var score = BulkImportText.Similarity(baseline[available[slot]].Text, text);
                if (score >= bestScore)
                {
                    bestScore = score;
                    bestSlot = slot;
                }
            }

            if (bestSlot < 0)
            {
                continue;
            }

            var oldIndex = available[bestSlot];
            used[oldIndex] = true;
            available.RemoveAt(bestSlot);
            plan.Lines[lineIndex].Id = baseline[oldIndex].Id;
        }
    }
}
