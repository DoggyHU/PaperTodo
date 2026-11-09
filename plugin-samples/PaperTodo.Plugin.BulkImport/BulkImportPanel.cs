using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// The import surface shared by the plugin's own paper body and the popup dialog. It owns the
/// text box, the split options, the live preview and the actual <c>todos.append</c> call.
/// </summary>
internal sealed class BulkImportPanel : UserControl
{
    private const double Gap = 10;

    private readonly IPaperTodoHostApi _workspace;
    private readonly IPaperBodyControls? _hostControls;
    private readonly string? _lockedPaperId;
    private readonly bool _allowCancel;
    private readonly Action? _cancelRequested;
    private readonly DispatcherTimer _previewDelay;

    private BulkPalette _palette;
    private bool _lockedPaperTitleKnown;
    private string _lockedPaperTitle = string.Empty;

    private readonly ComboBox _targetCombo = new();
    private readonly TextBlock _targetText = new();
    private readonly TextBlock _targetLabel = new();
    private readonly TextBox _input = new();
    private readonly TextBlock _inputHint = new();
    private readonly RadioButton _lineMode = new();
    private readonly RadioButton _paragraphMode = new();
    private readonly CheckBox _stripMarkers = new();
    private readonly CheckBox _dedupeBatch = new();
    private readonly CheckBox _skipExisting = new();
    private readonly CheckBox _collapseWhitespace = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _statusDetail = new();
    private readonly Chip _importChip;
    private readonly Chip _undoChip;
    private readonly Chip _cancelChip;
    private readonly Border _inputFrame;
    private readonly Grid _root = new();

    private bool _suppressPreview;
    private string? _lastImportPaperId;
    private IReadOnlyList<string>? _lastImportTodoIds;

    public BulkImportPanel(
        IPaperTodoHostApi workspace,
        IPaperBodyControls? hostControls,
        BulkPalette palette,
        string? paperId,
        string paperTitle,
        bool allowCancel,
        Action? cancelRequested)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _hostControls = hostControls;
        _palette = palette;
        _lockedPaperId = SortableId(paperId);
        _lockedPaperTitle = paperTitle ?? string.Empty;
        _allowCancel = allowCancel;
        _cancelRequested = cancelRequested;
        _importChip = new Chip("导入", palette, primary: true, onClick: Import);
        _undoChip = new Chip("撤销本次导入", palette, primary: false, onClick: UndoLastImport);
        _cancelChip = new Chip("取消", palette, primary: false, compact: true, onClick: () => _cancelRequested?.Invoke());
        _undoChip.ChipEnabled = false;
        _cancelChip.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        _inputFrame = new Border();

