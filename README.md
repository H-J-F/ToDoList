# ToDoList · Fluent 桌面待办

一款本地运行的 WPF 待办桌面应用。采用 WPF UI 4.3.0 的 Fluent 控件、浅蓝色任务图标；每本待办书使用独立 SQLite 文件。

## 直接使用

2026-09-20 补测修正版位于 `artifacts/publish-verified/ToDoList-win-x64`，修复了减少动态效果模式下的模板动画。升级时先关闭旧版，再将修正版 ZIP 解压覆盖原程序目录，保留原有 `Data`。

发布目录为 `artifacts/publish/ToDoList-win-x64`，双击其中的 `ToDoList.exe`。便携版自带 .NET 运行时，不需要安装 Rider 或 .NET。请把**整个目录**放在可写位置，不要只复制 exe。

首次启动没有预置数据。点击“创建待办书”，填写：

- **英文标识名**：1–64 个 ASCII 英文字母或数字，允许纯数字，大小写不敏感；不允许空格、符号或 Windows 保留文件名（含 COM1–9、LPT1–9）。
- **展示标题**：可以用中文，如“认真生活的小手账”。

书、设置和备份都在 exe 同级的 `Data` 中。移动整份程序目录即可一起带走数据。程序不会将任务上传到服务器。

## 操作

| 操作 | 方法 |
| --- | --- |
| 新建、切换待办书 | 左侧顶部的选择器与“新建待办书” |
| 导入、导出整本书 | 书名下方的 `···` 菜单 |
| 新建项目 | 项目目录右侧 `＋`，或在项目列表右键 |
| 新增任务 | 底部单选项目，输入内容后点“添加任务”或 Ctrl+Enter |
| 换行、格式与 emoji | Enter 换行；工具栏或 Ctrl+B/I/U；表情按钮或 Windows 的 Win+. |
| 修改内容 | 双击任务正文，Ctrl+Enter 保存，Esc 取消 |
| 完成／恢复未完成 | 单击勾选框，或焦点在勾选框时按空格 |
| 待验证／取消待验证 | 长按勾选框 600ms，或 Shift+空格 |
| 删除／恢复任务 | 任务右键菜单，或聚焦任务后按 Shift+F10；恢复到删除前状态 |
| 打开内容中的链接 | Ctrl+单击链接 |
| 完成任务排序 | 在已完成页签右上角悬停，选择添加日期或完成日期；键盘 Tab 也可到达 |
| 主题、字号、间距、动画 | 左下角“设置” |

“全部”查看本书所有任务，新增时选“全部”表示不归属具体项目。任务创建后只能改内容，不能修改项目归属。未完成包括黄色待验证任务；已完成只包括当前已完成任务。今天／本周／本月根据**添加日期**筛选，周一为一周开始。完成时间只用于完成信息与完成页签排序。

**已删除**使用红色图标及独立的红色删除线。日期页签继续显示已删除任务，未完成和已完成不显示；已删除页签按删除时间倒序分组。恢复已完成任务时保留其原完成时间。删除期间不能编辑正文或通过复选框切换状态，恢复后可继续操作；本版不提供永久删除。

颜色、粗体、斜体、下划线和链接会保存；图片、附件和复杂文档布局不属于本版本内容格式。emoji 使用系统字体显示，具体彩色呈现由 Windows/WPF 字体支持决定。

## 导入和备份

- 导出使用 SQLite 备份 API 创建一致性 `.db`，包含已经提交但尚在 WAL 中的内容。
- 导入通过库内部英文标识名识别同一本书，改文件名不会绕过同名检查。
- 同名时选择合并、覆盖或取消。合并按任务 UUID 去重，同一条任务保留修改时间较新的完整版本，时间相同保留本地。不同 ID 的相同文字任务不去重。
- 同名项目会合并关联，状态历史去重保留。本地展示标题和书内偏好在合并时保持不变。
- 合并和覆盖前，原书自动备份至 `Data/Backup`；备份可再次导入恢复。全局主题等设置不会被导入覆盖。
- v1 待办书首次使用时自动升级至 v2，升级前备份到 Data/Backup；迁移失败会回滚。导入 v1 时仅升级暂存副本，不改源文件。v2 库不能由旧版应用读取。
- 删除状态及恢复信息参与导入、导出和合并，较旧的备份不会覆盖较新的删除，较新的主动恢复可以覆盖删除。
- 导入先验证暂存副本再替换；不支持的格式版本、损坏库或非法内容不会替换现有书。
- 不要在应用运行时直接覆盖正在使用的 db，使用应用内导入。手动搬移数据前请先关闭程序，并连同整个 Data 目录一起搬移。

