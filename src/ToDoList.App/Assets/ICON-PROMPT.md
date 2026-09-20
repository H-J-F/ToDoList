# Fluent 图标源

本次重构用可缩放矢量替换先前的生成式卡通图标。设计：浅蓝圆角底、白色清单、蓝色勾选与浅蓝文字线，无文字和水印。

- 矢量原稿：`app-icon.svg`。
- 可复现绘制：`tools/New-FluentIcon.ps1`，以 WPF DrawingVisual 逐尺寸栅格化。
- 高分辨率原稿：`app-icon.png` / `icon-512.png`。
- Windows ICO：16、24、32、48、64、128、256 像素；小尺寸减少细节。
- 原生成式图标及其提示词保存在 Git 基线提交中，可追溯。
