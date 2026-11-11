using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// The task editor shared by the plugin's own paper body and the popup window: the text box holds
/// the paper's whole task list, one task per line, and 确定 writes the difference back through
/// <c>todos.append</c> / <c>todos.update</c> / <c>todos.delete</c>.
/// </summary>
internal sealed class BulkImportPanel : UserControl
{
    private const double Gap = 10;

    private readonly IPaperTodoHostApi _workspace;
    private readonly IPaperBodyControls? _hostControls;
    private readonly string? _lockedPaperId;
    private readonly bool _allowCancel;
    private readonly Action? _cancelRequested;
    private readonly Action? _savedRequested;
    private readonly DispatcherTimer _previewDelay;

    private BulkPalette _palette;
    private readonly List<EditorTodo> _baseline = [];
    private string? _loadedPaperId;
    private bool _deleteArmed;
    private int _armedDeleteCount;
    private bool _reloadArmed;
    private bool _suppress;
    private bool _busy;
    private string _lockedPaperTitle;

    private readonly ComboBox _targetCombo = new();
    private readonly TextBlock _targetText = new();
    private readonly TextBlock _targetLabel = new();
    private readonly TextBox _input = new();
    private readonly TextBlock _inputHint = new();
    private readonly CheckBox _stripMarkers = new();
    private readonly CheckBox _dedupeNew = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _statusDetail = new();
    private readonly Chip _applyChip;
    private readonly Chip _reloadChip;
    private readonly Chip _cancelChip;
    private readonly Border _inputFrame;
    private readonly Grid _root = new();

    public BulkImportPanel(
        IPaperTodoHostApi workspace,
        IPaperBodyControls? hostControls,
        BulkPalette palette,
        string? paperId,
        string paperTitle,
        bool allowCancel,
        Action? cancelRequested,
        Action? savedRequested = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _savedRequested = savedRequested;
        _hostControls = hostControls;
        _palette = palette;
        _lockedPaperId = string.IsNullOrWhiteSpace(paperId) ? null : paperId;
        _lockedPaperTitle = paperTitle ?? string.Empty;
        _allowCancel = allowCancel;
        _cancelRequested = cancelRequested;

        _applyChip = new Chip("确定", palette, primary: true, onClick: ApplyNow);
        _reloadChip = new Chip("重新载入", palette, primary: false, compact: true, onClick: ReloadClicked);
        _cancelChip = new Chip("取消", palette, primary: false, compact: true,
            onClick: () => _cancelRequested?.Invoke());
        _cancelChip.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        _inputFrame = new Border();

        _previewDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            RecomputePreview();
        };

        Content = BuildUi();
        ApplyPalette(palette);

