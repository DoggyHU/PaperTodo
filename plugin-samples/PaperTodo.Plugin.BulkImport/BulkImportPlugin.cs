using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Entry point of the 「批量编辑」 plugin. This is the only public <see cref="IPaperBodyPlugin"/>
/// implementation in the assembly, which is what the host's native plugin loader requires.
/// </summary>
public sealed class BulkImportPlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider
{
    public string Id => "com.doggyhu.bulk-import";

    public string DisplayName => "批量编辑";

    public string Description =>
        "把整张待办纸的任务当成一段文本编辑：一行一条，改完整体写回（新增 / 改名 / 删除 / 调序）。";

    public Version Version => new(1, 1, 1);

    public string ApiVersion => "2.0";

    public int StateVersion => 1;

    public PaperBodyRuntimeRequirements RuntimeRequirements => PaperBodyRuntimeRequirements.None;

    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;

    public IPaperBodySession Create(PaperBodyContext context) => new BulkImportBodySession(context);

    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) => new BulkImportRuntime(context);
}



