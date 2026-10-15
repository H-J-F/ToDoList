# 云 DB、加载与按需刷新验收

## 职责

记录云协议 v2、实际文件回收权限、数量上限、模块迁回，以及首次云加载、撤回和提示动画的验证入口与证据。全部写入测试使用隔离本地数据或独立 CloudCheck 云空间，不修改用户正式云笔记。

## 入口

- [CloudDbTests](../tests/ToDoList.Tests/CloudDbTests.cs)、[CloudRepairTests](../tests/ToDoList.Tests/CloudRepairTests.cs)：因果合并、DB 编解码、旧格式迁移、恢复、回收失败重试和模块往返；原 [CloudSyncTests](../tests/ToDoList.Tests/CloudSyncTests.cs)、[CloudUndoTests](../tests/ToDoList.Tests/CloudUndoTests.cs)继续覆盖同步和撤回补偿。
- [CloudDbLiveChecks](../tools/ToDoList.CloudChecks/CloudDbLiveChecks.cs)：从工程根目录执行 `dotnet run --project tools/ToDoList.CloudChecks -c Release -- --db-cleanup <原生会话.dpapi>`。会话只在内存读取，刷新凭证保存在新建测试工作区；不覆盖原会话。自动生成唯一 CloudCheck 空间，输出 `artifacts/cloud-db-live/<标识>/result.json`。
- [CloudDbChecks](../src/ToDoList.App/MainWindow.CloudDbChecks.cs)：从工程根目录执行 `ToDoList.exe --data-dir <工程绝对路径>/artifacts/cloud-db-ui/<新标识>/Data --ui-revision --cloud-db-ui-checks`，要求独立空工作区。输出 `ui-results/result.json` 和 PNG，检查 Completed、Passed、failure 与外部进程退出。`--cloud-db-routed-input` 可使用路由事件，但不算真实鼠标验收。

## 契约

- Data 每设备最多两份已校验 DB，Sync 一份协议种子；额外未知文件不计入清理对象。回收发生在新 DB 下载字节校验后，只使用精确文件身份并移入回收站。失败显示原因和持久待清理清单，不回滚已完成同步。
- 新格式逐记录因果元数据保留并发、删除标记和离线变更；v1 先验证完整历史与分支，备份原字节再迁移和回收。全部设备需升级；旧版新增提交会阻止新同步和清理。
- 启动与首次云内容初始化显示加载界面；未知总量不定进度，已知文件数显示真实进度，可取消。通用忙碌、撤回和日常刷新不触发全屏遮罩；变更集保留未变化对象、焦点与滚动。
- 提示标准动画 180ms，从右侧 48 DIP 飞入淡入、向右飞出淡出；减少动态效果 90ms 淡入淡出。快速替换隔离旧回调。

## 验证

2026-10-14 原生真实微云验证通过 19 项检查。报告：`artifacts/cloud-db-live/8ba653985c694739a7c5618af732ffb4/result.json`，独立空间 `ToDoList/CloudCheck-0e1f641d7b9c4b8bba4469c27dd17389`。合成 SQLite DB 上传/下载字节一致，实际调用回收并重新列目录确认文件消失，证明当前会话的实际删除权限。连续五次修改与同步后，单设备 Data 始终不超过两份 DB；离线新增在历史回收后成功合并，最终只保留有上限的 DB 和唯一协议 DB。

开发构建 WPF 专项使用真实 OS 鼠标，完成 15 项断言并正常退出；最终开发报告 `artifacts/cloud-db-ui/dev-dbcf351123614e40af23848659854eac/ui-results/result.json`。验证慢下载文件进度、完成/失败/取消收起加载、实际取消按钮、同行时间与耗时、逐帧飞入/飞出、减少动态效果、快速替换、撤回无任何遮罩帧且保留行、焦点与滚动。加载和账号面板 PNG 已检查；正式两版包补验以最终交付报告为准。

离线测试另覆盖多次模块迁回、同名合并、回收站和状态历史、双库失败回滚、长期离线后合并、回收拒绝后重启重试、迁移发布响应丢失、旧分支冲突保留与解决、旧版后续写入保护、同设备同代次分叉拒绝、删除容器的最新有效内容保留、因果合并交换/结合/幂等性质及损坏/错账号 DB 拒绝。精确最终测试数量以 Build.bat 为准。

本轮没有重新进行真实微信扫码、其他系统 DPI 或用户正式工作区迁移。真实云删除验证与 WPF 合成加载验证分别说明服务权限和界面行为，不互相替代。构建/文档结构检查不代表语义或界面验收。

文档闭环：更新云同步、项目架构、README 和主题与预览的行为契约；构建发布已复核，本轮不改变版本来源、递增、发布或包校验规则，保持原文。