        _input.TextChanged += (_, _) =>
        {
            _inputHint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_suppress)
            {
                return;
            }
            ResetArmed();
            _previewDelay.Stop();
            _previewDelay.Start();
        };

        foreach (var box in new[] { _stripMarkers, _dedupeNew })
        {
            box.Checked += (_, _) => OnOptionsChanged();
            box.Unchecked += (_, _) => OnOptionsChanged();
        }

        _targetCombo.SelectionChanged += (_, _) =>
        {
            if (_suppress)
            {
                return;
            }
            if (_targetCombo.SelectedItem is PaperOption selected)
            {
                BulkImportSettings.LastTargetPaperId = selected.Id;
                TargetChanged?.Invoke(selected.Id);
                LoadBaseline();
            }
        };

        var picker = _lockedPaperId == null;
        _targetCombo.Visibility = picker ? Visibility.Visible : Visibility.Collapsed;
        _targetText.Visibility = picker ? Visibility.Collapsed : Visibility.Visible;
        if (picker)
        {
            Loaded += (_, _) =>
            {
                LoadPapers();
                FocusInput();
            };
        }
    }

    /// <summary>Raised when the picker's target paper changes or a save lands.</summary>
    public event Action<string>? TargetChanged;

    /// <summary>True when the text box no longer matches the loaded task list.</summary>
    public bool HasPendingChanges
    {
        get
        {
            if (_loadedPaperId == null)
            {
                return BulkImportText.SplitLines(_input.Text).Count > 0;
            }

            var current = BulkImportText.SplitLines(_input.Text);
            if (current.Count != _baseline.Count)
            {
                return true;
            }

            for (var index = 0; index < current.Count; index++)
            {
                if (!string.Equals(
                        BulkImportText.Normalize(current[index]),
                        BulkImportText.Normalize(_baseline[index].Text),
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Number of tasks currently loaded (used by the diagnostics build).</summary>
    internal int BaselineCount => _baseline.Count;

    public string? CurrentPaperId => _lockedPaperId ?? (_targetCombo.SelectedItem as PaperOption)?.Id;

    public string CurrentPaperTitle =>
        _lockedPaperId != null
            ? _lockedPaperTitle
            : (_targetCombo.SelectedItem as PaperOption)?.Title ?? string.Empty;

    public void ApplyDefaults(BulkEditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _suppress = true;
        try
        {
            _stripMarkers.IsChecked = options.StripMarkersOnNewLines;
            _dedupeNew.IsChecked = options.DedupeNewLines;
        }
        finally
        {
            _suppress = false;
        }
        RecomputePreview();
    }

    public void SetLockedPaperTitle(string title)
    {
        if (_lockedPaperId == null)
        {
            return;
        }
        _lockedPaperTitle = title ?? string.Empty;
        _targetText.Text = DescribeTarget();
    }

    public void ApplyPalette(BulkPalette palette)
    {
        _palette = palette;
        FontFamily = palette.Font;
        FontSize = palette.Scale(12.5);

        _targetText.Foreground = palette.WeakBrush;
        _targetText.FontFamily = palette.Font;
        _targetText.FontSize = palette.Scale(12);
        _targetLabel.Foreground = palette.WeakBrush;
        _targetLabel.FontFamily = palette.Font;
        _targetLabel.FontSize = palette.Scale(12);

        _inputFrame.Background = palette.SurfaceBrush;
        _inputFrame.BorderBrush = palette.BorderBrush;
        _input.Foreground = palette.TextBrush;
        _input.Background = Brushes.Transparent;
        _input.CaretBrush = palette.TextBrush;
        _input.SelectionBrush = palette.AccentBrush;
        _input.SelectionOpacity = 0.35;
        _input.FontFamily = palette.Font;
        _input.FontSize = palette.Scale(13);
        _inputHint.Foreground = palette.WeakBrush;
        _inputHint.FontFamily = palette.Font;
        _inputHint.FontSize = palette.Scale(12.5);

        foreach (var box in new Control[] { _stripMarkers, _dedupeNew })
        {
            box.Foreground = palette.TextBrush;
            box.FontFamily = palette.Font;
            box.FontSize = palette.Scale(12);
        }

        _status.FontFamily = palette.Font;
        _status.FontSize = palette.Scale(12);
        _statusDetail.FontFamily = palette.Font;
        _statusDetail.FontSize = palette.Scale(11.5);

        _targetCombo.Foreground = palette.TextBrush;
        _targetCombo.FontFamily = palette.Font;
        _targetCombo.FontSize = palette.Scale(12);

        _applyChip.ApplyPalette(palette);
        _reloadChip.ApplyPalette(palette);
        _cancelChip.ApplyPalette(palette);

        _targetText.Text = DescribeTarget();
        RecomputePreview();
    }

    public void FocusInput()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (_input.IsVisible)
                {
                    _input.Focus();
                    Keyboard.Focus(_input);
                }
            }));
    }

    /// <summary>Loads the paper's current tasks into the editor, discarding uncommitted edits.</summary>
    public void LoadBaseline()
    {
        var paperId = CurrentPaperId;
        _baseline.Clear();
        _loadedPaperId = null;
        ResetArmed();

        if (paperId == null)
        {
            _suppress = true;
            _input.Text = string.Empty;
            _suppress = false;
            _inputHint.Visibility = Visibility.Visible;
            RecomputePreview();
            return;
        }

        IReadOnlyList<EditorTodo> todos;
        try
        {
            todos = ReadTodos(paperId);
        }
        catch (Exception ex)
        {
            ShowError($"读取任务失败：{ex.Message}");
            return;
        }

        _baseline.AddRange(todos);
        _loadedPaperId = paperId;

        _suppress = true;
        _input.Text = string.Join("\n", todos.Select(todo => todo.Text));
        _suppress = false;
        _inputHint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecomputePreview();
    }

    public void RefreshPapers()
    {
        if (_lockedPaperId == null)
        {
            LoadPapers();
        }
    }

    /// <summary>Wired by the window so Ctrl+Enter saves from anywhere in the dialog.</summary>
    public void ApplyNow()
    {
        if (_busy)
        {
            return;
        }

        var paperId = CurrentPaperId;
        if (paperId == null)
        {
            ShowError("没有可编辑的待办纸片。");
            return;
        }

        if (_loadedPaperId != paperId)
        {
            LoadBaseline();
        }

        var plan = TodoEditPlan.Build(_baseline, _input.Text, ReadOptions());
        if (!plan.HasChanges)
        {
            ShowTransientNotice("没有改动。");
            return;
        }

        // Optimistic concurrency: the paper may have been edited elsewhere while the editor was open.
        IReadOnlyList<EditorTodo> current;
        try
        {
            current = ReadTodos(paperId);
        }
        catch (Exception ex)
        {
            ShowError($"读取任务失败：{ex.Message}");
            return;
        }
        if (!SameAsBaseline(current))
        {
            ShowError("这张纸在编辑期间被改动过，请点「重新载入」再改。");
            return;
        }

        if (plan.Deletes.Count > 0 &&
            (!_deleteArmed || _armedDeleteCount != plan.Deletes.Count))
        {
            _deleteArmed = true;
            _armedDeleteCount = plan.Deletes.Count;
            RecomputePreview();
            return;
        }

        Execute(paperId, plan);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            ApplyNow();
        }
    }

    public void ShowTransientNotice(string message)
    {
        _status.Foreground = _palette.WeakBrush;
        _status.Text = message;
        _statusDetail.Text = string.Empty;
    }

    private UIElement BuildUi()
    {
        _root.Background = Brushes.Transparent;
        for (var index = 0; index < 5; index++)
        {
            _root.RowDefinitions.Add(index switch
            {
                1 => new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 150 },
                _ => new RowDefinition { Height = GridLength.Auto }
            });
        }

        // Row 0 — target paper.
        var targetRow = new Grid { Margin = new Thickness(0, 0, 0, Gap) };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _targetLabel.Text = "目标";
        _targetLabel.VerticalAlignment = VerticalAlignment.Center;
        _targetLabel.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_targetLabel, 0);
        targetRow.Children.Add(_targetLabel);

        var targetHost = new Grid();
        Grid.SetColumn(targetHost, 1);
        _targetText.VerticalAlignment = VerticalAlignment.Center;
        _targetText.TextTrimming = TextTrimming.CharacterEllipsis;
        _targetCombo.MinWidth = 200;
        _targetCombo.HorizontalAlignment = HorizontalAlignment.Left;
        targetHost.Children.Add(_targetText);
        targetHost.Children.Add(_targetCombo);
        targetRow.Children.Add(targetHost);
        Grid.SetRow(targetRow, 0);
        _root.Children.Add(targetRow);

        // Row 1 — the task list.
        _input.AcceptsReturn = true;
        _input.AcceptsTab = false;
        _input.TextWrapping = TextWrapping.NoWrap;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.Padding = new Thickness(10, 8, 10, 8);
        _input.BorderThickness = new Thickness(0);
        _input.VerticalContentAlignment = VerticalAlignment.Top;

        _inputHint.Text = "一行一条任务。当前纸上没有任何任务，直接写就行。";
        _inputHint.IsHitTestVisible = false;
        _inputHint.TextWrapping = TextWrapping.Wrap;
        _inputHint.Margin = new Thickness(12, 10, 12, 0);
        _inputHint.VerticalAlignment = VerticalAlignment.Top;

        _inputFrame.CornerRadius = new CornerRadius(8);
        _inputFrame.BorderThickness = new Thickness(1);
        _inputFrame.Child = new Grid { Children = { _input, _inputHint } };
        _inputFrame.Margin = new Thickness(0, 0, 0, Gap);
        Grid.SetRow(_inputFrame, 1);
        _root.Children.Add(_inputFrame);

        // Row 2 — options that only touch newly added lines.
        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _stripMarkers.Content = "新行去掉行首序号";
        _dedupeNew.Content = "新行去重";
        foreach (var box in new FrameworkElement[] { _stripMarkers, _dedupeNew })
        {
            box.Margin = new Thickness(0, 0, 16, 4);
            box.VerticalAlignment = VerticalAlignment.Center;
        }
        options.Children.Add(_stripMarkers);
        options.Children.Add(_dedupeNew);
        Grid.SetRow(options, 2);
        _root.Children.Add(options);

        // Row 3 — live diff / result.
        var statusPanel = new StackPanel { Margin = new Thickness(0, 0, 0, Gap) };
        _status.TextWrapping = TextWrapping.Wrap;
        _statusDetail.TextWrapping = TextWrapping.Wrap;
        statusPanel.Children.Add(_status);
        statusPanel.Children.Add(_statusDetail);
        Grid.SetRow(statusPanel, 3);
        _root.Children.Add(statusPanel);

        // Row 4 — actions.
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_reloadChip);
        Grid.SetColumn(left, 0);
        actions.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _cancelChip.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(_cancelChip);
        right.Children.Add(_applyChip);
        Grid.SetColumn(right, 1);
        actions.Children.Add(right);
        Grid.SetRow(actions, 4);
        _root.Children.Add(actions);

        return _root;
    }

    private void LoadPapers()
    {
        IReadOnlyList<PaperSnapshot> papers;
        try
        {
            papers = _workspace.ListPapers("todo");
        }
        catch (Exception ex)
        {
            ShowError($"读取待办纸片失败：{ex.Message}");
            return;
        }

        var options = papers
            .OrderBy(paper => paper.Title, StringComparer.CurrentCulture)
            .Select(paper => new PaperOption(
                paper.Id,
                string.IsNullOrWhiteSpace(paper.Title) ? "未命名待办" : paper.Title))
            .ToList();

        var previous = (_targetCombo.SelectedItem as PaperOption)?.Id;
        _suppress = true;
        _targetCombo.ItemsSource = options;
        _targetCombo.DisplayMemberPath = nameof(PaperOption.Title);
        _hostControls?.ApplySelectStyle(_targetCombo, FontSize);
        _targetCombo.SelectedItem =
            options.FirstOrDefault(option => option.Id == previous)
            ?? options.FirstOrDefault(option => option.Id == BulkImportSettings.LastTargetPaperId)
            ?? options.FirstOrDefault();
        _targetCombo.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _targetText.Visibility = Visibility.Collapsed;
        _suppress = false;

        if (options.Count == 0)
        {
            _targetText.Visibility = Visibility.Visible;
            _targetText.Text = "还没有待办纸片，请先新建一张待办纸。";
            _baseline.Clear();
            _loadedPaperId = null;
            ShowError("没有可编辑的待办纸片。");
            return;
        }

        LoadBaseline();
    }

    private void OnOptionsChanged()
    {
        if (_suppress)
        {
            return;
        }
        ResetArmed();
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private BulkEditOptions ReadOptions() => new()
    {
        StripMarkersOnNewLines = _stripMarkers.IsChecked == true,
        DedupeNewLines = _dedupeNew.IsChecked == true
    };

    private IReadOnlyList<EditorTodo> ReadTodos(string paperId) =>
        _workspace
            .ListTodos(paperId, includeBlank: false)
            .Select(todo => new EditorTodo(todo.Id, todo.Text, todo.Done))
            .ToList();

    private bool SameAsBaseline(IReadOnlyList<EditorTodo> current)
    {
        if (current.Count != _baseline.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!string.Equals(current[index].Id, _baseline[index].Id, StringComparison.Ordinal) ||
                !string.Equals(
                    BulkImportText.Normalize(current[index].Text),
                    BulkImportText.Normalize(_baseline[index].Text),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void RecomputePreview()
    {
        var paperId = CurrentPaperId;
        _applyChip.ChipEnabled = paperId != null && !_busy;
        _reloadChip.ChipEnabled = paperId != null && !_busy;

        if (paperId == null)
        {
            _status.Foreground = _palette.DangerBrush;
            _status.Text = "没有可编辑的待办纸片。";
            _statusDetail.Text = "先在桌面上新建一张待办纸，再回到这里编辑。";
            return;
        }

        var plan = TodoEditPlan.Build(_baseline, _input.Text, ReadOptions());
        if (!plan.HasChanges)
        {
            var doneCount = _baseline.Count(todo => todo.Done);
            _status.Foreground = _palette.WeakBrush;
            _status.Text = $"共 {plan.Lines.Count} 条任务，与当前一致。";
            _statusDetail.Text = doneCount > 0 ? $"其中已完成 {doneCount} 条。" : string.Empty;
            _applyChip.SetText("确定");
            _applyChip.ChipEnabled = false;
            return;
        }

        var parts = new List<string>();
        parts.Add($"共 {plan.Lines.Count} 条");
        if (plan.Adds.Count > 0)
        {
            parts.Add($"新增 {plan.Adds.Count}");
        }
        if (plan.Renames.Count > 0)
        {
            parts.Add($"改名 {plan.Renames.Count}");
        }
        if (plan.Deletes.Count > 0)
        {
            parts.Add($"删除 {plan.Deletes.Count}");
        }
        if (plan.Unchanged > 0)
        {
            parts.Add($"不变 {plan.Unchanged}");
        }
        if (plan.Rearranged &&
            plan.Adds.Count == 0 &&
            plan.Renames.Count == 0 &&
            plan.Deletes.Count == 0)
        {
            parts.Add("调整顺序");
        }

        var hasDelete = plan.Deletes.Count > 0;
        _status.Foreground = hasDelete ? _palette.DangerBrush : _palette.TextBrush;
        _status.Text = string.Join(" · ", parts);

        if (hasDelete)
        {
            _statusDetail.Text = _deleteArmed
                ? $"确认要删除 {plan.Deletes.Count} 条任务？删除会同时移除完成状态、提醒和关联，且无法撤销。"
                : $"删除的 {plan.Deletes.Count} 条任务会同时移除完成状态、提醒和关联，且无法撤销。";
        }
        else
        {
            _statusDetail.Text = plan.Warnings.Count > 0 ? string.Join(" · ", plan.Warnings) : string.Empty;
        }

        _applyChip.SetText(
            hasDelete && _deleteArmed && _armedDeleteCount == plan.Deletes.Count
                ? $"确认删除 {plan.Deletes.Count} 条"
                : "确定");
        _applyChip.ChipEnabled = true;
    }

    private void ResetArmed()
    {
        _deleteArmed = false;
        _armedDeleteCount = 0;
        _reloadArmed = false;
    }

    private void ReloadClicked()
    {
        if (HasPendingChanges && !_reloadArmed)
        {
            _reloadArmed = true;
            ShowTransientNotice("再点一次「重新载入」会丢弃当前修改。");
            return;
        }

        ResetArmed();
        LoadBaseline();
        ShowTransientNotice("已按纸上的最新内容重新载入。");
    }

    private void Execute(string paperId, TodoEditPlan plan)
    {
        _busy = true;
        _applyChip.ChipEnabled = false;
        var failures = new List<string>();
        var deleted = 0;
        var renamed = 0;
        var addedIds = new List<string>();

        try
        {
            foreach (var todoId in plan.Deletes)
            {
                try
                {
                    _workspace.DeleteTodo(new DeleteTodoRequest { PaperId = paperId, TodoId = todoId });
                    deleted++;
                }
                catch (Exception ex)
                {
                    failures.Add($"删除失败：{Describe(ex)}");
                }
            }

            foreach (var (todoId, text) in plan.Renames)
            {
                try
                {
                    _workspace.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = paperId,
                        TodoId = todoId,
                        Text = text
                    });
                    renamed++;
                }
                catch (Exception ex)
                {
                    failures.Add($"改名失败：{Describe(ex)}");
                }
            }

            if (plan.Adds.Count > 0)
            {
                foreach (var chunk in plan.Adds.Chunk(BulkImportLimits.MaxAddsPerBatch))
                {
                    try
                    {
                        var appended = _workspace.AppendTodos(new AppendTodosRequest
                        {
                            PaperId = paperId,
                            Todos = chunk
                                .Select(text => new TodoCreateItem { Text = text })
                                .ToList()
                        });
                        addedIds.AddRange(appended.TodoIds);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"新增失败：{Describe(ex)}");
                    }
                }
            }

            if (failures.Count == 0 && addedIds.Count == plan.Adds.Count)
            {
                var finalIds = new List<string>(plan.Lines.Count);
                var nextNew = 0;
                foreach (var line in plan.Lines)
                {
                    finalIds.Add(line.Id ?? addedIds[nextNew++]);
                }
                ReorderIfNeeded(paperId, finalIds, failures);
            }

            LoadBaseline();

            _status.Foreground = failures.Count > 0 ? _palette.DangerBrush : _palette.TextBrush;
            var summary = new List<string>();
            if (deleted > 0)
            {
                summary.Add($"删除 {deleted}");
            }
            if (renamed > 0)
            {
                summary.Add($"改名 {renamed}");
            }
            if (addedIds.Count > 0)
            {
                summary.Add($"新增 {addedIds.Count}");
            }
            _status.Text = failures.Count > 0
                ? $"部分完成：{string.Join(" · ", summary)}；{failures.Count} 项失败"
                : $"已保存：{string.Join(" · ", summary)}";
            _statusDetail.Text = failures.Count > 0 ? string.Join("；", failures.Take(3)) : string.Empty;
        }
        finally
        {
            _busy = false;
            if (failures.Count == 0 && _savedRequested != null)
            {
                // The popup closes itself once the paper is consistent; a partial failure keeps the
                // editor open so the errors stay visible.
                _savedRequested.Invoke();
            }
            else
            {
                RecomputePreview();
            }
        }
    }

    /// <summary>Makes the paper's managed tasks appear in the planned order using todos.update order.</summary>
    private void ReorderIfNeeded(string paperId, IReadOnlyList<string> finalIds, List<string> failures)
    {
        if (finalIds.Count < 2)
        {
            return;
        }

        var order = _workspace
            .ListTodos(paperId, includeBlank: true)
            .OrderBy(todo => todo.Order)
            .Select(todo => todo.Id)
            .ToList();
        var managed = new HashSet<string>(finalIds, StringComparer.Ordinal);
        var currentSequence = order.Where(managed.Contains).ToList();
        if (currentSequence.SequenceEqual(finalIds, StringComparer.Ordinal))
        {
            return;
        }

        var start = order.IndexOf(finalIds[0]);
        if (start < 0)
        {
            return;
        }

        for (var index = 0; index < finalIds.Count; index++)
        {
            var position = order.IndexOf(finalIds[index]);
            var target = start + index;
            if (position < 0 || position == target)
            {
                continue;
            }

            try
            {
                _workspace.UpdateTodo(new UpdateTodoRequest
                {
                    PaperId = paperId,
                    TodoId = finalIds[index],
                    Order = target
                });
                order.RemoveAt(position);
                order.Insert(Math.Min(target, order.Count), finalIds[index]);
            }
            catch (Exception ex)
            {
                failures.Add($"排序失败：{Describe(ex)}");
                return;
            }
        }
    }

    private static string Describe(Exception ex) =>
        ex is PaperTodoPluginException pluginException
            ? $"{pluginException.Code} {pluginException.Message}"
            : ex.Message;

    private void ShowError(string message)
    {
        _status.Foreground = _palette.DangerBrush;
        _status.Text = message;
        _statusDetail.Text = string.Empty;
    }

    private string DescribeTarget() =>
        _lockedPaperId == null
            ? string.Empty
            : _lockedPaperTitle.Length > 0 ? _lockedPaperTitle : "未命名待办";

    private sealed record PaperOption(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    /// <summary>Border-based button: a native plugin cannot host the app's own dialog chrome, and the
    /// stock WPF Button look would not follow the paper palette.</summary>
    private sealed class Chip : Border
    {
        private readonly bool _primary;
        private readonly TextBlock _label;
        private BulkPalette _palette;
        private bool _enabled = true;
        private bool _hover;

        public Chip(string text, BulkPalette palette, bool primary, Action onClick, bool compact = false)
        {
            _primary = primary;
            _palette = palette;
            CornerRadius = new CornerRadius(compact ? 5 : 7);
            BorderThickness = primary ? new Thickness(0) : new Thickness(1);
            Padding = compact ? new Thickness(10, 4, 10, 4) : new Thickness(16, 7, 16, 7);
            Cursor = Cursors.Hand;
            _label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Child = _label;
            MouseEnter += (_, _) =>
            {
                _hover = true;
                Repaint();
            };
            MouseLeave += (_, _) =>
            {
                _hover = false;
                Repaint();
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (!_enabled)
                {
                    return;
                }
                e.Handled = true;
                onClick();
            };
            ApplyPalette(palette);
        }

        public bool ChipEnabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                Opacity = value ? 1.0 : 0.45;
                Cursor = value ? Cursors.Hand : Cursors.Arrow;
                IsHitTestVisible = value;
            }
        }

        public void SetText(string text) => _label.Text = text;

        public void ApplyPalette(BulkPalette palette)
        {
            _palette = palette;
            _label.FontFamily = palette.Font;
            _label.FontSize = palette.Scale(12.5);
            _label.Foreground = _primary ? palette.OnAccentBrush : palette.TextBrush;
            Repaint();
        }

        private void Repaint()
        {
            if (_primary)
            {
                Background = _hover
                    ? new SolidColorBrush(Lighten(_palette.Accent, 0.14))
                    : _palette.AccentBrush;
                BorderBrush = Brushes.Transparent;
            }
            else
            {
                Background = _hover
                    ? new SolidColorBrush(Blend(_palette.Paper, _palette.Accent, 0.14))
                    : Brushes.Transparent;
                BorderBrush = _palette.BorderBrush;
            }
        }

        private static Color Lighten(Color color, double amount) => Color.FromRgb(
            (byte)Math.Min(255, color.R + ((255 - color.R) * amount)),
            (byte)Math.Min(255, color.G + ((255 - color.G) * amount)),
            (byte)Math.Min(255, color.B + ((255 - color.B) * amount)));

        private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }
}
