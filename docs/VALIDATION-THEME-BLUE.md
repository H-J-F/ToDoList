# 主题下拉框与蓝色候选验收 · 2026-09-25

## 职责

记录 2.3.7 候选版的主题控件修正、两套蓝色新增配色和真实 WPF 验证。原六套候选保留；晴空蔚蓝、深海夜蓝仍等待用户根据截图决定保留、调整或移除。

## 入口

- [主题契约与来源](主题与预览.md)
- [真实鼠标专项](../src/ToDoList.App/Services/UiThemeDropdownChecks.cs)
- [完整主题专项](../src/ToDoList.App/Services/UiThemePreview.cs)
- [开发验收结果](../artifacts/theme-blue-check-02/theme-results.json)
- [发布包截图画廊](../artifacts/theme-blue-candidates/index.html)
- [发布包主题结果](../artifacts/theme-blue-candidates/theme-results.json)与[重启结果](../artifacts/theme-blue-candidates/restart-results.json)

## 契约

主题选择框沿用设置框 164 DIP 宽度，三色缩略条占 24 DIP，主题名完整显示。专用弹层宽度跟随控件，40 DIP 固定行高，280 DIP 视口按整行滚动；普通模式仅淡入，减少动画时直接显示。保留滚轮和键盘操作，不通过屏蔽全部 BringIntoView 请求来阻止跳动。

主题候选由六套增加至八套，选择项共 11 项。晴空蔚蓝使用浅蓝侧栏、白色阅读区和深蓝操作色；深海夜蓝使用海军蓝底和浅蓝操作色。配色源四色及链接保留在 ThemeCatalog 中，交互颜色根据对比度派生。设置格式继续为 3，数据库格式不变。

## 验证

- `dotnet test tests/ToDoList.Tests -c Release --no-restore`：76 项通过，包含 16 项主题测试；十套固定主题的文字、按钮、选区混色与必要控件边界均满足约定对比度。
- 开发完整 WPF 专项：`Completed=true`、`Passed=true`，641 条断言。十套固定主题在 800×560、1100×760 和 12／14／16／18 DIP 下检查名称、编辑区、滚动条避让和设置框两侧对齐；最长名称额外检查展开弹层。
- 真实 OS 鼠标专项覆盖两种动画模式、首／中／末项选中、立即／延迟移入及反复移入移出；记录弹层、选中行的屏幕 Y 坐标和滚动偏移。覆盖滚轮、Home、End、Down、Enter、Esc，以及鼠标点击选择并保存。结果见 `dropdown-observations.json`。
- 更新后的完整 WPF smoke：`Passed=true`，109 条检查记录，见 `artifacts/theme-blue-smoke-02/screenshots/ui-smoke-results.json`。验收已按专用弹层更新动画断言，确认普通模式淡入、减少动画时直接显示；记录数量不等同独立断言数量。
- 原库模板采样记录在 `artifacts/theme-dropdown-before-02`：观察到标准展开过程的纵向位移；没有稳定复现动画结束后“上移一整行”，因此不将动画位移等同于该问题的完整根因。该早期采样仅用于诊断，包含尚未完善的等待条件，不能作为通过报告。新模板在全部指针场景下验证坐标与滚动位置稳定。
- 新增两套主界面、展开选择框、关闭选择框的对齐截图，以及菜单、日历、弹窗、编辑和最小窗口；八套候选均使用同一演示内容，提供四列总览与画廊。截图包含独立 Popup，来自实际 WPF 渲染。
- 发布验证执行根目录 `Build.bat`，退出码和 Lite／Portable 输出以 `Build/latest.json` 与构建日志为准。发布包重新生成画廊，并由 Portable 读取 Lite 的隔离数据验证新蓝色主题重启恢复；最终结果以以上 JSON 为准。

本轮语义复核并更新 README、架构、主题契约和专项入口；构建发布行为未改变，保留原构建文档。旧六套验收报告保留为历史记录。测试只使用 artifacts 下隔离数据，未操作用户数据库或清理历史 Build。

### 限制

未切换物理显示器 DPI 或用户 Windows 明暗设置；跟随系统的解析分支由单元测试覆盖。配色和截图仍需要用户审美确认。截图为 WPF 视觉树与实际位置的 Popup 合成，不代表整个桌面录屏。
