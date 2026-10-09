# 六套候选主题验收 · 2026-09-25

## 职责

记录本轮主题改造的可重复验证和视觉评审边界。六套新增配色仍为候选，最终保留名单等待用户逐套看图决定。

## 入口

- [主题定义与预览流程](主题与预览.md)
- [主题单元测试](../tests/ToDoList.Tests/ThemeTests.cs)
- [WPF 专项](../src/ToDoList.App/Services/UiThemePreview.cs)
- [开发阶段截图画廊](../artifacts/themes-selection-fix/index.html)
- [交付候选截图画廊](../artifacts/theme-candidates-final/index.html)（由本地发布包生成）

## 契约

所有测试使用各自的隔离 Data；未操作用户数据库，未退出旧版本程序。截图由真实 WPF 窗口与独立 Popup 视觉树渲染，再追加主题名、明暗和来源。六套使用同一演示笔记，便于横向比较。

本轮更新 README 外观说明、架构主题契约与主题专项文档，并将主题模块加入 doc-map。构建发布流程本身无语义变化，因此保留原构建文档；历史验收文档保留原记录。文档结构检查不作为语义验收证明。

## 验证

- 开发构建编译通过，无警告。单元测试 **74 项通过**，其中新增主题测试 14 项：八套固定主题对比度、四种旧模式迁移及持久化、未知／缺失主题回退、系统明暗解析。
- 更新后的完整 WPF smoke **Passed=true，105 条检查记录**：包含富文本、主题选择、设置布局、动画、任务操作与列表行为。记录在 `artifacts/themes-smoke-01/screenshots/ui-smoke-results.json`；记录数量不等同全部都是独立断言。
- 主题专项开发轮 **Completed=true、Passed=true，277 条断言通过**，记录在 `artifacts/themes-selection-fix/theme-results.json`。覆盖真实按钮、输入边界、三种任务状态、菜单打开时换色、输入弹窗、日历、编辑内容与选区保留、重置和保存；八套固定主题 × 四档字号均检查最小窗口。
- 已逐图复核六套主界面总览，并抽查设置下拉框、日历、弹窗、18 DIP 最小窗口。文本选区已改为主题色半透明覆盖层，避免 WPF Adorner 选区遮住字形；补充选区混色后的对比度测试、实际控件透明度断言，并重新目视确认浅色输入框、深色输入框和富文本全选文字可读。
- 交付包使用相同专项重新生成 `artifacts/theme-candidates-final`，最终结果以该目录的 `theme-results.json`、`restart-results.json` 和根目录 `Build/latest.json` 为准。每套至少两张评审图，额外提供编辑、菜单、日历、输入弹窗和最小窗口，合计 42 张单套场景图及一张总览。

### 限制

未切换物理显示器 DPI，未修改用户 Windows 明暗设置；跟随系统的两种解析分支由单元测试覆盖，实际窗口遍历经典浅／深色。未改变用户自定义富文本颜色，故这些自定义颜色不承诺跨主题对比度。配色计算及界面断言不能代替用户审美确认。
