# ToDoList 2.2.0 本地验收记录

验收日期：2026-09-22（Asia/Shanghai）

## 自动化结果

- Release 全量构建：0 警告、0 错误。
- 单元与存储测试：48/48 通过。
- WPF 功能回归：40/40 通过，覆盖 WPF-UI `CalendarDatePicker`、任务模块修改、报告弹窗和 Markdown/DOCX/PDF 导出。
- 字体、富文本与行内编辑回归：通过。
- 窗口生命周期：关闭隐藏到托盘、托盘恢复、最大化状态恢复、最小化后激活均通过。
- 单实例集成：隐藏主窗口后从另一数据目录启动第二进程，第二进程正常退出，原窗口恢复并激活。

## 报告检查

- 按任务添加时间的本地日期升序分组，跨日期保持全局连续编号。
- 任务正文先于状态元数据，Markdown、DOCX、PDF 使用统一语义模型。
- 状态颜色：未完成 `#42E9FF`、待验证 `#FFE500`、已完成 `#00FF73`、已删除 `#FF5141`。
- PDF 渲染为 A4，两页长正文测试未发现裁切或重叠。

## 数据迁移检查

- 源数据：`Build/Reports-49795f1/ToDoList-2.1.0-win-x64-portable/Data`。
- SQLite：`quick_check=ok`、外键违规 0、`application_id=1414480975`、`user_version=2`。
- 完整备份与逐文件 SHA-256 记录位于 `Build/DataBackups/20260922-2.2.0-migration/manifest.json`。
- 仅向 2.2.0 lite 复制数据；portable 保持无 `Data`。

本记录对应本地工作区验证构建；未创建标签、ZIP 或 GitHub Release。
