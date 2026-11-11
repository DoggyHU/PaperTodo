# 批量编辑（PaperTodo 插件）

把一张待办纸的**全部任务当成一段文本**来编辑：弹窗里一行就是一条任务，打开时自动载入当前清单，改完点「确定」整体写回。
追加、改名、删除、调序都在同一个文本框里完成，不再有「导入」和「编辑」两套入口。

- 插件 ID：`com.doggyhu.bulk-import`
- 插件协议：`apiVersion 2.0`（对应本仓库当前 `PaperTodo.Plugin.Abstractions`）
- 类型：Native（.NET 10 / WPF，随宿主进程运行）

## 怎么用

1. 重启 PaperTodo（协议 2.0 没有插件热重载）。
2. 任意纸片顶栏多出一个「文档 + 加号」按钮。在**待办纸**上点它：

   ```
   打开时（已有 1 2 3 4）           你粘贴 5 6，点确定后
   ┌───────────────┐               ┌───────────────┐
   │ 1             │               │ 1             │
   │ 2             │   ── 确定 ──▶  │ 2             │
   │ 3             │               │ 3             │
   │ 4             │               │ 4             │
   │               │               │ 5             │
   └───────────────┘               │ 6             │
                                   └───────────────┘
   ```

3. 也可以展开「批量编辑」胶囊纸片，在纸片里选目标待办纸再编辑。
4. `Ctrl+Enter` 等于「确定」；`Esc` 关闭（有未保存修改时会先提示一次）。
5. 弹窗里「确定」保存成功后**自动关闭**；只有部分写入失败时才留在原地显示错误。纸片内的编辑器不会自动关闭。

## 编辑语义（写完是什么就是什么）

提交时不是「追加」，而是把文本框里的内容与打开时的清单做差异比较：

| 你做的操作 | 实际发生 |
| --- | --- |
| 在后面加了几行 | `todos.append` 追加，原有任务 id/完成状态/提醒/关联全部不变 |
| 改掉某一行的文字 | `todos.update` 原地改名；**id、完成状态、位置、关联都保留** |
| 删掉某一行 | `todos.delete` 删除（需要二次确认） |
| 交换两行顺序 | `todos.update` 调整顺序 |
| 粘贴带 `1.` `-` `□` 等行首标记的行 | 只对**新增行**去掉标记；已有任务绝不会被改写 |
| 重复的行 | 默认跳过（只作用于新增行） |

实现细节上先按文本精确匹配保住身份，再按相似度把「被改写的行」认定为改名（而不是删除 + 新增），所以改一个字不会让任务丢掉完成状态或跑到列表末尾。

## 安全设计

- **删除要二次确认**：状态栏会变红提示「将删除 N 条」，按钮变成「确认删除 N 条」，再点一次才执行。
- 删除会连同该任务的完成状态、提醒、关联一起移除，**插件里无法撤销**（宿主对插件写入不提供撤销栈）。误删只能用程序目录下的 `data.backup.json` 恢复。
- **并发保护**：如果编辑期间这张纸在别处被改动（比如你勾了某项），提交会拒绝并提示「重新载入」，不会拿着旧快照乱写。
- 空文本框 = 清空整张纸（同样走二次确认）。

## 限制

- 单条任务最多 **5000 字**（宿主 `TodoTextMaxLength`），超长的新行会被截断并提示。
- 新增超过 **200 条**会自动拆成多次 `todos.append`（宿主单次上限）。
- 空行不会变成任务（宿主不允许空文本待办），载入时纸上的空白占位行也不会显示在编辑器里。
- 「每空行一段」的整篇文档拆分已随编辑器语义移除：现在文本框里一行就是一条。想批量导入整篇文档，先新建一张空待办纸，把内容整理成一行一条再粘进去。

## 权限与协议约束（为什么长这样）

- `appRuntime`：协议 2.0 的**全局顶栏按钮只能由 app runtime 注册**，而 app runtime 又要求「该 provider 至少有一张实体插件纸片」。所以插件声明了 `startupPaper`（胶囊），并且按钮会出现在**所有纸片**的顶栏——这是协议形状，不是本插件乱加。
- 协议 2.0 没有给 Native 插件提供宿主弹窗 API（宿主 popup 是 2.1 才有的），因此窗口是插件自己画的 WPF 窗口：不进任务栏、跟随当前纸片配色、按 `Application.Current.Windows` 找到被点的那张纸片做 Owner 与定位。
- 用到的权限：`papers.read`（列待办纸）、`todos.read`（载入清单/并发检查）、`todos.append`（新增）、`todos.update`（改名、调序）、`todos.delete`（删除）。

## 已知行为

- 如果开了「失焦自动收起为胶囊」，弹窗取得焦点时目标纸可能收成胶囊；编辑仍然按 paperId 进行，不受影响。
- 纸质清单里如果有空白占位行（点一下就能输入的空行），它不参与编辑；只有在发生调序时才可能被挤到列表末尾。
- 协议 2.0 的宿主（当前 fork）能加载；升级到上游 `4.0.0-beta2`（协议 2.3）后 `apiVersion 2.0` 的 manifest 会被拒绝加载，需要按新协议改写。

## 目录结构

```
plugin-samples/PaperTodo.Plugin.BulkImport/
├─ PaperTodo.Plugin.BulkImport.csproj
├─ plugin.json                  # manifest（id/权限/设置/启动纸片）
├─ BulkImportPlugin.cs          # 唯一公开的 IPaperBodyPlugin + IPaperAppRuntimeProvider
├─ BulkImportRuntime.cs         # app runtime：全局顶栏动作 + 弹窗生命周期
├─ BulkImportBodySession.cs     # 插件自己纸片的正文
├─ BulkImportPanel.cs           # 编辑器面板（载入/预览差异/保存/二次确认）
├─ BulkImportDialog.cs          # 自绘弹窗窗口
├─ BulkImportEditor.cs          # 差异计划：新增/改名/删除/调序
├─ BulkImportText.cs            # 行拆分、行首标记清洗、相似度
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