        _previewDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            UpdatePreview();
        };

        Content = BuildUi();
        ApplyPalette(palette);

        _input.TextChanged += OnInputChanged;
        _lineMode.Checked += OnOptionChanged;
        _paragraphMode.Checked += OnOptionChanged;
        foreach (var box in new[] { _stripMarkers, _dedupeBatch, _skipExisting, _collapseWhitespace })
        {
            box.Checked += OnOptionChanged;
            box.Unchecked += OnOptionChanged;
        }

        _targetCombo.SelectionChanged += (_, _) =>
        {
            _previewDelay.Stop();
            UpdatePreview();
            if (_targetCombo.SelectedItem is PaperOption selected)
            {
                BulkImportSettings.LastTargetPaperId = selected.Id;
                TargetChanged?.Invoke(selected.Id);
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

    /// <summary>Raised when the picker's target paper changes or an import succeeds.</summary>
    public event Action<string>? TargetChanged;

    /// <summary>True when the paste box still holds text (used by the dialog's Esc handling).</summary>
    public bool HasInput => _input.Text.Length > 0;

    /// <summary>The todo paper this import will write into, or null when none is available.</summary>
    public string? CurrentPaperId => _lockedPaperId ?? (_targetCombo.SelectedItem as PaperOption)?.Id;

    public string CurrentPaperTitle =>
        _lockedPaperId != null
            ? _lockedPaperTitle
            : (_targetCombo.SelectedItem as PaperOption)?.Title ?? string.Empty;

    /// <summary>Seeds the checkboxes/radios from the host-managed plugin settings.</summary>
    public void ApplyDefaults(BulkImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _suppressPreview = true;
        try
        {
            _lineMode.IsChecked = options.Mode == BulkSplitMode.Lines;
            _paragraphMode.IsChecked = options.Mode == BulkSplitMode.Paragraphs;
            _stripMarkers.IsChecked = options.StripListMarkers;
            _dedupeBatch.IsChecked = options.DedupeWithinBatch;
            _skipExisting.IsChecked = options.SkipExisting;
            _collapseWhitespace.IsChecked = options.CollapseWhitespace;
        }
        finally
        {
            _suppressPreview = false;
        }
        UpdatePreview();
    }

    public void SetLockedPaperTitle(string title)
    {
        if (_lockedPaperId == null)
        {
            return;
        }
        _lockedPaperTitle = title ?? string.Empty;
        _lockedPaperTitleKnown = true;
        _targetText.Text = DescribeTarget();
    }

    public void ApplyPalette(BulkPalette palette)
    {
        _palette = palette;
        FontFamily = palette.Font;
        FontSize = palette.Scale(12.5);
        _root.Background = Brushes.Transparent;

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
        _inputHint.FontSize = palette.Scale(13);

        foreach (var button in new Control[] { _lineMode, _paragraphMode })
        {
            button.Foreground = palette.TextBrush;
            button.FontFamily = palette.Font;
            button.FontSize = palette.Scale(12);
        }
        foreach (var box in new Control[] { _stripMarkers, _dedupeBatch, _skipExisting, _collapseWhitespace })
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

        _importChip.ApplyPalette(palette);
        _undoChip.ApplyPalette(palette);
        _cancelChip.ApplyPalette(palette);

        _targetText.Text = DescribeTarget();
        UpdatePreview();
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

    public void RefreshPapers()
    {
        if (_lockedPaperId != null)
        {
            return;
        }
        LoadPapers();
    }

    /// <summary>Wired by the dialog so Ctrl+Enter imports from anywhere in the window.</summary>
    public void ImportNow() => Import();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            Import();
        }
    }

    private UIElement BuildUi()
    {
        _root.Margin = new Thickness(0);
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
        var targetLabel = _targetLabel;
        targetLabel.Text = "导入到";
        targetLabel.VerticalAlignment = VerticalAlignment.Center;
        targetLabel.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(targetLabel, 0);
        targetRow.Children.Add(targetLabel);

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

        // Row 1 — the paste box.
        _input.AcceptsReturn = true;
        _input.AcceptsTab = false;
        _input.TextWrapping = TextWrapping.NoWrap;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.Padding = new Thickness(10, 8, 10, 8);
        _input.BorderThickness = new Thickness(0);
        _input.VerticalContentAlignment = VerticalAlignment.Top;

        _inputHint.Text = "把整篇文档粘贴到这里（Ctrl+V）。每个换行会拆成一条任务。";
        _inputHint.IsHitTestVisible = false;
        _inputHint.TextWrapping = TextWrapping.Wrap;
        _inputHint.Margin = new Thickness(12, 10, 12, 0);
        _inputHint.VerticalAlignment = VerticalAlignment.Top;

        _inputFrame.CornerRadius = new CornerRadius(8);
        _inputFrame.BorderThickness = new Thickness(1);
        _inputFrame.Child = new Grid
        {
            Children = { _input, _inputHint }
        };
        _inputFrame.Margin = new Thickness(0, 0, 0, Gap);
        Grid.SetRow(_inputFrame, 1);
        _root.Children.Add(_inputFrame);

        // Row 2 — options.
        var optionPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _lineMode.Content = "每行一条";
        _paragraphMode.Content = "每空行一段";
        _lineMode.GroupName = "split";
        _paragraphMode.GroupName = "split";
        _lineMode.Margin = new Thickness(0, 0, 14, 0);
        _paragraphMode.Margin = new Thickness(0, 0, 22, 0);
        _stripMarkers.Content = "去掉行首序号";
        _dedupeBatch.Content = "批内去重";
        _skipExisting.Content = "跳过已存在";
        _collapseWhitespace.Content = "合并空格";
        foreach (var control in new FrameworkElement[]
                 {
                     _lineMode, _paragraphMode, _stripMarkers, _dedupeBatch, _skipExisting, _collapseWhitespace
                 })
        {
            control.Margin = new Thickness(0, 0, 14, 4);
            control.VerticalAlignment = VerticalAlignment.Center;
        }
        optionPanel.Children.Add(_lineMode);
        optionPanel.Children.Add(_paragraphMode);
        optionPanel.Children.Add(_stripMarkers);
        optionPanel.Children.Add(_dedupeBatch);
        optionPanel.Children.Add(_skipExisting);
        optionPanel.Children.Add(_collapseWhitespace);
        Grid.SetRow(optionPanel, 2);
        _root.Children.Add(optionPanel);

        // Row 3 — live preview / result.
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
        left.Children.Add(_undoChip);
        Grid.SetColumn(left, 0);
        actions.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _cancelChip.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(_cancelChip);
        right.Children.Add(_importChip);
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
            .Select(paper => new PaperOption(paper.Id, string.IsNullOrWhiteSpace(paper.Title) ? "未命名待办" : paper.Title))
            .ToList();

        var previous = (_targetCombo.SelectedItem as PaperOption)?.Id;
        _targetCombo.ItemsSource = options;
        _targetCombo.DisplayMemberPath = nameof(PaperOption.Title);
        _hostControls?.ApplySelectStyle(_targetCombo, FontSize);
        _targetCombo.SelectedItem =
            options.FirstOrDefault(option => option.Id == previous)
            ?? options.FirstOrDefault(option => option.Id == BulkImportSettings.LastTargetPaperId)
            ?? options.FirstOrDefault();

        _targetCombo.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _targetText.Visibility = Visibility.Collapsed;

        if (options.Count == 0)
        {
            _targetText.Visibility = Visibility.Visible;
            _targetText.Text = "还没有待办纸片，请先新建一张待办纸。";
        }

        UpdatePreview();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        _inputHint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suppressPreview)
        {
            return;
        }
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressPreview)
        {
            return;
        }
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private BulkImportOptions ReadOptions() => new()
    {
        Mode = _paragraphMode.IsChecked == true ? BulkSplitMode.Paragraphs : BulkSplitMode.Lines,
        StripListMarkers = _stripMarkers.IsChecked == true,
        DedupeWithinBatch = _dedupeBatch.IsChecked == true,
        SkipExisting = _skipExisting.IsChecked == true,
        CollapseWhitespace = _collapseWhitespace.IsChecked == true
    };

    private IReadOnlyList<string>? ReadExistingTexts(string paperId)
    {
        try
        {
            return _workspace
                .ListTodos(paperId, includeBlank: true)
                .Select(todo => todo.Text)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePreview()
    {
        var paperId = CurrentPaperId;
        _importChip.ChipEnabled = paperId != null;

        if (paperId == null)
        {
            _status.Foreground = _palette.DangerBrush;
            _status.Text = "没有可导入的待办纸片。";
            _statusDetail.Text = "先在桌面上新建一张待办纸，再回到这里导入。";
            return;
        }

        if (_input.Text.Length == 0)
        {
            _status.Foreground = _palette.WeakBrush;
            _status.Text = "等待粘贴内容。";
            _statusDetail.Text = string.Empty;
            return;
        }

        var options = ReadOptions();
        var existing = options.SkipExisting ? ReadExistingTexts(paperId) : null;
        var result = BulkImportParser.Parse(_input.Text, options, existing);

        if (result.Items.Count == 0)
        {
            _status.Foreground = _palette.DangerBrush;
            _status.Text = "没有解析出可导入的任务。";
            _statusDetail.Text = Describe(result);
            return;
        }

        _status.Foreground = _palette.TextBrush;
        _status.Text = $"将导入 {result.Items.Count} 条任务";
        _statusDetail.Text = Describe(result);
    }

    private string Describe(BulkParseResult result)
    {
        var parts = new List<string>();
        if (result.BlankLines > 0)
        {
            parts.Add($"跳过空行 {result.BlankLines}");
        }
        if (result.Duplicates > 0)
        {
            parts.Add($"批内重复 {result.Duplicates}");
        }
        if (result.Existing > 0)
        {
            parts.Add($"已存在 {result.Existing}");
        }
        if (result.Truncated > 0)
        {
            parts.Add($"{result.Truncated} 行超长已截断到 {BulkImportParser.MaxItemLength} 字");
        }
        if (result.Overflow > 0)
        {
            parts.Add($"超出 {BulkImportParser.MaxItems} 条上限，还有 {result.Overflow} 条未导入");
        }
        return string.Join(" · ", parts);
    }

    private void Import()
    {
        var paperId = CurrentPaperId;
        if (paperId == null)
        {
            ShowError("没有可导入的待办纸片。");
            return;
        }

        _previewDelay.Stop();
        var options = ReadOptions();
        var existing = options.SkipExisting ? ReadExistingTexts(paperId) : null;
        var result = BulkImportParser.Parse(_input.Text, options, existing);
        if (result.Items.Count == 0)
        {
            ShowError("没有解析出可导入的任务。");
            return;
        }

        try
        {
            var appended = _workspace.AppendTodos(new AppendTodosRequest
            {
                PaperId = paperId,
                Todos = result.Items
                    .Select(text => new TodoCreateItem { Text = text })
                    .ToList()
            });

            _lastImportPaperId = appended.PaperId;
            _lastImportTodoIds = appended.TodoIds;
            _undoChip.ChipEnabled = appended.TodoIds.Count > 0;

            _suppressPreview = true;
            _input.Clear();
            _suppressPreview = false;
            _inputHint.Visibility = Visibility.Visible;

            _status.Foreground = _palette.TextBrush;
            _status.Text = $"已导入 {appended.TodoIds.Count} 条任务";
            _statusDetail.Text = Describe(result);
            BulkImportSettings.LastTargetPaperId = paperId;
            TargetChanged?.Invoke(paperId);
        }
        catch (PaperTodoPluginException ex)
        {
            ShowError($"导入失败（{ex.Code}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            ShowError($"导入失败：{ex.Message}");
        }
    }

    private void UndoLastImport()
    {
        if (_lastImportPaperId == null || _lastImportTodoIds == null || _lastImportTodoIds.Count == 0)
        {
            return;
        }

        var removed = 0;
        var failed = 0;
        foreach (var todoId in _lastImportTodoIds)
        {
            try
            {
                _workspace.DeleteTodo(new DeleteTodoRequest
                {
                    PaperId = _lastImportPaperId,
                    TodoId = todoId
                });
                removed++;
            }
            catch
            {
                failed++;
            }
        }

        _lastImportTodoIds = null;
        _undoChip.ChipEnabled = false;
        _status.Foreground = failed > 0 ? _palette.DangerBrush : _palette.TextBrush;
        _status.Text = failed > 0
            ? $"已删除 {removed} 条，{failed} 条没删掉（可能已被改动或删除）"
            : $"已撤销本次导入，删除 {removed} 条任务";
        _statusDetail.Text = string.Empty;
    }

    private void ShowError(string message)
    {
        _status.Foreground = _palette.DangerBrush;
        _status.Text = message;
        _statusDetail.Text = string.Empty;
    }

    /// <summary>Non-failing inline notice used by the dialog (for example the Esc-to-discard prompt).</summary>
    public void ShowTransientNotice(string message)
    {
        _status.Foreground = _palette.WeakBrush;
        _status.Text = message;
        _statusDetail.Text = string.Empty;
    }

    private string DescribeTarget()
    {
        if (_lockedPaperId == null)
        {
            return string.Empty;
        }
        return _lockedPaperTitle.Length > 0
            ? _lockedPaperTitle
            : _lockedPaperTitleKnown ? "未命名待办" : "…";
    }

    private static string? SortableId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record PaperOption(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    /// <summary>Border-based button: host dialogs are not available inside a native plugin, and the
    /// stock WPF Button chrome would not follow the paper palette.</summary>
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
            (byte)Math.Min(255, color.R + (255 - color.R) * amount),
            (byte)Math.Min(255, color.G + (255 - color.G) * amount),
            (byte)Math.Min(255, color.B + (255 - color.B) * amount));

        private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }
}
