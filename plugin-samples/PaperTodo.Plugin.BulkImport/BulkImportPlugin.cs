using PaperTodo.Plugin;

namespace PaperTodo.Plugin.BulkImport;

/// <summary>
/// Entry point of the 「批量导入」 plugin. This is the only public <see cref="IPaperBodyPlugin"/>
/// implementation in the assembly, which is what the host's native plugin loader requires.
/// </summary>
public sealed class BulkImportPlugin : IPaperBodyPlugin, IPaperAppRuntimeProvider
{
    public string Id => "com.doggyhu.bulk-import";

    public string DisplayName => "批量导入";

    public string Description =>
        "把整篇文档粘贴进来，按换行拆成待办任务，一次批量导入到当前待办纸。";

    public Version Version => new(1, 0, 0);

    public string ApiVersion => "2.0";

    public int StateVersion => 1;

    public PaperBodyRuntimeRequirements RuntimeRequirements => PaperBodyRuntimeRequirements.None;

    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;

    public IPaperBodySession Create(PaperBodyContext context) => new BulkImportBodySession(context);

    public IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context) => new BulkImportRuntime(context);
}