## 外观与交互

设置包含浅色／深色／跟随系统，浅蓝／青绿／橙色／紫色强调色，12／14／16／18 DIP 字号，紧凑／舒适间距，以及减少动态效果。设置即时生效并保存；原配色偏好自动迁移，书内数据不受影响。窗口按钮和控件图标采用 WPF UI，包含按钮反馈、下拉、切换、设置面板、编辑展开以及状态退出动画。

## Rider 开发

1. 安装 Windows 版 .NET 10 SDK，用 Rider 打开 `ToDoList.sln`。
2. 还原 NuGet，选择 `ToDoList.App` 启动。
3. 配置为 Release 可获得与性能测试相近的表现。

```powershell
dotnet build ToDoList.sln -c Release
dotnet test tests/ToDoList.Tests -c Release
```

项目划分：

- `src/ToDoList.Core`：状态、日期边界、富文本结构、查询模型及存储接口。
- `src/ToDoList.Storage`：SQLite、版本化建库、状态事务、书管理、合并和备份。
- `src/ToDoList.App`：MVVM、WPF UI Fluent 界面、原位编辑、FluentWindow / TitleBar、虚拟化及动画。
- `tests/ToDoList.Tests`：数据和业务自动化测试。
- `tools/ToDoList.Benchmarks`：可复现的落盘数据生成与查询基准。

## 性能设计

数据库按索引排序，使用 `(时间, ID)` 双向游标每批读取 200 条，不使用深 OFFSET，不在客户端加载全库排序。普通 SQLite View 不提供物化排序缓存，因此采用通用索引及状态部分索引。

界面使用 Recycling 虚拟化，日期标题包含在当天首条任务模板中。默认最多缓存 2,000 条任务、32MiB 内容，远离视口的内容淘汰后可反向重新加载。单条超大内容和正在编辑／提交的行必须保留，可能暂时超过内容预算；不限制数据库中任务总量。滚动条对应当前加载窗口，不代表整本书的绝对百分比。

SQLite 操作在后台队列执行，写入序列化；状态操作按任务独立控制动画。离开当前筛选时，先渐隐再收缩行高，最后移除；容器回收时重置动画状态，回调核对查询代号和任务 ID。

## 可复现的测试与发布

```powershell
# 生成 1 万、10 万、100 万条落盘数据并测试主要查询
dotnet run --project tools/ToDoList.Benchmarks -c Release -- artifacts/benchmarks

# 启动 WPF 的视觉与交互冒烟检查；使用新的隔离目录，测试后自动退出
& .\src\ToDoList.App\bin\Release\net10.0-windows\ToDoList.exe --data-dir "$PWD\artifacts\ui-check\Data" --ui-smoke

# 实际 WPF 首屏、页签切换及滚动缓存检查
& .\src\ToDoList.App\bin\Release\net10.0-windows\ToDoList.exe --data-dir "$PWD\artifacts\benchmarks\rows-100000\Data" --ui-perf

# 自包含便携发布与压缩包
.\tools\Publish.ps1
```

诊断参数 `--data-dir` 仅供测试隔离；普通启动始终使用 exe 同级 Data。UI 冒烟测试会向指定测试库写入样例数据，请使用新的测试目录。

图标源图及 imagegen 提示词在 `src/ToDoList.App/Assets`。图标采用用户确认的**浅蓝色**版本，`.ico` 含七个尺寸，可使用 `tools/New-AppIcon.ps1` 从源 PNG 重新封装。

实际测试记录及平台限制见 `docs/VALIDATION.md`。

## 动画演示与图标

实际 WPF 动画录制（仅在独立测试目录运行）：

```powershell
& .\src\ToDoList.App\bin\Release\net10.0-windows\ToDoList.exe --data-dir "$PWD\artifacts\demo\Data" --ui-demo
ffmpeg -y -f concat -safe 0 -i artifacts/demo/demo-frames/frames.txt -fps_mode vfr -c:v libx264 -crf 21 -pix_fmt yuv420p artifacts/demo/interactions.mp4
```

图标矢量源为 `src/ToDoList.App/Assets/app-icon.svg`。`tools/New-FluentIcon.ps1` 通过 WPF 矢量绘制生成各尺寸 PNG 和 16/24/32/48/64/128/256 ICO；16/24 像素版省略最下方装饰线。运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/New-FluentIcon.ps1` 可重建。

开源依赖与许可见 `docs/THIRD-PARTY.md`。本地 Git 主分支为 `main`，没有配置远端；真实任务、备份、构建和发布产物不纳入版本控制。
