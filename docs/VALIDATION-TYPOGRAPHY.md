# 字号、编辑展开与 Build 目录回归

2026-09-21，Windows 10 x64，.NET SDK 10.0.302，Release。未发布 GitHub Release，未创建标签；本轮只交付程序目录，不生成 ZIP。

## 修复与原因

- 原版本可复现：表情所在 Span 的嵌套 Run 使用 12 DIP，而编辑文档使用默认 16 DIP，未跟随应用的 14／18 DIP 设置。现在正文、嵌套文本、表情与 FlowDocument 都明确使用同一字号资源；不向保存的 JSON 写入字号，也不覆盖原有粗体、斜体、下划线或颜色。
- 连续普通文字合并为一个 Run，不再为表情旁的每个字创建一个 Run。每个窗口只保留一个可复用的任务编辑器，空闲时预热；结束编辑时解除事件、工具选区与父容器关联。
- 展开只改变外层裁剪容器高度，内部编辑器在动画期间保持固定尺寸；焦点设置推迟至布局阶段，并检查编辑代号，防止快速关闭／重开后的旧回调操作新编辑器。
- 应用内富文本粘贴统一字号和字体，保留内容格式；外部 RTF 粘贴同样规范显示字号。

## 实测

- 43 项业务／存储测试通过；编译 0 警告、0 错误。
- 完整实际 WPF 回归通过，111 条记录，包括既有颜色、表情、主题、布局、分页、删除恢复及并发状态交互。此次系统剪贴板写入／读回成功；这不等于所有外部应用和 IME 组合均已验证。
- `--ui-typography` 在 12／14／16／18 DIP 分别检查：添加表情任务后再添加任务、编辑原文与继续输入、粗体组合表情结尾继承、粘贴、JSON 往返、编辑中修改全局字号、快速取消／重新展开，以及跨任务复用同一编辑器。所有断言通过。
- 展开期间每 20ms 采样一次、共 8 次，RichTextBox 高度保持 75.2 DIP，未随外框逐帧变化。最终专项运行中首次 BeginEdit 同步处理 23.78ms，其余 9.44–19.83ms；此前一次修复运行首次为 13.85ms。该数字受 JIT、布局和机器负载影响，不是帧率或输入至显示延迟保证。
- 复用既有十万条落盘测试数据：首屏 1,250.44ms、切换页签 P95 77.72ms、最多 6 个任务控件、2,000 条缓存、1,924,000 字节内容缓存，目标通过。工作集包含运行时与图形资源，见 JSON 原始数据。
- 原始回归记录与截图位于 [evidence/typography](evidence/typography)。当前桌面、DPI 与 Windows 11 覆盖边界仍见 [2.1 报告](VALIDATION-2.1.md)。

## 构建与数据

`tools/Publish.ps1 -Variant Both` 自动创建项目根目录 Build，输出 lite、portable 程序目录及 sizes.json。只允许指定 Build 内的输出位置；已有程序目录拒绝覆盖，以保护 Data。`tools/Verify-Build.py Build` 核对共同干净源码提交、必要文件、许可 SHA-256 与精简版体积，不把用户 Data 算入程序体积。

升级时先关闭程序，完整备份 Data（包含设置、备份与可能存在的 WAL 文件），逐文件校验副本，再移除旧程序文件。运行新版前把 Data 放回程序同级目录。不同旧安装的数据分别保留，不直接覆盖或混合数据库。Build 全部被 Git 忽略，真实任务数据不进入仓库。

```powershell
dotnet test tests/ToDoList.Tests -c Release
./src/ToDoList.App/bin/Release/net10.0-windows/ToDoList.exe --data-dir H:/temporary-font-check/Data --ui-typography
./tools/Publish.ps1 -Variant Both
python tools/Verify-Build.py Build
```
