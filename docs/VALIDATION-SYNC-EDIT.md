# 授权、笔记身份、迁移与输入同步修复验收

## 职责

记录 2026-10-14 本轮八项修复的实际证据与覆盖边界；历史云服务和扫码记录不代表本轮已重新验收。

## 入口

- `tests/ToDoList.Tests/CloudRepairTests.cs`：可控时钟授权与调度、保留身份导入、持久冲突、迁移与双库恢复。
- `src/ToDoList.App/MainWindow.RepairChecks.cs`：从工程根目录启动程序，使用 `--data-dir <工程绝对路径>/artifacts/repair-ui/<新标识>/Data --ui-revision --sync-edit-checks`。
- 根目录 `Build.bat`：统一产品版本、编译、测试及 Lite／Portable 包验证；生命周期补验使用 `python -X utf8 tools/Verify-Lifecycle.py <发布包/ToDoList.exe>`。

## 契约

所有数据均为独立空目录中的合成笔记。WPF 专项使用真实 OS 鼠标键盘和本机简体中文输入法；同一进程内合成远端快照经过实际存储与界面应用链路，不连接微云。测试拒绝用户运行数据目录，结束后主动正常退出，不以终止进程伪造成功。

授权期限与同步计时采用可控时钟验证，不等待实际 14 天。真实扫码、服务端提前撤销、真实微云跨设备传输本轮未重新运行。单机输入法、显示缩放与前台输入结果不能外推至全部 Windows 环境。

## 验证

开发构建 `.NET` 回归 149 项通过，零失败；解决方案编译零警告、零错误。新增测试包括第 14 天边界、刷新不延长、旧授权一次性起算、九档计时、保存后重计周期、退避及编辑暂缓信号；同 ID 导入与重命名、幂等、版本冲突重启保留和离线解决；回收站／状态历史迁移、同名合并／改名、同 ID 不兼容记录拒绝、取消、第二本数据库写入失败恢复、文件名避重后覆盖身份保持。

开发构建真实 WPF 专项 46 项通过，进程退出码 0。报告 `artifacts/repair-ui/054f694276ae/ui-results/result.json`，`Completed=true`、`Passed=true`、`Limitations=[]`。覆盖三次连续发送后的真实焦点、两次发送后中文拼音输入与提交、发送失败草稿保留、点击外部／切换窗口／最小化自动保存、颜色 Popup／模块下拉保留编辑、验证失败保留编辑并阻止导航；超过 200 条的分页窗口、无变化同步无遮罩／集合 Reset／焦点丢失、有变化按 ID 保留对象、草稿暂缓、笔记重命名及编辑保护；“全部”选中时右键实际模块、迁移菜单、目标同名警告与合并完成。

初次辅助测试窗口受到隐式 Window 样式影响，已在测试入口为辅助窗口设置 `Style=null` 后重跑通过；生产窗口未因此修改。失败证据保留在 `artifacts/repair-ui/32ea8d4034b6`。

账号面板离线真实输入回归 79 项通过，进程退出码 0；报告 `artifacts/account-ui/repairs-57c77aa6015c/ui-results/result.json`。覆盖浅深主题、两种动画、最小窗口、账号切换与恢复、合成资料及退出；它不表示本轮真实微信资料已取得。

正式 Portable 2.3.16 包交互专项 46 项通过，进程退出码 0；报告 `artifacts/repair-ui/release-45a1c013ac76/ui-results/result.json`，真实中文输入法无未验收项。首次生命周期回归的旧编辑退出用例仍等待已移除的保存确认，导致测试未完成而程序正常退出；用例已调整为失焦自动保存与编辑区明确取消，原失败报告保留在 `artifacts/lifecycle/1d49ab09c6af/revision.json`。

正式发布包复验结果保存在各次运行的 `artifacts/repair-ui` 和 `artifacts/lifecycle`，最终准确包版本与位置以 `Build/latest.json` 和本轮交付说明为准。
