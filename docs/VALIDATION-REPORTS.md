# 所有页签、日历、报告与输入交互回归

2026-09-21，Windows 10 x64，.NET SDK 10.0.302，Release。

## 实现

- “所有”汇总当前模块所有状态任务；日历按添加时间选择整年、整月或整日，应用后取消普通页签选中。模块切换保留日期条件，普通页签退出日历筛选。
- 报告分别配置模块、任务状态、添加时间与格式，默认 MD；自定义范围包含结束日。数据库事务读取完整快照，独立于界面分页和缓存，统计与正文采用同一份结果。
- MD、DOCX、PDF 共用纯文本报告结构。DOCX 使用 ZIP/XML，PDF 使用 PDFsharp-WPF；WPF 将系统 TTC 字体中的字体面转换为独立字体数据，供 PDFsharp 按使用字形嵌入报告。表情以图片呈现在 PDF 中；正文为可选取的文本。运行时不依赖 Office 或打印机。
- PDF 包附带的 WPFonts、Snippets、Quality 示例程序集从编译／运行复制项目中排除，不随构建分发示例字体。两种依赖锁文件与许可清单已更新。
- 编辑提示与实际首个插入位置对齐，空格和换行不再显示提示；Enter 添加／保存、Shift+Enter 换行，保留 Ctrl+Enter。编辑器识别输入法组合状态及重复按键。
- 设置支持再次点击按钮及面板外点击关闭。底部输入高度为 80 DIP，小窗口为 76 DIP；行内编辑器最小高度为 92 DIP。

## 验证结果

- Release 构建：0 警告、0 错误；46 项业务／存储测试通过，含 2,405 条报告完整读取、模块／状态组合、日期边界、跨年、闰年及夏令时。
- [新增功能回归](evidence/reports/features.json)：40 条记录通过，包含日历实际应用／关闭、普通页签切换、设置关闭、12／14／16／18 DIP 提示对齐、Shift+Enter、添加与保存、报告配置与日期错误处理、三格式生成及失败清理。
- [既有 WPF 回归](evidence/reports/ui-smoke-results.json)：111 条记录通过，覆盖主题、颜色、表情、复制粘贴、最小窗口、分页虚拟化和并发状态更新。
- [字体与编辑专项](evidence/reports/typography.json)：200 条采样记录，断言通过，未破坏字体继承、编辑器复用和展开动画。
- 独立发布输出 `Build/ReportValidation` 在隔离 Data 下完成新增功能回归，确认未携带上述示例程序集。普通及便携版依赖均通过 locked restore；此次未生成便携版 EXE 或正式发布包。
- [报告文件验证](evidence/reports/report-verification.json)：本机 Word 成功打开 DOCX 并转为 3 页 PDF；原生 PDF 同为 3 页。3,000 个连续汉字及末尾任务完整保留，文本位于页面边界内；原生 PDF 文本与 DOCX 正文一致（表情图片除外），两张彩色表情图片存在。已查看首尾页与页面截图。
- 当前桌面 DPI 下完成界面验证；未逐一验证其他系统缩放或第三方中文输入法。输入法测试使用组合状态模拟，DOCX 表情显示由阅读器决定。

截图：[日历](evidence/reports/calendar.png)、[报告配置](evidence/reports/report-dialog.png)、[最小窗口](evidence/reports/compact.png)、[原生 PDF](evidence/reports/report.pdf.png)、[Word 渲染](evidence/reports/docx-rendered.pdf.png)。所有截图与测试报告均使用合成任务。

## 数据保护

实施前正常关闭原精简版实例，完整复制其 Data 到 Build 下的独立时间戳备份。原目录保持原位；源与备份的文件数量、大小、SHA-256 已核对，工作完成前再次核对未变化。真实 Data、备份、哈希清单及验证构建均被 Git 忽略，未用于测试或提交。

```powershell
dotnet build ToDoList.sln -c Release
dotnet test tests/ToDoList.Tests -c Release
# 使用新的、独立的数据目录：
./src/ToDoList.App/bin/Release/net10.0-windows/ToDoList.exe --data-dir H:/temporary-report-check/Data --ui-features
```
