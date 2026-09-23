# ToDoList 2.3.0 本地验收记录

## 变更范围

- 模块顺序持久化、重命名、删除及任务保留。
- 待办笔记删除前自动备份，用户文案统一为“待办笔记”。
- 状态颜色按待办笔记保存，并应用于界面和报告。
- 修复表情文字颜色、日期分隔线、日期控件、托盘菜单及报告排版。
- Lite 版增加 .NET 10 Desktop Runtime x64 启动检查。

## 验证要求

- `dotnet build ToDoList.sln -c Release`
- `dotnet test tests/ToDoList.Tests -c Release`
- Lite 与 Portable 发布脚本成功完成。
- `tools/Verify-Build.py` 对两套产物验证通过。
- 构建产物不包含用户 Data、数据库或调试符号。
