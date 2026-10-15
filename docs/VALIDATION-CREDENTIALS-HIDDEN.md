# 凭证目录、离线账号与笔记隐藏验收

## 职责

记录本轮凭证从用户目录迁至程序 Auth、登录失败仍加载账号本地工作区，以及整本笔记隐藏／恢复的验证。全部测试使用独立目录和合成账号，不读取、修改用户 Data 或正式授权。

## 入口

- `CloudCredentialLocationTests`：DPAPI、迁移、目录移动、目标不可写、损坏来源及退出后不恢复旧活动选择。
- `BookVisibilityTests` 与 `CloudVisibilityTests`：完整笔记导出／复制、跨设备隐藏和恢复、设置并发冲突。
- `MainWindow.BookVisibilityChecks`：`ToDoList.exe --data-dir <工程>/artifacts/book-visibility/<新标识>/Data --ui-revision --book-visibility-checks`。首次运行后同目录分别追加 `--book-restart-check` 和 `--book-corrupt-check`，验证真实进程重启时的过期／损坏凭证。
- 账号／内嵌浏览器回归沿用 `--data-dir <工程>/artifacts/account-ui/<新标识>/Data --ui-account --login-browser-checks --account-routed-input`。

## 契约

正式凭证位于程序根目录 Auth，隔离模式使用指定 Data/Auth。迁移只处理匹配当前 Data 的旧目录中已知文件，验证新位置后再清理；失败保存来源，不改用户数据库。DPAPI 仍绑定 Windows 用户，移动目录不代表可以跨 Windows 用户解密。

隐藏是数据库 BookSettings 的 IsHidden 设置，随账号同步。完整集合继续用于身份查重与存储操作，展示集合用于导航；全部隐藏时正文清空并提供恢复入口。显式导入并打开隐藏笔记会开启会话展示开关，保持原隐藏状态。

## 验证

2026-10-14 开发构建零警告、零错误；七项新增存储／同步／凭证测试通过。笔记 WPF 首轮 26 项、授权过期重启 5 项、凭证损坏重启 5 项均通过，进程退出码为 0；包含设置保存失败保持原笔记、草稿取消、同步修改隐藏设置，以及云目录初始化失败保留账号笔记。报告位于 `artifacts/book-visibility/ad10cfcc692b4e15a1634500f58a265b/ui-results/result-*.json`，浅深主题及全部隐藏截图已检查。

账号与内嵌浏览器专项 102 项通过，进程正常结束；合成网页运行真实 WebView2，并验证临时目录在隔离 Data/Auth/Browser 下。报告为 `artifacts/account-ui/9fbd1c941850494a86231f6cce6e6310/ui-results/result.json`。正式包与完整测试由 Build.bat 执行，版本及是否补跑包内 UI 以本轮交付结果为准。

项目架构、云同步、README 与构建发布文档已按新契约更新。主题与预览文档虽由共享 Models 和 XAML 命中变更映射，本轮没有修改主题契约、设置格式或预览入口，保持原文。

路由事件不能代替真实 OS 鼠标键盘验证；合成账号与拦截网页不代表真实微信扫码已验收。本轮不进行真实微云上传或操作用户云存档。构建脚本的文档结构校验不等于文档语义验收。
