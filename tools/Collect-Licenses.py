"""Refresh distributed notices from pinned upstream sources and restored packages.
Run after dotnet restore. No credentials are read; source URLs and hashes are recorded.
"""
import concurrent.futures
import hashlib
import json
import pathlib
import re
import urllib.request
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "docs/licenses"
OUT.mkdir(parents=True, exist_ok=True)
CACHE = pathlib.Path.home() / ".nuget/packages"
TYPO = "https://raw.githubusercontent.com/samhocevar-forks/typography/e413bc6c20709d71ed6f06e152eaa63a891cdd11/"
sources = {
    "Emoji-Wpf-WTFPL.txt": "https://raw.githubusercontent.com/samhocevar/emoji.wpf/488c716cd4255506fe073e3d80ecbbfdfe4c6cf5/COPYING",
    "Typography-LICENSE.md": TYPO + "LICENSE.md",
    "Apache-2.0.txt": "https://www.apache.org/licenses/LICENSE-2.0.txt",
    "FreeType-FTL.txt": "https://raw.githubusercontent.com/freetype/freetype/VER-2-6-5/docs/FTL.TXT",
    "Unicode-CLDR.txt": "https://raw.githubusercontent.com/unicode-org/cldr/fd39b21340a0f6e8eb27758c5094f1a66379a051/unicode-license.txt",
    "Microsoft-Data-Sqlite.txt": "https://raw.githubusercontent.com/dotnet/efcore/v10.0.10/LICENSE.txt",
    "SQLitePCLRaw.txt": "https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v3.0.5/LICENSE.TXT",
    "SQLite-public-domain.md": "https://raw.githubusercontent.com/sqlite/sqlite/version-3.53.4/LICENSE.md",
    "WPF-runtime-NOTICES.txt": "https://raw.githubusercontent.com/dotnet/wpf/v10.0.10/THIRD-PARTY-NOTICES.TXT",
}
records = []
for name, url in sources.items():
    print(name, flush=True)
    data = urllib.request.urlopen(url, timeout=40).read()
    (OUT / name).write_bytes(data)
    records.append({"file": name, "source": url, "sha256": hashlib.sha256(data).hexdigest()})

local = {
    "SQLite-package.txt": "sqlite/3.53.4/LICENSE.txt",
    "ILLink-build-NOTICES.txt": "microsoft.net.illink.tasks/10.0.10/THIRD-PARTY-NOTICES.TXT",
    "JeremyAnsel-HLSL-Targets.txt": "jeremyansel.hlsl.targets/1.0.13/LICENSE.txt",
    "NET-runtime-LICENSE.txt": "microsoft.netcore.app.runtime.win-x64/10.0.10/LICENSE.TXT",
    "NET-runtime-NOTICES.txt": "microsoft.netcore.app.runtime.win-x64/10.0.10/THIRD-PARTY-NOTICES.TXT",
    "WindowsDesktop-runtime-LICENSE.txt": "microsoft.windowsdesktop.app.runtime.win-x64/10.0.10/LICENSE",
}
for name, package in local.items():
    data = (CACHE / package).read_bytes()
    (OUT / name).write_bytes(data)
    records.append({"file": name, "source": "nuget:" + package, "sha256": hashlib.sha256(data).hexdigest()})

# Preserve per-file notices in the two assemblies bundled inside Emoji.Wpf.
tree = json.load(urllib.request.urlopen("https://api.github.com/repos/samhocevar-forks/typography/git/trees/e413bc6c20709d71ed6f06e152eaa63a891cdd11?recursive=1"))
paths = [x["path"] for x in tree["tree"] if x["path"].endswith(".cs") and x["path"].startswith(("Typography.OpenFont/", "Typography.GlyphLayout/", "Build/N20/Typography.OpenFont/", "Build/N20/Typography.GlyphLayout/"))]
def header(path):
    text = urllib.request.urlopen(TYPO + path, timeout=40).read().decode("utf-8-sig")
    lines = text.splitlines()
    notice = []
    for line in lines:
        if re.match(r"\s*(using |namespace |#|public |internal )", line):
            break
        notice.append(line)
    return path, "\n".join(notice).strip()
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    notices = list(pool.map(header, paths))
body = "Typography source notices (pinned submodule of Emoji.Wpf 0.3.4)\nSource: " + TYPO + "\n\n"
body += "\n\n".join("File: " + path + "\n" + notice for path, notice in notices if notice)
(OUT / "Typography-source-notices.txt").write_text(body, encoding="utf-8")
mit = (ROOT / "LICENSE").read_text(encoding="utf-8")
attributions = sorted({line.strip().removeprefix("//").strip() for _, notice in notices for line in notice.splitlines() if line.startswith("//MIT,")})
(OUT / "Typography-MIT.txt").write_text("Typography MIT portions — copyright holders and dates as recorded in the pinned source headers:\n" + "\n".join(attributions) + "\n\n" + mit.replace("Copyright (c) 2026 H-J-F\n\n", ""), encoding="utf-8")

inventory = []
for project in ["src/ToDoList.App", "tests/ToDoList.Tests"]:
    assets = json.loads((ROOT / project / "obj/project.assets.json").read_text(encoding="utf-8"))
    for key, item in assets["libraries"].items():
        if item["type"] != "package":
            continue
        package = CACHE / item["path"]
        ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
        doc = ET.parse(next(package.glob("*.nuspec")))
        # Nuspec schema namespace differs between older test packages.
        metadata = next(x for x in doc.getroot() if x.tag.endswith("metadata"))
        values = {x.tag.split("}")[-1]: x.text for x in metadata}
        scope = "build-time" if key.startswith(("JeremyAnsel.HLSL.Targets/", "Microsoft.NET.ILLink.Tasks/")) else "application" if project.startswith("src") else "development/test"
        inventory.append({"package": key, "scope": scope,
                          "authors": values.get("authors"), "copyright": values.get("copyright"),
                          "license": values.get("license"), "licenseUrl": values.get("licenseUrl"),
                          "source": values.get("projectUrl"), "packageSha512": item.get("sha512")})
(ROOT / "docs/DEPENDENCIES.json").write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(OUT / "SOURCES.json").write_text(json.dumps(records, indent=2) + "\n", encoding="utf-8")
print(f"Saved {len(records)} license sources, {len(notices)} source notices, {len(inventory)} dependency records")
