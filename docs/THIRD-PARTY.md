# 第三方软件与数据声明

本项目代码：MIT，Copyright (c) 2026 H-J-F。以下组件的原有版权及许可继续适用。本文件、`licenses` 全目录、`DEPENDENCIES.json` 随精简版和便携版分发。系统字体不随程序打包。

| 组件及实际版本 | 来源、版权 | 许可原文 |
| --- | --- | --- |
| PDFsharp-WPF、PDFsharp-MigraDoc-WPF 6.2.4 | [empira/PDFsharp v6.2.4](https://github.com/empira/PDFsharp/tree/v6.2.4)，© 2026 empira | MIT，`licenses/PDFsharp-MigraDoc-MIT.txt`；用于本地 PDF 报告；构建排除 WPFonts／Snippets／Quality 示例程序集 |
| Microsoft.Extensions.Logging.Abstractions 8.0.3、DependencyInjection.Abstractions 8.0.2 | Microsoft / .NET Foundation and contributors | MIT，`licenses/Microsoft-Extensions-LICENSE.txt`、`Microsoft-Extensions-NOTICES.txt` |
| WPF-UI、WPF-UI.Abstractions 4.3.0 | [lepoco/wpfui](https://github.com/lepoco/wpfui/tree/4.3.0)，Lepo / Leszek Pomianowski and WPF UI Contributors | MIT，`licenses/WPF-UI.txt` |
| Fluent System Icons（随 WPF UI 4.3.0 字体资源） | [Microsoft](https://github.com/microsoft/fluentui-system-icons)，Microsoft Corporation；上游包未提供独立图标版本号 | MIT，`licenses/Fluent-System-Icons.txt` |
| Emoji.Wpf 0.3.4 | [Sam Hocevar](https://github.com/samhocevar/emoji.wpf/tree/488c716cd4255506fe073e3d80ecbbfdfe4c6cf5)，Copyright © 2017–2021 Sam Hocevar | **WTFPL v2**，`licenses/Emoji-Wpf-WTFPL.txt` |
| Stfu 0.1.1 | [Sam Hocevar](https://github.com/samhocevar/stfu)，Copyright © 2017–2021 Sam Hocevar | WTFPL v2，同上；传递运行时依赖 |
| Typography.GlyphLayout、Typography.OpenFont（Emoji.Wpf 内置 DLL，无独立 NuGet 包版本） | [固定子模块 e413bc6](https://github.com/samhocevar-forks/typography/tree/e413bc6c20709d71ed6f06e152eaa63a891cdd11)，WinterDev 及各源文件作者 | 总体 MIT；包含 Apache-2.0、FreeType、Adobe BSD、Unicode 等来源；见下段 |
| Unicode Emoji 数据、CLDR 来源 | [Emoji 数据提交 963e436](https://github.com/samhocevar/unicode-emoji/tree/963e436f8ab08184c36c25032aec22a8eb85e05a)、[CLDR 提交 fd39b21](https://github.com/unicode-org/cldr/tree/fd39b21340a0f6e8eb27758c5094f1a66379a051)，Unicode, Inc. | `licenses/Unicode-CLDR.txt` 及 Typography 源声明；Emoji.Wpf 内嵌 Unicode 数据，CLDR 为上游生成来源 |
| Microsoft.Data.Sqlite、Microsoft.Data.Sqlite.Core 10.0.10 | [dotnet/efcore](https://github.com/dotnet/efcore/tree/v10.0.10)，.NET Foundation and other contributors | MIT，`licenses/Microsoft-Data-Sqlite.txt` |
| SQLitePCLRaw.bundle_e_sqlite3、config.e_sqlite3、core、provider.e_sqlite3 3.0.5 | [Eric Sink / SourceGear](https://github.com/ericsink/SQLitePCL.raw/tree/v3.0.5)，Copyright 2014–2026 SourceGear, LLC | Apache-2.0，`licenses/SQLitePCLRaw.txt` |
| SQLite 3.53.4（原生 e_sqlite3） | [SQLite](https://sqlite.org/)，NuGet 打包者 Eric Sink / SourceGear | Public domain，`licenses/SQLite-public-domain.md` |
| .NET、WPF / WindowsDesktop 10.0.10（便携版） | Microsoft、.NET Foundation and other contributors | `licenses/NET-runtime-LICENSE.txt`、`NET-runtime-NOTICES.txt`、`WindowsDesktop-runtime-LICENSE.txt`、`WPF-runtime-NOTICES.txt`；包含适用第三方 NOTICE |
| JeremyAnsel.HLSL.Targets 1.0.13 | [Jérémy Ansel](https://github.com/JeremyAnsel/JeremyAnsel.HLSL.Targets)，Copyright © 2019 Jérémy Ansel | MIT，`licenses/JeremyAnsel-HLSL-Targets.txt`；Emoji.Wpf 声明的构建工具依赖，无该工具 DLL 随应用运行 |
| Microsoft.NET.ILLink.Tasks 10.0.10 | [dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.10)，Microsoft / .NET Foundation and contributors | MIT，`licenses/NET-runtime-LICENSE.txt`、`ILLink-build-NOTICES.txt`；单文件发布引入的 SDK 构建工具，本应用明确 `PublishTrimmed=false`，不裁剪 WPF |

Typography 内置程序集根据 Emoji.Wpf DLL 的 InformationalVersion 追溯到确切源提交，再解析其固定子模块。`Typography-LICENSE.md` 保留上游整体来源列表；`Typography-source-notices.txt` 收录 OpenFont / GlyphLayout 及 N20 构建共享代码的版权及许可声明，包括 Adobe 字形数据的完整 BSD 条款、Mono.Xna Team 2006 的 System.Numerics 兼容代码 MIT 原文。`Typography-MIT.txt`、`Apache-2.0.txt`、`FreeType-FTL.txt`、Unicode 原文补充完整许可。上游整体来源表还列有演示、几何及平台代码；本应用不因此分发这些演示程序或字体文件。

开发测试依赖包括 Microsoft.NET.Test.Sdk、Microsoft.TestPlatform、Microsoft.CodeCoverage、xUnit、xUnit runner、Newtonsoft.Json 等，精确版本、作者、许可链接和包 SHA-512 在 `DEPENDENCIES.json` 的 `development/test` 条目中；不会随桌面应用打包。NuGet 元包与实际运行 DLL 的区别在上述清单中注明，不把测试工具声明为应用运行时组件。

应用浅蓝图标为本仓库自绘 SVG/PNG/ICO，适用项目 MIT；不代表 WPF UI 或 Microsoft 商标。Emoji.Wpf 调用系统 Segoe UI Emoji 获取字形，程序未分发该字体。

`tools/Collect-Licenses.py` 根据还原结果生成清单并获取许可；`licenses/SOURCES.json` 记录来源与 SHA-256。升级依赖后需重新核对清单和原文，再构建交付包。
