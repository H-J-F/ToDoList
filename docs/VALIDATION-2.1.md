# ToDoList 2.1.0 本地验证记录

2026-09-21，Windows 10 x64 19045，.NET SDK 10.0.302 / Runtime 10.0.10，Release 构建。维护者要求本轮暂不发布 GitHub Release；未创建版本标签或 Release。

## 自动化与实际界面

- 43 项业务／存储测试通过，覆盖事务、v1→v2、删除恢复、冲突合并、WAL 导出、校验拒绝、游标往返、最新页升序与相同时间稳定排序。
- 真实 WPF 窗口测试通过 109 项断言（111 条记录，另含富文本诊断及环境限制各一条），覆盖颜色连续输入和换行、自动颜色、清除格式、组合表情／肤色／旗帜、富文本链接往返、原生文档撤销重做、颜色／表情面板、设置布局、四套强调色×深浅主题、四档字号、最小窗口及 20 条并发状态更新。
- 首次只加载最新 200 条并定位底部，向上历史加载、向下返回、2,000 条缓存淘汰、普通刷新保留历史窗口以及历史位置新增定位底部均有界面断言。
- 关于卡片的点击及可访问 Invoke 路径验证了目标 URL；测试替换浏览器启动函数，并验证启动异常提示。公开仓库通过 GitHub API 核验；没有把拦截启动的测试描述为真实浏览器跨进程验证。

## 覆盖边界

- 当前会话 OLE 剪贴板返回 `CLIPBRD_E_CANT_OPEN`。验证了实际 DataObject 的 Unicode、RTF、自有 JSON 内容，以及粘贴适配、原生文档剪切、撤销重做；系统剪贴板跨程序复制粘贴尚需交互桌面复核。剪贴板忙时应用保留原文并提示。
- 中文输入使用 WPF TextComposition 测试；未宣称完成所有第三方 IME 的候选窗口实测。
- 当前物理桌面为 100% DPI；125%／150%／200% 为 WPF 离屏 RenderTargetBitmap 输出。Windows 11 Snap Layout、多显示器实际拖动及物理 DPI 切换未在此环境覆盖。
- 使用系统 Segoe UI Emoji。表情选择器过滤系统缺失的基础字形，现有内容遇到无法绘制的字符保留 Unicode；较新系统可能显示更多表情。
- 未进行可信代码签名，不申请证书或 Store 上架。

## 性能与构建

10 万条新建落盘数据库的 11 种索引查询 P95 为 2.35–3.31ms（含删除、模块、完成／添加排序、日期筛选及深处游标），均返回有界页；这些是数据库调用时间，不能代替 WPF 首屏时间。

真实 WPF 10 万条首屏 **1,493.07ms**，页签切换 **P95 114.66ms**，最大 **6 个任务控件**、**2,000 条缓存**、**1,924,000 字节内容缓存**；工作集从 244,023,296 增至 267,620,352 字节，包含运行时、字体与绘图等，不能等同于任务内容缓存。

最终包从同一干净提交 `9ecfcafe56be18bda2f92cb328c8daeefc20fed8` 构建（后续提交仅补充验证记录及校验工具）。精简版交付文件 **11,412,295 字节**，ZIP **4,669,059 字节**；便携版交付文件 **66,329,569 字节**，ZIP **60,616,184 字节**。最终交付大小及 ZIP 校验值亦见本地构建目录 `artifacts/local-2.1.0/sizes.json`、`SHA256SUMS.txt`。包中 BUILD.json 标明共同源码提交及构建模式。

最终精简版和便携版各自通过 109 项真实 WPF 断言及嵌入图标加载。便携版使用独立 `DOTNET_ROOT_X64`（不含共享框架）和全新 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 启动；主机日志确认 `Detected Single-File app bundle`、`Using internal fxr`，完成首次原生组件解压。精简版在同样缺少框架的根目录下正确返回缺失框架提示与官方安装链接；没有卸载或修改本机运行时。这些隔离测试不等于全新、断网 Windows 虚拟机验收。采用[官方运行时查找配置](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables)。

ZIP CRC、两包 SHA-256、两版共同源码提交、精简版 50MB 上限、无用户数据／调试符号／系统字体文件，以及每份第三方许可原文 SHA-256 均通过 `tools/Verify-Packages.py` 验证；结果见 `docs/evidence/v2.1/packages.json`。

公开前检查原有 6 次基线及本轮提交的完整历史、当前源码中的文件名和常见凭据模式，未发现 Data、备份、私钥或凭据命中；这是一项定向检查，不是完整安全审计。依赖版本通过 packages.lock.json / packages.portable.lock.json 分别固定，打包使用锁定还原。

实际 WPF 性能数据、界面断言、截图和约 20 秒交互演示位于 `docs/evidence/v2.1`。演示包含实际 WPF 动画帧与 Popup 绘制，按采集时间编码，不是界面模型图。

## 复现

```powershell
dotnet build ToDoList.sln -c Release
dotnet test tests/ToDoList.Tests -c Release
dotnet run --project tools/ToDoList.Benchmarks -c Release -- artifacts/benchmarks-2.1 100000
# 下列应用参数只能用于隔离数据目录
./src/ToDoList.App/bin/Release/net10.0-windows/ToDoList.exe --data-dir H:/temporary-test/Data --ui-smoke
./src/ToDoList.App/bin/Release/net10.0-windows/ToDoList.exe --data-dir H:/benchmark-test/Data --ui-perf
./tools/Publish.ps1 -Variant Both -OutputRoot ./artifacts/build-2.1.0
```

打包不包含 Data、Backup、测试数据、调试符号或编辑器缓存。精简版小于 50,000,000 字节由脚本强制检查。两版携带完整许可与来源清单；便携版包含运行时，精简版依赖单独安装的 .NET 10 Desktop Runtime x64。
