using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// The popup import surface. PaperTodo protocol 2.0 has no host popup API for native plugins, so the
/// plugin owns this small window: it is non-modal, themed from the current paper palette, anchored
/// over the paper whose top-bar button was clicked, and never enters the taskbar.
/// </summary>
internal sealed class BulkImportDialog : Window
{
    private const double ShadowMargin = 14;

    private readonly BulkPalette _palette;
    private readonly BulkImportPanel _panel;
    private bool _escapeArmed;
    private bool _resizing;
    private Point _resizeOrigin;
    private Size _resizeStartSize;

    public BulkImportDialog(
        IPaperTodoHostApi workspace,
        BulkPalette palette,
        string? paperId,
        string paperTitle,
        BulkEditOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(defaults);

        _palette = palette;
        _panel = new BulkImportPanel(
            workspace,
            hostControls: null,
            palette,
            paperId,
            paperTitle,
            allowCancel: true,
            cancelRequested: Close,
            savedRequested: Close);
        _panel.ApplyDefaults(defaults);
        _panel.LoadBaseline();

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = 660;
        Height = 560;
        MinWidth = 460;
        MinHeight = 400;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "编辑任务";

        Content = BuildContent();
    }

    public string? TargetPaperId => _panel.CurrentPaperId;

    public void FocusInput() => _panel.FocusInput();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_panel.HasPendingChanges && !_escapeArmed)
            {
                _escapeArmed = true;
                _panel.ShowTransientNotice("再按一次 Esc 关闭窗口（当前修改会丢弃）");
                return;
            }
            Close();
            return;
        }

        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _escapeArmed = false;
            _panel.ApplyNow();
        }
    }

    private UIElement BuildContent()
    {
        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rows.Children.Add(BuildTitleBar());

        var body = new Border
        {
            Padding = new Thickness(18, 2, 18, 16),
            Child = _panel
        };
        Grid.SetRow(body, 1);
        rows.Children.Add(body);

        var root = new Border
        {
            Margin = new Thickness(ShadowMargin),
            CornerRadius = new CornerRadius(12),
            Background = _palette.PaperBrush,
            BorderBrush = _palette.BorderBrush,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 3,
                Opacity = 0.26,
                Color = Colors.Black
            },
            Child = new Grid { Children = { rows, BuildResizeGrip() } }
        };

        return root;
    }

    private UIElement BuildTitleBar()
    {
        var bar = new Grid { Margin = new Thickness(18, 8, 10, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock
        {
            Text = "编辑任务",
            Foreground = _palette.TextBrush,
            FontFamily = _palette.Font,
            FontSize = _palette.Scale(13.5),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        caption.Children.Add(new TextBlock
        {
            Text = "  Ctrl+Enter 保存 · Esc 关闭",
            Foreground = _palette.WeakBrush,
            FontFamily = _palette.Font,
            FontSize = _palette.Scale(11),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(caption, 0);
        bar.Children.Add(caption);

        var close = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "✕",
                Foreground = _palette.WeakBrush,
                FontSize = _palette.Scale(11),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseEnter += (_, _) => close.Background = _palette.SurfaceBrush;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);

        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove throws when the button was already released; nothing to recover.
            }
        };

        return bar;
    }

    private UIElement BuildResizeGrip()
    {
        var grip = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNWSE,
            Child = new Path
            {
                Data = Geometry.Parse("M 2,12 L 12,2 M 2,18 L 18,2 M 8,20 L 20,8"),
                Stroke = _palette.WeakBrush,
                StrokeThickness = 1.1,
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        grip.MouseLeftButtonDown += (_, e) =>
        {
            _resizing = true;
            _resizeOrigin = e.GetPosition(this);
            _resizeStartSize = new Size(Width, Height);
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!_resizing)
            {
                return;
            }
            var delta = e.GetPosition(this) - _resizeOrigin;
            Width = Math.Max(MinWidth, _resizeStartSize.Width + delta.X);
            Height = Math.Max(MinHeight, _resizeStartSize.Height + delta.Y);
        };
        grip.MouseLeftButtonUp += (_, _) =>
        {
            _resizing = false;
            grip.ReleaseMouseCapture();
        };

        return grip;
    }
}


