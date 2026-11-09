# 批量导入（PaperTodo 插件）

在待办纸顶栏加一个「批量导入」按钮：点开弹出输入窗，把整篇文档一次性粘进去，按换行拆成任务，一次全部落到当前这张待办纸上。

- 插件 ID：`com.doggyhu.bulk-import`
- 插件协议：`apiVersion 2.0`（对应本仓库当前 `PaperTodo.Plugin.Abstractions`）
- 类型：Native（.NET 10 / WPF，随宿主进程运行）

## 怎么用

1. 重启 PaperTodo（协议 2.0 没有插件热重载）。
2. 桌面上会出现一个「批量导入」胶囊纸片，所有纸片的顶栏会多出一个带加号的文档图标按钮。
3. 在**任意一张待办纸**上点这个按钮：
   - 目标就是这张纸（点的是便签/笔记纸时，会落到最近一次导入过或第一张待办纸）。
   - 弹出输入窗，把文档粘进去。
   - 勾好选项，点「导入」或按 `Ctrl+Enter`。
4. 导入完成后按钮变「撤销本次导入」，误导入可以一次性删掉这一批。

也可以直接展开「批量导入」胶囊纸片，在纸片正文里选目标待办纸再导入 —— 弹窗和纸片正文是同一套界面。

## 弹窗里的选项

| 选项 | 说明 |
| --- | --- |
| **每行一条** | 每个非空行 = 一条任务（默认）。 |
| **每空行一段** | 空行分段，段内被硬换行拆开的软换行会合并成一条。适合从 PDF / Word 复制过来的正文。 |
| **去掉行首序号** | 去掉 `1.` `1、` `1)` `（1）` `-` `*` `•` `□` `[ ]` `##` 这类行首标记；`3.5 米` 这种小数不会被误伤。 |
| **批内去重** | 同一次粘贴里完全相同的行只导入一条。 |
| **跳过已存在** | 目标纸上已有同名任务就跳过（按文本比较，忽略首尾空白与大小写）。 |
| **合并空格** | 行内连续空格 / 制表符合成一个半角空格。 |

输入区下方实时显示「将导入 N 条 · 跳过空行 X · 批内重复 Y …」，超限会直接提示。

## 硬限制（跟宿主一致，不会让你撞到报错）

- 一次最多 **200 条**（宿主 `MaxPastedTodoLines`）。超出的部分不会提交，界面会提示还剩多少条。
- 单条最长 **5000 字**（宿主 `TodoTextMaxLength`）。超长行截断到 5000 字并计数提示。
- 整批是一次 `todos.append` 调用：宿主侧原子提交，失败整批回滚，不会留半批脏数据。
- 空行不会变成任务（宿主不允许空文本的待办）。

## 快捷键

| 位置 | 按键 | 行为 |
| --- | --- | --- |
| 弹窗内 | `Ctrl+Enter` | 立即导入 |
| 弹窗内 | `Esc` | 有未提交内容时先提示，再按一次关闭并丢弃 |
| 弹窗内 | 标题栏拖动 / 右下角拖拽 | 移动 / 改变窗口大小 |

## 权限与协议约束（为什么长这样）

- `appRuntime`：协议 2.0 的**全局顶栏按钮只能由 app runtime 注册**，而 app runtime 又要求「该 provider 至少有一张实体插件纸片」。所以插件声明了 `startupPaper`（胶囊），并且按钮会出现在**所有纸片**的顶栏——这是协议形状，不是本插件乱加。
- 关掉设置里的「显示「批量导入」纸片」后，如果没有其他本插件纸片，app runtime 不会启动，顶栏按钮也不会出现。想保留按钮就保留那张纸片（可以一直折叠成胶囊）。
- 协议 2.0 没有给 Native 插件提供宿主弹窗 API（宿主 popup 是 2.1 才有的），因此输入窗是插件自己画的 WPF 窗口：不进任务栏、跟随当前纸片配色、按 `Application.Current.Windows` 找到被点的那张纸片做 Owner 与定位。
- 用到的权限：`papers.read`（列待办纸）、`todos.read`（预览时统计已存在项）、`todos.append`（导入）、`todos.delete`（撤销本次导入）。

## 已知行为

- 如果开了「失焦自动收起为胶囊」，弹窗取得焦点时目标纸可能收成胶囊；导入仍然按 paperId 进行，不受影响。
- 与内置「多行粘贴智能拆分」的区别：内置方式是把多行直接粘进某一条待办的编辑框；本插件是**独立输入窗 + 预览 + 选项 + 可撤销**，适合几行到两百行的整段文档。
- 协议 2.0 的宿主（当前 fork）能加载；升级到上游 `4.0.0-beta2`（协议 2.3）后 `apiVersion 2.0` 的 manifest 会被拒绝加载，需要按新协议改写（`capabilities: ["runtime"]`、`context.Papers`/`Popups` 等）。

## 目录结构

```
plugin-samples/PaperTodo.Plugin.BulkImport/
├─ PaperTodo.Plugin.BulkImport.csproj
├─ plugin.json                  # manifest（id/权限/设置/启动纸片）
├─ BulkImportPlugin.cs          # 唯一公开的 IPaperBodyPlugin + IPaperAppRuntimeProvider
├─ BulkImportRuntime.cs         # app runtime：全局顶栏动作 + 弹窗生命周期
├─ BulkImportBodySession.cs     # 插件自己纸片的正文
├─ BulkImportPanel.cs           # 共享导入面板（输入框/选项/预览/导入/撤销）
├─ BulkImportDialog.cs          # 自绘弹窗窗口
├─ BulkImportParser.cs          # 纯解析逻辑（拆分/去符号/去重/限额）
├─ BulkImportPalette.cs         # 跟随宿主纸片配色
├─ BulkImportWindowPlacement.cs # 光标取窗 + 定位
└─ BulkImportSettings.cs        # 读宿主设置与 per-paper state
```

## 构建与安装

正常流程（会拒绝在 PaperTodo 运行时覆盖 Native 插件）：

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.BulkImport\PaperTodo.Plugin.BulkImport.csproj
```

只打包装、不安装：

```powershell
dotnet publish .\plugin-samples\PaperTodo.Plugin.BulkImport\PaperTodo.Plugin.BulkImport.csproj `
  -c Release -r win-x64 --self-contained false -o <输出目录> `
  /p:DebugType=none /p:DebugSymbols=false /p:GenerateDocumentationFile=false
```

然后把 `plugin.json` 一起放进 `plugins/<id>/`。已构建的产物在：

```
plugins/com.doggyhu.bulk-import/
输出/PaperTodo-v4.0.0-preview/plugins/com.doggyhu.bulk-import/   # 运行中的 exe 读这里
输出/PaperTodo-self-contained/plugins/com.doggyhu.bulk-import/
```

只保留 `plugin.json` + `PaperTodo.Plugin.BulkImport.dll` + `.deps.json`；不要带 PDB/XML，也不要带宿主共享的 `PaperTodo.Plugin.Abstractions.dll`。
