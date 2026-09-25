# 任务界面与修改时间验收（2026-09-24）

本次仅修改源码、测试和说明，程序集版本保持 2.3.1，未发布安装包。数据库格式升级为 v4。

## 实现与兼容

- 已删除页签的多选框替换垃圾桶，位于状态框同一列，不显示“选择此任务”。成功彻底删除后退出多选，取消确认或失败保留选择。
- 编辑器挂入实际布局后按可用宽度测量，固定内容尺寸，仅动画裁剪宿主；动画结束恢复自动宽高。延迟回调带版本校验；同一任务随托盘窗口卸载、加载时保留未保存编辑。
- `TodoItem.EditedAt` 为可空 Unix 毫秒时间。仅实际修改内容（含格式）或所属模块时更新；删除模块导致归属清空也计入修改。无变化保存、状态切换、删除和恢复不改变该字段。`UpdatedAt` 仍用于整条任务的合并比较。
- v1/v2/v3 升级前备份，事务内迁移到 v4；导入只升级暂存副本。旧任务与新任务均不补填修改时间，无记录时界面及报告省略该项。MD、DOCX、PDF 共用包含修改时间的报告模型。
- 导出默认颜色为 `#00CAE5`、`#E6A409`、`#07E355`，已有自定义颜色保留。设置值左侧实时预览合法颜色；任务使用独立的 `TaskOpenBrush`、`TaskVerificationBrush`、`TaskCompletedBrush`，不读取导出设置。
- 托盘退出不再无条件恢复窗口；需要处理未保存内容或错误才显示窗口。取消或保存失败会重置退出标志。异步退出先让 WPF 完成取消关闭回调，避免在 `Closing` 内调用 `Show`；恢复窗口时不重复执行启动初始化。

## 构建和自动测试

| 检查 | 结果 | 本地证据 |
| --- | --- | --- |
| `dotnet build ToDoList.sln -c Release --no-restore` | 通过，0 警告、0 错误 | 本次构建输出 |
| `dotnet test tests/ToDoList.Tests -c Release --no-build` | 60 项通过 | 本次测试输出 |
| WPF 修改专项 | 89 项通过，`Completed=true` | `artifacts/revision-verified/revision.json` |
| 草稿保存后退出／放弃后退出 | 各 2 项通过 | `artifacts/revision-exit-save/draft-exit.json`、`artifacts/revision-exit-discard/draft-exit.json` |
| 240 条任务真实滚动与容器复用 | 4 项通过 | `artifacts/revision-recycling/recycling.json` |
| 原有筛选、编辑、设置、报告功能 | 40 项通过 | `artifacts/revision-features/features.json` |
| 原有任务与剪贴板压力回归复测 | 35 项通过，含 100 轮剪贴板操作 | `artifacts/revision-taskfixes-recheck/taskfixes.json` |

新增存储测试覆盖无变化保存、格式修改、模块变更、状态及删除恢复独立性、删除模块影响范围、旧库迁移与备份、只升级导入副本、合并保留修改时间、报告默认与自定义配色、无效修改时间拒绝导入。

WPF 专项记录了 12 组展开动画逐帧宿主高度，覆盖 850/1150 DIP 窗宽、12/18 DIP 字号、短文本、多行文本及编辑器复用。全部采样没有超过起始与最终高度范围，结束后宿主恢复自动高度。另验证减少动态效果和立即取消编辑。

多选验证覆盖位置重合、图标互斥、数据刷新、取消、版本冲突失败、成功后剩余任务退出多选，以及真实滚动造成的容器复用。颜色验证检查浅色／深色下实际 CheckBox 模板边框及填充画刷，包含设置输入预览、笔记切换和持久化。

三种报告均生成成功，DOCX 逐行比对共享模型，Markdown 比对完整内容；另用 PyMuPDF 提取 PDF 文本，确认“上次修改时间”出现且只属于有记录的任务。见 `artifacts/revision-verified/report-verification.json` 及同目录报告文件。

退出验证覆盖隐藏状态等待进行中的操作、重复点击退出、草稿取消、注入 SQL 保存失败、任务编辑保存／放弃、草稿保存／放弃。干净退出监听窗口可见性变化，直至 `Closed` 未重新变为可见。

## 测试中发现的问题及验证边界

- 初轮退出测试确实触发过未处理的 WPF 异常：在 `Closing` 回调中恢复隐藏窗口。根据 Windows `.NET Runtime` 事件定位后已修复；最终退出专项完整通过。
- SQL 保存失败使用独立测试数据库的 `fail_add` 触发器主动注入，用于验证保留草稿、显示错误并取消退出。完整专项结束后触发器已删除，检查结果为空；未对实际用户数据库注入故障。
- 中间版本的退出测试用 `SetContent` 装载内容，没有设置编辑器的 Dirty 状态，导致未走任务保存提示便提前退出。已改为通过 RichTextBox 的选区写入模拟真实修改，并新增未保存状态断言及运行完成标志。
- 原有剪贴板压力测试首次在第 22 轮内容替换断言失败，见 `artifacts/revision-taskfixes/taskfixes.json`。未改剪贴板实现；单独复测全部通过。首次失败原因未确定，不将复测通过描述为已修复该偶发问题。
- 界面测试在独立 Data 目录启动实际 WPF 窗口，调用生产事件及控件命令、采样渲染和滚动状态；不等同于人工鼠标全流程验收。截图位于 `artifacts/revision-verified/`。未进行安装包发布或所有 Windows/DPI 组合验证。

## 复现专项

对每个场景使用新的空目录，避免误用已有用户笔记：

```powershell
dotnet build ToDoList.sln -c Release
dotnet test tests/ToDoList.Tests -c Release --no-build
```

构建后的 `src/ToDoList.App/bin/Release/net10.0-windows/ToDoList.exe` 支持以下参数：

- `--data-dir <空目录> --ui-revision`：完整修改专项。
- `--data-dir <空目录> --ui-revision --exit-draft-save`：保存草稿后退出。
- `--data-dir <空目录> --ui-revision --exit-draft-discard`：放弃草稿后退出。
- `--data-dir <空目录> --ui-revision --selection-recycling`：多选框滚动复用。

结果 JSON 写入各 Data 目录的父目录。完整专项需同时检查 `Completed=true` 与 `Passed=true`。
