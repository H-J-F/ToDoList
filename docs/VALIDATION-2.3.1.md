# ToDoList 2.3.1 本地验收

版本定为 2.3.1，窗口标题移除“修订4”，程序集信息版本移除预验收后缀。下方修订记录保留为历史验收证据。

## 修订4：浮动排序预览和日历圆形标记

- 长按 500ms 后显示 72% 不透明度的模块副本，保持按下位置与鼠标的相对偏移；原位置留空。其他模块在跨过行中点后以 180ms 缓动平移，让出插入位置；遵循“减少动态效果”设置。
- 预览只修改模块行的渲染位移，使用原始布局坐标判定位置，避免动画中的控件反复触发换位。越界松手、Esc、失去捕获或窗口失焦取消预览，不写入临时顺序。
- 松手时通过 ObservableCollection.Move 移动已有模块对象，再持久化顺序；不 Clear/重建集合、不调用 RefreshProjectsAsync/SyncSelectors、不切换全局 IsBusy，也不显示成功通知。右键上移/下移复用同一路径。当前模块、草稿归属和任务编辑状态保留。
- 日历面板继续与日期按钮等宽；日按钮居中且固定 40×40 DIP。保留 WPF-UI 的模板及状态，避免圈选随列宽拉伸。对应库模板：[WPF-UI 4.3.0 Calendar.xaml](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Controls/Calendar/Calendar.xaml)。
- `artifacts/drag-motion-final/drag-boundaries.json`：98 项检查通过，包括半透明预览、指针偏移、真实 WPF 动画的中间位移、双向让位、100 轮越界移回、取消与持久化，以及排序过程中零任务集合变化、零模块 Reset、零全局忙碌变化、零选择变化和任务编辑器对象/内容不变。
- 浅色/深色各检查 320、420、520 DIP 宽度下，当天与非当天选择标记在实际 Popup 中的宽高相等；截图保存在同一证据目录的 `calendar-*.png`。拖动截图为 `drag-preview-middle.png`、`drag-preview.png`、`drag-preview-up.png`。
- 本轮使用生产拖动方法、真实 WPF 控件及动画时钟，通过直接传入坐标复测；没有新增系统鼠标键盘实测。历史输入测试证据不能代替本次浮动预览测试。
- 最终 Lite、Portable 各通过 98 项同样的检查，记录为 `artifacts/drag-motion-packaged-lite/drag-boundaries.json`、`artifacts/drag-motion-packaged-portable/drag-boundaries.json`。Lite 另通过 35 项草稿/粘贴/日历/删除回归（`artifacts/revision4-taskfixes/taskfixes.json`）；业务/存储 54 项通过，产物检查通过，Release 构建无警告、无错误。

## 修订3：长按拖动越界崩溃

2026-09-23 16:49:49（Lite）和 16:51:32（Rider Debug）系统异常日志均记录 `ArgumentNullException(element)`，调用点为 `MainWindow.TaskManagement.cs` 中的 `ItemsControl.ContainerFromElement`。鼠标捕获后，移出列表仍会收到移动事件；`InputHitTest` 返回 null，旧代码未检查便传入 WPF。修订2的列表内实测未覆盖此路径。

- 修复：命中元素判空并限定列表边界；离开列表清除落点及高亮；松手时重新命中，防止沿用旧落点；取消后不再产生落点。
- 先将原处理代码提取为生产方法，用真实 WPF 列表的边界坐标重现相同异常，再加入修复。修复前失败记录：`artifacts/drag-boundaries-before/drag-boundaries.json`。
- Debug 修复后 25 项检查通过：四边及远离窗口、列表外松手、未收到最后移动事件的松手、全部模块/空白/自身、取消后移动、100 轮移出移回、重新进入后排序及独立数据库连接读取。记录：`artifacts/drag-boundaries-after/drag-boundaries.json`。
- 最终 Lite、Portable 产物各通过同样的 25 项边界检查：`artifacts/drag-boundaries-final-lite/drag-boundaries.json`、`artifacts/drag-boundaries-final-portable/drag-boundaries.json`。业务/存储 54 项通过，构建产物检查通过。
- 本轮边界回归直接向真实界面的生产处理方法传入坐标，不注入系统鼠标键盘，不等同于新增一次人工/系统鼠标实测。前轮系统输入证据保留在下文。
- 标题标识 `2.3.1 修订3`，ProductVersion 为 `2.3.1-recheck.3`；保持版本号 2.3.1。

## 重新验收说明

上一版只调用排序方法、只比较日期容器宽度，不能证明真实拖动和展开日历正确；此前“11 项完成”的结论超出了证据覆盖。用户反馈后补充了系统鼠标、键盘和剪贴板操作，以及实际 Popup 边界检查。

Windows 事件日志 2026-09-23 16:27:22 记录的崩溃进程是旧版 ToDoList.App.exe，栈为 InsertContent → NormalizeTypography → ResourceReferenceExpression.OnDetach。新版采用纯文本路径；全局单实例可能导致打开新版时仍唤起旧版，修订2现已在标题中显示版本，启动遇到其他版本时明确提示。

