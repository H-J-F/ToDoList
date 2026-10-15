# 同步、运行日志与差量刷新验收

## 职责

记录请求减少、真实微云失败诊断、Release 日志、撤回逐帧视觉和导出命名验证。用户运行的 2.3.20 Lite 保持运行；测试使用 artifacts 下独立 H 盘 Data，不修改用户数据库、云笔记或原凭证。

## 入口

- 存储／协议：CloudCheckpointTests、CloudTransportTests、RuntimeLogTests；调度／撤回测试按正常周期契约更新。
- WPF：`--data-dir <工程绝对路径>/artifacts/<新标识>/Data --ui-revision --cloud-db-ui-checks --cloud-db-routed-input`；撤回全链路替换为 `--undo-checks --undo-routed-input`。
- 只读诊断：`ToDoList.CloudChecks --diagnose <原实例凭证.dpapi>`，仅列授权根目录，不创建目录、不刷新保存原凭证、不上传删除。

## 契约

本地哈希命名基线与云 DB 哈希分别比较，不能按物理文件判断 WAL 数据。只有成功应用及必要上传校验后推进基线；晚到修改不被本轮确认。限制持久保存，不被手动／重启绕过；无变化检查不声称执行上传。

Log 在程序目录，Release 启用、无 C 盘回退。导出名来自当前标题，数据库身份与源路径保持稳定。

## 验证

2026-10-14 11:31（北京时间）原凭证单次 `weiyun.list` 返回 HTTP 200，JSON-RPC -32603，内层业务码 **117401**，服务端明确说明 daily quota used up / try again tomorrow。请求约 0.46 秒，诊断总计 0.497 秒，云写入零。证据：`artifacts/sync-diagnosis/92d32cc95a93402f9ce1f166d3962c45/result.json` 及同目录 Log。已确认每日接口额度耗尽，未发现账号封禁证据。

开发 Release 202 项回归通过，覆盖两次目录列表的无变化检查／零下载上传、远端变化、无变化上传零请求、损坏基线、并发修改、失败暂缓待发送、下载后继续上传、WAL、外观排除、限制重启、实际错误响应、SSE 不等 EOF、连接复用与目录身份变化、日志滚动脱敏及安全导出名。

开发 WPF 报告 `artifacts/sync-repair/ui-final-7b3b3bdac09f445082e95003f486afa1/ui-results/result.json` 完成且 20 项通过：逐帧模块像素一致、零禁用帧、无关任务不通知，保留焦点滚动对象集合，状态更新保留 inline，普通新增不重置任务集合，改名使用当前标题，程序旁有 Release 日志。已查看渲染 PNG。输入为 WPF 路由事件，不声称重复真实鼠标验收，仅本机 DPI 与合成云。发布包验收另记。

历史 72 秒没有原始请求及阶段日志，不能精确归因。重复查询和下载已减少，新日志记录阶段、请求与等待耗时。当天配额耗尽，本轮停止真实重试，无法声称云端恢复或取得新的真实传输耗时对比。编译和脚本检查不代替 WPF 行为验证。

完整撤回、并发同步代次、期限动画、两种主题／动态模式及九档滑块的开发 WPF 回归 111 项通过：`artifacts/sync-repair/undo-final-68880baf4c084a93b7917e8592cf17dc/ui-results/result.json`。所有实例正常退出，原 2.3.20 进程及启动时间保持不变。正式 Lite／Portable 的专项报告输出在 `artifacts/sync-repair/published-*`，以报告 Passed 和进程退出确认包验收。
