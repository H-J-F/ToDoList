# 托盘退出进程残留验收

## 问题与修复

2026-10-08，在隔离数据中打开实际托盘 ContextMenu 后执行退出，复现主窗口 Closed、应用 OnExit 和托盘图标释放全部完成，但进程仍在运行。诊断堆栈停在 Dispatcher.ShutdownFinished 销毁 HwndSource → ReleaseCapture → StylusWisp → PenThreadWorker.WorkerGetTabletsInfo；UI 线程等待触笔工作线程。堆栈证据位于 `artifacts/exit-fix/shutdown-hang-stack.txt`，失败的外部进程检查位于 `artifacts/lifecycle/7c4115d6e8f5/process-exit.json`。

修复在托盘菜单关闭前主动释放鼠标捕获，并关闭该菜单 Popup 的系统弹窗动画，使弹窗资源在 Dispatcher 结束前释放；没有禁用全局触笔／触摸支持。同时采用显式应用关闭，在主窗口通过未保存内容处理并实际关闭后调用 Shutdown，防止其他隐藏窗口延长进程生命周期。退出时忽略新的托盘与单实例激活。

## 验证方法与结果

`python -X utf8 tools/Verify-Lifecycle.py <ToDoList.exe>` 创建全新的 artifacts/lifecycle 子目录，使用独立 Data，不接触用户数据库。外部程序等待 Completed 报告后检查目标进程在 5 秒内结束、退出码为 0；Completed 本身不能证明进程退出。

开发构建的完整报告：`artifacts/lifecycle/4ecac329d67e/process-exit.json`，8 个场景均通过，报告观察后进程等待耗时 0.207～0.297 秒。

- 主窗口隐藏／显示时，从实际托盘菜单 Click 路径退出。
- 主窗口隐藏／显示且额外存在隐藏 WPF 窗口时，整个应用仍能退出。
- 应用退出事件确认 NotifyIcon.Visible 为 false，ContextMenu 已关闭。
- 现有 revision 专项覆盖取消退出、保存失败保留草稿、行编辑保存／放弃、重复退出和等待后台操作；退出后由外部程序确认进程结束。
- 草稿保存和放弃分别检查数据库，保存只写入一次，放弃不插入。
- 复用首次场景的同一隔离数据目录、同一单实例标识重新启动并退出，验证实例资源释放和窗口状态持久化。

修复前复现产生的挂起测试进程在保存失败报告与堆栈后，按进程 ID、可执行文件路径和隔离数据参数核对后清理；清理不计为任何成功场景。用户运行实例未被终止。

## 验证边界

生命周期专项打开真实 WPF 托盘 ContextMenu，并触发实际 MenuItem.Click 处理；没有自动点击 Explorer 的托盘图标。computer-use 尝试实际界面验证时截图接口超时，窗口无障碍树未提供独立托盘弹窗，因此不声称已完成物理右键、鼠标点击或任务管理器截图验收。NotifyIcon 已在退出事件中确认隐藏并释放，进程结束由外部等待确认。

构建脚本的文档结构检查不代表语义验收。架构及构建文档已补充生命周期契约与验证入口；主题契约没有变化，主题文档保持现状。
