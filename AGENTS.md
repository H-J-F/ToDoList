# ToDoList 项目管家约定

项目管家是维护本工程的 Agent；Keeper 是工具，源码是最终事实。参考 BM2_Client 的治理方式，按本项目规模维护，不引入 Unity、Codely 或其权限规则。

- 首次写入前执行 `python -X utf8 .codex/项目管家/keeper.py identity`，确认当前 Git 根目录和 ToDoList 工程一致。
- 代码定位先用 `python -X utf8 .codex/项目管家/keeper.py query "关键词"`，默认三条，再读相关源码。索引只在 `artifacts/keeper`，可重建、不提交。
- 修改前阅读对应 [项目架构](docs/项目架构.md) 或 [构建发布](docs/构建发布.md)。保留已有未提交改动；不要清理或改写用户 Data、Backup、历史 Build。
- 每轮有实际文件修改，完成后必须执行根目录 `Build.bat`。默认版本末位 +1；同一轮的构建失败重试不重复递增，无改动重复执行不递增。用户指定版本时执行 `Build.bat --version X.Y.Z`，不可再自行 +1。只读问答不手动构建。
- `Directory.Build.props` 是产品版本唯一来源；数据库 `Database.Version` 是独立格式版本，不随产品版本自动递增。关于页和程序集从产品版本派生。
- 交付前按 [项目闭环文档](.codex/项目闭环文档.md) 复核受影响文档；契约变化更新原文，无语义变化可不改文档，最终说明判断。禁止把脚本结构校验描述成语义验收。
- Stop hook 自动补跑同一构建流程；不依赖 hook 是否已加载或信任，Agent 主动执行 Build.bat 并检查退出码。失败要修复并重试；环境阻塞如实交代，不声称已发布。
- 测试使用隔离数据目录，不能操作用户数据库。构建脚本已执行工程编译、单元测试和两版包校验；界面行为变化再按影响补实际 WPF 验证。
- 仅发布到本地 Build；不隐含 Git 提交、推送、标签、远程 Release 或上传授权。

管家介绍：[管家自述](.codex/项目管家/管家自述.md)。Hook 接入与限制：[HOOKS](.codex/HOOKS.md)。
