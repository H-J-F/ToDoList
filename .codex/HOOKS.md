# 项目 Hook

## 触发与闭环

`.codex/hooks.json` 注册一个同步 Stop command hook，Windows 通过 Python 调用 `.codex/hooks/stop.py`。命令从当前目录向上定位仓库，支持从 src 等子目录启动。Stop 统一调用 `tools/Build.py`，与 Build.bat 使用同一入口，超时预算为 1800 秒。

构建以最近成功发布的哈希为基线；未跟踪文件、Shell 修改、删除都参与检测，生成物和数据不参与。没有新改动时不发布也不递增。因而 Agent 主动执行 Build.bat 后，Stop 不会再重复构建。

成功仅输出 `{}`；详细日志写入 `artifacts/keeper/stop-build.log`。失败返回 JSON 的 `decision: block` 和日志路径，要求继续修复。已由 Stop 续跑的回合再次失败时只给 systemMessage，防止环境故障造成无限循环；此时不得宣称交付成功，最近成功基线不推进。

## 加载与信任

按 [OpenAI Hooks 官方说明](https://learn.chatgpt.com/docs/hooks)，项目 `.codex/hooks.json` 属于受支持的来源，新增或变更 hook 必须先由用户审阅并信任当前定义，未信任的 hook 会被跳过。可在支持的 Codex CLI `/hooks` 中审阅与信任，重新开启项目会话核对加载状态。不修改全局设置、不写入信任记录、不绕过信任机制。

文件配置完成和本地模拟执行不等于宿主已经自动加载。Agent 每轮仍必须主动执行 Build.bat；手动编辑器保存文件不会触发 Codex Stop，手工修改完成后运行 Build.bat。

## 复查

从项目根目录运行下面的命令可模拟 Stop，stdout 应是合法 JSON；真实发布细节查看日志。

```powershell
'{"hook_event_name":"Stop","stop_hook_active":false}' | python -X utf8 .codex/hooks/stop.py
```

源码与文档契约见 [闭环](项目闭环文档.md) 和 [构建发布](../docs/构建发布.md)。
