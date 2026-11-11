using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Body session for the plugin's own paper. The paper is the "import desk": the same panel as the
/// popup, plus a target picker. Renaming the paper to its only purpose keeps the capsule meaningful.
/// </summary>
internal sealed class BulkImportBodySession : IPaperBodySession
{
    private readonly PaperBodyContext _context;
    private readonly BulkImportPanel _panel;
    private readonly Border _root;
    private BulkPalette _palette;

    public BulkImportBodySession(PaperBodyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _palette = BulkPalette.From(context.Theme);
        BulkPalette.Current = _palette;
        BulkImportSettings.LastTargetPaperId =
            BulkImportSettings.ReadLastTargetPaperId(context.StateJson) ??
            BulkImportSettings.LastTargetPaperId;

        _panel = new BulkImportPanel(
            context.Workspace,
            context.Body.Controls,
            _palette,
            paperId: null,
            paperTitle: string.Empty,
            allowCancel: false,
            cancelRequested: null);
        _panel.TargetChanged += OnTargetChanged;
        _panel.ApplyDefaults(BulkImportSettings.Parse(context.SettingsJson));

        _root = new Border
        {
            Background = _palette.PaperBrush,
            Padding = new Thickness(14),
            Child = _panel
        };

        context.Paper.SetTitle("编辑任务");
        context.Paper.SetHeaderText("编辑任务");
        context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
        {
            PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
            PlainText = "编辑任务",
            ToolTip = "编辑任务：一行一条，改完整体写回",
            Components =
            [
                new PaperCapsuleComponent
                {
                    Kind = PaperCapsuleComponentKind.Text,
                    Text = "编辑任务",
                    Fill = true
                }
            ]
        });
    }

    public FrameworkElement View => _root;

    public void OnThemeChanged(PaperBodyTheme theme)
    {
        _palette = BulkPalette.From(theme);
        BulkPalette.Current = _palette;
        _root.Background = _palette.PaperBrush;
        _panel.ApplyPalette(_palette);
    }

    public void Dispose()
    {
        _panel.TargetChanged -= OnTargetChanged;
    }

    private void OnTargetChanged(string paperId)
    {
        BulkImportSettings.LastTargetPaperId = paperId;
        try
        {
            _context.SaveStateJson(BulkImportSettings.WriteLastTargetPaperId(paperId));
        }
        catch
        {
            // State persistence is a convenience; a failure must not break the import surface.
        }
    }
}