- 无干扰实测：按住约 1.5 秒后拖动模块 A 到 C 下方，数据库顺序变为 B/C/A；定时器约 500ms 进入拖动状态。
- 60 轮系统键盘快捷键执行复制、追加粘贴、撤销、重做、全选替换，每轮比对完整多行/空行/表情内容，共 124 项断言通过，无崩溃。
- 后台 WPF Copy/Paste 路由命令另执行 100 轮复制/追加/撤销/重做/替换；专项总计 35 项检查通过。
- 日历页签展开面板及日期按钮均为 320 DIP，左右屏幕坐标一致；报告的年/月/日/范围日期逐一打开 Popup 检查，范围日期改为上下布局。
- 本轮鼠标键盘原始证据：artifacts/interaction-uninterrupted-01/interaction.json；后台专项：artifacts/taskfixes-recheck-01/taskfixes.json。
- 前台焦点变化/外部输入干扰的早期交互记录不作为功能失败或通过的依据；实际键盘测试禁用测试编辑器 IME，既有 IME 提交逻辑另由功能回归覆盖。


## 修订2最终构建结果

- 最终 Lite 与 Portable 各通过 35 项专项检查，原始结果为 artifacts/v231-final-lite/taskfixes.json 和 artifacts/v231-final-portable/taskfixes.json。
- 最终 Lite 通过 40 项既有界面功能回归：artifacts/v231-final-features/features.json。
- 业务/存储测试 54 项通过，Release 构建无警告、无错误。
- 两套安装更新时原 Data 逐文件 SHA-256 不变；任务及设置保留。原程序另备份到 Build/PreservedData。

## 逐项修改

1. 粘贴统一使用纯文本、自动主题文字色；修复 WPF 克隆行内动态字体资源后引发的异常，多行与表情可正常粘贴。
2. 业务操作提交后立即写入数据库；外观设置即时保存，窗口位置短防抖保存。
3. 数据库导入导出覆盖内容、状态历史、删除恢复信息、模块顺序、状态颜色及完成排序；本地设置单独保存。
4. 模块长按 500ms 拖动排序，显示落点；顺序同步侧栏与下拉框并持久化，新模块追加。
5. 已删除页签支持单项或多选彻底删除，确认后在事务中校验状态和修订号并清理任务历史；冲突整体回滚。
6. 日期选择继续使用 WPF-UI CalendarDatePicker，日期区与上方控件拉伸对齐。
7. 托盘菜单使用常规字重、12 DIP 字号与布局取整。
8. 任务分隔线从圆角边框独立，右边界与日期分隔线一致。
9. 存储错误及用户提示统一为“待办笔记”，数据库格式保持版本 3。
10. Lite 使用原生 apphost，仅保留 ToDoList.exe 一个可执行文件，提供缺少运行时的安装提示。
11. 底部草稿切换页签不弹保存提示；切换具体模块同步归属，全部保留原归属。已有任务编辑仍保留确认逻辑。

## 验证

- Release 构建：0 警告、0 错误；业务与存储测试 54 项通过。
- 逐项界面专项 24 项通过：同框“测试”复制、多行/空行/表情、撤销重做、纯文本替换富文本、草稿导航、旧任务编辑确认、持久化、排序、彻底删除确认/取消。
- 既有功能界面回归 40 项通过，包括日历、设置、报告和输入法提交行为。
- 字号专项 200 条记录通过，覆盖 12/14/16/18 DIP、表情、编辑复用和实时字号切换。
- 不将历史验收记录视为本轮全部环境覆盖；长按真实鼠标拖动已补测；高 DPI 跨屏托盘清晰度仍需人工体验验收。
- Lite 与 Portable 最终 首次 2.3.1 产物各自通过 24 项专项 UI 检查（修订2另按以上新增检查重新验证）；构建产物检查通过，Lite 恰好一个 EXE。
- 隔离运行时目录（含 hostfxr 与普通 .NET Runtime，不含 Desktop Runtime）已验证 Lite 拒绝启动并准确指出 Microsoft.WindowsDesktop.App 10.0.0 x64；宿主日志生成对应微软下载链接和 GUI 对话框请求。本轮未点击下载按钮，浏览器跳转和纯净 Windows 安装流程未实测。
- 真实数据不会进入源码或公开证据，原任务状态不自动标记完成。

原始结果位于 artifacts/v231-lite-taskfixes/taskfixes.json、artifacts/v231-portable-taskfixes/taskfixes.json、artifacts/taskfixes-features-01/features.json、artifacts/taskfixes-typography-02/typography.json。

## 构建与数据迁移

输出 Build/ToDoList-2.3.1-win-x64-lite 与 Build/ToDoList-2.3.1-win-x64-portable。先验证不含用户数据的产物，再向两套安装写入当前运行实例 Data 的 SQLite 一致性快照及本地设置、历史备份。保留旧版与原数据；两个新版本的数据独立，不会自动同步。
