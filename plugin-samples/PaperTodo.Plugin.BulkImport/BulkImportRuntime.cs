using System.Windows;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Provider-level runtime. Protocol 2.0 only allows global top-bar contributions from an app
/// runtime, and an app runtime only exists while at least one real plugin paper exists — that is why
/// the manifest also declares a startup capsule paper. The button therefore appears on every paper's
/// top bar, and the click decides which todo paper receives the batch.
/// </summary>
internal sealed class BulkImportRuntime : IPaperAppRuntime
{
    private const string ActionId = "bulk-import";

    // Document + plus, drawn on the host's 16x16 stroke grid.
    private const string IconPath =
        "M3,2.5 L10,2.5 L13,5.5 L13,13.5 L3,13.5 Z M10,2.5 L10,5.5 L13,5.5 M5.4,8 L10.6,8 M8,5.4 L8,10.6";

    private readonly PaperAppRuntimeContext _context;
    private BulkImportDialog? _dialog;

    public BulkImportRuntime(PaperAppRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        context.GlobalTopBar.SetActionHandler(OnTopBarInvoked);
        context.GlobalTopBar.SetActions(
        [
            new PaperTopBarAction
            {
                Id = ActionId,
                Icon = PaperTopBarIcon.SvgPath(
                    IconPath,
                    PaperTopBarSvgRenderMode.Stroke,
                    1.25),
                ToolTip = "批量导入任务（粘贴整篇文档，按换行拆条）",
                Priority = 180
            }
        ]);
    }

    public void Dispose()
    {
        try
        {
            _context.GlobalTopBar.Clear();
        }
        catch
        {
            // Provider teardown continues regardless of contribution cleanup failures.
        }

        var dialog = _dialog;
        _dialog = null;
        if (dialog != null)
        {
            try
            {
                dialog.Close();
            }
            catch
            {
                // The window may already be closing.
            }
        }
    }

    private void OnTopBarInvoked(PaperTopBarActionInvocation invocation)
    {
        if (!string.Equals(invocation.ActionId, ActionId, StringComparison.Ordinal))
        {
            return;
        }

        OpenForTarget(invocation.TargetPaperId, invocation.TargetPaperType);
    }

    private void OpenForTarget(string? targetPaperId, string? targetPaperType)
    {
        var paperId = ResolveTargetPaper(targetPaperId, targetPaperType);
        var title = paperId == null ? string.Empty : SafeTitle(paperId);

        CloseDialog();

        var owner = BulkImportWindowPlacement.FindWindowUnderCursor();
        var dialog = new BulkImportDialog(
            _context.Workspace,
            BulkPalette.Current,
            paperId,
            title,
            BulkImportSettings.Parse(_context.Settings.Json));

        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dialog, dialog))
            {
                _dialog = null;
            }
        };

        if (owner != null)
        {
            dialog.Owner = owner;
        }

        BulkImportWindowPlacement.Place(dialog, owner);
        _dialog = dialog;
        dialog.Show();
        dialog.Activate();
        dialog.FocusInput();
    }

    private string? ResolveTargetPaper(string? targetPaperId, string? targetPaperType)
    {
        try
        {
            if (string.Equals(targetPaperType, "todo", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(targetPaperId))
            {
                return targetPaperId;
            }

            var todoPapers = _context.Workspace.ListPapers("todo");
            if (todoPapers.Count == 0)
            {
                return null;
            }

            var last = BulkImportSettings.LastTargetPaperId;
            if (!string.IsNullOrWhiteSpace(last) &&
                todoPapers.Any(paper => string.Equals(paper.Id, last, StringComparison.Ordinal)))
            {
                return last;
            }

            return todoPapers[0].Id;
        }
        catch
        {
            return null;
        }
    }

    private string SafeTitle(string paperId)
    {
        try
        {
            return _context.Workspace.GetPaper(paperId)?.Title ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void CloseDialog()
    {
        var dialog = _dialog;
        _dialog = null;
        if (dialog == null)
        {
            return;
        }

        try
        {
            dialog.Close();
        }
        catch
        {
            // Already closed.
        }
    }
}
